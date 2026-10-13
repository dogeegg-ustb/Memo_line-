using System.Drawing;

namespace MemolineDemo.Editing;

internal static class CanvasSelectionGeometry
{
    internal static PointF[] Rectangle(PointF start, PointF end)
        => [new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
            new(Math.Max(start.X, end.X), Math.Min(start.Y, end.Y)),
            new(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)),
            new(Math.Min(start.X, end.X), Math.Max(start.Y, end.Y))];

    // Rasterize pixel centers with the even-odd rule; a lasso's bounding box is not its selection.
    internal static PixelMask Polygon(Size canvas, IReadOnlyList<PointF> points)
    {
        var mask = new PixelMask(canvas.Width, canvas.Height);
        if (points.Count < 3 || points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) return mask;
        int top = (int)Math.Clamp(Math.Floor(points.Min(p => p.Y)), 0, canvas.Height);
        int bottom = (int)Math.Clamp(Math.Ceiling(points.Max(p => p.Y)), 0, canvas.Height);
        var intersections = new List<double>(points.Count);
        for (int y = top; y < bottom; y++)
        {
            intersections.Clear();
            double scan = y + .5;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                if ((a.Y <= scan && b.Y > scan) || (b.Y <= scan && a.Y > scan))
                    intersections.Add(a.X + (scan - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            intersections.Sort();
            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                int left = (int)Math.Clamp(Math.Ceiling(intersections[i] - .5), 0, canvas.Width);
                int right = (int)Math.Clamp(Math.Ceiling(intersections[i + 1] - .5), 0, canvas.Width);
                mask.SetRun(left, right - 1, y);
            }
        }
        return mask;
    }

    internal static bool Intersects(PixelMask selection, PixelMask footprint)
        => selection.Intersects(footprint);

    internal static PixelMask StrokeSelection(Size canvas, IEnumerable<EditableStroke> strokes)
    {
        var selection = new PixelMask(canvas.Width, canvas.Height);
        foreach (var stroke in strokes.Where(s => s.Eligible))
        {
            var mask = stroke.WriteMask;
            if (mask.Left == 0 && mask.Top == 0 && mask.Width == canvas.Width && mask.Height == canvas.Height)
                selection.UnionInPlace(mask);
            else
                foreach (var (x, y) in mask.Pixels())
                    if (x >= 0 && y >= 0 && x < canvas.Width && y < canvas.Height) selection.Set(x, y);
        }
        return selection;
    }
}
