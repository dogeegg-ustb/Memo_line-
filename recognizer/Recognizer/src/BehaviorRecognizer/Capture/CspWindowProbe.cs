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
    private static nint _lastForeground;
    public static bool IsCspForeground()
    {
        var window = Native.GetForegroundWindow();
        if (!IsCspWindow(window)) return false;
        lock (CacheSync) _lastForeground = window;
        return true;
    }

    /// <summary>Asks Windows to bring CSP to the front; true when the request was accepted. The switch itself is
    /// asynchronous (CSP's UI thread activates its window when it next pumps messages), so right afterwards
    /// the foreground may still read as no window or another one: callers poll <see cref="IsCspForeground"/>.</summary>
    public static bool TryActivateCspWindow()
    {
        nint window;
        lock (CacheSync) window = _lastForeground;
        // The last CSP foreground can be one of its popups, since hidden; activate the frame that owns it.
        if (IsCspWindow(window)) window = Native.GetAncestor(window, 3); // GA_ROOTOWNER
        if (!IsCspWindow(window) || !Native.IsWindowVisible(window))
        {
            var windows = new List<nint>();
            foreach (var process in Process.GetProcessesByName("CLIPStudioPaint"))
                using (process) try { if (process.MainWindowHandle != 0) windows.Add(process.MainWindowHandle); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            if (windows.Count != 1) return false;
            window = windows[0];
        }
        if (Native.IsIconic(window)) Native.ShowWindow(window, 9); // SW_RESTORE
        return Native.SetForegroundWindow(window);
    }

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

    /// <summary>Visible top-level windows of the process that owns <paramref name="window"/>: a dropdown or
    /// menu opened by a click appears here as a window that was not there before the click.</summary>
    public static IReadOnlySet<nint> VisibleTopLevelWindows(nint window)
    {
        var result = new HashSet<nint>();
        if (window == 0) return result;
        Native.GetWindowThreadProcessId(window, out var owner);
        if (owner == 0) return result;
        Native.EnumWindows((candidate, _) =>
        {
            if (Native.IsWindowVisible(candidate) && Native.GetWindowThreadProcessId(candidate, out var id) != 0 && id == owner)
                result.Add(candidate);
            return true;
        }, 0);
        return result;
    }

    /// <summary>The window's UI thread is inside a menu loop, a move/size loop or holds mouse capture.</summary>
    public static bool InModalUi(nint window)
    {
        if (window == 0) return false;
        uint thread = Native.GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !Native.GetGUIThreadInfo(thread, ref info)) return false;
        const uint InMoveSize = 0x2, InMenuMode = 0x4, SystemMenuMode = 0x8, PopupMenuMode = 0x10;
        return (info.Flags & (InMoveSize | InMenuMode | SystemMenuMode | PopupMenuMode)) != 0
            || info.Capture != 0 || info.MenuOwner != 0 || info.MoveSize != 0;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size; public uint Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    private static class Native
    {
        public delegate bool EnumWindowsProc(nint window, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] public static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(ScreenPoint point);
        [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out ScreenPoint point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
