using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace CSPevent;

internal sealed record TextPresence(bool HasText, Rectangle? Bounds, float Score)
{
    internal static readonly TextPresence None = new(false, null, 0);
}

// Detection is independent of OCR: a recognizer may hallucinate characters on an icon.
internal sealed class TextPresenceDetector : IDisposable
{
    private readonly TextDetector _detector = new();

    internal TextPresenceDetector()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx");
        _detector.InitModel(path, 1);
    }

    internal TextPresence Detect(Bitmap source, int? clickX = null)
    {
        if (source.Width < 8 || source.Height < 8) return TextPresence.None;

        // Small UI labels need more pixels than their native 20-30 px height.
        int scale = Math.Clamp((int)Math.Ceiling(96.0 / source.Height), 1, 4);
        int width = source.Width * scale;
        int height = source.Height * scale;
        using var enlarged = new Bitmap(width + 32, height + 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(16, 16, width, height));
        }
        using var stream = new MemoryStream();
        enlarged.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        using SKBitmap? bitmap = SKBitmap.Decode(stream);
        if (bitmap is null) return TextPresence.None;

        ScaleParam sizing = ScaleParam.GetScaleParam(bitmap, Math.Min(1024, Math.Max(bitmap.Width, bitmap.Height)));
        IReadOnlyList<RapidOcrNet.TextBox>? boxes = _detector.GetTextBoxes(bitmap, sizing, 0.55f, 0.30f, 1.4f);
        if (boxes is null) return TextPresence.None;

        TextPresence best = TextPresence.None;
        foreach (RapidOcrNet.TextBox box in boxes)
        {
            int left = box.BoxPoints.Min(p => p.X);
            int top = box.BoxPoints.Min(p => p.Y);
            int right = box.BoxPoints.Max(p => p.X);
            int bottom = box.BoxPoints.Max(p => p.Y);
            var rect = Rectangle.FromLTRB(
                Math.Clamp((left - 16) / scale, 0, source.Width),
                Math.Clamp((top - 16) / scale, 0, source.Height),
                Math.Clamp((right - 16 + scale - 1) / scale, 0, source.Width),
                Math.Clamp((bottom - 16 + scale - 1) / scale, 0, source.Height));
            // A single character and a small icon have similar geometry. Do not force either class.
            if (rect.Height < 7 || rect.Width < 18 || rect.Width < rect.Height * 1.35 || box.Score < 0.55f)
                continue;
            if (clickX.HasValue && (clickX.Value < rect.Left - 8 || clickX.Value > rect.Right + 8))
                continue;
            if (rect.Width * rect.Height > (best.Bounds?.Width ?? 0) * (best.Bounds?.Height ?? 0))
                best = new TextPresence(true, rect, box.Score);
        }
        return best;
    }

    public void Dispose() => _detector.Dispose();
}
