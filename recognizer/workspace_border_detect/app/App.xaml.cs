using System.Runtime.InteropServices;
using System.Windows;

namespace WorkspaceBorderDetect;

public partial class App : Application
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = (IntPtr)(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Prefer programmatic Per-Monitor V2; manifest also declares it.
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        base.OnStartup(e);
    }
}
