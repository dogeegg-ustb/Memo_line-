using System.Drawing;
using System.Drawing.Imaging;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Services;

/// <summary>
/// Stores and matches compact luminance fingerprints outside archived ROI boundaries.
/// Samples never cross into the workspace or Navigator thumbnail, so canvas zoom,
/// artwork and viewport ink cannot invalidate an otherwise unchanged UI layout.
/// </summary>
public static class ArchiveVisualFingerprintService
{
    public const int FingerprintVersion = 2;
    public const int SamplesPerEdge = 48;
    public const int NormalBandDepth = 3;
    private const double MaximumMeanAbsoluteError = 10.0;

    private enum Side { Left, Top, Right, Bottom }

    private sealed record EdgeMatch(
        bool Matched,
        int Offset,
        double Similarity,
        float Strength,
        double MeanAbsoluteError);

    public sealed record MatchResult(
        bool IsMatch,
        string Message,
        int WorkspaceMatchedEdges,
        int ThumbnailMatchedEdges,
        int OffsetX,
        int OffsetY);

    public static unsafe ArchiveVisualFingerprintDto Capture(
        CaptureSession session,
        IntRect workspaceScreen,
        IntRect thumbnailScreen)
    {
        ArgumentNullException.ThrowIfNull(session);
        IntRect workspace = session.ScreenToCapture(workspaceScreen);
        IntRect thumbnail = session.ScreenToCapture(thumbnailScreen);
        EnsureInsideCapture(workspace, session.CaptureBounds, "工作区");
        EnsureInsideCapture(thumbnail, session.CaptureBounds, "导航器缩略图");

        return WithLockedBitmap(session, (scan0, stride, width, height) => new ArchiveVisualFingerprintDto
        {
            Version = FingerprintVersion,
            SamplesPerEdge = SamplesPerEdge,
            NormalBandDepth = NormalBandDepth,
            Workspace = CaptureRoi(scan0, stride, width, height, workspace),
            NavigatorThumbnail = CaptureRoi(scan0, stride, width, height, thumbnail)
        });
    }

    public static unsafe MatchResult Match(CaptureSession session, SaveArchive archive)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(archive);
        ArchiveVisualFingerprintDto fingerprint = archive.VisualFingerprint
            ?? throw new InvalidOperationException("存档缺少边界视觉指纹，请重新创建存档。");

        IntRect workspace = session.ScreenToCapture(archive.SystemWorkspaceRoiScreen.ToIntRect());
        IntRect thumbnail = session.ScreenToCapture(archive.SystemNavigatorThumbnailRoiScreen.ToIntRect());
        EnsureInsideCapture(workspace, session.CaptureBounds, "工作区");
        EnsureInsideCapture(thumbnail, session.CaptureBounds, "导航器缩略图");

        int searchRadius = Math.Clamp(
            (int)Math.Round(8 * Math.Max(session.DpiX, session.DpiY) / 96f), 8, 20);

