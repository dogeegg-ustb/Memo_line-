using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Realtime;

public static class RecorderRealtimeTopics
{
    public const string Keyboard = "keyboard", Mouse = "mouse", Tablet = "tablet";
    public const string TabletMetadata = "tablet.metadata";
    public const string Shortcuts = "shortcuts", Layers = "layers", LayerStage = "layerstage", Subtools = "subtools";
    public static readonly string[] CoreModules = ["brushState", "subtoolState", "currentLayerState", "colorState", "canvasViewState", "clipState"];
    public static IReadOnlyList<string> All { get; } = new[] { Keyboard, Mouse, Tablet, Shortcuts, Layers, LayerStage, Subtools }.Concat(CoreModules.Select(m => "core." + m)).ToArray();
    public static string[] Expand(IEnumerable<string> topics)
    {
        var expanded = topics.SelectMany(t => t switch { "all" => All, "cores" => All.Where(x => x.StartsWith("core.")), _ => [t] })
            .Distinct(StringComparer.Ordinal).ToArray();
        if (expanded.Length == 0 || expanded.Any(t => !All.Contains(t) && t != TabletMetadata)) throw new ArgumentException("Unknown or empty realtime channel selection.");
        return expanded;
    }
}

public sealed record RecorderRealtimeEvent(int SchemaVersion, string SessionId, long Sequence, string Channel,
    string Kind, bool IsSnapshot, ulong AppendId, ulong EventId, long Ticks, long AppendedTicks, long PublishedTicks,
    ulong? OperationId, ulong[]? RelatedEventIds, HardwareDeviceSource? DeviceSource, JsonElement Data);

public sealed class RecorderStreamGapException(long lastAvailableSequence) : IOException("Realtime subscriber fell behind; recover from the recording file.")
{
    public long LastAvailableSequence { get; } = lastAvailableSequence;
}

public sealed class RecorderRealtimeSubscription : IAsyncDisposable
{
    internal readonly HashSet<string> Topics;
    internal readonly Channel<RecorderRealtimeEvent> Queue;
    private readonly RecorderRealtimeHub _owner;
    internal RecorderRealtimeSubscription(RecorderRealtimeHub owner, string[] topics, int capacity)
    {
        _owner = owner;
        Topics = topics.ToHashSet(StringComparer.Ordinal);
        Queue = Channel.CreateBounded<RecorderRealtimeEvent>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }
    internal bool Accepts(RecorderRealtimeEvent message) => message.Channel == "system" || Topics.Contains(message.Channel)
        || (Topics.Contains(RecorderRealtimeTopics.TabletMetadata) && message.Channel == "tablet"
            && message.Kind is "tabletDeviceChanged" or "driverConfiguration");
    public IAsyncEnumerable<RecorderRealtimeEvent> ReadAllAsync(CancellationToken cancellationToken = default) => Queue.Reader.ReadAllAsync(cancellationToken);
    public ValueTask DisposeAsync() { _owner.Unsubscribe(this); return ValueTask.CompletedTask; }
}

/// <summary>Ordered, asynchronous fan-out. Subscriber code never runs on an input/recording producer.</summary>
public sealed class RecorderRealtimeHub : IAsyncDisposable
{
    private readonly MemolineWriter _writer;
    private readonly Channel<MemolineEvent> _pending = Channel.CreateUnbounded<MemolineEvent>(new() { SingleReader = true });
    private readonly object _sync = new();
    private readonly HashSet<RecorderRealtimeSubscription> _subscribers = [];
    private readonly Dictionary<string, RecorderRealtimeEvent> _latest = [];
    private readonly Dictionary<string, RecorderRealtimeEvent> _confirmedMessages = [];
    private readonly Dictionary<string, JsonElement> _confirmed = [];
    private readonly Task _pump;
    private bool _closed;
    private long _sequence;
    public string SessionId => _writer.SessionId;
    public string FilePath => _writer.FilePath;
    public string LiveFilePath => _writer.LiveFilePath;
    public long ClockOriginTicks => _writer.ClockOriginTicks;
    public long CurrentSequence { get { lock (_sync) return _sequence; } }

    public RecorderRealtimeHub(MemolineWriter writer)
    {
        _writer = writer;
        _writer.RecordAppended += OnRecord;
        _writer.DiagnosticRecorded += OnRecord;
        _pump = Task.Run(PumpAsync);
    }

