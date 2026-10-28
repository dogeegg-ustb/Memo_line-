using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal enum EventSelectionMode { RegionRectangle, RegionLasso, EventSweep, BrushPanelRectangle }

/// <summary>A read-only native snapshot. Capturing never activates CSP or injects input.</summary>
internal sealed class FrozenCspDesktop : IDisposable
{
    internal Bitmap Image { get; }
    internal Rectangle Bounds { get; }
    internal Rectangle ClientBounds { get; }
    internal nint TargetWindow { get; }
    internal IReadOnlyList<Rectangle> CspWindowBounds { get; }
    internal FrozenCspDesktop(Bitmap image, Rectangle bounds, Rectangle clientBounds, nint targetWindow,IReadOnlyList<Rectangle>? cspWindowBounds=null)
    {
        if (image.Size != bounds.Size || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("冻结截图必须与虚拟屏幕物理尺寸相同。", nameof(image));
        Image = image; Bounds = bounds; ClientBounds = clientBounds; TargetWindow = targetWindow;
        CspWindowBounds=cspWindowBounds??[clientBounds];
    }
    internal static FrozenCspDesktop Capture()
    {
        var desktop = SystemInformation.VirtualScreen;
        var windows = new List<WindowEntry>();
        CspSnapshotNative.EnumWindows((window, _) =>
        {
            if (!CspSnapshotNative.IsWindowVisible(window) || CspSnapshotNative.IsIconic(window) ||
                !CspSnapshotNative.GetWindowRect(window, out var rect)) return true;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width < 2 || bounds.Height < 2 || !bounds.IntersectsWith(desktop)) return true;
            uint thread = CspSnapshotNative.GetWindowThreadProcessId(window, out uint processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!process.ProcessName.Equals("CLIPStudioPaint", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            catch (System.ComponentModel.Win32Exception) { return true; }
            var title = new StringBuilder(512); CspSnapshotNative.GetWindowText(window, title, title.Capacity);
            windows.Add(new(window, bounds, processId, thread, title.ToString().Contains("CLIP STUDIO PAINT", StringComparison.OrdinalIgnoreCase)));
            return true;
        }, 0);
        if (windows.Count == 0) throw new InvalidOperationException("未找到可见的 CLIP STUDIO PAINT 窗口。");
        var foreground = CspSnapshotNative.GetForegroundWindow();
        var target = windows.FirstOrDefault(w => w.Handle == foreground);
        if (target is null)
        {
            if (windows.Select(w => w.ProcessId).Distinct().Count() != 1)
                throw new InvalidOperationException("存在多个 CSP 实例，请先将当前录制的 CSP 窗口置于前台。");
            target = windows.OrderByDescending(w => w.IsPaintTitle).ThenByDescending(w => (long)w.Bounds.Width * w.Bounds.Height).First();
        }
        if (!CspSnapshotNative.TryClientBounds(target.Handle, out var client) || client.Width < 32 || client.Height < 32)
            throw new InvalidOperationException("无法确认 CSP 客户区，请展开当前绘画窗口。");
        // Only the monitors the CSP window is on. The virtual screen also spans the panel stash's monitor
        // (up to 1536×6144 beside the main screen): freezing it made a ~100 MB snapshot, layered surface and
        // back buffer each, and every pointer move redrew all of it.
        desktop = MonitorsUnder(client, desktop);
        windows.RemoveAll(w => !w.Bounds.IntersectsWith(desktop));
        var frozen = new Bitmap(desktop.Width, desktop.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(frozen);
            // Keep other applications usable only after the selection is dismissed.
            graphics.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size, CopyPixelOperation.SourceCopy);
            foreach (var window in windows.Where(w => w.ProcessId == target.ProcessId && w.ThreadId == target.ThreadId).Reverse())
            {
                using var image = new Bitmap(window.Bounds.Width, window.Bounds.Height);
                using var capture = Graphics.FromImage(image);
                nint dc = capture.GetHdc(); bool success;
                try { success = CspSnapshotNative.PrintWindow(window.Handle, dc, 2) || CspSnapshotNative.PrintWindow(window.Handle, dc, 0); }
                finally { capture.ReleaseHdc(dc); }
                if (!success) throw new InvalidOperationException("CSP 窗口冻结截图失败，请确保窗口可见。");
                graphics.DrawImageUnscaled(image, window.Bounds.X - desktop.X, window.Bounds.Y - desktop.Y);
            }
            return new(frozen, desktop, client, target.Handle,windows.Where(w=>w.ProcessId==target.ProcessId&&w.ThreadId==target.ThreadId).Select(w=>w.Bounds).ToArray());
        }
        catch { frozen.Dispose(); throw; }
    }
    internal static Rectangle MonitorsUnder(Rectangle window, Rectangle virtualScreen)
        => MonitorsUnder(window, Screen.AllScreens.Select(screen => screen.Bounds), virtualScreen);
    internal static Rectangle MonitorsUnder(Rectangle window, IEnumerable<Rectangle> monitors, Rectangle virtualScreen)
    {
        // A maximized window overhangs its monitor by its 8 px frame; such a sliver is not "on" the neighbour.
        var under = monitors.Where(monitor => Rectangle.Intersect(monitor, window) is { Width: >= 16, Height: >= 16 }).ToArray();
        return under.Length == 0 ? virtualScreen : under.Aggregate(Rectangle.Union);
    }
    internal bool IsTargetUnchanged() => TargetWindow == 0 || CspSnapshotNative.IsWindow(TargetWindow) &&
        CspSnapshotNative.IsWindowVisible(TargetWindow) && !CspSnapshotNative.IsIconic(TargetWindow) &&
        CspSnapshotNative.TryClientBounds(TargetWindow, out var client) && client == ClientBounds;
    public void Dispose() => Image.Dispose();
    private sealed record WindowEntry(nint Handle, Rectangle Bounds, uint ProcessId, uint ThreadId, bool IsPaintTitle);
}

/// <summary>Screen-sized frozen CSP surface; selection input targets Memoline, never CSP.</summary>
internal class EventSelectionOverlay : Form
{
    private readonly FrozenCspDesktop _frozen;
    private readonly System.Windows.Forms.Timer _targetWatch = new() { Interval = 500 };
    private readonly List<PointF> _drag = [];
    private PointF[] _regionOutline = [];
    private IReadOnlyList<CanvasStrokePath> _preciseUndo = [];
    private bool _showPreciseUndo;
    private PointF? _hover;
    private EventSelectionMode _mode;
    private bool _interactionEnabled = true, _pointerActive, _sweeping, _targetInvalidated;
    internal CanvasInteraction Model { get; }
    internal PixelMask? RegionSelection => Model.RegionSelection;
    internal IReadOnlySet<EditableStrokeKey> SelectedEvents => Model.SelectedEvents;
    internal DirtyImageKey? ChosenImage => Model.ChosenImage;
    internal new event EventHandler? RegionChanged;
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? TargetInvalidated;
    internal event EventHandler? EscapeRequested;
    internal event Action<Rectangle>? BrushPanelSelected;
    internal Bitmap CropScreenRegion(Rectangle rectangle)
    {
        if(!_frozen.Bounds.Contains(rectangle))throw new InvalidDataException("属性截图选区超出冻结画面。");
        return _frozen.Image.Clone(new Rectangle(rectangle.X-_frozen.Bounds.X,rectangle.Y-_frozen.Bounds.Y,rectangle.Width,rectangle.Height),_frozen.Image.PixelFormat);
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal EventSelectionMode Mode
    {
        get => _mode;
        set { CancelDrag(); _mode = value; Cursor = Cursors.Cross; Invalidate(); }
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool InteractionEnabled
    {
        get => _interactionEnabled;
        set
        {
            if (_interactionEnabled == value) return;
            CancelDrag(); _interactionEnabled = value;
            if (IsHandleCreated)
            {
                long style = CspSnapshotNative.GetWindowLongPtr(Handle, -20).ToInt64();
                CspSnapshotNative.SetWindowLongPtr(Handle, -20, (nint)(value ? style & ~0x20L : style | 0x20L));
            }
            Invalidate();
        }
    }
    internal EventSelectionOverlay(CanvasInteraction interaction, FrozenCspDesktop? frozen = null)
    {
        Model = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _frozen = frozen ?? FrozenCspDesktop.Capture();
        if (Model.Transform.Viewport is { } viewport && !viewport.IntersectsWith(_frozen.ClientBounds))
        { _frozen.Dispose(); throw new InvalidDataException("画布矩阵视图不属于已冻结的 CSP 客户区。"); }
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = _frozen.Bounds;
        TopMost = true; ShowInTaskbar = false; KeyPreview = true; DoubleBuffered = true; Cursor = Cursors.Cross;
        Model.RegionChanged += ModelRegionChanged; Model.SelectionChanged += ModelSelectionChanged;
        _targetWatch.Tick += (_, _) =>
        {
            if (_targetInvalidated || _frozen.IsTargetUnchanged()) return;
            _targetInvalidated = true; InteractionEnabled = false; Hide(); TargetInvalidated?.Invoke(this, EventArgs.Empty);
        };
    }
    private void ModelRegionChanged(object? sender, EventArgs e)
    { _preciseUndo = []; _showPreciseUndo=false; if (Model.RegionSelection is null) _regionOutline = []; Invalidate(); RegionChanged?.Invoke(this, e); }
    private void ModelSelectionChanged(object? sender, EventArgs e)
    { Invalidate(); SelectionChanged?.Invoke(this, e); }
    internal void SelectImage(DirtyImageKey image)
    { Model.SelectImage(image); ShowEventSelection(); Mode = EventSelectionMode.EventSweep; }
    internal void ShowPreciseUndo(IEnumerable<EditableStrokeKey> events)
    { _preciseUndo = Model.PathsForEvents(events); _showPreciseUndo=true; Invalidate(); }
    internal void ShowEventSelection(){_showPreciseUndo=false;_preciseUndo=[];Invalidate();}
    internal void ClearSelection() { CancelDrag(); Model.ClearSelection(); }
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80000 | 0x8 | 0x80; // WS_EX_LAYERED | TOPMOST | TOOLWINDOW
            if (!_interactionEnabled) parameters.ExStyle |= 0x20; // WS_EX_TRANSPARENT
            return parameters;
        }
    }
    /// <summary>
    /// While the editing boundary is still being built in the background, CSP must stay the foreground window:
    /// the boundary saves through CSP's own shortcut. The overlay then takes mouse and pen input without activation.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool KeepTargetForeground { get; set; }
    protected override bool ShowWithoutActivation => KeepTargetForeground;
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); CspSnapshotNative.SetLayeredWindowAttributes(Handle, 0, 255, 2); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); _targetWatch.Start(); }
    protected override void OnVisibleChanged(EventArgs e)
    { base.OnVisibleChanged(e); if (!Visible) { _targetWatch.Stop(); CancelDrag(); } else _targetWatch.Start(); }
    protected override void OnMouseDown(MouseEventArgs e)
    { base.OnMouseDown(e); if (!_pointerActive && e.Button == MouseButtons.Left) BeginPointer(PointToScreen(e.Location)); }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (!_pointerActive) MovePointer(PointToScreen(e.Location)); }
    protected override void OnMouseUp(MouseEventArgs e)
    { base.OnMouseUp(e); if (!_pointerActive && e.Button == MouseButtons.Left) EndPointer(PointToScreen(e.Location)); }
    private void BeginPointer(PointF screen)
    {
        if (!InteractionEnabled || _targetInvalidated || (Mode==EventSelectionMode.BrushPanelRectangle
            ? !_frozen.Bounds.Contains(Point.Round(screen)) : !Model.Transform.IsCanvasPoint(screen))) return;
        Focus(); Capture = true; _hover = screen;
        if (Mode == EventSelectionMode.EventSweep) { _sweeping = true; Model.BeginSweep(screen); }
        else { _drag.Clear(); _drag.Add(screen); }
        Invalidate();
    }
    private void MovePointer(PointF screen)
    {
        if (!InteractionEnabled) return;
        _hover = screen;
        if (_sweeping) Model.ContinueSweep(screen);
        else if (_drag.Count > 0)
        {
            if (Mode is EventSelectionMode.RegionRectangle or EventSelectionMode.BrushPanelRectangle)
            { if (_drag.Count == 1) _drag.Add(screen); else _drag[1] = screen; }
            else if (_drag[^1] != screen) _drag.Add(screen);
        }
        Invalidate();
    }
    private void EndPointer(PointF screen)
    {
        if (_sweeping) { Model.ContinueSweep(screen); Model.EndSweep(); _sweeping = false; Capture = false; Invalidate(); return; }
        if (_drag.Count == 0) return;
        MovePointer(screen); var points = DragOutline(); _drag.Clear(); Capture = false;
        if(Mode==EventSelectionMode.BrushPanelRectangle)
        {
            if(points.Length>=3)
            {
                var region=Rectangle.Intersect(_frozen.Bounds,Rectangle.FromLTRB((int)Math.Floor(points.Min(p=>p.X)),(int)Math.Floor(points.Min(p=>p.Y)),
                    (int)Math.Ceiling(points.Max(p=>p.X)),(int)Math.Ceiling(points.Max(p=>p.Y))));
                if(region.Width is >=8 and <=8192&&region.Height is >=8 and <=8192&&_frozen.CspWindowBounds.Any(bounds=>bounds.Contains(region)))
                {_regionOutline=points;BrushPanelSelected?.Invoke(region);}
            }
            Invalidate();return;
        }
        if (points.Length >= 3)
        {
            var mask = CanvasSelectionGeometry.Polygon(Model.Transform.CanvasSize, points.Select(Model.Transform.ScreenToCanvas).ToArray());
            if (!mask.IsEmpty) { _regionOutline = points; Model.SetRegion(points); }
        }
        Invalidate();
    }
    private PointF[] DragOutline() => (Mode is EventSelectionMode.RegionRectangle or EventSelectionMode.BrushPanelRectangle) && _drag.Count >= 2
        ? CanvasSelectionGeometry.Rectangle(_drag[0], _drag[1]) : _drag.ToArray();
    private void CancelDrag() { _drag.Clear(); _sweeping = false; _pointerActive = false; Model.EndSweep(); Capture = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { base.OnMouseCaptureChanged(e); if (!Capture) { _drag.Clear(); _sweeping = false; Model.EndSweep(); Invalidate(); } }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            bool dragging=_drag.Count>0||_sweeping;
            CancelDrag();if(!dragging)EscapeRequested?.Invoke(this,EventArgs.Empty);e.Handled=true;
        }
        base.OnKeyDown(e);
    }
    protected override void WndProc(ref Message message)
    {
        // WM_MOUSEACTIVATE → MA_NOACTIVATE, WM_POINTERACTIVATE → PA_NOACTIVATE.
        if (KeepTargetForeground && message.Msg is 0x21 or 0x24B) { message.Result = 3; return; }
        if (message.Msg is 0x245 or 0x246 or 0x247 && InteractionEnabled)
        {
            uint id = (uint)(message.WParam.ToInt64() & 0xffff);
            if (CspSnapshotNative.GetPointerInfo(id, out var pointer) && pointer.PointerType is 2 or 3)
            {
                var screen = new PointF(pointer.Pixel.X, pointer.Pixel.Y);
                if (message.Msg == 0x246) { _pointerActive = true; BeginPointer(screen); }
                else if (message.Msg == 0x245) MovePointer(screen);
                else { EndPointer(screen); _pointerActive = false; }
                message.Result = 0; return;
            }
        }
        base.WndProc(ref message);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.DrawImage(_frozen.Image, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        e.Graphics.CompositingMode = CompositingMode.SourceOver;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        PointF Local(PointF p) => new(p.X - _frozen.Bounds.Left, p.Y - _frozen.Bounds.Top);
        var outline = (_drag.Count > 0 ? DragOutline() : _regionOutline).Select(Local).ToArray();
        if (outline.Length >= 3)
        {
            using var fill = new SolidBrush(Color.FromArgb(28, 70, 150, 255));
            e.Graphics.FillPolygon(fill, outline, FillMode.Alternate);
            using var dark = new Pen(Color.Black, 3); using var white = new Pen(Color.White, 1) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawPolygon(dark, outline); e.Graphics.DrawPolygon(white, outline);
        }
        if(Mode==EventSelectionMode.BrushPanelRectangle)
        {
            var location=Local(new(_frozen.ClientBounds.Left+12,_frozen.ClientBounds.Top+12));
            using var background=new SolidBrush(Color.FromArgb(220,30,30,30));
            e.Graphics.FillRectangle(background,location.X,location.Y,430,38);
            e.Graphics.DrawString("框选完整笔刷属性面板（含标题和数值）。Esc 取消。",Font,Brushes.White,location.X+6,location.Y+7);return;
        }
        if (!_showPreciseUndo && Model.ChosenImage is { } key)
        {
            var mask = Model.Candidates.Single(c => c.Image.Key == key).Image.Mask;
            var border = CanvasSelectionGeometry.Rectangle(new(mask.Left, mask.Top), new(mask.Left + mask.Width, mask.Top + mask.Height))
                .Select(Model.Transform.CanvasToScreen).Select(Local).ToArray();
            using var pen = new Pen(Color.FromArgb(255, 175, 55), 2); e.Graphics.DrawPolygon(pen, border);
        }
        void DrawPaths(IEnumerable<CanvasStrokePath> paths, Pen pen)
        {
            foreach (var path in paths)
                foreach (var points in path.Paths)
                {
                    var local = points.Select(Local).ToArray();
                    if (local.Length == 1) e.Graphics.DrawEllipse(pen, local[0].X - 1, local[0].Y - 1, 2, 2);
                    else if (local.Length > 1) e.Graphics.DrawLines(pen, local);
                }
        }
        var clip = e.Graphics.Save();
        if (Model.Transform.Viewport is { } viewport) e.Graphics.SetClip(new Rectangle(viewport.X - _frozen.Bounds.X, viewport.Y - _frozen.Bounds.Y, viewport.Width, viewport.Height));
        using var selected = new Pen(Color.FromArgb(70, 220, 255), 3);
        if(!_showPreciseUndo)DrawPaths(Model.SelectedPaths, selected);
        using var eventWhite = new Pen(Color.White, 2);
        if (!_showPreciseUndo && _hover is { } pointer) DrawPaths(Model.HoverPaths(pointer).Where(p => !Model.SelectedEvents.Contains(p.Key)), eventWhite);
        DrawPaths(_preciseUndo, eventWhite);
        e.Graphics.Restore(clip);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _targetWatch.Dispose(); Model.RegionChanged -= ModelRegionChanged; Model.SelectionChanged -= ModelSelectionChanged;
            Model.EndSweep(); _frozen.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class CspSnapshotNative
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE: the window stays on the monitor but is absent from screen captures (BitBlt,
    /// duplication, window capture). The Recognizer reads CSP's panels from the screen, so Memoline's own
    /// windows must not cover them while it is asked for state. False when Windows refuses (before 10 2004).
    /// </summary>
    internal static bool ExcludeFromCapture(Control window, bool excluded)
        => SetWindowDisplayAffinity(window.Handle, excluded ? 0x11u : 0u);
    internal delegate bool EnumWindowsCallback(nint window, nint state);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo
    {
        internal uint PointerType, PointerId, FrameId, Flags;
        internal nint SourceDevice, Target;
        internal NativePoint Pixel, Himetric, PixelRaw, HimetricRaw;
        internal uint Time, HistoryCount;
        internal int InputData;
        internal uint KeyStates;
        internal ulong PerformanceCount;
        internal uint ButtonChangeType;
    }
    internal static bool TryClientBounds(nint window, out Rectangle bounds)
    {
        bounds = default; var origin = new NativePoint();
        if (!GetClientRect(window, out var rect) || !ClientToScreen(window, ref origin)) return false;
        bounds = new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top); return true;
    }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback callback, nint state);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder title, int max);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool GetPointerInfo(uint pointer, out PointerInfo info);
}
