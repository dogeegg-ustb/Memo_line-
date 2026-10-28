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
    Control? CreateConfigurationControl() => null;
    Task<string> PrepareAsync(string clipPath, CancellationToken token);
    void Attach(RecognizerMonitor monitor);
    void CaptureQueued(CaptureRequest request);
    Task PacketCommittedAsync(CaptureRequest request, SnapshotUpdate update, CancellationToken token);
    void CaptureFailed(CaptureRequest request, string reason);
    /// <summary>The native editingActionCompleted frame of a Memoline edit: the observation its operation packets are sealed at.</summary>
    void MemolineOperationRecorded(string transactionId, ulong appendId, long ticks) { }
    void RecordingEndFailed(string reason) { }
    /// <summary>The UUID of the layer a newly created layer continues (a paste of Memoline's recycle bin copy of it),
    /// judged from the new layer's first pixels; null when it is a layer of its own.</summary>
    Task<string?> PredecessorOfCreatedLayerAsync(LayerReference layer, string pngPath, CancellationToken token) => Task.FromResult<string?>(null);
    Task FinishRecordingAsync(CancellationToken token);
    Task<string> SealAsync(CancellationToken token);
}
