using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace CSPevent;

internal sealed class OcrReader : IDisposable
{
    private readonly TextRecognizer _recognizer = new();

    internal OcrReader()
    {
        string modelRoot = Path.Combine(AppContext.BaseDirectory, "models", "v6");
        _recognizer.InitModel(
            Path.Combine(modelRoot, "PP-OCRv6_rec_small.onnx"),
            Path.Combine(modelRoot, "ppocrv6_dict.txt"),
            numThread: 1);
    }

    internal string Availability(string language) => "可用（离线单行多语言模型）";

    internal OcrReadResult Read(Bitmap source)
    {
        if (source.Width <= 0 || source.Height <= 0) return new OcrReadResult([]);

        // 1. 边缘背景亮度采样：检测是否为暗黑主题（深色底浅色字）
        long totalLum = 0;
        int samples = 0;
        int step = Math.Max(1, source.Width / 16);
        for (int x = 0; x < source.Width; x += step)
        {
            Color cTop = source.GetPixel(x, 0);
            Color cBot = source.GetPixel(x, source.Height - 1);
            totalLum += (long)(0.299 * cTop.R + 0.587 * cTop.G + 0.114 * cTop.B);
            totalLum += (long)(0.299 * cBot.R + 0.587 * cBot.G + 0.114 * cBot.B);
            samples += 2;
        }
        bool isDarkBg = samples > 0 && (totalLum / samples) < 128;

        // 2. 目标高度适配 PP-OCR（模型最佳识别高度 48px）
        int targetH = 48;
        float scale = (float)targetH / Math.Max(1, source.Height);
        int targetW = Math.Max(16, (int)Math.Round(source.Width * scale));

        using var processed = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(processed))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (isDarkBg)
            {
                // 暗黑主题反色为白底黑字，最符合 PP-OCR 模型的训练分布
                using var ia = new ImageAttributes();
                var invertMatrix = new ColorMatrix(new float[][] {
                    new float[] {-1,  0,  0,  0, 0},
                    new float[] { 0, -1,  0,  0, 0},
                    new float[] { 0,  0, -1,  0, 0},
                    new float[] { 0,  0,  0,  1, 0},
                    new float[] { 1,  1,  1,  0, 1}
                });
                ia.SetColorMatrix(invertMatrix);
                graphics.DrawImage(source, new Rectangle(0, 0, targetW, targetH),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                graphics.DrawImage(source, new Rectangle(0, 0, targetW, targetH),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
        }

        using var stream = new MemoryStream();
        processed.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        using var bitmap = SKBitmap.Decode(stream);
        if (bitmap is null) return new OcrReadResult([]);
        TextLine line = _recognizer.GetTextLine(bitmap, CancellationToken.None);
        string[] characters = line.Chars ?? [];
        if (characters.Length == 0) return new OcrReadResult([]);
        int[] columns = line.CharCols ?? [];
        var hits = new List<OcrHit>(characters.Length);
        for (int i = 0; i < characters.Length; i++)
        {
            double left = columns.Length == characters.Length && line.ColCount > 0
                ? (double)columns[i] / line.ColCount * source.Width :
                (double)i / characters.Length * source.Width;
            double right = columns.Length == characters.Length && line.ColCount > 0 &&
                i + 1 < columns.Length
                ? (double)columns[i + 1] / line.ColCount * source.Width :
                (double)(i + 1) / characters.Length * source.Width;
            hits.Add(new OcrHit(characters[i], left, Math.Max(left + 1, right),
                source.Height / 2.0));
        }
        return new OcrReadResult(hits);
    }

    public void Dispose() => _recognizer.Dispose();
}

internal sealed record OcrHit(string Text, double Left, double Right, double CenterY);

internal sealed record OcrReadResult(IReadOnlyList<OcrHit> Hits)
{
    internal string Text => string.Concat(Hits.Select(hit => hit.Text));
}
