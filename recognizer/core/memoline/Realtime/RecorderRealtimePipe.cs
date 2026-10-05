using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Realtime;

public sealed record RecorderRealtimeRequest(int SchemaVersion, string[] Channels, bool IncludeSnapshot = true);

internal sealed class PipeJsonWriter : StreamWriter
{
    public PipeJsonWriter(Stream stream) : base(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true; }
    public override async ValueTask DisposeAsync()
    {
        // Writes still fail normally. Cleanup after a disconnected client is not a recording error.
        try { await base.DisposeAsync(); } catch (IOException) { }
    }
}

/// <summary>Per-session, current-user-only local IPC. Each client owns its subscription and queue.</summary>
public sealed class RecorderRealtimePipeServer : IAsyncDisposable
{
    private readonly RecorderRealtimeHub _hub;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _clients = [];
    private readonly Task _accept;
    private int _disposed;
    public string PipeName { get; }
    public string ManifestPath { get; }

    public RecorderRealtimePipeServer(RecorderRealtimeHub hub)
    {
        _hub = hub;
        PipeName = "MemoLine.Recognizer." + hub.SessionId;
        ManifestPath = hub.FilePath + ".live.json";
        var listener = CreatePipe();
        try
        {
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, pipeName = PipeName, sessionId = hub.SessionId, processId = Environment.ProcessId,
                filePath = hub.FilePath, liveFilePath = hub.LiveFilePath, frequency = Stopwatch.Frequency,
                channels = RecorderRealtimeTopics.All, subscriptionFilters = new[] { RecorderRealtimeTopics.TabletMetadata }
            }, MemolineWriter.Json), new UTF8Encoding(false));
        }
        catch { listener.Dispose(); throw; }
        _accept = Task.Run(() => AcceptAsync(listener));
    }

    private NamedPipeServerStream CreatePipe() => new(PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptAsync(NamedPipeServerStream listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(_stop.Token);
                var connected = listener;
                listener = CreatePipe();
                var client = ServeAsync(connected);
                lock (_sync) _clients.Add(client);
                _ = client.ContinueWith(t => { lock (_sync) _clients.Remove(t); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { listener.Dispose(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        await using (var writer = new PipeJsonWriter(pipe))
        {
            long lastDelivered = 0;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                string requestLine = await reader.ReadLineAsync(handshake.Token) ?? throw new IOException("Missing subscription request.");
                if (requestLine.Length > 4096) throw new InvalidDataException("Subscription request too large.");
                var request = JsonSerializer.Deserialize<RecorderRealtimeRequest>(requestLine, MemolineWriter.Json)
                    ?? throw new InvalidDataException("Invalid subscription request.");
                if (request.SchemaVersion != 1) throw new InvalidDataException("Unsupported realtime schema.");
                await using var subscription = _hub.Subscribe(request.Channels, request.IncludeSnapshot);
                await WriteAsync(writer, _hub.Control("hello", new
                {
                    pipeName = PipeName, channels = RecorderRealtimeTopics.Expand(request.Channels),
                    frequency = Stopwatch.Frequency, originTicks = _hub.ClockOriginTicks, filePath = _hub.FilePath, liveFilePath = _hub.LiveFilePath,
                    delivery = "inMemoryBeforeDurableFlush", scope = "CSP", evidenceCapturedNotifications = true
                }));
                await foreach (var message in subscription.ReadAllAsync(_stop.Token))
                {
                    await WriteAsync(writer, message);
                    lastDelivered = message.Sequence;
                }
            }
            catch (RecorderStreamGapException ex)
            {
                await TryControlAsync(writer, "streamGap", new
                { reason = "clientTooSlow", lastDeliveredSequence = lastDelivered, lastAvailableSequence = ex.LastAvailableSequence, liveFilePath = _hub.LiveFilePath });
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // A client disconnect must not affect recording.
            catch (Exception ex) { await TryControlAsync(writer, "error", new { message = ex.Message }); }
        }
    }

    private Task WriteAsync(StreamWriter writer, RecorderRealtimeEvent message) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(message, MemolineWriter.Json).AsMemory(), _stop.Token);
    private async Task TryControlAsync(StreamWriter writer, string kind, object data)
    { try { await WriteAsync(writer, _hub.Control(kind, data)); } catch (Exception) { } }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task[] draining;
        lock (_sync) draining = _clients.ToArray();
        try { await Task.WhenAll(draining).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { }
        await _stop.CancelAsync();
        await _accept;
        Task[] clients;
        lock (_sync) clients = _clients.ToArray();
        await Task.WhenAll(clients);
        _stop.Dispose();
    }
}

public static class RecorderRealtimeClient
{
    public static IAsyncEnumerable<RecorderRealtimeEvent> FollowKeyboardAsync(string pipeName, CancellationToken token = default) => SubscribeAsync(pipeName, ["keyboard"], cancellationToken: token);
    public static IAsyncEnumerable<RecorderRealtimeEvent> FollowMouseAsync(string pipeName, CancellationToken token = default) => SubscribeAsync(pipeName, ["mouse"], cancellationToken: token);
    public static IAsyncEnumerable<RecorderRealtimeEvent> FollowTabletAsync(string pipeName, CancellationToken token = default) => SubscribeAsync(pipeName, ["tablet"], cancellationToken: token);
    public static IAsyncEnumerable<RecorderRealtimeEvent> FollowCoreAsync(string pipeName, string module, CancellationToken token = default) => SubscribeAsync(pipeName, ["core." + module], cancellationToken: token);
    public static IAsyncEnumerable<RecorderRealtimeEvent> FollowCoresAsync(string pipeName, CancellationToken token = default) => SubscribeAsync(pipeName, ["cores"], cancellationToken: token);

    public static async IAsyncEnumerable<RecorderRealtimeEvent> SubscribeAsync(string pipeName, IEnumerable<string> channels,
        bool includeSnapshot = true, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new PipeJsonWriter(pipe);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderRealtimeRequest(1,
            RecorderRealtimeTopics.Expand(channels), includeSnapshot), MemolineWriter.Json).AsMemory(), cancellationToken);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var message = JsonSerializer.Deserialize<RecorderRealtimeEvent>(line, MemolineWriter.Json)
                ?? throw new InvalidDataException("Invalid realtime message.");
            if (message.SchemaVersion != 1) throw new InvalidDataException("Unsupported realtime schema.");
            if (message.Kind == "streamGap") throw new RecorderStreamGapException(message.Data.GetProperty("lastAvailableSequence").GetInt64());
            if (message.Channel == "system" && message.Kind == "error") throw new InvalidDataException(message.Data.GetProperty("message").GetString());
            yield return message;
            if (message.Channel == "system" && message.Kind == "sessionEnded") yield break;
        }
        throw new EndOfStreamException("Realtime connection ended before sessionEnded; reconnect or recover from the recording.");
    }
}
