using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScreenCanvasTransform.Ocr;

/// <summary>
/// Reads CSP navigator scale/rotation from two partitioned numeric slots
/// below the navigator thumbnail.
/// </summary>
public sealed class NavigatorOcrService
{
    public static bool DebugEnabled { get; set; } = true;

    private const int MinSlotSizePx = 8;
    private const int Upscale = 6;
    private const double LeftColumnFraction = 0.42;
    private const double HorizontalInsetFraction = 0.10;
    private const double VerticalInsetFraction = 0.14;

    private static readonly Regex NumberRegex = new(
        @"[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.Compiled);

    public static OcrLayoutScreen ComputeOcrLayout(IntRect navigatorPanelScreen, IntRect thumbnailScreen)
    {
        IntRect chrome = ChromeBand(navigatorPanelScreen, thumbnailScreen);
        if (chrome.IsEmpty)
            return new OcrLayoutScreen(chrome, chrome);

        int colWidth = Math.Max(MinSlotSizePx, (int)Math.Round(chrome.Width * LeftColumnFraction));
        IntRect leftCol = new(chrome.Left, chrome.Top, chrome.Left + colWidth, chrome.Bottom);

        int midY = leftCol.Top + leftCol.Height / 2;
        IntRect scaleBand = new(leftCol.Left, leftCol.Top, leftCol.Right, midY);
        IntRect rotationBand = new(leftCol.Left, midY, leftCol.Right, leftCol.Bottom);

        return new OcrLayoutScreen(Inset(scaleBand), Inset(rotationBand));
    }

    public async Task<NavigatorNumericReadingDto> ReadWithLayoutAsync(
        CaptureSession session,
        OcrLayoutScreen layoutScreen,
        CancellationToken cancellationToken = default)
    {
        IntRect scaleSlot = session.ScreenToCapture(layoutScreen.ScaleSlotScreen).ClampTo(session.CaptureBounds);
        IntRect rotationSlot = session.ScreenToCapture(layoutScreen.RotationSlotScreen).ClampTo(session.CaptureBounds);
        return await ReadSlotsAsync(session, scaleSlot, rotationSlot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NavigatorNumericReadingDto> ReadAsync(
        CaptureSession session,
        IntRect navigatorRoiCapturePx,
        IntRect navigatorThumbnailRoiCapturePx,
        CancellationToken cancellationToken = default)
    {
        var layout = ComputeOcrLayout(
            session.CaptureToScreen(navigatorRoiCapturePx),
            session.CaptureToScreen(navigatorThumbnailRoiCapturePx));
        return await ReadWithLayoutAsync(session, layout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NavigatorNumericReadingDto> ReadSlotsAsync(
        CaptureSession session,
        IntRect scaleSlotCapture,
        IntRect rotationSlotCapture,
        CancellationToken cancellationToken)
    {
        string debugDir = Path.Combine(Path.GetTempPath(), "sct_ocr_debug", session.CaptureId);
        if (DebugEnabled)
            Directory.CreateDirectory(debugDir);

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"));

        if (engine is null)
        {
            Log(debugDir, "engine=null");
            return Empty(session.CaptureId);
        }

        Log(debugDir, $"slots scale={scaleSlotCapture} rot={rotationSlotCapture}");

        var scaleHits = await RecognizeSlotAsync(
                session, scaleSlotCapture, engine, debugDir, "scale", invert: true, cancellationToken)
            .ConfigureAwait(false);
        if (scaleHits.Count == 0)
        {
            scaleHits = await RecognizeSlotAsync(
                    session, scaleSlotCapture, engine, debugDir, "scale_raw", invert: false, cancellationToken)
                .ConfigureAwait(false);
        }

        var rotHits = await RecognizeSlotAsync(
                session, rotationSlotCapture, engine, debugDir, "rotation", invert: true, cancellationToken)
            .ConfigureAwait(false);
        if (rotHits.Count == 0)
        {
            rotHits = await RecognizeSlotAsync(
                    session, rotationSlotCapture, engine, debugDir, "rotation_raw", invert: false, cancellationToken)
                .ConfigureAwait(false);
        }

        bool scaleOk = TryPickSlotValue(scaleHits, scaleSlotCapture, TryParseScale, out float scaleVal, out string scaleRaw);
        bool rotOk = TryPickSlotValue(rotHits, rotationSlotCapture, TryParseRotation, out float rotVal, out string rotRaw);
        if (!rotOk)
        {
            rotOk = true;
            rotVal = 0;
            rotRaw = "0";
        }

        Log(debugDir,
            $"result scaleOk={scaleOk} raw='{scaleRaw}' val={scaleVal}; " +
            $"rotOk={rotOk} raw='{rotRaw}' val={rotVal}");

        return new NavigatorNumericReadingDto
        {
            ScalePercent = scaleOk ? scaleVal : 0,
            RotationDegrees = rotOk ? rotVal : 0,
            ScaleConfidence = scaleOk ? 0.85f : 0,
            RotationConfidence = rotOk ? 0.85f : 0,
            ScaleRawText = scaleRaw,
            RotationRawText = rotRaw,
            SourceCaptureId = session.CaptureId,
            CapturedAt = DateTime.UtcNow
        };
    }

    private static IntRect ChromeBand(IntRect nav, IntRect thumb)
    {
        if (nav.IsEmpty)
            return nav;

        int minH = Math.Max(48, nav.Height / 5);
        int topFromThumb = Math.Max(nav.Top, thumb.Bottom);
        int topMinBand = Math.Max(nav.Top, nav.Bottom - minH);
        int top = Math.Min(topFromThumb, topMinBand);
        if (nav.Bottom - top < MinSlotSizePx)
            return new IntRect(nav.Left, nav.Top, nav.Left, nav.Top);
        return new IntRect(nav.Left, top, nav.Right, nav.Bottom);
    }

    private static IntRect Inset(IntRect rect)
    {
        if (rect.IsEmpty)
            return rect;

        int dx = Math.Max(1, (int)Math.Round(rect.Width * HorizontalInsetFraction));
        int dy = Math.Max(1, (int)Math.Round(rect.Height * VerticalInsetFraction));
        int left = rect.Left + dx;
        int top = rect.Top + dy;
        int right = rect.Right - dx;
        int bottom = rect.Bottom - dy;
        if (right - left < MinSlotSizePx || bottom - top < MinSlotSizePx)
            return rect;
        return new IntRect(left, top, right, bottom);
    }

    private readonly record struct Hit(string Text, double CenterY, double CenterX);

    private static async Task<List<Hit>> RecognizeSlotAsync(
        CaptureSession session,
        IntRect slotCapture,
        OcrEngine engine,
        string debugDir,
        string tag,
        bool invert,
        CancellationToken cancellationToken)
    {
        if (slotCapture.Width < MinSlotSizePx || slotCapture.Height < MinSlotSizePx)
        {
            Log(debugDir, $"{tag}: slot too small {slotCapture}");
            return new List<Hit>();
        }

        int tw = Math.Max(8, slotCapture.Width * Upscale);
        int th = Math.Max(8, slotCapture.Height * Upscale);

        using var crop = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.White);
            g.DrawImage(
                session.FrozenCapture,
                new Rectangle(0, 0, tw, th),
                new Rectangle(slotCapture.Left, slotCapture.Top, slotCapture.Width, slotCapture.Height),
                GraphicsUnit.Pixel);
        }

        if (invert)
            InvertInPlace(crop);

        if (DebugEnabled)
        {
            try { crop.Save(Path.Combine(debugDir, $"{tag}.png"), ImageFormat.Png); }
            catch { /* ignore */ }
        }

        int packedStride = tw * 4;
        byte[] pixels = new byte[packedStride * th];
        var data = crop.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < th; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * packedStride, packedStride);
        }
        finally
        {
            crop.UnlockBits(data);
        }

        using var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8, tw, th, BitmapAlphaMode.Ignore);
        softwareBitmap.CopyFromBuffer(pixels.AsBuffer());

        OcrResult ocr = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);

