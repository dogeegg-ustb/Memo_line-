using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Capture;

/// <summary>Local JSONL control endpoint; original input still travels through the recorder hooks.</summary>
public sealed class RecorderInputControlServer : IAsyncDisposable
{
    private sealed class ControlWriter(Stream stream) : StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
    {
        protected override void Dispose(bool disposing)
        {
            // An RPC client normally disconnects after one response. Its cleanup
            // must not terminate the server before the next controller connects.
            try { base.Dispose(disposing); } catch (IOException) { }
        }
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly IRecorderInputControl _control;
    private readonly Func<bool> _recordingReady;
    private readonly MemolineWriter? _recording;
    private readonly Func<RecorderStateRequest, CancellationToken, Task<RecorderStateResponse>>? _requestStates;
    private readonly UnifiedInputCapture? _capture;
    private MemolineEvent? _endBoundary;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    public string PipeName { get; }

    public RecorderInputControlServer(IRecorderInputControl control, Func<bool>? recordingReady = null, string? pipeName = null,
        MemolineWriter? recording = null, Func<RecorderStateRequest, CancellationToken, Task<RecorderStateResponse>>? requestStates = null,
        UnifiedInputCapture? capture = null)
    {
        _control = control;
        _recordingReady = recordingReady ?? (() => true);
        _recording = recording;
        _requestStates = requestStates;
        _capture = capture;
        PipeName = pipeName ?? $"memoline-recorder-input-{System.Environment.ProcessId}";
        _server = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new ControlWriter(pipe) { AutoFlush = true };
                try
                {
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        object response;
                        try
                        {
                            if (line.Length > 16_384) throw new ArgumentException("Control request too long.");
                            using var document = JsonDocument.Parse(line);
                            var request = document.RootElement;
                            switch (request.GetProperty("command").GetString())
                            {
                                case "requestStates":
                                    if (request.EnumerateObject().Any(p => p.Name is not ("command" or "requestId" or "sessionId" or "modules" or "saveId" or "brushPanelRoi")))
                                        throw new ArgumentException("Unknown state request option.");
                                    var stateRequest = request.Deserialize<RecorderStateRequest>(Json) ?? throw new ArgumentException("Missing state request.");
                                    if (string.IsNullOrWhiteSpace(stateRequest.RequestId) || stateRequest.RequestId.Length > 128
                                        || stateRequest.Modules is null || stateRequest.Modules.Length is < 1 or > 8
                                        || stateRequest.Modules.Distinct().Count() != stateRequest.Modules.Length
                                        || stateRequest.Modules.Any(m => !RecorderStateRequests.Modules.Contains(m)) || !stateRequest.HasValidPanelSelection()
                                        || stateRequest.SaveId is { } saveId && (saveId.Length is < 1 or > 128 || saveId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
                                        throw new ArgumentException("Invalid state request identity, modules or saveId.");
                                    if (!_recordingReady() || _recording is null || _requestStates is null || stateRequest.SessionId != _recording.SessionId)
                                        throw new InvalidOperationException("State request requires the current initialized recording session.");
                                    response = await _requestStates(stateRequest, _stop.Token);
                                    break;
                                case "getInputControlStatus":
                                    response = _control.GetInputControlStatus();
                                    break;
                                case "getRecordingStatus":
                                    ValidateOptions(request, "command");
                                    response = new { success = true, sessionId = _recording?.SessionId,
                                        recordingReady = _recordingReady(), inputCaptureAvailable = _recording is not null && _capture is not null };
                                    break;
                                case "prepareEditBoundary":
                                    RequireEditCapture();
                                    ValidateOptions(request, "command", "requestId");
                                    string editRequestId = ReadIdentity(request, "requestId");
                                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                                    {
                                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                                        MemolineEvent editBoundary;
                                        try { editBoundary = await _capture!.PrepareEditBoundaryAsync(editRequestId, timeout.Token); }
                                        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                                        { throw new TimeoutException("Wait for the current pen contact to end (30 second timeout)."); }
                                        await _recording!.FlushAsync(cancellationToken: _stop.Token);
                                        response = new { success = true, sessionId = _recording.SessionId, requestId = editRequestId,
                                            appendId = editBoundary.AppendId, triggerTicks = editBoundary.Ticks };
                                    }
                                    break;
                                case "beginEditTransaction":
                                    RequireEditCapture();
                                    ValidateOptions(request, "command", "transactionId");
                                    string beginId = ReadIdentity(request, "transactionId");
                                    var began = await _capture!.BeginEditTransactionAsync(beginId, _control, _stop.Token);
                                    response = new { success = true, sessionId = _recording!.SessionId, transactionId = beginId, appendId = began.AppendId };
                                    break;
                                case "endEditTransaction":
                                    RequireEditCapture();
                                    ValidateOptions(request, "command", "transactionId", "outcome");
                                    string endId = ReadIdentity(request, "transactionId");
                                    var ended = await _capture!.EndEditTransactionAsync(endId, ReadIdentity(request, "outcome"), _control, _stop.Token);
                                    response = new { success = true, sessionId = _recording!.SessionId, transactionId = endId, appendId = ended.AppendId };
                                    break;
                                case "recordEditingAction":
                                    RequireEditCapture();
                                    ValidateOptions(request, "command", "transactionId", "editKind", "changes");
                                    string actionId = ReadIdentity(request, "transactionId");
                                    string editKind = ReadIdentity(request, "editKind");
                                    if (editKind is not ("eventEditing" or "preciseUndo")) throw new ArgumentException("Unknown editing action.");
                                    var action = await _capture!.RecordEditingActionAsync(actionId,editKind,request.GetProperty("changes"),_stop.Token);
                                    await _recording!.FlushAsync(cancellationToken: _stop.Token);
                                    response = new { success = true, sessionId = _recording.SessionId, transactionId = actionId, appendId = action.AppendId };
                                    break;
                                case "prepareRecordingEnd":
                                    if (request.EnumerateObject().Any(p => p.Name != "command"))
                                        throw new ArgumentException("prepareRecordingEnd only accepts command.");
                                    if (!_recordingReady() || _recording is null || _capture is null)
                                        throw new InvalidOperationException("Recording is not ready for an end boundary.");
                                    if (_endBoundary is null)
                                    {
                                        using var endTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                                        endTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                                        try { _endBoundary = await _capture.PrepareRecordingEndAsync(endTimeout.Token); }
                                        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                                        { throw new TimeoutException("Wait for the current pen or mouse contact to end (30 second timeout)."); }
                                    }
                                    var boundary = _endBoundary ?? throw new IOException("End boundary was not appended.");
                                    await _recording.FlushAsync(cancellationToken: _stop.Token);
                                    response = new { success = true, sessionId = _recording.SessionId,
                                        triggerTicks = boundary.Ticks, appendId = boundary.AppendId };
                                    break;
                                case "configureInputInterception":
                                    if (_capture?.EditTransactionActive == true) throw new InvalidOperationException("Input protection is owned by the active edit transaction.");
                                    foreach (var property in request.EnumerateObject())
                                        if (property.Name is not ("command" or "enabled"))
                                            throw new ArgumentException($"Unknown input control option: {property.Name}");
                                    if (!request.TryGetProperty("enabled", out var enabled)
                                        || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                        throw new ArgumentException("enabled must be an explicit boolean.");
                                    var configuration = request.Deserialize<RecorderInputControlRequest>(Json)
                                        ?? throw new ArgumentException("Missing configuration.");
                                    response = configuration.Enabled && !_recordingReady()
                                        ? _control.GetInputControlStatus() with { Success = false, Error = "Wait for recorder initialization before enabling interception." }
                                        : await _control.ConfigureInputInterceptionAsync(configuration, _stop.Token);
                                    break;
                                case "restoreAutomaticInputProtection":
                                    if (_capture?.EditTransactionActive == true) throw new InvalidOperationException("Input protection is owned by the active edit transaction.");
                                    response = _control.RestoreAutomaticInputProtection();
                                    break;
                                case "requestClipSave":
                                    foreach (var property in request.EnumerateObject())
                                        if (property.Name is not ("command" or "expectedClipPath" or "requestId" or "triggerTicks" or "activateCsp"))
                                            throw new ArgumentException($"Unknown save option: {property.Name}");
                                    string expectedPath = request.GetProperty("expectedClipPath").GetString()
                                        ?? throw new ArgumentException("expectedClipPath is required.");
                                    string requestId = request.GetProperty("requestId").GetString()
                                        ?? throw new ArgumentException("requestId is required.");
                                    if (!Path.IsPathFullyQualified(expectedPath) || !expectedPath.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)
                                        || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
                                        throw new ArgumentException("Expected an absolute .clip path and a nonempty requestId of at most 128 characters.");
                                    long? triggerTicks = null;
                                    bool activateCsp = false;
                                    if (request.TryGetProperty("activateCsp", out var activate))
                                    {
                                        if (activate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                            throw new ArgumentException("activateCsp must be a boolean.");
                                        activateCsp = activate.GetBoolean();
                                    }
                                    if (request.TryGetProperty("triggerTicks", out var trigger) && trigger.ValueKind != JsonValueKind.Null)
                                    {
                                        if (trigger.ValueKind != JsonValueKind.Number || !trigger.TryGetInt64(out long ticks) || ticks < 0)
                                            throw new ArgumentException("triggerTicks must be a nonnegative 64-bit integer or null.");
                                        triggerTicks = ticks;
                                    }
                                    response = !_recordingReady()
                                        ? new RecorderClipSaveResult(false, requestId, false, null, false, "Wait for recorder initialization before saving.", triggerTicks)
                                        : await _control.RequestClipSaveAsync(new(expectedPath, requestId, triggerTicks, activateCsp), _stop.Token);
                                    break;
                                default:
                                    throw new ArgumentException("Unknown input control command.");
                            }
                        }
                        catch (Exception ex) when (ex is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or IOException or TimeoutException)
                        {
                            response = new { success = false, error = ex.Message };
                        }
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), _stop.Token);
                    }
                }
                catch (IOException) { /* A disconnected controller does not stop recording. */ }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server;
        _stop.Dispose();
    }

    private void RequireEditCapture()
    {
        if (!_recordingReady() || _recording is null || _capture is null)
            throw new InvalidOperationException("Editing requires the current initialized input capture session.");
    }
    private static void ValidateOptions(JsonElement request, params string[] allowed)
    {
        if (request.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Unknown editing option.");
    }
    private static string ReadIdentity(JsonElement request, string name)
    {
        string? identity = request.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 128) throw new ArgumentException($"{name} must be nonempty and at most 128 characters.");
        return identity;
    }
}
