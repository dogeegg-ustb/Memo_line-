using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BehaviorRecognizer.Capture;

/// <summary>Reads the actual pointer target on every call; no cached hover decision.</summary>
public static class CspWindowProbe
{
    public static void EnableDpiAwareness()
    {
        if (OperatingSystem.IsWindows()) Native.SetProcessDpiAwarenessContext(new nint(-4));
    }
    private static readonly object CacheSync = new();
    private static uint _cachedPid;
    private static long _cacheExpires;
    private static bool _cachedCsp;
    public static bool IsCspForeground() => IsCspWindow(Native.GetForegroundWindow());

    public static bool TryGetCspCursor(out ScreenPoint point)
    {
        point = default;
        if (!Native.GetCursorPos(out point)) return false;
        return IsCspPoint(point.X, point.Y);
    }

    public static bool IsCspPoint(int x, int y)
    {
        var window = Native.GetAncestor(Native.WindowFromPoint(new ScreenPoint { X = x, Y = y }), 2); // GA_ROOT
        return IsCspWindow(window);
    }

    private static bool IsCspWindow(nint window)
    {
        if (window == 0) return false;
        Native.GetWindowThreadProcessId(window, out var id);
        if (id == 0) return false;
        lock (CacheSync)
        {
            long now = Stopwatch.GetTimestamp();
            if (id == _cachedPid && now < _cacheExpires) return _cachedCsp;
            bool result;
            try
            {
                using var process = Process.GetProcessById((int)id);
                result = process.ProcessName.Equals("CLIPStudioPaint", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { result = false; }
            catch (System.ComponentModel.Win32Exception) { result = false; }
            catch (InvalidOperationException) { result = false; }
            _cachedPid = id;
            _cachedCsp = result;
            _cacheExpires = now + Stopwatch.Frequency;
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenPoint { public int X; public int Y; }

    private static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(ScreenPoint point);
        [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out ScreenPoint point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
