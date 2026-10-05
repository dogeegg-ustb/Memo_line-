namespace DirtyMatrix.Core;

public enum BrushUnit { Px, Mm }
public enum InputSpace { TabletRaw, CanvasPixels, ScreenPixels }
public readonly record struct PointD(double X, double Y);
public readonly record struct StrokeSegment(PointD Start, PointD End);
public sealed record StrokeCoverage(IReadOnlyList<StrokeSegment> Segments, double Radius);
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PointD p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    public RectD Intersect(RectD other) => new(Math.Max(Left, other.Left), Math.Max(Top, other.Top),
        Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}
// Half-open integer canvas pixel bounds: [Left, Right) × [Top, Bottom).
public readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record CoverageSettings
{
    public int CanvasWidth { get; init; } = 2000;
    public int CanvasHeight { get; init; } = 1500;
    public double BrushSize { get; init; } = 20;
    public BrushUnit BrushUnit { get; init; }
    public double Dpi { get; init; } = 300;
    public double ZoomPercent { get; init; } = 50;
    public bool SpecifyBySizeOnScreen { get; init; }
    public double BrushEnvelopeFactor { get; init; } = 1;
    public double AntiAliasRadius { get; init; } = 2;
    public double SafetyRadius { get; init; } = 4;
    public double CoveragePaddingScreenPixels { get; init; } = 12;
    public bool ClipToCanvasBounds { get; init; }
    public double OriginX { get; init; }
    public double OriginY { get; init; }
    public RectD Viewport { get; init; } = new(0, 0, 1920, 1080);
    public InputSpace FileInputSpace { get; init; }
    // Raw ranges must match the driver mapping used during the original capture.
    public RectD RawArea { get; init; } = new(0, 0, 32767, 32767);
    public RectD MappedScreen { get; init; } = new(0, 0, 1920, 1080);
    public bool RawMappingConfirmed { get; init; }
    public int TileSize { get; init; } = 64;
    public int OpacityPercent { get; init; } = 28;
    public bool ShowTiles { get; init; }

    public double Zoom => ZoomPercent / 100;
    public double NominalBrushPixels => BrushUnit == BrushUnit.Mm ? BrushSize * Dpi / 25.4 : BrushSize;
    public double BrushRadius => NominalBrushPixels * BrushEnvelopeFactor / 2 /
        (SpecifyBySizeOnScreen ? Zoom : 1);
    public double CoveragePaddingRadius => CoveragePaddingScreenPixels / Zoom;
    public double Radius => BrushRadius + AntiAliasRadius + SafetyRadius + CoveragePaddingRadius;
    public RectD CanvasOnScreen => new(OriginX, OriginY, OriginX + CanvasWidth * Zoom, OriginY + CanvasHeight * Zoom);
    public RectD VisibleCanvas => ClipToCanvasBounds ? CanvasOnScreen.Intersect(Viewport) : Viewport;

    public void Validate()
    {
        if (CanvasWidth is < 1 or > 100000 || CanvasHeight is < 1 or > 100000)
            throw new ArgumentException("画布宽高须为 1–100000 px。");
        Positive(BrushSize, "笔刷尺寸"); Positive(Dpi, "DPI"); Positive(ZoomPercent, "缩放比例");
        Positive(BrushEnvelopeFactor, "笔刷包络系数");
        if (ZoomPercent > 10000) throw new ArgumentException("缩放比例不能超过 10000%。");
        Nonnegative(AntiAliasRadius, "抗锯齿半径"); Nonnegative(SafetyRadius, "安全半径");
        Nonnegative(CoveragePaddingScreenPixels, "覆盖额外扩展");
        Finite(OriginX, "画布原点 X"); Finite(OriginY, "画布原点 Y");
        CheckRect(Viewport, "可见画布窗口"); CheckRect(RawArea, "原始坐标范围"); CheckRect(MappedScreen, "数位板映射屏幕");
        if (!Enum.IsDefined(BrushUnit) || !Enum.IsDefined(FileInputSpace)) throw new ArgumentException("未知的笔刷单位或坐标类型。");
        if (TileSize is < 4 or > 4096) throw new ArgumentException("矩阵单元尺寸须为 4–4096 px。");
        if (OpacityPercent is < 5 or > 80) throw new ArgumentException("覆盖层不透明度须为 5–80%。");
        Positive(Radius, "总扩展半径");
    }
    private static void Finite(double v, string name) { if (!double.IsFinite(v)) throw new ArgumentException($"{name}必须为有限值。"); }
    private static void Positive(double v, string name) { Finite(v, name); if (v <= 0) throw new ArgumentException($"{name}必须大于 0。"); }
    private static void Nonnegative(double v, string name) { Finite(v, name); if (v < 0) throw new ArgumentException($"{name}不能为负数。"); }
    private static void CheckRect(RectD r, string name)
    {
        Finite(r.Left, name); Finite(r.Top, name); Finite(r.Right, name); Finite(r.Bottom, name);
        Finite(r.Width, name); Finite(r.Height, name);
        if (r.IsEmpty) throw new ArgumentException($"{name}的右／下边界必须大于左／上边界。");
    }

    public PointD ScreenToCanvas(PointD p) => new((p.X - OriginX) / Zoom, (p.Y - OriginY) / Zoom);
    public PointD InputToCanvas(PointD p, InputSpace space) => space switch
    {
        InputSpace.CanvasPixels => p,
        InputSpace.ScreenPixels => ScreenToCanvas(p),
        _ => ScreenToCanvas(new(MappedScreen.Left + (p.X - RawArea.Left) / RawArea.Width * MappedScreen.Width,
            MappedScreen.Top + (p.Y - RawArea.Top) / RawArea.Height * MappedScreen.Height))
    };
    public RectD Project(PixelBox box) => new(OriginX + box.Left * Zoom, OriginY + box.Top * Zoom,
        OriginX + box.Right * Zoom, OriginY + box.Bottom * Zoom);
}

