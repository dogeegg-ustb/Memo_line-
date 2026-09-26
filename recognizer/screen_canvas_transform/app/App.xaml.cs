using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ScreenCanvasTransform.Diagnostics;

namespace ScreenCanvasTransform;

public partial class App : Application
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = (IntPtr)(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        _ = AllocConsole();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            File.WriteAllText(LiveDebugLog.LogFilePath, "");
        }
        catch
        {
            // ignore
        }

        LiveDebugLog.Write("ScreenCanvasTransform 启动（实时调试日志）");
        LiveDebugLog.Write($"日志文件: {LiveDebugLog.LogFilePath}");
        base.OnStartup(e);
    }
}