        var hits = new List<Hit>();
        foreach (OcrLine line in ocr.Lines)
        {
            foreach (OcrWord word in line.Words)
            {
                string w = word.Text?.Trim() ?? "";
                foreach (Match m in NumberRegex.Matches(w))
                {
                    var rect = word.BoundingRect;
                    double cx = slotCapture.Left + (rect.X + rect.Width * 0.5) / Upscale;
                    double cy = slotCapture.Top + (rect.Y + rect.Height * 0.5) / Upscale;
                    hits.Add(new Hit(m.Value, cy, cx));
                }
            }
        }

        hits = hits
            .GroupBy(h => h.Text + "@" + ((int)(h.CenterY / 8)))
            .Select(g => g.First())
            .ToList();

        Log(debugDir, $"{tag}: invert={invert} full='{ocr.Text}' digitHits={hits.Count}");
        return hits;
    }

    private static void InvertInPlace(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int height = bitmap.Height;
            byte[] row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int i = x * 4;
                    row[i] = (byte)(255 - row[i]);
                    row[i + 1] = (byte)(255 - row[i + 1]);
                    row[i + 2] = (byte)(255 - row[i + 2]);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool TryPickSlotValue(
        IReadOnlyList<Hit> hits,
        IntRect slot,
        TryParseSlot parse,
        out float value,
        out string raw)
    {
        value = 0;
        raw = "";
        if (hits.Count == 0)
            return false;

        double cx = (slot.Left + slot.Right) * 0.5;
        double cy = (slot.Top + slot.Bottom) * 0.5;
        var ordered = hits
            .OrderBy(h => Math.Abs(h.CenterX - cx) + Math.Abs(h.CenterY - cy))
            .ThenBy(h => h.CenterX)
            .ToList();

        foreach (var hit in ordered)
        {
            if (parse(hit.Text, out value))
            {
                raw = hit.Text;
                return true;
            }
        }

        raw = ordered[0].Text;
        return false;
    }

    private delegate bool TryParseSlot(string raw, out float value);

    private static void Log(string dir, string msg)
    {
        if (!DebugEnabled || string.IsNullOrEmpty(dir)) return;
        try
        {
            File.AppendAllText(Path.Combine(dir, "ocr.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    private static NavigatorNumericReadingDto Empty(string captureId) => new()
    {
        SourceCaptureId = captureId,
        CapturedAt = DateTime.UtcNow,
        ScaleConfidence = 0,
        RotationConfidence = 0,
        ScaleRawText = "",
        RotationRawText = ""
    };

    private static bool TryParseNumber(string raw, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string token = raw.Replace(',', '.').Trim();
        return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseScale(string raw, out float value)
    {
        value = 0;
        if (!TryParseNumber(raw.Replace("%", "", StringComparison.Ordinal), out value))
            return false;
        return value is >= 1f and <= 6400f;
    }

    public static bool TryParseRotation(string raw, out float value)
    {
        value = 0;
        string cleaned = raw.Replace("°", "", StringComparison.Ordinal)
            .Replace("deg", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (!TryParseNumber(cleaned, out value))
            return false;
        return value is >= -720f and <= 720f;
    }
}
