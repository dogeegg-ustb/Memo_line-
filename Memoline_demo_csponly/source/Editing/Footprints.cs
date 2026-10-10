using DirtyMatrix.Core;
using StrokeReplay;
using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>Pixel accurate write, probe and causal impact footprints for one recorded contact.</summary>
public static class Footprints
{
    internal static bool TryMapContactSamples(IReadOnlyList<MemolineReplaySample> samples, JsonElement viewData,
        int canvasWidth, int canvasHeight, out IReadOnlyList<(double X, double Y)> points)
    {
        var mapped = new List<(double X, double Y)>(); points = mapped;
        if (CanvasLayerWatcher.View.Parse(viewData) is not { } view) return false;
        foreach (var sample in samples.Where(s => s.InContact))
        {
            if (!CanvasLayerWatcher.RecognitionEvidence.TryCanvasPoint(viewData, view, sample.X, sample.Y,
                new(canvasWidth, canvasHeight), out var point, out _)) return false;
            mapped.Add((point.X, point.Y));
        }
        return mapped.Count > 0;
    }

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

    // Historical projects can contain thousands of contacts on a large canvas.
    // Keep their exact pixel footprints within the observed geometry's bounds.
    internal static PixelMask RasterizeCoverageBounded(IEnumerable<LabelEvidence> labels, int width, int height)
    {
        var capsules = labels.SelectMany(l => l.Segments.Select(s =>
            (A: (s.StartX, s.StartY), B: (s.EndX, s.EndY), Radius: l.Radius))).ToArray();
        var result = CapsuleBounds(capsules, width, height);
        FillCapsules(result, capsules);
        return result;
    }

    internal static PixelMask RasterizeCenterlineBounded(IReadOnlyList<(double X, double Y)> points, int width, int height)
    {
        var capsules = points.Select((p,i) => (A: i == 0 ? p : points[i-1], B: p, Radius: .75)).ToArray();
        var result = CapsuleBounds(capsules, width, height);
        FillCapsules(result, capsules);
        return result;
    }