        return WithLockedBitmap(session, (scan0, stride, width, height) =>
        {
            var ws = MatchRoi(scan0, stride, width, height, workspace, fingerprint.Workspace,
                fingerprint.NormalBandDepth, searchRadius);
            var nav = MatchRoi(scan0, stride, width, height, thumbnail,
                fingerprint.NavigatorThumbnail, fingerprint.NormalBandDepth, searchRadius);

            // Workspace exterior UI is the validity anchor. Navigator exterior
            // samples are diagnostic support and must not reject an archive just
            // because its surrounding panel is visually uniform.
            var xOffsets = ws.Edges
                .Where(e => e.Side is Side.Left or Side.Right && e.Match.Matched)
                .Select(e => e.Match.Offset).ToArray();
            var yOffsets = ws.Edges
                .Where(e => e.Side is Side.Top or Side.Bottom && e.Match.Matched)
                .Select(e => e.Match.Offset).ToArray();
            int offsetX = Median(xOffsets);
            int offsetY = Median(yOffsets);
            bool offsetsAgree = Spread(xOffsets) <= 4 && Spread(yOffsets) <= 4;
            bool matched = ws.IsMatch && offsetsAgree;
            string message = matched
                ? $"外侧 UI 指纹匹配：工作区 {ws.MatchedCount}/4，导航器参考 {nav.MatchedCount}/4，偏移=({offsetX},{offsetY})。"
                : $"外侧 UI 指纹不匹配：工作区 {ws.MatchedCount}/4，导航器参考 {nav.MatchedCount}/4，" +
                  $"工作区偏移一致={offsetsAgree}。";
            return new MatchResult(matched, message, ws.MatchedCount, nav.MatchedCount, offsetX, offsetY);
        });
    }

    public static string? ValidateFingerprint(ArchiveVisualFingerprintDto? fingerprint)
    {
        if (fingerprint is null)
            return "旧存档缺少边界视觉指纹，请重新创建";
        if (fingerprint.Version != FingerprintVersion)
            return $"不支持的边界视觉指纹版本={fingerprint.Version}";
        if (fingerprint.SamplesPerEdge != SamplesPerEdge
            || fingerprint.NormalBandDepth < 1 || fingerprint.NormalBandDepth > 8)
            return "边界视觉指纹参数无效";

        string? error = ValidateRoi(fingerprint.Workspace, "WorkspaceFingerprint");
        return error ?? ValidateRoi(fingerprint.NavigatorThumbnail, "NavigatorThumbnailFingerprint");
    }

    private static string? ValidateRoi(RoiBoundaryFingerprintDto? roi, string name)
    {
        if (roi is null)
            return $"缺少 {name}";
        foreach (var (edge, edgeName, _) in Enumerate(roi))
        {
            if (edge is null || edge.Values is null || edge.Values.Length != SamplesPerEdge
                || edge.Values.Any(v => !float.IsFinite(v)) || !float.IsFinite(edge.Strength))
                return $"{name}.{edgeName} 无效";
        }
        return null;
    }

    private static unsafe RoiBoundaryFingerprintDto CaptureRoi(
        byte* scan0, int stride, int width, int height, IntRect rect)
        => new()
        {
            Left = SampleEdge(scan0, stride, width, height, rect, Side.Left, 0, NormalBandDepth),
            Top = SampleEdge(scan0, stride, width, height, rect, Side.Top, 0, NormalBandDepth),
            Right = SampleEdge(scan0, stride, width, height, rect, Side.Right, 0, NormalBandDepth),
            Bottom = SampleEdge(scan0, stride, width, height, rect, Side.Bottom, 0, NormalBandDepth)
        };

    private sealed record RoiMatch(bool IsMatch, int MatchedCount, (Side Side, EdgeMatch Match)[] Edges);

    private static unsafe RoiMatch MatchRoi(
        byte* scan0, int stride, int width, int height, IntRect rect,
        RoiBoundaryFingerprintDto reference, int depth, int searchRadius)
    {
        var matches = Enumerate(reference)
            .Select(item => (item.Side, MatchEdge(
                scan0, stride, width, height, rect, item.Side, item.Edge, depth, searchRadius)))
            .ToArray();
        int matched = matches.Count(x => x.Item2.Matched);
        bool vertical = matches.Any(x => x.Item1 is Side.Left or Side.Right && x.Item2.Matched);
        bool horizontal = matches.Any(x => x.Item1 is Side.Top or Side.Bottom && x.Item2.Matched);
        return new RoiMatch(matched >= 2 && vertical && horizontal, matched,
            matches.Select(x => (x.Item1, x.Item2)).ToArray());
    }

    private static unsafe EdgeMatch MatchEdge(
        byte* scan0, int stride, int width, int height, IntRect rect, Side side,
        EdgeFingerprintDto reference, int depth, int searchRadius)
    {
        EdgeMatch bestOverall = new(false, 0, double.NegativeInfinity, 0, double.PositiveInfinity);
        EdgeMatch? bestMatched = null;

        for (int offset = -searchRadius; offset <= searchRadius; offset++)
        {
            EdgeFingerprintDto current = SampleEdge(
                scan0, stride, width, height, rect, side, offset, depth);
            double error = MeanAbsoluteError(reference.Values, current.Values);
            double similarity = Math.Clamp(1.0 - error / 255.0, -1.0, 1.0);
            bool isMatch = error <= MaximumMeanAbsoluteError;
            var candidate = new EdgeMatch(isMatch, offset, similarity, current.Strength, error);
            if (error < bestOverall.MeanAbsoluteError - 1e-6
                || (Math.Abs(error - bestOverall.MeanAbsoluteError) <= 1e-6
                    && Math.Abs(offset) < Math.Abs(bestOverall.Offset)))
                bestOverall = candidate;
            if (isMatch && (bestMatched is null
                || error < bestMatched.MeanAbsoluteError - 1e-6
                || (Math.Abs(error - bestMatched.MeanAbsoluteError) <= 1e-6
                    && Math.Abs(offset) < Math.Abs(bestMatched.Offset))))
                bestMatched = candidate;
        }
        return bestMatched ?? bestOverall;
    }

    private static unsafe EdgeFingerprintDto SampleEdge(
        byte* scan0, int stride, int width, int height, IntRect rect,
        Side side, int offset, int depth)
    {
        bool horizontal = side is Side.Top or Side.Bottom;
        int length = horizontal ? rect.Width : rect.Height;
        int inset = Math.Clamp(length / 12, 2, Math.Max(2, length / 4));
        int usable = Math.Max(1, length - 2 * inset);
        var values = new float[SamplesPerEdge];

        for (int sample = 0; sample < SamplesPerEdge; sample++)
        {
            int along = (horizontal ? rect.Left : rect.Top) + inset
                + (int)Math.Round((sample + 0.5) * usable / SamplesPerEdge);
            int boundary = side switch
            {
                Side.Left => rect.Left + offset,
                Side.Top => rect.Top + offset,
                Side.Right => rect.Right + offset,
                Side.Bottom => rect.Bottom + offset,
                _ => 0
            };
            int sum = 0;
            int count = 0;
            for (int band = 1; band <= depth; band++)
            {
                GetExteriorPoint(side, boundary, along, band, out int x, out int y);
                if (!Inside(x, y, width, height))
                    continue;
                sum += Luma(scan0, stride, x, y);
                count++;
            }
            values[sample] = count > 0 ? (float)sum / count : 0;
        }

        return new EdgeFingerprintDto
        {
            Values = values,
            Strength = MeanDeviation(values)
        };
    }

    private static void GetExteriorPoint(
        Side side, int boundary, int along, int band,
        out int x, out int y)
    {
        switch (side)
        {
            case Side.Left:
                x = boundary - band; y = along; break;
            case Side.Top:
                x = along; y = boundary - band; break;
            case Side.Right:
                x = boundary + band - 1; y = along; break;
            case Side.Bottom:
                x = along; y = boundary + band - 1; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(side));
        }
    }

    private static IEnumerable<(EdgeFingerprintDto Edge, string Name, Side Side)> Enumerate(
        RoiBoundaryFingerprintDto roi)
    {
        yield return (roi.Left, "Left", Side.Left);
        yield return (roi.Top, "Top", Side.Top);
        yield return (roi.Right, "Right", Side.Right);
        yield return (roi.Bottom, "Bottom", Side.Bottom);
    }

    private static double MeanAbsoluteError(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return double.PositiveInfinity;
        double error = 0;
        for (int i = 0; i < a.Length; i++)
            error += Math.Abs(a[i] - b[i]);
        return error / a.Length;
    }

    private static float MeanDeviation(float[] values)
    {
        if (values.Length == 0) return 0;
        float mean = values.Average();
        return values.Sum(v => Math.Abs(v - mean)) / values.Length;
    }

    private static int Median(int[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static int Spread(int[] values)
        => values.Length < 2 ? 0 : values.Max() - values.Min();

    private static void EnsureInsideCapture(IntRect rect, IntRect bounds, string name)
    {
        if (rect.IsEmpty || rect.Left < bounds.Left || rect.Top < bounds.Top
            || rect.Right > bounds.Right || rect.Bottom > bounds.Bottom)
            throw new InvalidOperationException($"{name} ROI 超出当前截图范围。");
    }

    private unsafe delegate T LockedBitmapAction<T>(byte* scan0, int stride, int width, int height);

    private static unsafe T WithLockedBitmap<T>(
        CaptureSession session, LockedBitmapAction<T> action)
    {
        Bitmap bitmap = session.FrozenCapture;
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { return action((byte*)data.Scan0, data.Stride, bitmap.Width, bitmap.Height); }
        finally { bitmap.UnlockBits(data); }
    }

    private static bool Inside(int x, int y, int width, int height)
        => x >= 0 && y >= 0 && x < width && y < height;

    private static unsafe int Luma(byte* scan0, int stride, int x, int y)
    {
        byte* p = scan0 + y * stride + x * 4;
        return (29 * p[0] + 150 * p[1] + 77 * p[2]) >> 8;
    }
}
