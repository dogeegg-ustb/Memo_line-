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
/// Reads CSP navigator scale/rotation from a user-selected left chrome region.
/// Upper numbers = scale %, lower numbers = rotation degrees (by OCR bbox Y order).
/// </summary>
public sealed class NavigatorOcrService
{
    public static bool DebugEnabled { get; set; } = true;

    private const int MinSlotSizePx = 8;
    private const int Upscale = 6;

    /// <summary>Only numeric tokens (optional sign / decimal). Units like % ° are stripped before parse.</summary>
    private static readonly Regex NumberRegex = new(
        @"[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.Compiled);

    private static readonly Regex DigitsOnlyTokenRegex = new(
        @"^[-+]?\d+(?:[.,]\d+)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Persist a user-selected left chrome block as top=scale / bottom=rotation slots (full width).
    /// </summary>
    public static OcrLayoutScreen LayoutFromUserRegion(IntRect regionScreen)
    {
        if (regionScreen.IsEmpty || regionScreen.Width < MinSlotSizePx || regionScreen.Height < MinSlotSizePx)
            return new OcrLayoutScreen(regionScreen, regionScreen);

        int midY = regionScreen.Top + Math.Max(MinSlotSizePx, regionScreen.Height / 2);
        if (midY >= regionScreen.Bottom)
            midY = regionScreen.Bottom - 1;
        if (midY <= regionScreen.Top)
            midY = regionScreen.Top + 1;

        IntRect scale = new(regionScreen.Left, regionScreen.Top, regionScreen.Right, midY);
        IntRect rotation = new(regionScreen.Left, midY, regionScreen.Right, regionScreen.Bottom);
        return new OcrLayoutScreen(scale, rotation);
    }

    /// <summary>
    /// Legacy auto layout (navigator left column). Prefer <see cref="LayoutFromUserRegion"/> for init.
    /// </summary>
    public static OcrLayoutScreen ComputeOcrLayout(
        IntRect navigatorPanelScreen,
        IntRect thumbnailScreen,
        double leftColumnFraction = 0.55)
    {
        IntRect chrome = ChromeBand(navigatorPanelScreen, thumbnailScreen);
        if (chrome.IsEmpty)
            return new OcrLayoutScreen(chrome, chrome);

        double fraction = Math.Clamp(leftColumnFraction, 0.35, 0.95);
        int colWidth = Math.Max(MinSlotSizePx, (int)Math.Round(chrome.Width * fraction));
        IntRect leftCol = new(chrome.Left, chrome.Top, chrome.Left + colWidth, chrome.Bottom);
        return LayoutFromUserRegion(leftCol);
    }

    public async Task<NavigatorNumericReadingDto> ReadWithLayoutAsync(
        CaptureSession session,
        OcrLayoutScreen layoutScreen,
        CancellationToken cancellationToken = default)
    {
        IntRect scaleSlot = session.ScreenToCapture(layoutScreen.ScaleSlotScreen).ClampTo(session.CaptureBounds);
        IntRect rotationSlot = session.ScreenToCapture(layoutScreen.RotationSlotScreen).ClampTo(session.CaptureBounds);
        return await ReadByVerticalOrderAsync(session, scaleSlot, rotationSlot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// OCR the union of scale+rotation slots once; assign uppermost scale-like number and
    /// lowermost rotation-like number by bbox Y (digits may drift left/right inside the block).
    /// </summary>
    private static async Task<NavigatorNumericReadingDto> ReadByVerticalOrderAsync(
        CaptureSession session,
        IntRect scaleSlotCapture,
        IntRect rotationSlotCapture,
        CancellationToken cancellationToken)
    {
        string debugDir = Path.Combine(Path.GetTempPath(), "sct_ocr_debug", session.CaptureId);
        if (DebugEnabled)
            Directory.CreateDirectory(debugDir);

        // Prefer en-US for Western decimals; zh-Hans often splits "14.8" into "14" + "，" + "8".
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"));

        if (engine is null)
        {
            Log(debugDir, "engine=null");
            return Empty(session.CaptureId);
        }

        Log(debugDir, $"engineLang={engine.RecognizerLanguage.LanguageTag}");

        IntRect union = Union(scaleSlotCapture, rotationSlotCapture);
        Log(debugDir, $"slots scale={scaleSlotCapture} rot={rotationSlotCapture} union={union}");

        // Bright-digit binarize (+ dilate/pad) first — plain invert often returns empty on teen %.
        var hits = await RecognizeSlotAsync(
                session, union, engine, debugDir, "region", cancellationToken)
            .ConfigureAwait(false);

        double midY = (union.Top + union.Bottom) * 0.5;
        // Prefer geometry split if slots are stacked; otherwise mid of union.
        if (!scaleSlotCapture.IsEmpty && !rotationSlotCapture.IsEmpty
            && scaleSlotCapture.Bottom <= rotationSlotCapture.Top + 2)
        {
            midY = (scaleSlotCapture.Bottom + rotationSlotCapture.Top) * 0.5;
        }

        var topHits = hits.Where(h => h.CenterY < midY).OrderBy(h => h.CenterY).ThenBy(h => h.CenterX).ToList();
        var bottomHits = hits.Where(h => h.CenterY >= midY).OrderByDescending(h => h.CenterY).ThenBy(h => h.CenterX).ToList();
        // If partition empty (both numbers landed same half), fall back to global Y order.
        if (topHits.Count == 0 || bottomHits.Count == 0)
        {
            var byY = hits.OrderBy(h => h.CenterY).ThenBy(h => h.CenterX).ToList();
            topHits = byY.Take(Math.Max(1, byY.Count / 2)).ToList();
            bottomHits = byY.Skip(Math.Max(0, byY.Count - Math.Max(1, byY.Count / 2))).Reverse().ToList();
            Log(debugDir, "partition-empty → global Y split");
        }

        bool scaleOk = TryPickOrdered(topHits, TryParseScale, out float scaleVal, out string scaleRaw);
        bool rotOk = TryPickOrdered(bottomHits, TryParseRotation, out float rotVal, out string rotRaw);
        if (!rotOk)
        {
            // Try any remaining hits as rotation (still prefer lower Y).
            var rest = hits.OrderByDescending(h => h.CenterY).ToList();
            rotOk = TryPickOrdered(rest, TryParseRotation, out rotVal, out rotRaw);
        }

        if (!rotOk)
        {
            rotOk = true;
            rotVal = 0;
            rotRaw = "0";
        }

        Log(debugDir,
            $"result scaleOk={scaleOk} raw='{scaleRaw}' val={scaleVal}; " +
            $"rotOk={rotOk} raw='{rotRaw}' val={rotVal}; hits={hits.Count} midY={midY:F1}");

        return new NavigatorNumericReadingDto
        {
            ScalePercent = scaleOk ? scaleVal : 0,
            RotationDegrees = rotOk ? rotVal : 0,
            ScaleConfidence = scaleOk ? 0.85f : 0,
            RotationConfidence = rotOk && rotRaw != "0" ? 0.85f : (rotOk ? 0.5f : 0),
            ScaleRawText = scaleRaw,
            RotationRawText = rotRaw,
            SourceCaptureId = session.CaptureId,
            CapturedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Init helper: require a readable scale (and prefer a real rotation token) from a user region.
    /// </summary>
    public async Task<(bool Ok, OcrLayoutScreen Layout, NavigatorNumericReadingDto Numbers)> TryReadUserRegionAsync(
        CaptureSession session,
        IntRect regionScreen,
        CancellationToken cancellationToken = default)
    {
        var layout = LayoutFromUserRegion(regionScreen);
        var numbers = await ReadWithLayoutAsync(session, layout, cancellationToken).ConfigureAwait(false);
        bool ok = numbers.ScaleConfidence >= 0.2f && numbers.ScalePercent > 0;
        return (ok, layout, numbers);
    }

    private static IntRect Union(IntRect a, IntRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new IntRect(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));
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

    private readonly record struct Hit(string Text, double CenterY, double CenterX);

    private readonly record struct RawToken(
        string Text, double CenterY, double CenterX, double Left, double Right, double Height);

    private enum DigitFragKind { Digits, DecimalMark }

    private readonly record struct DigitFrag(
        DigitFragKind Kind, string Digits, double CenterY, double CenterX, double Left, double Right, double Height);

    /// <summary>
    /// Digit-only reassembly: map OCR confusions to digits, ignore junk, insert decimals from
    /// punctuation / "&"(≈"8.") / gaps. Public for unit tests.
    /// </summary>
    public static IReadOnlyList<(string Text, double CenterY, double CenterX)> CoalesceOcrNumberTokens(
        IReadOnlyList<(string Text, double CenterY, double CenterX, double Left, double Right, double Height)> tokens)
    {
        if (tokens.Count == 0)
            return Array.Empty<(string, double, double)>();

        var frags = new List<DigitFrag>();
        foreach (var t in tokens)
        {
            string text = t.Text?.Trim() ?? "";
            if (text.Length == 0) continue;
            AppendDigitFrags(text, t.CenterY, t.CenterX, t.Left, t.Right, t.Height, frags);
        }

        if (frags.Count == 0)
            return Array.Empty<(string, double, double)>();

        frags = frags
            .OrderBy(f => f.CenterY)
            .ThenBy(f => f.Left)
            .ToList();

        var rows = new List<List<DigitFrag>>();
        foreach (var f in frags)
        {
            if (rows.Count == 0 || !SameRowFrag(rows[^1][^1], f))
                rows.Add(new List<DigitFrag> { f });
            else
                rows[^1].Add(f);
        }

        var hits = new List<(string Text, double CenterY, double CenterX)>();
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.Left.CompareTo(b.Left));
            AssembleDigitRow(row, hits);
        }

        return hits
            .GroupBy(h => h.Text + "@" + ((int)(h.CenterY / 8)))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Map one OCR word into digit / decimal fragments only.</summary>
    private static void AppendDigitFrags(
        string text, double cy, double cx, double left, double right, double height, List<DigitFrag> sink)
    {
        // "&" is a common zh/en OCR mash of "8." on CSP navigator glyphs.
        if (text is "&" or "＆")
        {
            double mid = (left + right) * 0.5;
            sink.Add(new DigitFrag(DigitFragKind.Digits, "8", cy, (left + mid) * 0.5, left, mid, height));
            sink.Add(new DigitFrag(DigitFragKind.DecimalMark, "", cy, mid, mid, mid, height));
            return;
        }

        if (IsDecimalPunctuation(text))
        {
            sink.Add(new DigitFrag(DigitFragKind.DecimalMark, "", cy, cx, left, right, height));
            return;
        }

        // Char-wise: keep digits / mapped confusions / decimal marks; drop the rest.
        var digits = new System.Text.StringBuilder();
        double digLeft = left;
        double digRight = left;
        bool inDigits = false;
        double w = Math.Max(1.0, right - left);
        double unit = w / Math.Max(1, text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            char mapped = MapConfusionToDigitOrZero(ch);
            double cLeft = left + i * unit;
            double cRight = left + (i + 1) * unit;
            double cCx = (cLeft + cRight) * 0.5;

            if (mapped != '\0')
            {
                if (!inDigits)
                {
                    inDigits = true;
                    digLeft = cLeft;
                    digits.Clear();
                }
                digits.Append(mapped);
                digRight = cRight;
                continue;
            }

            if (inDigits)
            {
                sink.Add(new DigitFrag(DigitFragKind.Digits, digits.ToString(), cy, (digLeft + digRight) * 0.5,
                    digLeft, digRight, height));
                inDigits = false;
                digits.Clear();
            }

            if (IsDecimalPunctuation(ch.ToString()))
                sink.Add(new DigitFrag(DigitFragKind.DecimalMark, "", cy, cCx, cLeft, cRight, height));
        }

        if (inDigits && digits.Length > 0)
        {
            sink.Add(new DigitFrag(DigitFragKind.Digits, digits.ToString(), cy, (digLeft + digRight) * 0.5,
                digLeft, digRight, height));
        }
    }

    /// <summary>Returns digit char, or '\0' if not a digit confusion.</summary>
    private static char MapConfusionToDigitOrZero(char ch)
    {
        if (ch is >= '0' and <= '9') return ch;
        return ch switch
        {
            'O' or 'o' or 'D' or '〇' or '○' => '0',
            'I' or 'l' or '|' or '！' or '!' => '1',
            'Z' or 'z' => '2',
            'S' or 's' => '8', // CSP navigator "8" often OCR'd as S
            'G' or 'b' => '6',
            'T' => '7',
            'B' => '8',
            'g' or 'q' => '9',
            _ => '\0'
        };
    }

    private static void AssembleDigitRow(List<DigitFrag> row, List<(string Text, double CenterY, double CenterX)> hits)
    {
        if (row.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        bool sawDecimal = false;
        double startLeft = row[0].Left;
        double endRight = row[0].Right;
        double sumY = 0;
        int n = 0;

        void Flush()
        {
            if (sb.Length == 0) return;
            string text = sb.ToString();
            // Trailing/leading lone dots from marks with no digits — drop.
            text = text.Trim('.');
            if (text.Length > 0 && text.Any(char.IsDigit))
            {
                hits.Add((text, sumY / Math.Max(1, n), (startLeft + endRight) * 0.5));
            }
            sb.Clear();
            sawDecimal = false;
            n = 0;
            sumY = 0;
        }

        for (int i = 0; i < row.Count; i++)
        {
            var f = row[i];
            if (i > 0 && sb.Length > 0)
            {
                double gap = f.Left - endRight;
                double refW = Math.Max(12.0, Math.Max(endRight - startLeft, f.Right - f.Left) / Math.Max(1, sb.Length));
                // Large gap → separate number — but not right after a decimal mark (fraction pending).
                bool waitingFraction = sawDecimal && sb[^1] == '.';
                if (gap > refW * 2.2 && f.Kind == DigitFragKind.Digits && !waitingFraction)
                    Flush();
            }

            if (f.Kind == DigitFragKind.DecimalMark)
            {
                if (sb.Length > 0 && !sawDecimal)
                {
                    sb.Append('.');
                    sawDecimal = true;
                }
                endRight = Math.Max(endRight, f.Right);
                continue;
            }

            // Digits only — decimals come from explicit marks / "&"→"8." mapping, not gap heuristics.
            if (sb.Length == 0)
            {
                startLeft = f.Left;
                sumY = 0;
                n = 0;
            }
            sb.Append(f.Digits);
            endRight = f.Right;
            sumY += f.CenterY;
            n++;
        }

        Flush();
    }

    private static bool SameRowFrag(DigitFrag a, DigitFrag b)
    {
        double tol = Math.Max(8.0, Math.Max(a.Height, b.Height) * 0.6);
        return Math.Abs(a.CenterY - b.CenterY) <= tol;
    }

    private static bool IsDecimalPunctuation(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s.Trim();
        return t is "." or "," or "，" or "．" or "。" or "·" or "｡" or "﹒";
    }

    public static string NormalizeDecimalPunctuation(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        // Digit-only path: strip non-digit/non-dot after mapping confusions & fullwidth dots.
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (ch is '，' or '．' or '。' or '·' or '｡' or '﹒' or ',')
            {
                sb.Append('.');
                continue;
            }
            if (ch is >= '0' and <= '9' or '.' or '-' or '+')
            {
                sb.Append(ch);
                continue;
            }
            char mapped = MapConfusionToDigitOrZero(ch);
            if (mapped != '\0')
                sb.Append(mapped);
        }
        return sb.ToString();
    }

    private static bool SameRow(RawToken a, RawToken b)
    {
        double tol = Math.Max(8.0, Math.Max(a.Height, b.Height) * 0.6);
        return Math.Abs(a.CenterY - b.CenterY) <= tol;
    }

    private static bool HorizontallyClose(RawToken a, RawToken b)
    {
        double gap = b.Left - a.Right;
        double tol = Math.Max(24.0, Math.Max(a.Right - a.Left, b.Right - b.Left) * 1.5);
        return gap >= -8 && gap <= tol;
    }

    private static bool HorizontallyClose(RawToken a, RawToken b, RawToken c)
        => HorizontallyClose(a, b) && HorizontallyClose(b, c);

    private static readonly int[] BrightDigitThresholds = { 100, 110, 120, 130 };
    private const int OcrPadPx = 16;

    private static int Luma(byte b, byte g, byte r) => (b * 29 + g * 150 + r * 77) >> 8;

    /// <summary>Keep bright pixels as black ink on white (CSP digits are light on dark chrome).</summary>
    private static Bitmap BinarizeBrightDigits(Bitmap src, int threshold)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var s = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var d = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[s.Stride];
            byte[] o = new byte[d.Stride];
            for (int y = 0; y < src.Height; y++)
            {
                Marshal.Copy(s.Scan0 + y * s.Stride, row, 0, s.Stride);
                for (int x = 0; x < src.Width; x++)
                {
                    int i = x * 4;
                    bool ink = Luma(row[i], row[i + 1], row[i + 2]) >= threshold;
                    byte v = ink ? (byte)0 : (byte)255;
                    o[i] = v;
                    o[i + 1] = v;
                    o[i + 2] = v;
                    o[i + 3] = 255;
                }
                Marshal.Copy(o, 0, d.Scan0 + y * d.Stride, d.Stride);
            }
        }
        finally
        {
            src.UnlockBits(s);
            dst.UnlockBits(d);
        }
        return dst;
    }

    private static Bitmap DilateBlackInk3(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var s = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var d = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] buf = new byte[s.Stride * src.Height];
            Marshal.Copy(s.Scan0, buf, 0, buf.Length);
            byte[] o = new byte[d.Stride * dst.Height];
            for (int i = 0; i < o.Length; i += 4)
            {
                o[i] = o[i + 1] = o[i + 2] = 255;
                o[i + 3] = 255;
            }

            for (int y = 1; y < src.Height - 1; y++)
            for (int x = 1; x < src.Width - 1; x++)
            {
                bool ink = false;
                for (int dy = -1; dy <= 1 && !ink; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int i = (y + dy) * s.Stride + (x + dx) * 4;
                    if (buf[i] < 128)
                    {
                        ink = true;
                        break;
                    }
                }

                int oi = y * d.Stride + x * 4;
                byte v = ink ? (byte)0 : (byte)255;
                o[oi] = o[oi + 1] = o[oi + 2] = v;
                o[oi + 3] = 255;
            }

            Marshal.Copy(o, 0, d.Scan0, o.Length);
        }
        finally
        {
            src.UnlockBits(s);
            dst.UnlockBits(d);
        }
        return dst;
    }

    private static Bitmap PadWhite(Bitmap src, int pad)
    {
        var dst = new Bitmap(src.Width + pad * 2, src.Height + pad * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.White);
        g.DrawImageUnscaled(src, pad, pad);
        return dst;
    }

    private static async Task<List<Hit>> RecognizeSlotAsync(
        CaptureSession session,
        IntRect slotCapture,
        OcrEngine engine,
        string debugDir,
        string tag,
        CancellationToken cancellationToken)
    {
        if (slotCapture.Width < MinSlotSizePx || slotCapture.Height < MinSlotSizePx)
        {
            Log(debugDir, $"{tag}: slot too small {slotCapture}");
            return new List<Hit>();
        }

        int tw = Math.Max(8, slotCapture.Width * Upscale);
        int th = Math.Max(8, slotCapture.Height * Upscale);

        using var upscaled = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(upscaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Black);
            g.DrawImage(
                session.FrozenCapture,
                new Rectangle(0, 0, tw, th),
                new Rectangle(slotCapture.Left, slotCapture.Top, slotCapture.Width, slotCapture.Height),
                GraphicsUnit.Pixel);
        }

        if (DebugEnabled)
        {
            try { upscaled.Save(Path.Combine(debugDir, $"{tag}_upscaled.png"), ImageFormat.Png); }
            catch { /* ignore */ }
        }

        List<Hit>? bestPartial = null;

        foreach (int threshold in BrightDigitThresholds)
        {
            using var bin = BinarizeBrightDigits(upscaled, threshold);
            using var dil = DilateBlackInk3(bin);
            using var padded = PadWhite(dil, OcrPadPx);
            if (DebugEnabled)
            {
                try { padded.Save(Path.Combine(debugDir, $"{tag}_binT{threshold}.png"), ImageFormat.Png); }
                catch { /* ignore */ }
            }

            var hits = await OcrBitmapToHitsAsync(
                    padded, slotCapture, engine, debugDir, $"{tag}_T{threshold}", OcrPadPx, cancellationToken)
                .ConfigureAwait(false);

            if (hits.Any(h => TryParseScale(h.Text, out _)))
                return hits;
            if (hits.Count > 0 && bestPartial is null)
                bestPartial = hits;
        }

        using (var inv = (Bitmap)upscaled.Clone())
        {
            InvertInPlace(inv);
            using var padded = PadWhite(inv, OcrPadPx);
            if (DebugEnabled)
            {
                try { padded.Save(Path.Combine(debugDir, $"{tag}_invert.png"), ImageFormat.Png); }
                catch { /* ignore */ }
            }

            var hits = await OcrBitmapToHitsAsync(
                    padded, slotCapture, engine, debugDir, $"{tag}_invert", OcrPadPx, cancellationToken)
                .ConfigureAwait(false);
            if (hits.Any(h => TryParseScale(h.Text, out _)))
                return hits;
            if (hits.Count > 0 && bestPartial is null)
                bestPartial = hits;
        }

        {
            using var padded = PadWhite(upscaled, OcrPadPx);
            var hits = await OcrBitmapToHitsAsync(
                    padded, slotCapture, engine, debugDir, $"{tag}_raw", OcrPadPx, cancellationToken)
                .ConfigureAwait(false);
            if (hits.Count > 0)
                return hits;
        }

        return bestPartial ?? new List<Hit>();
    }

