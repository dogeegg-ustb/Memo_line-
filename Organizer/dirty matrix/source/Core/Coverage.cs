namespace DirtyMatrix.Core;

public enum BrushUnit { Px, Mm }
public enum InputSpace { TabletRaw, CanvasPixels, ScreenPixels }
public readonly record struct PointD(double X, double Y);
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
    public double Radius => BrushRadius + AntiAliasRadius + SafetyRadius;
    public RectD CanvasOnScreen => new(OriginX, OriginY, OriginX + CanvasWidth * Zoom, OriginY + CanvasHeight * Zoom);
    public RectD VisibleCanvas => CanvasOnScreen.Intersect(Viewport);

    public void Validate()
    {
        if (CanvasWidth is < 1 or > 100000 || CanvasHeight is < 1 or > 100000)
            throw new ArgumentException("画布宽高须为 1–100000 px。");
        Positive(BrushSize, "笔刷尺寸"); Positive(Dpi, "DPI"); Positive(ZoomPercent, "缩放比例");
        Positive(BrushEnvelopeFactor, "笔刷包络系数");
        if (ZoomPercent > 10000) throw new ArgumentException("缩放比例不能超过 10000%。");
        Nonnegative(AntiAliasRadius, "抗锯齿半径"); Nonnegative(SafetyRadius, "安全半径");
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

/// <summary>Stores only contact-point extrema. Hover never contributes to a stroke.</summary>
public sealed class StrokeExtent
{
    public int PointCount { get; private set; }
    public double MinX { get; private set; } = double.PositiveInfinity;
    public double MinY { get; private set; } = double.PositiveInfinity;
    public double MaxX { get; private set; } = double.NegativeInfinity;
    public double MaxY { get; private set; } = double.NegativeInfinity;
    public void Add(PointD p, bool inContact = true)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) throw new InvalidDataException("笔迹坐标包含 NaN 或 Infinity。");
        if (!inContact) return;
        MinX = Math.Min(MinX, p.X); MinY = Math.Min(MinY, p.Y);
        MaxX = Math.Max(MaxX, p.X); MaxY = Math.Max(MaxY, p.Y); PointCount++;
    }
    public PixelBox? Expand(CoverageSettings settings)
    {
        if (PointCount == 0) return null;
        // Outward rounding includes the pixel containing an exact integer endpoint.
        var r = settings.Radius;
        var left = Math.Clamp(Math.Floor(MinX - r), 0, settings.CanvasWidth);
        var top = Math.Clamp(Math.Floor(MinY - r), 0, settings.CanvasHeight);
        var right = Math.Clamp(Math.Floor(MaxX + r) + 1, 0, settings.CanvasWidth);
        var bottom = Math.Clamp(Math.Floor(MaxY + r) + 1, 0, settings.CanvasHeight);
        return right <= left || bottom <= top ? null : new((int)left, (int)top, (int)right, (int)bottom);
    }
}

/// <summary>Sparse row intervals, avoiding a canvas-width × canvas-height allocation.</summary>
public sealed class DirtyTileMatrix(int width, int height, int tileSize)
{
    public int Columns { get; } = (width + tileSize - 1) / tileSize;
    public int Rows { get; } = (height + tileSize - 1) / tileSize;
    public int TileSize { get; } = tileSize;
    private readonly SortedDictionary<int, List<(int Start, int End)>> _rows = [];
    public long DirtyCellCount => _rows.Values.Sum(r => r.Sum(s => (long)s.End - s.Start));
    public IEnumerable<(int Row, int Start, int End)> Runs => _rows.SelectMany(r => r.Value.Select(s => (r.Key, s.Start, s.End)));
    public void Add(PixelBox box)
    {
        box = new(Math.Max(0, box.Left), Math.Max(0, box.Top), Math.Min(width, box.Right), Math.Min(height, box.Bottom));
        if (box.Width <= 0 || box.Height <= 0) return;
        int firstColumn = box.Left / TileSize, lastColumnExclusive = (box.Right - 1) / TileSize + 1;
        for (int row = Math.Max(0, box.Top / TileSize); row < Math.Min(Rows, (box.Bottom - 1) / TileSize + 1); row++)
        {
            int start = firstColumn, end = lastColumnExclusive;
            if (!_rows.TryGetValue(row, out var list)) _rows[row] = list = [];
            int i = 0;
            while (i < list.Count && list[i].End < start) i++;
            while (i < list.Count && list[i].Start <= end)
            {
                start = Math.Min(start, list[i].Start); end = Math.Max(end, list[i].End); list.RemoveAt(i);
            }
            list.Insert(i, (start, end));
        }
    }
    public IEnumerable<PixelBox> Boxes => Runs.Select(r => new PixelBox(r.Start * TileSize, r.Row * TileSize,
        Math.Min(width, r.End * TileSize), Math.Min(height, (r.Row + 1) * TileSize)));
}
