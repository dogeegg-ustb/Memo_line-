using DirtyMatrix.Core;
using StrokeReplay;

namespace MemolineDemo.Editing;

/// <summary>Pixel accurate write, probe and causal impact footprints for one recorded contact.</summary>
public static class Footprints
{
    internal static (PixelMask Write, PixelMask Probe, PixelMask Impact) Build(
        IReadOnlyList<MemolineReplaySample> samples,
        CanvasViewSnapshot view,
        int canvasWidth,
        int canvasHeight,
        CoverageSettings settings,
        IReadOnlyDictionary<string, double>? numbers = null,
        bool causal = false,
        bool copyStamp = false,
        bool antiOverflow = false,
        bool probeGuaranteed = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (canvasWidth <= 0 || canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        settings = settings with { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight, OriginX = view.OriginX, OriginY = view.OriginY,
            ZoomPercent = view.ScalePercent, ClipToCanvasBounds = true };
        settings.Validate();
        var points = samples.Where(s => s.InContact).Select(s => ScreenToCanvas(s.X, s.Y, view)).ToArray();
        var write = RasterizeCapsules(points, EffectiveSettings(settings, numbers), canvasWidth, canvasHeight);
        var probe = probeGuaranteed ? RasterizeCenterline(points, canvasWidth, canvasHeight) : write.Clone();
        var impact = !causal ? Empty(canvasWidth, canvasHeight) :
            (copyStamp ? Full(canvasWidth, canvasHeight) : write.Clone());
        if (causal && antiOverflow) impact = write.Clone();
        return (write, probe, impact);
    }

    public static PixelMask RasterizeCapsules(IReadOnlyList<(double X, double Y)> points,
        CoverageSettings settings, int width, int height)
    {
        var result = Empty(width, height);
        if (points.Count == 0) return result;
        settings.Validate();
        for (int i = 0; i < points.Count; i++)
            AddCapsule(result, i == 0 ? points[i] : points[i - 1], points[i], settings.Radius);
        return result;
    }

    public static PixelMask RasterizeCenterline(IReadOnlyList<(double X, double Y)> points, int width, int height)
    {
        var result = Empty(width, height);
        if (points.Count == 0) return result;
        for (int i = 0; i < points.Count; i++)
            AddCapsule(result, i == 0 ? points[i] : points[i - 1], points[i], .75);
        return result;
    }

    public static PixelMask RasterizeCoverage(IEnumerable<LabelEvidence> labels, int width, int height, bool centerline = false)
    {
        var result = Empty(width, height);
        foreach (var label in labels.OrderBy(l => l.FromTicks).ThenBy(l => l.ToTicks))
            foreach (var segment in label.Segments)
                AddCapsule(result, (segment.StartX, segment.StartY), (segment.EndX, segment.EndY), centerline ? .75 : label.Radius);
        return result;
    }

    private static void AddCapsule(PixelMask result, (double X, double Y) a, (double X, double Y) b, double radius)
    {
        if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(radius) || radius < 0)
            throw new InvalidDataException("笔迹胶囊几何无效。");
        int left = (int)Math.Clamp(Math.Floor(Math.Min(a.X, b.X) - radius), 0, result.Width);
        int top = (int)Math.Clamp(Math.Floor(Math.Min(a.Y, b.Y) - radius), 0, result.Height);
        int right = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, b.X) + radius), 0, result.Width);
        int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, b.Y) + radius), 0, result.Height);
        for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
            if (DistanceToSegment(new(x + .5, y + .5), new(a.X, a.Y), new(b.X, b.Y)) <= radius) result.Set(x, y);
    }

    public static (double X, double Y) ScreenToCanvas(double x, double y, CanvasViewSnapshot view)
    {
        double angle = view.RotationDegrees * Math.PI / 180.0;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        double zoom = view.ScalePercent / 100.0;
        if (!double.IsFinite(zoom) || zoom <= 0) throw new InvalidDataException("画布缩放无效。");
        double dx = x - view.OriginX, dy = y - view.OriginY;
        return ((c * dx + s * dy) / zoom, (-s * dx + c * dy) / zoom);
    }

    public static double DistanceToPolyline(PointD point, IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            var a = new PointD(points[i].X, points[i].Y);
            if (i == 0) best = Math.Min(best, Distance(point, a));
            else best = Math.Min(best, DistanceToSegment(point, new PointD(points[i - 1].X, points[i - 1].Y), a));
        }
        return best;
    }

    private static CoverageSettings EffectiveSettings(CoverageSettings settings, IReadOnlyDictionary<string, double>? numbers)
    {
        if (numbers is null || !numbers.TryGetValue("brush_size", out var size) || !double.IsFinite(size) || size <= 0)
            return settings;
        return settings with { BrushSize = size };
    }
    private static PixelMask Empty(int width, int height) => new(0, 0, width, height);
    private static PixelMask Full(int width, int height) => PixelMask.FromPredicate(0, 0, width, height, static (_, _) => true);
    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length2 = dx * dx + dy * dy;
        if (length2 <= double.Epsilon) return Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0, 1);
        return Distance(p, new(a.X + t * dx, a.Y + t * dy));
    }
}
