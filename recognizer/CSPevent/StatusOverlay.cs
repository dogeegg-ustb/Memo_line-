using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CSPevent;

internal sealed class StatusOverlay : Form
{
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 2600 };
    private string _caption = "";

    internal StatusOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        Width = 340;
        Height = 68;
        _hideTimer.Tick += (_, _) => { Hide(); _hideTimer.Stop(); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= Native.WsExTransparent | Native.WsExNoActivate;
            return parameters;
        }
    }

    internal void Present(string caption, Point click)
    {
        _caption = caption;
        Screen screen = Screen.FromPoint(click);
        int left = Math.Min(click.X + 18, screen.WorkingArea.Right - Width - 8);
        int top = Math.Min(click.Y + 22, screen.WorkingArea.Bottom - Height - 8);
        Location = new Point(Math.Max(screen.WorkingArea.Left + 8, left),
            Math.Max(screen.WorkingArea.Top + 8, top));
        Invalidate();
        Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        var rect = new Rectangle(2, 2, Width - 4, Height - 4);
        int radius = 16;
        path.AddArc(rect.Left, rect.Top, radius, radius, 180, 90);
        path.AddArc(rect.Right - radius, rect.Top, radius, radius, 270, 90);
        path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure();
        using var background = new SolidBrush(Color.FromArgb(35, 38, 48));
        e.Graphics.FillPath(background, path);
        using var font = new Font("Microsoft YaHei UI", 11, FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, _caption, font,
            new Rectangle(14, 8, Width - 28, Height - 16), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
