using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScreenCanvasTransform.Ocr;

/// <summary>
/// Reads CSP navigator numbers relative to NavigatorRoi.
/// Upper band → ScalePercent; lower band → RotationDegrees.
/// </summary>
public sealed class NavigatorOcrService
{
    private static readonly Regex NumberRegex = new(
        @"[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.Compiled);

    public async Task<NavigatorNumericReadingDto> ReadAsync(
        CaptureSession session,
        IntRect navigatorRoiCapturePx,
        CancellationToken cancellationToken = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"));

        if (engine is null)
        {
            return new NavigatorNumericReadingDto
            {
                SourceCaptureId = session.CaptureId,
                CapturedAt = DateTime.UtcNow,
                ScaleConfidence = 0,
                RotationConfidence = 0,
                ScaleRawText = "",
                RotationRawText = ""
            };
        }

        var scaleBand = TopBand(navigatorRoiCapturePx, 0.18);
        var rotBand = BottomBand(navigatorRoiCapturePx, 0.18);

        var scaleTask = RecognizeBandAsync(session, scaleBand, engine, cancellationToken);
        var rotTask = RecognizeBandAsync(session, rotBand, engine, cancellationToken);
        await Task.WhenAll(scaleTask, rotTask).ConfigureAwait(false);

        var (scaleRaw, scaleConf) = scaleTask.Result;
        var (rotRaw, rotConf) = rotTask.Result;

        bool scaleOk = TryParseScale(scaleRaw, out float scale);
        bool rotOk = TryParseRotation(rotRaw, out float rot);

        return new NavigatorNumericReadingDto
        {
            ScalePercent = scaleOk ? scale : 0,
            RotationDegrees = rotOk ? rot : 0,
            ScaleConfidence = scaleOk ? Math.Max(0.25f, scaleConf) : 0,
            RotationConfidence = rotOk ? Math.Max(0.25f, rotConf) : 0,
            ScaleRawText = scaleRaw,
            RotationRawText = rotRaw,
            SourceCaptureId = session.CaptureId,
            CapturedAt = DateTime.UtcNow
        };
    }

    private static IntRect TopBand(IntRect roi, double fraction)
    {
        int h = Math.Max(1, (int)Math.Ceiling(roi.Height * fraction));
        return new IntRect(roi.Left, roi.Top, roi.Right, Math.Min(roi.Bottom, roi.Top + h));
    }

    private static IntRect BottomBand(IntRect roi, double fraction)
    {
        int h = Math.Max(1, (int)Math.Ceiling(roi.Height * fraction));
        return new IntRect(roi.Left, Math.Max(roi.Top, roi.Bottom - h), roi.Right, roi.Bottom);
    }

    private static async Task<(string text, float confidence)> RecognizeBandAsync(
        CaptureSession session,
        IntRect bandCapture,
        OcrEngine engine,
        CancellationToken cancellationToken)
    {
        if (bandCapture.Width < 4 || bandCapture.Height < 4)
            return ("", 0);

        using var crop = new Bitmap(bandCapture.Width, bandCapture.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.DrawImage(
                session.FrozenCapture,
                new Rectangle(0, 0, bandCapture.Width, bandCapture.Height),
                new Rectangle(bandCapture.Left, bandCapture.Top, bandCapture.Width, bandCapture.Height),
                GraphicsUnit.Pixel);
        }

        var data = crop.LockBits(
            new Rectangle(0, 0, crop.Width, crop.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            using var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                crop.Width,
                crop.Height,
                BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixels.AsBuffer());

            OcrResult ocr = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken)
                .ConfigureAwait(false);
            string text = ocr.Text?.Trim() ?? "";
            float conf = string.IsNullOrWhiteSpace(text) ? 0f : 0.7f;
            return (text, conf);
        }
        finally
        {
            crop.UnlockBits(data);
        }
    }

    public static bool TryParseScale(string raw, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string cleaned = raw.Replace("%", "", StringComparison.Ordinal).Trim();
        var m = NumberRegex.Match(cleaned);
        if (!m.Success)
            return false;

        string token = m.Value.Replace(',', '.');
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        // OCR may drop decimal: 1000 for 100.0 — keep plausible CSP range.
        if (value is < 1f or > 6400f)
            return false;
        return true;
    }

    public static bool TryParseRotation(string raw, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string cleaned = raw.Replace("°", "", StringComparison.Ordinal)
            .Replace("deg", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        var m = NumberRegex.Match(cleaned);
        if (!m.Success)
            return false;

        string token = m.Value.Replace(',', '.');
        // Recover missing minus if raw contains dash near number.
        if (cleaned.Contains('-', StringComparison.Ordinal) && !token.StartsWith('-'))
            token = "-" + token.TrimStart('+', '-');

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        if (value is < -720f or > 720f)
            return false;
        return true;
    }
}
