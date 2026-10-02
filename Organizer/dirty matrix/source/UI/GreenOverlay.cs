using DirtyMatrix.Core;

namespace DirtyMatrix.UI;

public sealed class GreenOverlay : Form
{
    private CoverageSettings _settings = new();
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
    public void UpdateCoverage(CoverageSettings settings, IReadOnlyList<PixelBox> boxes)
    {
        _settings = settings; _boxes = boxes;
        Bounds = SystemInformation.VirtualScreen;
        Opacity = settings.OpacityPercent / 100.0;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var visible = _settings.VisibleCanvas.Intersect(new(Left, Top, Right, Bottom));
        if (visible.IsEmpty) return;
        using var union = new Region(); union.MakeEmpty();
        foreach (var box in _boxes)
        {
            var r = _settings.Project(box).Intersect(visible);
            if (r.IsEmpty) continue;
            // Outward rounding avoids a subpixel gap at small zoom factors.
            union.Union(Rectangle.FromLTRB((int)Math.Floor(r.Left - Left), (int)Math.Floor(r.Top - Top),
                (int)Math.Ceiling(r.Right - Left), (int)Math.Ceiling(r.Bottom - Top)));
        }
        using var green = new SolidBrush(Color.FromArgb(0, 230, 90));
        e.Graphics.FillRegion(green, union); // Draw union once: overlaps retain the same alpha.
    }
}
