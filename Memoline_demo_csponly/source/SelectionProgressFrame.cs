using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MemolineDemo;

/// <summary>
/// The fixed selection box while an edit runs, with an orange glow around it that fills with progress.
/// Per-pixel alpha, click-through, never activated and absent from screen captures: CSP keeps every
/// injected pointer event and the Recognizer never sees it in a panel screenshot.
/// </summary>
internal sealed class SelectionProgressFrame : Form
{
    private const int Margin = 18;
    private readonly PointF[] _polygon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private double _shown, _target;
    private float _phase;

    internal SelectionProgressFrame(IReadOnlyList<PointF> screenPolygon)
    {
        if (screenPolygon.Count < 3) throw new ArgumentException("选区至少需要三个屏幕点。", nameof(screenPolygon));
        var bounds = Rectangle.Round(Bounding(screenPolygon));
        bounds.Inflate(Margin, Margin);
        _polygon = screenPolygon.Select(p => new PointF(p.X - bounds.Left, p.Y - bounds.Top)).ToArray();
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        TopMost = true; Bounds = bounds; Enabled = false;
        _timer.Tick += (_, _) =>
        {
            // Ease toward the reported progress; a slow creep keeps long steps (saving, pasting) visibly alive.
            _target = Math.Min(Math.Max(_target, _shown + .0006), .97);
            _shown += (_target - _shown) * .12;
            _phase = (_phase + .015f) % 1;
            Render();
        };
    }

    /// <summary>0..1; never moves backwards.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal double Progress { get => _target; set => _target = Math.Clamp(Math.Max(_target, value), 0, 1); }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST; WS_DISABLED keeps it out of WindowFromPoint.
            parameters.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
            parameters.Style |= 0x08000000;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CspSnapshotNative.ExcludeFromCapture(this, true);
        Render(); _timer.Start();
    }

    internal void Complete() { _target = _shown = 1; Render(); }

    private void Render()
    {
        if (!IsHandleCreated || IsDisposed) return;
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
            using var outline = new GraphicsPath(); outline.AddPolygon(_polygon);
            // The selection itself: a dark rim under a white dashed line reads on any canvas.
            using (var rim = new Pen(Color.FromArgb(150, 20, 20, 20), 3)) g.DrawPath(rim, outline);
            using (var dash = new Pen(Color.White, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(dash, outline);
            var track = Perimeter(_polygon, 0, 1);
            using (var faint = new Pen(Color.FromArgb(70, 255, 140, 0), 6) { LineJoin = LineJoin.Round }) g.DrawLines(faint, track);
            if (_shown > .002)
            {
                var done = Perimeter(_polygon, 0, _shown);
                foreach (var (width, alpha) in new[] { (16f, 40), (10f, 80), (5f, 230) })
                    using (var glow = new Pen(Color.FromArgb(alpha, 255, 140, 0), width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        if (done.Length > 1) g.DrawLines(glow, done);
            }
            if (_shown < 1)
            {
                // A short bright comet circling the box shows that work continues between progress reports.
                var comet = Perimeter(_polygon, _phase, Math.Min(_phase + .06, 1));
                using var head = new Pen(Color.FromArgb(200, 255, 200, 120), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (comet.Length > 1) g.DrawLines(head, comet);
            }
        }
        Native.Publish(Handle, bitmap, Location);
    }

    /// <summary>The part of the closed outline between two fractions of its length, as a polyline.</summary>
    private static PointF[] Perimeter(PointF[] polygon, double from, double to)
    {
        var closed = polygon.Append(polygon[0]).ToArray();
        var lengths = new double[closed.Length - 1];
        for (int i = 0; i < lengths.Length; i++) lengths[i] = Distance(closed[i], closed[i + 1]);
        double total = lengths.Sum(), start = from * total, end = to * total, walked = 0;
        var points = new List<PointF>();
        for (int i = 0; i < lengths.Length && walked <= end; i++)
        {
            double a = walked, b = walked + lengths[i];
            if (b >= start && lengths[i] > 0)
            {
                PointF At(double d) { float t = (float)((d - a) / lengths[i]); return new(closed[i].X + (closed[i + 1].X - closed[i].X) * t, closed[i].Y + (closed[i + 1].Y - closed[i].Y) * t); }
                if (points.Count == 0) points.Add(At(Math.Max(start, a)));
                points.Add(At(Math.Min(end, b)));
            }
            walked = b;
        }
        return points.ToArray();
    }

    private static double Distance(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static RectangleF Bounding(IReadOnlyList<PointF> points)
    {
        float left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return RectangleF.FromLTRB(left, top, points.Max(p => p.X), points.Max(p => p.Y));
    }

    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] private struct Size32 { public int Cx, Cy; }
        [StructLayout(LayoutKind.Sequential)] private struct Point32 { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(nint window, nint screenDc, ref Point32 position, ref Size32 size,
            nint sourceDc, ref Point32 sourcePosition, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] private static extern nint GetDC(nint window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);

        internal static void Publish(nint window, Bitmap bitmap, Point location)
        {
            nint screen = GetDC(0), memory = CreateCompatibleDC(screen), handle = bitmap.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memory, handle);
            try
            {
                var size = new Size32 { Cx = bitmap.Width, Cy = bitmap.Height };
                var source = new Point32(); var position = new Point32 { X = location.X, Y = location.Y };
                var blend = new Blend { Op = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                UpdateLayeredWindow(window, screen, ref position, ref size, memory, ref source, 0, ref blend, 2); // ULW_ALPHA
            }
            finally { SelectObject(memory, old); DeleteObject(handle); DeleteDC(memory); ReleaseDC(0, screen); }
        }
    }
}