/// <summary>Contact polylines and their bounds. Hover breaks the line without adding coverage.</summary>
public sealed class StrokeExtent
{
    private readonly List<StrokeSegment> _segments = [];
    private PointD? _lastPoint;
    public IReadOnlyList<StrokeSegment> Segments => _segments;
    public int PointCount { get; private set; }
    public double MinX { get; private set; } = double.PositiveInfinity;
    public double MinY { get; private set; } = double.PositiveInfinity;
    public double MaxX { get; private set; } = double.NegativeInfinity;
    public double MaxY { get; private set; } = double.NegativeInfinity;
    public void Add(PointD p, bool inContact = true)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) throw new InvalidDataException("笔迹坐标包含 NaN 或 Infinity。");
        if (!inContact) { _lastPoint = null; return; }
        if (_lastPoint is not PointD last) _segments.Add(new(p, p));
        else if (last != p) _segments.Add(new(last, p));
        _lastPoint = p;
        MinX = Math.Min(MinX, p.X); MinY = Math.Min(MinY, p.Y);
        MaxX = Math.Max(MaxX, p.X); MaxY = Math.Max(MaxY, p.Y); PointCount++;
    }
    public PixelBox? Expand(CoverageSettings settings)
    {
        if (PointCount == 0) return null;
        // Outward rounding includes the pixel containing an exact integer endpoint.
        var r = settings.Radius;
        double minX = settings.ClipToCanvasBounds ? 0 : int.MinValue;
        double minY = settings.ClipToCanvasBounds ? 0 : int.MinValue;
        double maxX = settings.ClipToCanvasBounds ? settings.CanvasWidth : int.MaxValue;
        double maxY = settings.ClipToCanvasBounds ? settings.CanvasHeight : int.MaxValue;
        var left = Math.Clamp(Math.Floor(MinX - r), minX, maxX);
        var top = Math.Clamp(Math.Floor(MinY - r), minY, maxY);
        var right = Math.Clamp(Math.Floor(MaxX + r) + 1, minX, maxX);
        var bottom = Math.Clamp(Math.Floor(MaxY + r) + 1, minY, maxY);
        return right <= left || bottom <= top ? null : new((int)left, (int)top, (int)right, (int)bottom);
    }
}

