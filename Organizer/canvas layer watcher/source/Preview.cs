using System.Drawing.Drawing2D;

namespace CanvasLayerWatcher;

internal sealed record PreviewOverlay(Rectangle Bounds, Color Color, bool Fill = false, int MinimumScreenSize = 0);

internal sealed class Preview : ScrollableControl
{
    // A full layer at 4961×7016 takes 139 MB. A fitted view draws a copy reduced to this edge;
    // actual size decodes the full image only while it is shown.
    internal const int ReducedEdge = 2048;
    private Bitmap? _image, _detail;
    private Func<Bitmap>? _loadDetail;
    private bool _fit = true;
    private Rectangle? _canvasBounds, _imageBounds;
    private PreviewOverlay[] _overlays = [];
    private string _message = "在 CSP 绘画后平移、缩放或旋转画布\n保存完成后，这里显示切换时的图层图像";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Bitmap? Image { get => _image; set { DropDetail(); _loadDetail = null; _image = value; AutoScrollPosition = Point.Empty; LayoutImage(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Fit { get => _fit; set { _fit = value; UpdateDetail(); LayoutImage(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle? CanvasBounds { get => _canvasBounds; set { _canvasBounds = value; LayoutImage(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle? ImageBounds { get => _imageBounds; set { _imageBounds = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PreviewOverlay[] Overlays { get => _overlays; set { _overlays = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Message { get => _message; set { _message = value; Invalidate(); } }
    public Preview() { DoubleBuffered = true; AutoScroll = true; BackColor = Color.FromArgb(35, 38, 43); Resize += (_, _) => LayoutImage(); }

    /// <summary>
    /// Shows <paramref name="image"/>, possibly made by <see cref="Reduce"/>; the caller keeps owning it.
    /// <paramref name="loadDetail"/> returns the full-resolution image for actual size, which this control owns.
    /// Positions come from CanvasBounds/ImageBounds, which must then be set.
    /// </summary>
    public void Show(Bitmap image, Func<Bitmap>? loadDetail) { Image = image; _loadDetail = loadDetail; UpdateDetail(); Invalidate(); }

    /// <summary>A copy no longer than <see cref="ReducedEdge"/>; smaller images are copied unchanged.</summary>
    internal static Bitmap Reduce(Image source)
    {
        double scale = (double)ReducedEdge / Math.Max(source.Width, source.Height);
        if (scale >= 1) return new Bitmap(source);
        var reduced = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(reduced);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var edges = new System.Drawing.Imaging.ImageAttributes(); edges.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(source, new Rectangle(Point.Empty, reduced.Size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edges);
            return reduced;
        }
        catch { reduced.Dispose(); throw; }
    }

    private void UpdateDetail()
    {
        if (_fit || _loadDetail is null) { DropDetail(); return; }
        if (_detail is not null) return;
        // A missing or unreadable source leaves the reduced image at actual size.
        try { _detail = _loadDetail(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
    }
    private void DropDetail() { _detail?.Dispose(); _detail = null; }
    protected override void Dispose(bool disposing) { if (disposing) DropDetail(); base.Dispose(disposing); }
    private void LayoutImage()
    {
        var size = _canvasBounds?.Size ?? _image?.Size ?? Size.Empty;
        AutoScrollMinSize = !_fit && _image is not null ? new(size.Width + 24, size.Height + 24) : Size.Empty; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_image is null) { TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, Color.LightGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        var canvas = _canvasBounds ?? new Rectangle(Point.Empty, _image.Size);
        double scale = _fit ? Math.Max(.001, Math.Min((ClientSize.Width - 24.0) / canvas.Width, (ClientSize.Height - 24.0) / canvas.Height)) : 1;
        int w = Math.Max(1, (int)(canvas.Width * scale)), h = Math.Max(1, (int)(canvas.Height * scale));
        var destination = new Rectangle(_fit ? (Width - w) / 2 : 12 + AutoScrollPosition.X, _fit ? (Height - h) / 2 : 12 + AutoScrollPosition.Y, w, h);
        var visible = Rectangle.Intersect(destination, e.ClipRectangle); if (visible.IsEmpty) return;
        var state = e.Graphics.Save(); e.Graphics.SetClip(visible);
        using var light = new SolidBrush(Color.FromArgb(222, 225, 230)); using var dark = new SolidBrush(Color.FromArgb(179, 185, 194));
        e.Graphics.FillRectangle(light, visible);
        for (int y = Math.Max(0, (visible.Top - destination.Top) / 16); y <= (visible.Bottom - destination.Top) / 16; y++)
            for (int x = Math.Max(0, (visible.Left - destination.Left) / 16); x <= (visible.Right - destination.Left) / 16; x++)
                if ((x + y) % 2 == 1) e.Graphics.FillRectangle(dark, destination.Left + x * 16, destination.Top + y * 16, 16, 16);
        RectangleF Project(Rectangle box) => new(destination.Left + (float)((box.Left - canvas.Left) * scale),
            destination.Top + (float)((box.Top - canvas.Top) * scale), (float)(box.Width * scale), (float)(box.Height * scale));
        var imageBounds = _imageBounds ?? canvas;
        var drawn = _detail ?? _image;
        e.Graphics.InterpolationMode = scale >= 1 && drawn.Size == imageBounds.Size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(drawn, Project(imageBounds));
        foreach (var overlay in _overlays)
        {
            var box = Project(overlay.Bounds);
            if (!box.IntersectsWith(visible)) continue;
            if (overlay.MinimumScreenSize > 0)
            {
                float extraX = Math.Max(0, overlay.MinimumScreenSize - box.Width), extraY = Math.Max(0, overlay.MinimumScreenSize - box.Height);
                box.Inflate(extraX / 2, extraY / 2);
            }
            if (overlay.Fill) { using var brush = new SolidBrush(Color.FromArgb(28, overlay.Color)); e.Graphics.FillRectangle(brush, box); }
            using var pen = new Pen(overlay.Color, overlay.Fill ? 1 : 2);
            e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }
        e.Graphics.Restore(state);
    }
}