    public RecorderRealtimeSubscription Subscribe(IEnumerable<string> topics, bool includeSnapshot = true, int capacity = 2048)
    {
        if (capacity < 8) throw new ArgumentOutOfRangeException(nameof(capacity));
        var subscription = new RecorderRealtimeSubscription(this, RecorderRealtimeTopics.Expand(topics), capacity);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (includeSnapshot)
                foreach (var item in _latest.Values.Concat(_confirmedMessages.Values).DistinctBy(m => m.Sequence)
                    .Where(subscription.Accepts).OrderBy(m => m.Sequence))
                    if (!subscription.Queue.Writer.TryWrite(item with { IsSnapshot = true }))
                        throw new ArgumentException("Capacity is too small for the initial snapshot.", nameof(capacity));
            _subscribers.Add(subscription);
        }
        return subscription;
    }
    public RecorderRealtimeSubscription SubscribeKeyboard(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.Keyboard], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeMouse(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.Mouse], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeTablet(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.Tablet], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeCore(string module, bool includeSnapshot = true) => Subscribe(["core." + module], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeCores(bool includeSnapshot = true) => Subscribe(["cores"], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeShortcuts(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.Shortcuts], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeLayers(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.Layers], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeLayerStage(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.LayerStage], includeSnapshot);
    public RecorderRealtimeSubscription SubscribeSubtools(bool includeSnapshot = true) => Subscribe([RecorderRealtimeTopics.Subtools], includeSnapshot);

    internal void Unsubscribe(RecorderRealtimeSubscription subscription)
    {
        lock (_sync) { _subscribers.Remove(subscription); subscription.Queue.Writer.TryComplete(); }
    }

    private static (string Topic, string Kind)? Route(MemolineEvent frame) => frame.Kind switch
    {
        "keyboardStateChanged" => ("keyboard", frame.Data.GetProperty("action").GetString()!),
        "keyInput" => ("keyboard", "keyInput"),
        "shortcutResolved" => (frame.Data.TryGetProperty("inputType", out var input) && input.GetString() == "mouseWheel" ? "mouse" : "keyboard", "shortcutResolved"),
        "mouseCursorChanged" => ("mouse", "cursor"),
        "mouseDown" or "mouseUp" or "mouseDrag" or "mouseWheel" or "mouseInterrupted" => ("mouse", frame.Kind),
        "penBegin" or "penSample" or "penEnd" or "penInterrupted" => ("tablet", frame.Kind),
        "tabletStateChanged" => ("tablet", frame.Data.GetProperty("action").GetString()!),
        "tabletDeviceChanged" or "driverConfiguration" => ("tablet", frame.Kind),
        "coreStateUpdated" => ("core." + frame.Data.GetProperty("module").GetString(), "stateUpdated"),
        "coreEvidenceCaptured" => ("core." + frame.Data.GetProperty("module").GetString(), "evidenceCaptured"),
        "shortcutConfiguration" => (RecorderRealtimeTopics.Shortcuts, "configurationUpdated"),
        "recordingEndRequested" => ("system", frame.Kind),
        _ => null
    };
    private void OnRecord(MemolineEvent frame)
    {
        // Do not parse data or run subscribers under the recording producer's lock.
        if (frame.Kind is "keyboardStateChanged" or "keyInput" or "shortcutResolved" or
            "mouseCursorChanged" or "mouseDown" or "mouseUp" or "mouseDrag" or "mouseWheel" or "mouseInterrupted" or
            "penBegin" or "penSample" or "penEnd" or "penInterrupted" or "tabletStateChanged" or
            "tabletDeviceChanged" or "driverConfiguration" or "coreStateUpdated" or "coreEvidenceCaptured" or "shortcutConfiguration" or "recordingEndRequested") _pending.Writer.TryWrite(frame);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var frame in _pending.Reader.ReadAllAsync())
            {
                var (topic, kind) = Route(frame)!.Value;
                JsonElement data = frame.Data;
                if (frame.Kind == "coreStateUpdated")
                {
                    var value = JsonNode.Parse(data.GetRawText())!.AsObject();
                    var status = data.GetProperty("status").GetString();
                    _confirmed.TryGetValue(topic, out var previous);
                    var state = data.GetProperty("state");
                    value["changedFields"] = JsonSerializer.SerializeToNode(status == "changed"
                        ? ChangedFields(previous, state) : Array.Empty<string>());
                    if ((status is "changed" or "unchanged") && state.ValueKind != JsonValueKind.Null) _confirmed[topic] = state.Clone();
                    value["lastConfirmedState"] = _confirmed.TryGetValue(topic, out var confirmed) ? JsonNode.Parse(confirmed.GetRawText()) : null;
                    data = JsonSerializer.SerializeToElement(value);
                }
                lock (_sync)
                {
                    var message = new RecorderRealtimeEvent(1, SessionId, ++_sequence, topic, kind, false,
                        frame.AppendId, frame.EventId, frame.Ticks, frame.AppendedTicks, _writer.NowTicks, frame.OperationId,
                        frame.RelatedEventIds, frame.DeviceSource, data);
                    // Evidence notifications are live boundaries, not confirmed
                    // states or initial snapshots. Do not overwrite either cache.
                    if (frame.Kind is "coreEvidenceCaptured" or "recordingEndRequested") { Publish(message); continue; }
                    if (frame.Kind == "coreStateUpdated" && data.GetProperty("status").GetString() is "changed" or "unchanged"
                        && data.GetProperty("state").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        _confirmedMessages[topic] = message;
                    if (kind is not ("keyInput" or "shortcutResolved" or "tabletDeviceChanged" or "driverConfiguration")) _latest[topic] = message;
                    else if (topic == "tablet")
                    {
                        string key = topic + "." + kind;
                        if (kind == "tabletDeviceChanged" && data.TryGetProperty("deviceId", out var deviceId)) key += "." + deviceId.GetString();
                        _latest[key] = message;
                    }
                    Publish(message);
                    if (frame.Kind == "coreStateUpdated" && topic is "core.clipState" or "core.currentLayerState" or "core.subtoolState")
                    {
                        // Each core has its own interface, cache and subscribers. A current
                        // layer observation cannot replace a file-based structure snapshot.
                        var (channel, projectionKind) = topic switch {
                            "core.clipState" => (RecorderRealtimeTopics.Layers, "layerStructureUpdated"),
                            "core.subtoolState" => (RecorderRealtimeTopics.Subtools, "ocrUpdated"),
                            _ => (RecorderRealtimeTopics.LayerStage, "currentLayerUpdated")
                        };
                        var projection = message with { Sequence = ++_sequence, Channel = channel,
                            Kind = projectionKind };
                        _latest[channel] = projection;
                        if (_confirmedMessages.TryGetValue(topic, out var latestConfirmed)
                            && latestConfirmed.Sequence == message.Sequence)
                            _confirmedMessages[channel] = projection;
                        Publish(projection);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_sync) foreach (var subscriber in _subscribers) subscriber.Queue.Writer.TryComplete(ex);
            throw;
        }
    }

    private void Publish(RecorderRealtimeEvent message)
    {
        foreach (var subscriber in _subscribers.ToArray())
            if (subscriber.Accepts(message) && !subscriber.Queue.Writer.TryWrite(message))
            {
                subscriber.Queue.Writer.TryComplete(new RecorderStreamGapException(message.Sequence));
                _subscribers.Remove(subscriber);
            }
    }

    private static string[] ChangedFields(JsonElement previous, JsonElement current)
    {
        if (previous.ValueKind == JsonValueKind.Undefined) return ["/"];
        var before = JsonNode.Parse(previous.GetRawText());
        var after = JsonNode.Parse(current.GetRawText());
        if (JsonNode.DeepEquals(before, after)) return [];
        if (before is not JsonObject left || after is not JsonObject right) return ["/"];
        return left.Select(p => p.Key).Union(right.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal)
            .Where(k => !JsonNode.DeepEquals(left[k], right[k]))
            .Select(k => "/" + k.Replace("~", "~0").Replace("/", "~1")).ToArray();
    }

    public RecorderRealtimeEvent Control(string kind, object data) => new(1, SessionId, CurrentSequence, "system", kind,
        false, 0, 0, _writer.NowTicks, _writer.NowTicks, _writer.NowTicks, null, null, null, JsonSerializer.SerializeToElement(data, MemolineWriter.Json));

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _writer.RecordAppended -= OnRecord;
            _writer.DiagnosticRecorded -= OnRecord;
            _pending.Writer.TryComplete();
        }
        await _pump;
        lock (_sync)
        {
            Publish(Control("sessionEnded", new { filePath = FilePath, lastSequence = _sequence }));
            foreach (var subscriber in _subscribers) subscriber.Queue.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
}
