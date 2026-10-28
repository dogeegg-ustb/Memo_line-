using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace StrokeReplay;

/// <summary>
/// An invisible topmost window over the whole desktop that takes every pointer and mouse input while Memoline injects
/// into CSP, with a small hole wherever an injected input has to reach the window below.
/// <para>CSP draws from every pen it is shown, whatever its pointer ID. A real pen hovering over the tablet entered range
/// first, so it is the primary pointer and the injected pen is not: CSP pulled each replayed stroke towards the hovering
/// pen ("rays") and kept the replayed strokes as one undo step (seen live 2026-10-25, reproduced 2026-10-26 with a second
/// synthetic pen). With this window up CSP never sees the real pen and each stroke is its own undo step.</para>
/// <para>Measured in CSP (2026-10-26): it has to stay up for the whole replay. Raised per stroke, a real pen that reached
/// CSP in between leaves it in the middle of the next stroke and CSP ends that stroke after a few samples. CSP also
/// confirms a pen-down some time after it handled it: closing the hole right after the down's back-pressure round left
/// nothing drawn, 30 ms later the whole stroke was drawn (<see cref="MinimumHole"/>).</para>
/// </summary>
internal sealed class InputShield
{
    /// <summary>How long a hole stays open at least, counted from when it opened.</summary>
    internal static readonly TimeSpan MinimumHole = TimeSpan.FromMilliseconds(150);
    /// <summary>A shield nobody opened a hole in for this long is taken down: it must never keep the desktop from
    /// the user after a transaction that did not end.</summary>
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);
    private const int HoleRadius = 6;
    private static readonly object s_gate = new();
    private static InputShield? s_active;
    private static int s_raised;

    // The class is registered once per process, with this procedure: it must outlive every shield.
    private static readonly Native.WindowProcedure s_procedure = Procedure;

    private readonly object _sync = new();
    private readonly List<Hole> _holes = [];
    private readonly Thread _thread;
    private readonly System.Threading.Timer _idle;
    private Rectangle _bounds;
    private nint _window;
    private bool _closed;

    /// <summary>The shield up right now, or null.</summary>
    internal static InputShield? Active { get { lock (s_gate) return s_active; } }

    /// <summary>Raises the shield (or keeps the one already up) until the returned handle is disposed.</summary>
    internal static IDisposable Raise()
    {
        lock (s_gate)
        {
            if (s_active is null || s_active._closed) s_active = new InputShield();
            s_raised++;
            s_active.Touch();
        }
        return new Lowering();
    }

    private sealed class Lowering : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            InputShield? closing = null;
            lock (s_gate)
                if (--s_raised == 0) { closing = s_active; s_active = null; }
            closing?.Close();
        }
    }

    private InputShield()
    {
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try { Create(); }
            catch (Exception error) { failure = error; }
            finally { ready.Set(); }
            if (failure is null) Pump();
        }) { IsBackground = true, Name = "Memoline input shield" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure is not null) throw new InvalidOperationException("无法创建输入屏蔽窗口：" + failure.Message, failure);
        _idle = new System.Threading.Timer(_ => Close(), null, IdleLimit, Timeout.InfiniteTimeSpan);
    }

    private void Create()
    {
        // Physical pixels on every monitor, whatever the process default is.
        Native.SetThreadDpiAwarenessContext(-4);
        _bounds = new Rectangle(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
        var instance = Native.GetModuleHandle(null);
        var windowClass = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Procedure = s_procedure, Instance = instance,
            Cursor = Native.LoadCursor(0, 32512), ClassName = "MemolineInputShield"
        };
        if (Native.RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410) // class already exists
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // TOPMOST | TOOLWINDOW | NOREDIRECTIONBITMAP | NOACTIVATE: above everything, no taskbar button, nothing drawn
        // (the screen looks the same, so screenshots and CSP's screen colour picker see through it), never the foreground.
        const uint exStyle = 0x8 | 0x80 | 0x200000 | 0x8000000;
        _window = Native.CreateWindowEx(exStyle, "MemolineInputShield", "Memoline input shield", 0x80000000,
            _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, 0, 0, instance, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Native.ShowWindow(_window, 4); // SW_SHOWNOACTIVATE
    }

    private void Pump()
    {
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    private static nint Procedure(nint window, uint message, nint wParam, nint lParam)
    {
        const uint MouseActivate = 0x21, PointerActivate = 0x24B, Close = 0x10, Destroy = 0x2;
        if (message is MouseActivate or PointerActivate) return 3; // MA_NOACTIVATE / PA_NOACTIVATE
        if (message == Close) { Native.DestroyWindow(window); return 0; }
        if (message == Destroy) { Native.PostQuitMessage(0); return 0; }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private void Touch() => _idle?.Change(IdleLimit, Timeout.InfiniteTimeSpan);

    private void Close()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _holes.Clear();
        }
        _idle?.Dispose();
        Native.PostMessage(_window, 0x10, 0, 0);
        lock (s_gate)
            if (ReferenceEquals(s_active, this)) s_active = null;
    }

    /// <summary>Opens a hole at a screen point; disposing it closes the hole, not before <see cref="MinimumHole"/>.</summary>
    internal Hole OpenHole(Point point)
    {
        var hole = new Hole(this, point);
        lock (_sync)
        {
            if (_closed) return hole;
            _holes.Add(hole);
            UpdateRegion();
        }
        Touch();
        return hole;
    }

    internal sealed class Hole : IDisposable
    {
        private readonly InputShield _shield;
        private readonly long _opened = System.Diagnostics.Stopwatch.GetTimestamp();
        private int _closing;
        internal Point Point { get; private set; }
        internal Hole(InputShield shield, Point point) { _shield = shield; Point = point; }

        /// <summary>Moves the hole along with a dragged cursor.</summary>
        internal void MoveTo(Point point)
        {
            lock (_shield._sync)
            {
                Point = point;
                if (!_shield._closed && _shield._holes.Contains(this)) _shield.UpdateRegion();
            }
            _shield.Touch();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            var left = MinimumHole - System.Diagnostics.Stopwatch.GetElapsedTime(_opened);
            if (left <= TimeSpan.Zero) _shield.Remove(this);
            else _ = Task.Delay(left).ContinueWith(_ => _shield.Remove(this), TaskScheduler.Default);
        }
    }

    private void Remove(Hole hole)
    {
        lock (_sync)
            if (_holes.Remove(hole) && !_closed) UpdateRegion();
    }

    /// <summary>The desktop minus the holes. SetWindowRgn hit-tests by the new region once it returns: an input injected
    /// right after it reaches the window below the hole.</summary>
    private void UpdateRegion()
    {
        var region = Native.CreateRectRgn(0, 0, _bounds.Width, _bounds.Height);
        foreach (var hole in _holes)
        {
            int x = hole.Point.X - _bounds.Left, y = hole.Point.Y - _bounds.Top;
            var cut = Native.CreateRectRgn(x - HoleRadius, y - HoleRadius, x + HoleRadius + 1, y + HoleRadius + 1);
            Native.CombineRgn(region, region, cut, 4); // RGN_DIFF
            Native.DeleteObject(cut);
        }
        // The window owns the region from here on.
        if (Native.SetWindowRgn(_window, region, false) == 0) Native.DeleteObject(region);
    }

    /// <summary>
    /// The window an input at <paramref name="point"/> would reach if the shield were not up: what the point is over.
    /// Without a shield, or in a hole, this is WindowFromPoint; under the shield it is the top-level window below it
    /// there (visible, enabled, not cloaked, not click-through, its region containing the point).
    /// </summary>
    internal static nint WindowFromPoint(Point point)
    {
        var window = Native.WindowFromPoint(new Native.NativePoint(point.X, point.Y));
        var shield = Active;
        if (shield is null || window != shield._window) return window;
        for (var below = Native.GetWindow(shield._window, 2); below != 0; below = Native.GetWindow(below, 2)) // GW_HWNDNEXT
            if (Covers(below, point)) return below;
        return 0;
    }

    private static bool Covers(nint window, Point point)
    {
        if (!Native.IsWindowVisible(window) || !Native.IsWindowEnabled(window) || !Native.GetWindowRect(window, out var rect) ||
            point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
            return false;
        long exStyle = Native.GetWindowLongPtr(window, -20);
        if ((exStyle & 0x80020) == 0x80020) return false; // WS_EX_LAYERED | WS_EX_TRANSPARENT: input passes through
        if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            return Native.GetWindowRgn(window, region) == 0 || Native.PtInRegion(region, point.X - rect.Left, point.Y - rect.Top);
        }
        finally { Native.DeleteObject(region); }
    }

    private static class Native
    {
        internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct NativePoint(int X, int Y);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public NativePoint Point; public uint Private; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            public uint Size, Style;
            public WindowProcedure Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? MenuName;
            public string ClassName;
            public nint SmallIcon;
        }
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", EntryPoint = "GetMessageW")] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll", EntryPoint = "LoadCursorW")] internal static extern nint LoadCursor(nint instance, nint name);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint window, nint region, bool redraw);
        [DllImport("user32.dll")] internal static extern int GetWindowRgn(nint window, nint region);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] internal static extern nint GetModuleHandle(string? name);
        [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern int CombineRgn(nint destination, nint first, nint second, int mode);
        [DllImport("gdi32.dll")] internal static extern bool PtInRegion(nint region, int x, int y);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint handle);
    }
}
