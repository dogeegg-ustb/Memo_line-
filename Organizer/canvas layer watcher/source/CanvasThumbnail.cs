using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CanvasLayerWatcher;

internal static class CanvasThumbnail
{
    internal const int Edge = 1600;
    internal static Size SizeFor(Size canvas)
    {
        double scale = Math.Min(1, (double)Edge / Math.Max(canvas.Width, canvas.Height));
        return new(Math.Max(1, (int)Math.Ceiling(canvas.Width * scale)), Math.Max(1, (int)Math.Ceiling(canvas.Height * scale)));
    }
    internal static Bitmap Load(string path, Size canvas, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = Image.FromFile(path);
        var size = SizeFor(canvas);
        if (source.Size != canvas && source.Size != size) throw new InvalidDataException("画布总览的尺寸与完整画布不一致");
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(Point.Empty, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            token.ThrowIfCancellationRequested();
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    internal static void Save(string source, string output, Size canvas, CancellationToken token)
    {
        using var image = Load(source, canvas, token);
        image.Save(output, ImageFormat.Png);
    }
}
