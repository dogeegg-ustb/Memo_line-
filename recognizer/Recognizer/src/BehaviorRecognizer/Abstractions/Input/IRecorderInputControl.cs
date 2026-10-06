namespace BehaviorRecognizer.Abstractions.Input;

public sealed record RecorderInputControlRequest
{
    public bool Enabled { get; init; }
}

public sealed record RecorderInputControlStatus(
    bool Success, bool Controlled, bool Enabled, bool SaveWorkerReady, bool Busy,
    string? Error);

public sealed record RecorderClipSaveRequest(string ExpectedClipPath, string RequestId, long? TriggerTicks = null,
    bool ActivateCsp = false);
public sealed record RecorderClipSaveResult(bool Success, string RequestId, bool SaveInputDispatched,
    long? SaveInputDispatchedTicks, bool SaveCompletionConfirmed, string? Error, long? TriggerTicks = null);

/// <summary>Control CSP keyboard/mouse delivery while continuing to record original hardware observations. Pen input passes through.</summary>
public interface IRecorderInputControl
{
    RecorderInputControlStatus GetInputControlStatus();
    Task<RecorderInputControlStatus> ConfigureInputInterceptionAsync(
        RecorderInputControlRequest request, CancellationToken cancellationToken = default);
    RecorderInputControlStatus RestoreAutomaticInputProtection();
    Task<RecorderClipSaveResult> RequestClipSaveAsync(RecorderClipSaveRequest request,
        CancellationToken cancellationToken = default);
}
