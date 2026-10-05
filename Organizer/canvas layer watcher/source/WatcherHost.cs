namespace CanvasLayerWatcher;

// Optional session ownership; the standalone watcher continues to use its own
// start/stop behavior. The demo owns recorder startup and final sealing.
internal interface IWatcherHost
{
    string Title { get; }
    string DiffRoot { get; }
    string SettingsPath { get; }
    string OutputDirectory { get; }
    bool HasPendingSeal { get; }
    event Action<string>? Status;
    Task<string> PrepareAsync(string clipPath, CancellationToken token);
    void Attach(RecognizerMonitor monitor);
    void CaptureQueued(CaptureRequest request);
    Task PacketCommittedAsync(CaptureRequest request, SnapshotUpdate update, CancellationToken token);
    void CaptureFailed(CaptureRequest request, string reason);
    Task FinishRecordingAsync(CancellationToken token);
    Task<string> SealAsync(CancellationToken token);
}
