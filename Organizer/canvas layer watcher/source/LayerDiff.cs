using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DirtyMatrix.Core;
using BehaviorRecognizer.Storage.Memoline;

namespace CanvasLayerWatcher;

internal sealed record MatrixRun(int Row, int StartColumn, int EndColumnExclusive);
internal sealed record MatrixData(int CanvasWidth, int CanvasHeight, int TileSize, int OriginX, int OriginY, MatrixRun[] RowRuns)
{
    public static MatrixData From(DirtyTileMatrix matrix, Size size) => new(size.Width, size.Height, matrix.TileSize,
        matrix.Bounds.Left, matrix.Bounds.Top, matrix.Runs.Select(r => new MatrixRun(r.Row, r.Start, r.End)).ToArray());
}
internal sealed record ImageDirtyLabel(string Id, string Source, long? OperationId, long FromTicks, long ToTicks,
    string[] StateIds, StrokeCoverage? Coverage, MatrixData ImpactRange, string[] Warnings, string[] ImageIds,
    PenDownLocation? PenDownLocation = null);
internal sealed record DiffImage(string Id, PixelBox Bounds, long ChangedPixels, string AfterImage, string NowImage,
    string MaskImage, string DifferenceImage, string[] LabelIds)
{
    // One primary diff image: the original now pixels only where after/now
    // differ. Before pixels and mask are auxiliary data for inspecting erasure.
    public string Image => NowImage;
}
internal sealed record LayerDiffResult(MatrixData PredictedMatrix, ImageDirtyLabel[] Labels, DiffImage[] Images,
    long ChangedPixels, long FullResolutionComparedPixels, long CoarseComparedPixels, int CoarseWidth, int CoarseHeight);

