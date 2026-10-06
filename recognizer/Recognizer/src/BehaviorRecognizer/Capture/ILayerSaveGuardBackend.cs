namespace BehaviorRecognizer.Capture;

/// <summary>Save dispatch and input delivery boundary, replaceable for offline queue verification.</summary>
internal interface ILayerSaveGuardBackend
{
    bool SaveWorkerReady { get; }
    bool IsCspForeground { get; }
    bool TryActivateCspWindow() => false;
    nint ForegroundWindow { get; }
    string ForegroundWindowTitle { get; }
    bool IsCspPoint(int x, int y);
    bool IsKeyHeld(int vk);
    int GetSystemMetric(int metric);
    Task InitializeSaveWorkerAsync(string executable);
    Task DispatchSaveAsync(nint hwnd);
    uint ReplayInputs(LayerSaveGuard.Input[] inputs);
}
