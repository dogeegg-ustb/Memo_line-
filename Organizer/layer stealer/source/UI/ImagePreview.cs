using System.Drawing.Drawing2D;

namespace LayerStealer.UI;

internal sealed class ImagePreview : ScrollableControl
{
    private Bitmap? _image;
    private bool _fit = true;
    public ImagePreview()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(31, 34, 39);
        DoubleBuffered = true;
        AutoScroll = true;
        Resize += (_, _) => UpdateLayout();
    }
    public void SetImage(Bitmap? image) { _image = image; AutoScrollPosition = Point.Empty; UpdateLayout(); }
    public void SetFit(bool fit) { _fit = fit; AutoScrollPosition = Point.Empty; UpdateLayout(); }
    private void UpdateLayout()
    {
        AutoScrollMinSize = !_fit && _image is not null ? new(_image.Width + 32, _image.Height + 32) : Size.Empty;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_image is null)
        {
            TextRenderer.DrawText(e.Graphics, "在 CSP 选中图层，然后点击「复制并呈现」\nCtrl + Alt + F8 可在 CSP 中直接触发",
                Font, ClientRectangle, Color.FromArgb(184, 190, 201), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        double scale = _fit ? Math.Min((ClientSize.Width - 32.0) / _image.Width, (ClientSize.Height - 32.0) / _image.Height) : 1;
        scale = Math.Max(.001, scale);
        int width = Math.Max(1, (int)Math.Round(_image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(_image.Height * scale));
        var destination = new Rectangle(_fit ? (ClientSize.Width - width) / 2 : 16 + AutoScrollPosition.X,
            _fit ? (ClientSize.Height - height) / 2 : 16 + AutoScrollPosition.Y, width, height);
        var visible = Rectangle.Intersect(destination, e.ClipRectangle);
        if (visible.IsEmpty) return;
        var state = e.Graphics.Save();
        e.Graphics.SetClip(visible);
        using var light = new SolidBrush(Color.FromArgb(220, 223, 228));
        using var dark = new SolidBrush(Color.FromArgb(180, 185, 193));
        e.Graphics.FillRectangle(light, visible);
        const int tile = 16;
        int firstX = Math.Max(0, (visible.Left - destination.Left) / tile);
        int firstY = Math.Max(0, (visible.Top - destination.Top) / tile);
        int lastX = (visible.Right - destination.Left) / tile;
        int lastY = (visible.Bottom - destination.Top) / tile;
        for (int y = firstY; y <= lastY; y++)
            for (int x = firstX; x <= lastX; x++)
                if ((x + y) % 2 == 1) e.Graphics.FillRectangle(dark, destination.Left + x * tile, destination.Top + y * tile, tile, tile);
        e.Graphics.InterpolationMode = scale == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_image, destination, new Rectangle(0, 0, _image.Width, _image.Height), GraphicsUnit.Pixel);
        e.Graphics.Restore(state);
    }
}