/// <summary>Sparse row intervals, avoiding a canvas-width × canvas-height allocation.</summary>
public sealed class DirtyTileMatrix
{
    private readonly int _width;
    private readonly int _height;
    public PixelBox Bounds { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int TileSize { get; }
    public DirtyTileMatrix(int width, int height, int tileSize) : this(new PixelBox(0, 0, width, height), tileSize) { }
    public DirtyTileMatrix(PixelBox bounds, int tileSize)
    {
        long width = (long)bounds.Right - bounds.Left, height = (long)bounds.Bottom - bounds.Top;
        if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue || tileSize <= 0)
            throw new ArgumentException("矩阵范围或单元尺寸无效。");
        Bounds = bounds; _width = (int)width; _height = (int)height; TileSize = tileSize;
        Columns = (int)((width + tileSize - 1) / tileSize); Rows = (int)((height + tileSize - 1) / tileSize);
    }
    public static DirtyTileMatrix ForCoverage(CoverageSettings settings, IEnumerable<PixelBox> strokeBounds)
    {
        var bounds = new PixelBox(0, 0, settings.CanvasWidth, settings.CanvasHeight);
        if (!settings.ClipToCanvasBounds)
        {
            foreach (var box in strokeBounds)
                bounds = new(Math.Min(bounds.Left, box.Left), Math.Min(bounds.Top, box.Top),
                    Math.Max(bounds.Right, box.Right), Math.Max(bounds.Bottom, box.Bottom));
            // Keep the grid aligned with canvas (0,0), even as coverage extends left or up.
            bounds = bounds with { Left = checked((int)(Math.Floor((double)bounds.Left / settings.TileSize) * settings.TileSize)),
                Top = checked((int)(Math.Floor((double)bounds.Top / settings.TileSize) * settings.TileSize)) };
        }
        return new(bounds, settings.TileSize);
    }
    private readonly SortedDictionary<int, List<(int Start, int End)>> _rows = [];
    public long DirtyCellCount => _rows.Values.Sum(r => r.Sum(s => (long)s.End - s.Start));
    public IEnumerable<(int Row, int Start, int End)> Runs => _rows.SelectMany(r => r.Value.Select(s => (r.Key, s.Start, s.End)));
    public void Add(PixelBox box)
    {
        box = new(Math.Max(Bounds.Left, box.Left), Math.Max(Bounds.Top, box.Top),
            Math.Min(Bounds.Right, box.Right), Math.Min(Bounds.Bottom, box.Bottom));
        if (box.Width <= 0 || box.Height <= 0) return;
        box = new(box.Left - Bounds.Left, box.Top - Bounds.Top, box.Right - Bounds.Left, box.Bottom - Bounds.Top);
        int firstColumn = box.Left / TileSize, lastColumnExclusive = (box.Right - 1) / TileSize + 1;
        for (int row = Math.Max(0, box.Top / TileSize); row < Math.Min(Rows, (box.Bottom - 1) / TileSize + 1); row++)
            AddRun(row, firstColumn, lastColumnExclusive);
    }
    public void AddCoverage(StrokeCoverage coverage)
    {
        foreach (var segment in coverage.Segments) Add(segment, coverage.Radius);
    }
    /// <summary>Scan the swept disk one tile row at a time, without filling the segment's bounding box.</summary>
    public void Add(StrokeSegment segment, double radius)
    {
        if (!double.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var a = new PointD(segment.Start.X - Bounds.Left, segment.Start.Y - Bounds.Top);
        var b = new PointD(segment.End.X - Bounds.Left, segment.End.Y - Bounds.Top);
        var minY = Math.Min(a.Y, b.Y) - radius; var maxY = Math.Max(a.Y, b.Y) + radius;
        if (maxY < 0 || minY >= _height || Math.Max(a.X, b.X) + radius < 0 || Math.Min(a.X, b.X) - radius >= _width) return;
        int firstRow = (int)Math.Clamp(Math.Floor(minY / TileSize), 0, Rows);
        int endRow = (int)Math.Clamp(Math.Floor(maxY / TileSize) + 1, 0, Rows);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        var offset = length == 0 ? new PointD(0, 0) : new PointD(-dy / length * radius, dx / length * radius);
        PointD[] body = [new(a.X + offset.X, a.Y + offset.Y), new(b.X + offset.X, b.Y + offset.Y),
            new(b.X - offset.X, b.Y - offset.Y), new(a.X - offset.X, a.Y - offset.Y)];
        for (int row = firstRow; row < endRow; row++)
        {
            double top = (double)row * TileSize, bottom = Math.Min(_height, ((double)row + 1) * TileSize);
            double left = double.PositiveInfinity, right = double.NegativeInfinity;
            IncludeDisk(a); IncludeDisk(b);
            for (int i = 0; i < body.Length; i++)
            {
                var p = body[i]; var q = body[(i + 1) % body.Length];
                if (p.Y >= top && p.Y <= bottom) Include(p.X);
                if (p.Y == q.Y) continue;
                IncludeCrossing(top); IncludeCrossing(bottom);
                void IncludeCrossing(double y)
                {
                    if (y < Math.Min(p.Y, q.Y) || y > Math.Max(p.Y, q.Y)) return;
                    Include(p.X + (q.X - p.X) * ((y - p.Y) / (q.Y - p.Y)));
                }
            }
            if (left > right || right < 0 || left >= _width) continue;
            int start = (int)Math.Clamp(Math.Floor(left / TileSize), 0, Columns);
            int end = (int)Math.Clamp(Math.Floor(right / TileSize) + 1, 0, Columns);
            AddRun(row, start, end);

            void Include(double x) { left = Math.Min(left, x); right = Math.Max(right, x); }
            void IncludeDisk(PointD center)
            {
                double distance = Math.Max(0, Math.Max(top - center.Y, center.Y - bottom));
                if (distance > radius) return;
                double reach = Math.Sqrt(Math.Max(0, (radius - distance) * (radius + distance)));
                Include(center.X - reach); Include(center.X + reach);
            }
        }
    }
    private void AddRun(int row, int start, int end)
    {
        if (start >= end) return;
        if (!_rows.TryGetValue(row, out var list)) _rows[row] = list = [];
        int i = 0;
        while (i < list.Count && list[i].End < start) i++;
        int first = i;
        while (i < list.Count && list[i].Start <= end)
        {
            start = Math.Min(start, list[i].Start); end = Math.Max(end, list[i].End); i++;
        }
        if (i > first) list.RemoveRange(first, i - first);
        list.Insert(first, (start, end));
    }
    public IEnumerable<PixelBox> Boxes => Runs.Select(r => new PixelBox(
        checked((int)((long)Bounds.Left + (long)r.Start * TileSize)), checked((int)((long)Bounds.Top + (long)r.Row * TileSize)),
        (int)Math.Min(Bounds.Right, (long)Bounds.Left + (long)r.End * TileSize),
        (int)Math.Min(Bounds.Bottom, (long)Bounds.Top + ((long)r.Row + 1) * TileSize)));
}
