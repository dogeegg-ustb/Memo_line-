using System.Runtime.InteropServices;
using System.Windows;

namespace ScreenCanvasTransform;

public partial class App : Application
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = (IntPtr)(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(StartupEventArgs e)
    {
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        base.OnStartup(e);
    }
}
