using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace StrokeReplay;

/// <summary>A frozen visual copy of the active CSP UI thread with a movable stroke footprint.</summary>
internal sealed class CspPlacement : Form
{
    private readonly Rectangle _desktop;
    private readonly Rectangle _client;
    private readonly Bitmap _frozen;
    private Rectangle _selection;
    private Point? _dragStart;
    private Point _selectionStart;

    public IntPtr TargetWindow { get; }
    public Rectangle SelectedBounds => _selection;

    public CspPlacement(double minX, double minY, double maxX, double maxY)
    {
        _desktop = SystemInformation.VirtualScreen;
        var windows = FindCspWindows();
        if (windows.Count == 0)
            throw new InvalidOperationException("未找到可见的 CLIP STUDIO PAINT 窗口。");

        var foreground = GetForegroundWindow();
        var main = windows.FirstOrDefault(w => w.Handle == foreground);
        if (main.Handle == IntPtr.Zero)
            main = windows.OrderByDescending(w => w.IsPaintTitle).ThenByDescending(w => w.Bounds.Width * (long)w.Bounds.Height).First();
        TargetWindow = main.Handle;

        if (!GetClientRect(TargetWindow, out var clientRect))
            throw new InvalidOperationException("无法取得 CSP 客户区。");
        var clientOrigin = new NativePoint();
        if (!ClientToScreen(TargetWindow, ref clientOrigin))
            throw new InvalidOperationException("无法定位 CSP 客户区。");
        _client = Rectangle.Intersect(new Rectangle(clientOrigin.X, clientOrigin.Y,
            clientRect.Right, clientRect.Bottom), _desktop);
        if (_client.Width < 32 || _client.Height < 32)
            throw new InvalidOperationException("CSP 客户区太小，请展开绘画窗口。");

        // A visual snapshot blocks interaction with the live app while the position is chosen.
        _frozen = new Bitmap(_desktop.Width, _desktop.Height);
        using (var g = Graphics.FromImage(_frozen))
        {
            g.Clear(Color.FromArgb(25, 25, 25));
            uint threadId = GetWindowThreadProcessId(TargetWindow, out uint pid);
            foreach (var window in windows.Where(w => w.ProcessId == pid && w.ThreadId == threadId).Reverse())
            {
                using var image = CaptureWindow(window.Handle, window.Bounds.Size);
                g.DrawImageUnscaled(image, window.Bounds.X - _desktop.X, window.Bounds.Y - _desktop.Y);
            }
        }

        double rawWidth = Math.Max(maxX - minX, 1);
        double rawHeight = Math.Max(maxY - minY, 1);
        double scale = Math.Min(_client.Width * 0.6 / rawWidth, _client.Height * 0.6 / rawHeight);
        int width = Math.Clamp((int)Math.Round(rawWidth * scale), 1, _client.Width);
        int height = Math.Clamp((int)Math.Round(rawHeight * scale), 1, _client.Height);
        _selection = new Rectangle(_client.X + (_client.Width - width) / 2,
            _client.Y + (_client.Height - height) / 2, width, height);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _desktop;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_frozen, 0, 0);
        var rect = new Rectangle(_selection.X - _desktop.X, _selection.Y - _desktop.Y,
            _selection.Width, _selection.Height);
        using var fill = new SolidBrush(Color.FromArgb(45, Color.Lime));
        using var pen = new Pen(Color.Lime, 3);
        e.Graphics.FillRectangle(fill, rect);
        e.Graphics.DrawRectangle(pen, rect);
        using var banner = new SolidBrush(Color.FromArgb(225, 20, 20, 20));
        e.Graphics.FillRectangle(banner, 8, 8, 590, 40);
        e.Graphics.DrawString("拖动绿色框选择重放位置    Enter 确认    Esc 取消",
            Font, Brushes.White, 20, 20);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var screen = PointToScreen(e.Location);
        if (!_selection.Contains(screen)) return;
        _dragStart = screen;
        _selectionStart = _selection.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        var current = PointToScreen(e.Location);
        _selection.X = Math.Clamp(_selectionStart.X + current.X - start.X,
            _client.Left, _client.Right - _selection.Width);
        _selection.Y = Math.Clamp(_selectionStart.Y + current.Y - start.Y,
            _client.Top, _client.Bottom - _selection.Height);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragStart = null;
        Capture = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        else if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
        e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _frozen.Dispose();
        base.Dispose(disposing);
    }

    public static void PrepareDpiAwareness() => _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
    public static bool IsTargetWindowAvailable(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);
    public static void ActivateTarget(IntPtr hwnd)
    {
        _ = SetForegroundWindow(hwnd);
        uint expectedThread = GetWindowThreadProcessId(hwnd, out uint expectedProcess);
        uint actualThread = GetWindowThreadProcessId(GetForegroundWindow(), out uint actualProcess);
        if (expectedThread == 0 || actualThread == 0 || expectedProcess != actualProcess)
            throw new InvalidOperationException("无法将 CSP 置于前台，已中止笔输入。");
    }

    private static Bitmap CaptureWindow(IntPtr hwnd, Size size)
    {
        using var raw = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(raw);
        IntPtr dc = graphics.GetHdc();
        bool ok;
        try { ok = PrintWindow(hwnd, dc, 2) || PrintWindow(hwnd, dc, 0); }
        finally { graphics.ReleaseHdc(dc); }
        if (!ok) throw new InvalidOperationException("CSP 窗口冻结截图失败，请确保窗口可见。");
        return (Bitmap)raw.Clone();
    }

    private static List<WindowEntry> FindCspWindows()
    {
        var list = new List<WindowEntry>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect)) return true;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width < 2 || bounds.Height < 2 || !bounds.IntersectsWith(SystemInformation.VirtualScreen)) return true;
            uint tid = GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals("CLIPStudioPaint", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            var title = new StringBuilder(512);
            _ = GetWindowText(hwnd, title, title.Capacity);
            list.Add(new WindowEntry(hwnd, bounds, pid, tid,
                title.ToString().Contains("CLIP STUDIO PAINT", StringComparison.OrdinalIgnoreCase)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private readonly record struct WindowEntry(IntPtr Handle, Rectangle Bounds, uint ProcessId, uint ThreadId, bool IsPaintTitle);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