    // Consecutive samples overlap almost entirely, so filling capsules one by one sets the same
    // pixels hundreds of times. Merge every capsule's row intervals first and set each pixel once.
    private static void FillCapsules(PixelMask result, ((double X, double Y) A, (double X, double Y) B, double Radius)[] capsules)
    {
        if (result.Width == 0 || result.Height == 0) return;
        var rows = new List<(int Start, int End)>?[result.Height];
        foreach (var (a, b, radius) in capsules)
        {
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(radius) || radius < 0)
                throw new InvalidDataException("笔迹胶囊几何无效。");
            int left = (int)Math.Clamp(Math.Floor(Math.Min(a.X, b.X) - radius), result.Left, result.Left + result.Width);
            int top = (int)Math.Clamp(Math.Floor(Math.Min(a.Y, b.Y) - radius), result.Top, result.Top + result.Height);
            int right = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, b.X) + radius), result.Left, result.Left + result.Width);
            int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, b.Y) + radius), result.Top, result.Top + result.Height);
            var start = new PointD(a.X, a.Y); var end = new PointD(b.X, b.Y);
            for (int y = top; y < bottom; y++)
            {
                if (!RowSpan(start, end, radius, y + .5, out double lo, out double hi)) continue;
                int x0 = Math.Max(left, (int)Math.Ceiling(lo - .5) - 1), x1 = Math.Min(right - 1, (int)Math.Floor(hi - .5) + 1);
                while (x0 <= x1 && DistanceToSegment(new(x0 + .5, y + .5), start, end) > radius) x0++;
                while (x1 >= x0 && DistanceToSegment(new(x1 + .5, y + .5), start, end) > radius) x1--;
                if (x0 <= x1) (rows[y - result.Top] ??= []).Add((x0, x1));
            }
        }
        for (int row = 0; row < rows.Length; row++)
        {
            if (rows[row] is not { } spans) continue;
            spans.Sort();
            int y = result.Top + row, filled = int.MinValue;
            foreach (var (s, e) in spans)
                for (int x = Math.Max(s, filled + 1); x <= e; x++) { result.Set(x, y); filled = x; }
        }
    }

    internal static PixelMask UnionBounded(IEnumerable<PixelMask> masks)
    {
        var items = masks.Where(m => !m.IsEmpty).ToArray();
        if (items.Length == 0) return new(0,0);
        int left=items.Min(m=>m.Left), top=items.Min(m=>m.Top);
        var result=new PixelMask(left,top,items.Max(m=>m.Left+m.Width)-left,items.Max(m=>m.Top+m.Height)-top);
        foreach(var mask in items) foreach(var (x,y) in mask.Pixels()) result.Set(x,y);
        return result;
    }

    private static PixelMask CapsuleBounds(((double X,double Y) A,(double X,double Y) B,double Radius)[] capsules,int width,int height)
    {
        if(capsules.Length==0)return new(0,0);
        if(capsules.Any(c=>!double.IsFinite(c.A.X)||!double.IsFinite(c.A.Y)||!double.IsFinite(c.B.X)||!double.IsFinite(c.B.Y)||!double.IsFinite(c.Radius)||c.Radius<0))
            throw new InvalidDataException("笔迹胶囊几何无效。");
        int left=(int)Math.Clamp(Math.Floor(capsules.Min(c=>Math.Min(c.A.X,c.B.X)-c.Radius)),0,width);
        int top=(int)Math.Clamp(Math.Floor(capsules.Min(c=>Math.Min(c.A.Y,c.B.Y)-c.Radius)),0,height);
        int right=(int)Math.Clamp(Math.Ceiling(capsules.Max(c=>Math.Max(c.A.X,c.B.X)+c.Radius)),0,width);
        int bottom=(int)Math.Clamp(Math.Ceiling(capsules.Max(c=>Math.Max(c.A.Y,c.B.Y)+c.Radius)),0,height);
        return new(left,top,right-left,bottom-top);
    }

    private static void AddCapsule(PixelMask result, (double X, double Y) a, (double X, double Y) b, double radius)
    {
        if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(radius) || radius < 0)
            throw new InvalidDataException("笔迹胶囊几何无效。");
        int left = (int)Math.Clamp(Math.Floor(Math.Min(a.X, b.X) - radius), result.Left, result.Left+result.Width);
        int top = (int)Math.Clamp(Math.Floor(Math.Min(a.Y, b.Y) - radius), result.Top, result.Top+result.Height);
        int right = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, b.X) + radius), result.Left, result.Left+result.Width);
        int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, b.Y) + radius), result.Top, result.Top+result.Height);
        var start = new PointD(a.X, a.Y); var end = new PointD(b.X, b.Y);
        for (int y = top; y < bottom; y++)
        {
            // A capsule is convex, so one row of pixel centres meets it in a single interval.
            // Solve that interval, then confirm its edges with the exact distance test.
            if (!RowSpan(start, end, radius, y + .5, out double lo, out double hi)) continue;
            int x0 = Math.Max(left, (int)Math.Ceiling(lo - .5) - 1), x1 = Math.Min(right - 1, (int)Math.Floor(hi - .5) + 1);
            while (x0 <= x1 && DistanceToSegment(new(x0 + .5, y + .5), start, end) > radius) x0++;
            while (x1 >= x0 && DistanceToSegment(new(x1 + .5, y + .5), start, end) > radius) x1--;
            for (int x = x0; x <= x1; x++) result.Set(x, y);
        }
    }

    private static bool RowSpan(PointD a, PointD b, double r, double y, out double lo, out double hi)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        void Disk(PointD c)
        {
            double dy = y - c.Y, k = r * r - dy * dy;
            if (k < 0) return;
            double h = Math.Sqrt(k); min = Math.Min(min, c.X - h); max = Math.Max(max, c.X + h);
        }
        Disk(a); Disk(b);
        double dx = b.X - a.X, dyy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dyy * dyy);
        if (length > double.Epsilon)
        {
            // Band: |cross(d, p-a)| <= r*L and 0 <= dot(d, p-a) <= L², both linear in x for a fixed row.
            double bandLo = double.NegativeInfinity, bandHi = double.PositiveInfinity;
            bool Clip(double coefficient, double constant, double min, double max)
            {
                // min <= coefficient*x + constant <= max
                if (Math.Abs(coefficient) < 1e-12) return constant >= min && constant <= max;
                double p = (min - constant) / coefficient, q = (max - constant) / coefficient;
                bandLo = Math.Max(bandLo, Math.Min(p, q)); bandHi = Math.Min(bandHi, Math.Max(p, q));
                return true;
            }
            double ry = y - a.Y;
            // cross = dx*ry - dyy*(x - a.X); dot = dx*(x - a.X) + dyy*ry
            if (Clip(-dyy, dx * ry + dyy * a.X, -r * length, r * length) &&
                Clip(dx, dyy * ry - dx * a.X, 0, length * length) && bandLo <= bandHi)
            { min = Math.Min(min, bandLo); max = Math.Max(max, bandHi); }
        }
        lo = min; hi = max;
        return lo <= hi;
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