    private static async Task<List<Hit>> OcrBitmapToHitsAsync(
        Bitmap image,
        IntRect slotCapture,
        OcrEngine engine,
        string debugDir,
        string tag,
        int padPx,
        CancellationToken cancellationToken)
    {
        int tw = image.Width;
        int th = image.Height;
        int packedStride = tw * 4;
        byte[] pixels = new byte[packedStride * th];
        var data = image.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < th; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * packedStride, packedStride);
        }
        finally
        {
            image.UnlockBits(data);
        }

        using var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8, tw, th, BitmapAlphaMode.Ignore);
        softwareBitmap.CopyFromBuffer(pixels.AsBuffer());

        OcrResult ocr = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);

        var rawTokens = new List<(string Text, double CenterY, double CenterX, double Left, double Right, double Height)>();
        foreach (OcrLine line in ocr.Lines)
        {
            foreach (OcrWord word in line.Words)
            {
                string w = word.Text?.Trim() ?? "";
                if (w.Length == 0) continue;
                var rect = word.BoundingRect;
                double left = slotCapture.Left + (rect.X - padPx) / Upscale;
                double right = slotCapture.Left + (rect.X + rect.Width - padPx) / Upscale;
                double top = slotCapture.Top + (rect.Y - padPx) / Upscale;
                double bottom = slotCapture.Top + (rect.Y + rect.Height - padPx) / Upscale;
                rawTokens.Add((w, (top + bottom) * 0.5, (left + right) * 0.5, left, right, bottom - top));
            }
        }

        var coalesced = CoalesceOcrNumberTokens(rawTokens);
        var hits = coalesced.Select(h => new Hit(h.Text, h.CenterY, h.CenterX)).ToList();

        Log(debugDir,
            $"{tag}: full='{ocr.Text}' rawWords={rawTokens.Count} digitHits={hits.Count} " +
            $"hits=[{string.Join(", ", hits.Select(h => h.Text))}]");
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

    private static bool TryPickOrdered(
        IReadOnlyList<Hit> hits,
        TryParseSlot parse,
        out float value,
        out string raw)
    {
        value = 0;
        raw = "";
        if (hits.Count == 0)
            return false;

        foreach (var hit in hits)
        {
            if (parse(hit.Text, out value))
            {
                raw = hit.Text;
                return true;
            }
        }

        raw = hits[0].Text;
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
        string token = NormalizeDecimalPunctuation(raw).Trim();
        if (!DigitsOnlyTokenRegex.IsMatch(token))
            return false;
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
