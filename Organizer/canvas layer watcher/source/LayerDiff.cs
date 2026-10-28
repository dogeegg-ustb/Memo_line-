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
    PenDownLocation? PenDownLocation = null)
{
    public CoverageSettings? CoverageSettings { get; init; }
}
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
    private static IEnumerable<int> Cells(DirtyTileMatrix matrix) => matrix.Runs.SelectMany(r =>
        Enumerable.Range(r.Start, r.End - r.Start).Select(c => r.Row * matrix.Columns + c));
    internal static ImageDirtyLabel[] Describe(DirtyEvidence evidence, Size size)
        => evidence.Labels.Select(label =>
        {
            var m = new DirtyTileMatrix(size.Width, size.Height, TileSize); m.AddCoverage(label.Coverage);
            return new ImageDirtyLabel(label.Id, label.Source, label.OperationId, label.FromTicks, label.ToTicks, label.StateIds,
                label.Coverage, MatrixData.From(m, size), label.Warnings, [], label.PenDownLocation)
                { CoverageSettings = label.CoverageSettings };
        }).ToArray();
    internal static MatrixData Predicted(DirtyEvidence evidence, Size size)
    {
        var matrix = new DirtyTileMatrix(size.Width, size.Height, TileSize);
        foreach (var label in evidence.Labels) matrix.AddCoverage(label.Coverage);
        return MatrixData.From(matrix, size);
    }

    public static LayerDiffResult Compare(string afterPath, string nowPath, DirtyEvidence evidence, string outputDirectory, CancellationToken token)
    {
        // The decodes dominate a large compare: run both at once, and read the decoded bitmaps through the same
        // 32bpp ARGB lock that Load copies from, instead of copying ~140 MB per A4 canvas first.
        var decodingAfter = Task.Run(() => (Bitmap)Image.FromFile(afterPath));
        Bitmap decodedNow;
        try { decodedNow = (Bitmap)Image.FromFile(nowPath); }
        catch { _ = decodingAfter.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion); throw; }
        using var now = decodedNow; using var after = decodingAfter.GetAwaiter().GetResult();
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
        // Keep the recognizer prediction as ownership metadata. Full
        // resolution scanning below still visits every tile, including tiles
        // outside this set.
        var predicted = Cells(matrix).ToHashSet();
        Directory.CreateDirectory(outputDirectory);
        using var fullAfter = new Pixels(after, ImageLockMode.ReadOnly); using var fullNow = new Pixels(now, ImageLockMode.ReadOnly);
        var changedCells = new Dictionary<int, (PixelBox Box, long Count)>(); long compared = 0;
        // Every tile participates in the same full-resolution comparison. The
        // predicted matrix remains evidence metadata and label ownership only;
        // it is never used to skip pixels because edits can occur outside the
        // recognizer's predicted coverage.
        // Rows are scanned whole, one band of tiles at a time: identical bytes are never Different, so one
        // vectorized compare clears an unchanged row, and only differing tile rows are visited pixel by pixel.
        int columns = matrix.Columns, rowBytes = size.Width * 4;
        var minXs = new int[columns]; var minYs = new int[columns]; var maxXs = new int[columns]; var maxYs = new int[columns];
        var counts = new long[columns];
        for (int cy = 0; cy < matrix.Rows; cy++)
        {
            token.ThrowIfCancellationRequested();
            int top = cy * TileSize, bottom = Math.Min(size.Height, top + TileSize);
            Array.Clear(counts);
            for (int y = top; y < bottom; y++)
            {
                if (new ReadOnlySpan<byte>(fullAfter.At(0, y), rowBytes).SequenceEqual(new ReadOnlySpan<byte>(fullNow.At(0, y), rowBytes))) continue;
                for (int cx = 0; cx < columns; cx++)
                {
                    int left = cx * TileSize, right = Math.Min(size.Width, left + TileSize);
                    if (new ReadOnlySpan<byte>(fullAfter.At(left, y), (right - left) * 4).SequenceEqual(new ReadOnlySpan<byte>(fullNow.At(left, y), (right - left) * 4))) continue;
                    for (int x = left; x < right; x++)
                    {
                        if (!Different(fullAfter.At(x, y), fullNow.At(x, y))) continue;
                        if (counts[cx]++ == 0) { minXs[cx] = maxXs[cx] = x; minYs[cx] = y; }
                        minXs[cx] = Math.Min(minXs[cx], x); maxXs[cx] = Math.Max(maxXs[cx], x + 1); maxYs[cx] = y + 1;
                    }
                }
            }
            for (int cx = 0; cx < columns; cx++)
                if (counts[cx] > 0) changedCells[cy * columns + cx] = (new(minXs[cx], minYs[cx], maxXs[cx], maxYs[cx]), counts[cx]);
        }
        compared = (long)size.Width * size.Height;
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
            // All four patches in one pass, written by FastPng (the same BGRA pixels GDI+ saved, ~10x faster).
            var patches = new byte[files.Length][];
            for (int kind = 0; kind < files.Length; kind++) patches[kind] = new byte[checked(bounds.Width * bounds.Height * 4)];
            fixed (byte* afterPatch = patches[0], nowPatch = patches[1], maskPatch = patches[2], differencePatch = patches[3])
                for (int y = bounds.Top; y < bounds.Bottom; y++)
                {
                    if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                    for (int x = bounds.Left; x < bounds.Right; x++)
                    {
                        int cell = y / TileSize * matrix.Columns + x / TileSize;
                        byte* a = fullAfter.At(x, y), b = fullNow.At(x, y);
                        if (!component.Contains(cell) || !Different(a, b)) continue;
                        long offset = ((long)(y - bounds.Top) * bounds.Width + (x - bounds.Left)) * 4;
                        *(uint*)(afterPatch + offset) = *(uint*)a;
                        *(uint*)(nowPatch + offset) = *(uint*)b;
                        *(uint*)(maskPatch + offset) = 0xffffffff;
                        byte* difference = differencePatch + offset;
                        for (int c = 0; c < 3; c++) difference[c] = (byte)Math.Max(Math.Abs(a[c] - b[c]), Math.Abs(a[3] - b[3]));
                        difference[3] = 255;
                    }
                }
            for (int kind = 0; kind < files.Length; kind++)
            {
                token.ThrowIfCancellationRequested();
                File.WriteAllBytes(Path.Combine(outputDirectory, files[kind]), FastPng.Encode(bounds.Width, bounds.Height, patches[kind], bgra: true));
            }
            images.Add(new(id, bounds, component.Sum(c => changedCells[c].Count), files[0], files[1], files[2], files[3], ids.Order().ToArray()));
        }
        var resultLabels = labelList.Select(label => label with { ImageIds = images.Where(image => image.LabelIds.Contains(label.Id)).Select(image => image.Id).ToArray() }).ToArray();
        return new(MatrixData.From(matrix, size), resultLabels, images.ToArray(), images.Sum(i => i.ChangedPixels), compared,
            0, 0, 0);
    }
}
