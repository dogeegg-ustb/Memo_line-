namespace BehaviorRecognizer.Capture;

/// <summary>Save dispatch and input delivery boundary, replaceable for offline queue verification.</summary>
internal interface ILayerSaveGuardBackend
{
    bool SaveWorkerReady { get; }
    bool IsCspForeground { get; }
    /// <summary>Requests the switch to CSP; true when accepted. CSP becomes foreground some time later.</summary>
    bool TryActivateCspWindow() => false;
    nint ForegroundWindow { get; }
    string ForegroundWindowTitle { get; }
    bool IsCspPoint(int x, int y);
    bool IsKeyHeld(int vk);
    int GetSystemMetric(int metric);
    Task InitializeSaveWorkerAsync(string executable);
    Task DispatchSaveAsync(nint hwnd);
    uint ReplayInputs(LayerSaveGuard.Input[] inputs);
    IReadOnlySet<nint> VisibleTopLevelWindows(nint hwnd) => new HashSet<nint>();
    bool InModalUi(nint hwnd) => false;
}