internal static unsafe class LayerDiff
{
    internal const int TileSize = 64, CoarseFactor = 8;
    private sealed class Pixels : IDisposable
    {
        private readonly Bitmap _image;
        private readonly BitmapData _data;
        public Pixels(Bitmap image, ImageLockMode mode)
        { _image = image; _data = image.LockBits(new(Point.Empty, image.Size), mode, PixelFormat.Format32bppArgb); }
        public byte* At(int x, int y) => (byte*)_data.Scan0 + y * _data.Stride + x * 4;
        public void Dispose() => _image.UnlockBits(_data);
    }
    internal static Bitmap Load(string path)
    {
        using var original = (Bitmap)Image.FromFile(path);
        var result = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
        using var source = new Pixels(original, ImageLockMode.ReadOnly);
        using var destination = new Pixels(result, ImageLockMode.WriteOnly);
        for (int y = 0; y < result.Height; y++)
            Buffer.MemoryCopy(source.At(0, y), destination.At(0, y), (long)result.Width * 4, (long)result.Width * 4);
        return result;
    }
    private static bool Different(byte* a, byte* b) => (a[3] != 0 || b[3] != 0) && *(uint*)a != *(uint*)b;
    private static Bitmap Small(Bitmap image)
    {
        var result = new Bitmap((image.Width + CoarseFactor - 1) / CoarseFactor, (image.Height + CoarseFactor - 1) / CoarseFactor, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result); g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(image, new Rectangle(Point.Empty, result.Size), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        return result;
    }
    private static IEnumerable<int> Cells(DirtyTileMatrix matrix) => matrix.Runs.SelectMany(r =>
        Enumerable.Range(r.Start, r.End - r.Start).Select(c => r.Row * matrix.Columns + c));
    internal static ImageDirtyLabel[] Describe(DirtyEvidence evidence, Size size)
        => evidence.Labels.Select(label =>
        {
            var m = new DirtyTileMatrix(size.Width, size.Height, TileSize); m.AddCoverage(label.Coverage);
            return new ImageDirtyLabel(label.Id, label.Source, label.OperationId, label.FromTicks, label.ToTicks, label.StateIds,
                label.Coverage, MatrixData.From(m, size), label.Warnings, [], label.PenDownLocation);
        }).ToArray();
    internal static MatrixData Predicted(DirtyEvidence evidence, Size size)
    {
        var matrix = new DirtyTileMatrix(size.Width, size.Height, TileSize);
        foreach (var label in evidence.Labels) matrix.AddCoverage(label.Coverage);
        return MatrixData.From(matrix, size);
    }

    public static LayerDiffResult Compare(string afterPath, string nowPath, DirtyEvidence evidence, string outputDirectory, CancellationToken token)
    {
        using var after = Load(afterPath); using var now = Load(nowPath);
        if (after.Size != now.Size) throw new InvalidDataException("after/now 的完整画布尺寸不一致");
        var size = now.Size; var matrix = new DirtyTileMatrix(size.Width, size.Height, TileSize);
        var labelList = Describe(evidence, size).ToList();
        var owners = new Dictionary<int, HashSet<string>>();
        foreach (var label in evidence.Labels)
        {
            matrix.AddCoverage(label.Coverage);
            var own = new DirtyTileMatrix(size.Width, size.Height, TileSize); own.AddCoverage(label.Coverage);
            foreach (int cell in Cells(own))
            { if (!owners.TryGetValue(cell, out var ids)) owners[cell] = ids = []; ids.Add(label.Id); }
        }
        var predicted = Cells(matrix).ToHashSet(); var search = new HashSet<int>(predicted);
        using var smallAfter = Small(after); using var smallNow = Small(now);
        long coarseCompared = 0;
        using (var a = new Pixels(smallAfter, ImageLockMode.ReadOnly))
        using (var b = new Pixels(smallNow, ImageLockMode.ReadOnly))
            for (int row = 0; row < matrix.Rows; row++)
                for (int col = 0; col < matrix.Columns; col++)
                {
                    token.ThrowIfCancellationRequested(); int cell = row * matrix.Columns + col;
                    if (predicted.Contains(cell)) continue;
                    int left = col * TileSize * smallNow.Width / size.Width, top = row * TileSize * smallNow.Height / size.Height;
                    int right = (int)Math.Ceiling((double)Math.Min(size.Width, (col + 1) * TileSize) * smallNow.Width / size.Width);
                    int bottom = (int)Math.Ceiling((double)Math.Min(size.Height, (row + 1) * TileSize) * smallNow.Height / size.Height);
                    bool changed = false;
                    for (int y = top; y < bottom && !changed; y++)
                        for (int x = left; x < right; x++)
                        { coarseCompared++; if (Different(a.At(x, y), b.At(x, y))) { changed = true; break; } }
                    if (changed) search.Add(cell);
                }
        Directory.CreateDirectory(outputDirectory);
        using var fullAfter = new Pixels(after, ImageLockMode.ReadOnly); using var fullNow = new Pixels(now, ImageLockMode.ReadOnly);
        var changedCells = new Dictionary<int, (PixelBox Box, long Count)>(); long compared = 0;
        foreach (int cell in search.Order())
        {
            token.ThrowIfCancellationRequested(); int cx = cell % matrix.Columns, cy = cell / matrix.Columns;
            int left = cx * TileSize, top = cy * TileSize, right = Math.Min(size.Width, left + TileSize), bottom = Math.Min(size.Height, top + TileSize);
            int minX = right, minY = bottom, maxX = left, maxY = top; long changed = 0;
            for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
            {
                compared++;
                if (!Different(fullAfter.At(x, y), fullNow.At(x, y))) continue;
                changed++; minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x + 1); maxY = Math.Max(maxY, y + 1);
            }
            if (changed > 0) changedCells[cell] = (new(minX, minY, maxX, maxY), changed);
        }
        var remaining = changedCells.Keys.ToHashSet(); var images = new List<DiffImage>();
        while (remaining.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var component = new HashSet<int>(); var queue = new Queue<int>();
            int first = remaining.Min(); remaining.Remove(first); queue.Enqueue(first);
            while (queue.TryDequeue(out int cell))
            {
                component.Add(cell); int x = cell % matrix.Columns, y = cell / matrix.Columns;
                foreach (int neighbour in new[] { x > 0 ? cell - 1 : -1, x + 1 < matrix.Columns ? cell + 1 : -1,
                    y > 0 ? cell - matrix.Columns : -1, y + 1 < matrix.Rows ? cell + matrix.Columns : -1 })
                    if (remaining.Remove(neighbour)) queue.Enqueue(neighbour);
            }
            var bounds = new PixelBox(component.Min(c => changedCells[c].Box.Left), component.Min(c => changedCells[c].Box.Top),
                component.Max(c => changedCells[c].Box.Right), component.Max(c => changedCells[c].Box.Bottom));
            string id = "image-" + (images.Count + 1).ToString("D4");
            var ids = component.Where(owners.ContainsKey).SelectMany(c => owners[c]).ToHashSet();
            if (component.Any(c => !predicted.Contains(c)))
            {
                string fallbackId = "coarse-" + id; ids.Add(fallbackId);
                var impact = new DirtyTileMatrix(size.Width, size.Height, TileSize);
                foreach (int cell in component.Where(c => !predicted.Contains(c)))
                    impact.Add(new(cell % matrix.Columns * TileSize, cell / matrix.Columns * TileSize,
                        Math.Min(size.Width, (cell % matrix.Columns + 1) * TileSize), Math.Min(size.Height, (cell / matrix.Columns + 1) * TileSize)));
                labelList.Add(new(fallbackId, "low-resolution-fallback", null, evidence.Recognizer.FromTicks, evidence.Recognizer.ToTicks,
                    evidence.Recognizer.CaptureStateIds, null, MatrixData.From(impact, size), ["outsidePredictedStrokeCoverage"], [id]));
            }
            string[] files = [id + "-after.png", id + "-now.png", id + "-mask.png", id + "-difference.png"];
            for (int kind = 0; kind < files.Length; kind++)
            {
                token.ThrowIfCancellationRequested(); using var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var pixels = new Pixels(image, ImageLockMode.WriteOnly))
                    for (int y = bounds.Top; y < bounds.Bottom; y++)
                    {
                        if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                        for (int x = bounds.Left; x < bounds.Right; x++)
                        {
                            int cell = y / TileSize * matrix.Columns + x / TileSize;
                            byte* a = fullAfter.At(x, y), b = fullNow.At(x, y), target = pixels.At(x - bounds.Left, y - bounds.Top);
                            if (!component.Contains(cell) || !Different(a, b)) { *(uint*)target = 0; continue; }
                            if (kind == 0) *(uint*)target = *(uint*)a;
                            else if (kind == 1) *(uint*)target = *(uint*)b;
                            else if (kind == 2) *(uint*)target = 0xffffffff;
                            else { for (int c = 0; c < 3; c++) target[c] = (byte)Math.Max(Math.Abs(a[c] - b[c]), Math.Abs(a[3] - b[3])); target[3] = 255; }
                        }
                    }
                image.Save(Path.Combine(outputDirectory, files[kind]), ImageFormat.Png);
            }
            images.Add(new(id, bounds, component.Sum(c => changedCells[c].Count), files[0], files[1], files[2], files[3], ids.Order().ToArray()));
        }
        var resultLabels = labelList.Select(label => label with { ImageIds = images.Where(image => image.LabelIds.Contains(label.Id)).Select(image => image.Id).ToArray() }).ToArray();
        return new(MatrixData.From(matrix, size), resultLabels, images.ToArray(), images.Sum(i => i.ChangedPixels), compared,
            coarseCompared, smallNow.Width, smallNow.Height);
    }
}
