using DirtyMatrix.Core;

namespace DirtyMatrix.UI;

public sealed class GreenOverlay : Form
{
    private CoverageSettings _settings = new();
    private IReadOnlyList<StrokeCoverage> _coverage = [];
    private IReadOnlyList<PixelBox> _boxes = [];
    public GreenOverlay()
    {
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        Bounds = SystemInformation.VirtualScreen;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000; return p; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) { message.Result = -1; return; } // HTTRANSPARENT
        if (message.Msg == 0x21) { message.Result = 3; return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }
    public void UpdateCoverage(CoverageSettings settings, IReadOnlyList<StrokeCoverage> coverage, IReadOnlyList<PixelBox> boxes)
    {
        _settings = settings; _coverage = coverage; _boxes = boxes;
        Bounds = SystemInformation.VirtualScreen;
        Opacity = settings.OpacityPercent / 100.0;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var visible = _settings.VisibleCanvas.Intersect(new(Left, Top, Right, Bottom));
        if (visible.IsEmpty) return;
        // Rasterize in screen pixels so small zoom factors retain complete lines and round ends.
        // The same swept-line algorithm drives both display modes and the exported matrix.
        var pixels = new DirtyTileMatrix(Width, Height, 1);
        if (_settings.ShowTiles)
        {
            foreach (var box in _boxes)
            {
                var r = _settings.Project(box).Intersect(visible);
                if (r.IsEmpty) continue;
                pixels.Add(new PixelBox((int)Math.Floor(r.Left - Left), (int)Math.Floor(r.Top - Top),
                    (int)Math.Ceiling(r.Right - Left), (int)Math.Ceiling(r.Bottom - Top)));
            }
        }
        else
            foreach (var stroke in _coverage)
                foreach (var segment in stroke.Segments)
                    pixels.Add(new StrokeSegment(Project(segment.Start), Project(segment.End)), stroke.Radius * _settings.Zoom);

        e.Graphics.SetClip(Rectangle.FromLTRB((int)Math.Floor(visible.Left - Left), (int)Math.Floor(visible.Top - Top),
            (int)Math.Ceiling(visible.Right - Left), (int)Math.Ceiling(visible.Bottom - Top)), System.Drawing.Drawing2D.CombineMode.Intersect);
        using var green = new SolidBrush(Color.FromArgb(0, 230, 90));
        foreach (var box in pixels.Boxes) e.Graphics.FillRectangle(green, box.Left, box.Top, box.Width, box.Height);
        // Row runs never overlap, so each pixel is filled once at the requested alpha.
        PointD Project(PointD p) => new(_settings.OriginX + p.X * _settings.Zoom - Left,
            _settings.OriginY + p.Y * _settings.Zoom - Top);
    }
}
