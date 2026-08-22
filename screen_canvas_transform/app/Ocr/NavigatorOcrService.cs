using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScreenCanvasTransform.Ocr;

/// <summary>
/// Reads CSP navigator numbers in NavigatorRoi, below NavigatorThumbnailRoi.
/// Among digit hits: upper = ScalePercent, lower = RotationDegrees.
/// </summary>
public sealed class NavigatorOcrService
{
    public static bool DebugEnabled { get; set; } = true;

    private static readonly Regex NumberRegex = new(
        @"[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.Compiled);

    private static readonly Regex HasDigit = new(@"\d", RegexOptions.Compiled);

    public async Task<NavigatorNumericReadingDto> ReadAsync(
        CaptureSession session,
        IntRect navigatorRoiCapturePx,
        IntRect navigatorThumbnailRoiCapturePx,
        CancellationToken cancellationToken = default)
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

        // Numbers live under the thumbnail, still inside the navigator panel.
        IntRect search = BelowThumbnailInsideNavigator(
            navigatorRoiCapturePx, navigatorThumbnailRoiCapturePx);
        Log(debugDir,
            $"engine={engine.RecognizerLanguage?.LanguageTag}; nav={navigatorRoiCapturePx}; " +
            $"thumb={navigatorThumbnailRoiCapturePx}; search={search}");

        if (search.Width < 8 || search.Height < 8)
        {
            Log(debugDir, "search band empty/too small");
            return Empty(session.CaptureId);
        }

        var region = await RecognizeRegionAsync(
                session, search, engine, debugDir, "below_thumb", cancellationToken)
            .ConfigureAwait(false);

        // CSP puts scale/rotation on the left of the bottom control row — also OCR left half.
        IntRect leftHalf = new(
            search.Left,
            search.Top,
            search.Left + Math.Max(8, search.Width / 2),
            search.Bottom);
        var leftRegion = await RecognizeRegionAsync(
                session, leftHalf, engine, debugDir, "below_thumb_left", cancellationToken)
            .ConfigureAwait(false);

        var mergedHits = region.Hits.Concat(leftRegion.Hits).ToList();

        // Sort digit hits by Y (top → bottom). Upper = scale, lower = rotation.
        var hits = mergedHits
            .Where(h => TryParseNumber(h.Text, out _))
            .GroupBy(h => $"{h.Text}@{(int)(h.CenterY / 6)}")
            .Select(g => g.OrderBy(h => h.CenterX).First())
            .OrderBy(h => h.CenterY)
            .ThenBy(h => h.CenterX)
            .ToList();

        Log(debugDir, $"hits={hits.Count}: " +
                       string.Join(" | ", hits.Select(h => $"'{h.Text}'@y={h.CenterY:F0}")));

        bool scaleOk = false;
        bool rotOk = false;
        float scaleVal = 0, rotVal = 0;
        string scaleRaw = "", rotRaw = "";

        if (hits.Count >= 2)
        {
            scaleOk = TryParseScale(hits[0].Text, out scaleVal);
            scaleRaw = hits[0].Text;
            rotOk = TryParseRotation(hits[^1].Text, out rotVal);
            rotRaw = hits[^1].Text;
            // If first parse failed as scale but looks numeric, still try raw float with looser gate.
            if (!scaleOk && TryParseNumber(hits[0].Text, out float s0) && s0 > 0)
            {
                scaleVal = s0;
                scaleOk = s0 >= 1f;
                scaleRaw = hits[0].Text;
            }
        }
        else if (hits.Count == 1)
        {
            // Single hit: prefer as scale if plausible; rotation defaults to 0.
            if (TryParseScale(hits[0].Text, out scaleVal))
            {
                scaleOk = true;
                scaleRaw = hits[0].Text;
                rotOk = true;
                rotVal = 0;
                rotRaw = "0";
            }
            else if (TryParseRotation(hits[0].Text, out rotVal))
            {
                rotOk = true;
                rotRaw = hits[0].Text;
            }
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

    /// <summary>
    /// Prefer strip below thumbnail inside navigator. If that strip is too short
    /// (oversized thumbnail), expand upward to a minimum height so the control
    /// row with scale/rotation remains inside the OCR band.
    /// </summary>
    private static IntRect BelowThumbnailInsideNavigator(IntRect nav, IntRect thumb)
    {
        if (nav.IsEmpty)
            return nav;

        int minH = Math.Max(72, nav.Height / 4);
        int topFromThumb = Math.Max(nav.Top, thumb.Bottom - 4);
        int topMinBand = Math.Max(nav.Top, nav.Bottom - minH);
        int top = Math.Min(topFromThumb, topMinBand);
        if (nav.Bottom - top < 8)
            return new IntRect(nav.Left, nav.Top, nav.Left, nav.Top);
        return new IntRect(nav.Left, top, nav.Right, nav.Bottom);
    }

    private readonly record struct Hit(string Text, double CenterY, double CenterX);

    private sealed class RegionResult
    {
        public List<Hit> Hits { get; init; } = new();
    }

    private static async Task<RegionResult> RecognizeRegionAsync(
        CaptureSession session,
        IntRect bandCapture,
        OcrEngine engine,
        string debugDir,
        string tag,
        CancellationToken cancellationToken)
    {
        const int scale = 4;
        int tw = Math.Max(8, bandCapture.Width * scale);
        int th = Math.Max(8, bandCapture.Height * scale);

        using var crop = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.White);
            g.DrawImage(
                session.FrozenCapture,
                new Rectangle(0, 0, tw, th),
                new Rectangle(bandCapture.Left, bandCapture.Top, bandCapture.Width, bandCapture.Height),
                GraphicsUnit.Pixel);
        }

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
                if (!HasDigit.IsMatch(w))
                    continue;
                // Keep only the numeric token(s) inside the word.
                foreach (Match m in NumberRegex.Matches(w))
                {
                    var rect = word.BoundingRect;
                    double cx = bandCapture.Left + (rect.X + rect.Width * 0.5) / scale;
                    double cy = bandCapture.Top + (rect.Y + rect.Height * 0.5) / scale;
                    hits.Add(new Hit(m.Value, cy, cx));
                }
            }
        }

        // Merge near-duplicate tokens (same number read twice).
        hits = hits
            .GroupBy(h => h.Text + "@" + ((int)(h.CenterY / 8)))
            .Select(g => g.First())
            .ToList();

        Log(debugDir, $"{tag}: full='{ocr.Text}' digitHits={hits.Count}");
        return new RegionResult { Hits = hits };
    }

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
