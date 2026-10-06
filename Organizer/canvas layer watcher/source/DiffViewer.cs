using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using DirtyMatrix.Core;

namespace CanvasLayerWatcher;

internal enum DiffDisplayMode { Difference, After, Now, Mask }
internal sealed record DiffViewPacket(string Directory, bool Baseline, Size Canvas, string LayerName,
    DiffImage[] Images, ImageDirtyLabel[] Labels, RecognizerState[] States, string? CanvasImagePath = null)
{
    public static DiffViewPacket Read(string manifest)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifest)); var data = document.RootElement;
        if (J.Text(data, "schema") != "dirty-matrix-image-diff/v1") throw new InvalidDataException("请选择本程序生成的图像差异包 manifest.json");
        bool baseline = J.Text(data, "kind") == "baseline";
        var snapshot = J.Get(data, baseline ? "after" : "now");
        int width = checked((int)J.Tick(snapshot, "width", 0)), height = checked((int)J.Tick(snapshot, "height", 0));
        if (width <= 0 || height <= 0 || (long)width * height > 64_000_000) throw new InvalidDataException("差异包的画布尺寸无效");
        var images = J.Get(data, "images").Deserialize<DiffImage[]>(SnapshotHistory.Json) ?? [];
        var labels = J.Get(data, "labels").Deserialize<ImageDirtyLabel[]>(SnapshotHistory.Json) ?? [];
        var states = J.Get(J.Get(data, "recognizer"), "states").Deserialize<RecognizerState[]>(SnapshotHistory.Json) ?? [];
        if (images.Any(i => i.Bounds.Width <= 0 || i.Bounds.Height <= 0 || i.Bounds.Left < 0 || i.Bounds.Top < 0
            || i.Bounds.Right > width || i.Bounds.Bottom > height)) throw new InvalidDataException("差异图像范围超出了完整画布");
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        string? preview = J.Text(data, "canvasPreviewImage");
        string? canvasImage = preview is null ? CurrentSnapshot(directory, J.Text(snapshot, "id")) : Resolve(directory, preview);
        return new(directory, baseline, new(width, height),
            J.Text(J.Get(snapshot, "layer"), "name") ?? "图层", images, labels, states, canvasImage);
    }
    public string ImagePath(DiffImage image, DiffDisplayMode mode)
    {
        string file = mode switch { DiffDisplayMode.After => image.AfterImage, DiffDisplayMode.Now => image.Image,
            DiffDisplayMode.Mask => image.MaskImage, _ => image.DifferenceImage };
        return Resolve(Directory, file);
    }
    private static string Resolve(string directory, string file)
    {
        string path = Path.GetFullPath(Path.Combine(directory, file));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("差异图像路径超出了差异包目录");
        return path;
    }
    private static string? CurrentSnapshot(string directory, string? id)
    {
        // Old packets have no thumbnail. Use a retained full snapshot only
        // when its ID matches this packet, never a newer layer image.
        if (id is null || Path.GetFileName(Path.GetDirectoryName(directory)) != "packets") return null;
        string root = Path.GetDirectoryName(Path.GetDirectoryName(directory))!;
        try
        {
            var index = JsonSerializer.Deserialize<SnapshotHistoryIndex>(File.ReadAllText(Path.Combine(root, "current.json")), SnapshotHistory.Json);
            var snapshots = new[] { index?.After, index?.Now }.Concat(index?.LayerStacks?.Values
                .SelectMany(pair => new[] { pair.After, pair.Now }) ?? []);
            var snapshot = snapshots.FirstOrDefault(s => s?.Id == id);
            if (snapshot is null) return null;
            string path = Resolve(root, snapshot.ImageFile);
            return path.StartsWith(Path.Combine(root, "snapshots") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public ImageDirtyLabel[] LabelsFor(DiffImage? image) => Labels.Where(l => image is null
        ? l.ImageIds.Length > 0 : image.LabelIds.Contains(l.Id)).ToArray();
    public Rectangle[] ImpactBoxes(DiffImage? image) => LabelsFor(image).SelectMany(label =>
    {
        var matrix = label.ImpactRange;
        if (matrix.TileSize <= 0) throw new InvalidDataException("影响范围矩阵的单元尺寸无效");
        return matrix.RowRuns.Select(run =>
        {
            long left = matrix.OriginX + (long)run.StartColumn * matrix.TileSize;
            long top = matrix.OriginY + (long)run.Row * matrix.TileSize;
            long right = matrix.OriginX + (long)run.EndColumnExclusive * matrix.TileSize;
            long bottom = top + matrix.TileSize;
            return Rectangle.FromLTRB((int)Math.Clamp(left, 0, Canvas.Width), (int)Math.Clamp(top, 0, Canvas.Height),
                (int)Math.Clamp(right, 0, Canvas.Width), (int)Math.Clamp(bottom, 0, Canvas.Height));
        }).Where(box => box.Width > 0 && box.Height > 0);
    }).Distinct().ToArray();
    internal static Rectangle Box(PixelBox b) => Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom);
}

internal sealed record DiffDisplayFrame(Bitmap Image, Rectangle CanvasBounds, Rectangle ImageBounds);

internal static class DiffDisplay
{
    internal const int OverviewEdge = CanvasThumbnail.Edge;
    public static DiffDisplayFrame Render(DiffViewPacket packet, int selection, DiffDisplayMode mode, CancellationToken token)
    {
        if (selection > 0)
        {
            var selected = packet.Images[selection - 1]; var bounds = DiffViewPacket.Box(selected.Bounds);
            // Focus on the selected change, even when one stroke's predicted
            // matrix spans most of the canvas. Overview shows the full matrix.
            var context = bounds; context.Inflate(LayerDiff.TileSize / 2, LayerDiff.TileSize / 2);
            context.Intersect(new Rectangle(Point.Empty, packet.Canvas));
            token.ThrowIfCancellationRequested();
            var image = Load(packet, selected, mode);
            return new(image, context, bounds);
        }
        double scale = Math.Min(1, (double)OverviewEdge / Math.Max(packet.Canvas.Width, packet.Canvas.Height));
        var overview = new Bitmap(Math.Max(1, (int)Math.Ceiling(packet.Canvas.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(packet.Canvas.Height * scale)), PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(overview); graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            // Transparent bounding-box pixels from one patch must not erase a
            // different connected component whose bounding box overlaps it.
            graphics.CompositingMode = CompositingMode.SourceOver;
            foreach (var patch in packet.Images)
            {
                token.ThrowIfCancellationRequested(); using var image = Load(packet, patch, mode);
                var b = patch.Bounds;
                graphics.DrawImage(image, new RectangleF((float)(b.Left * scale), (float)(b.Top * scale),
                    (float)(b.Width * scale), (float)(b.Height * scale)));
            }
            var canvas = new Rectangle(Point.Empty, packet.Canvas);
            return new(overview, canvas, canvas);
        }
        catch { overview.Dispose(); throw; }
    }
    public static DiffDisplayFrame Focus(DiffViewPacket packet, int selection, DiffDisplayMode mode, CancellationToken token)
    {
        if (selection > 0)
        {
            var patch = packet.Images[selection - 1]; var bounds = DiffViewPacket.Box(patch.Bounds);
            token.ThrowIfCancellationRequested();
            return new(Load(packet, patch, mode), Context(bounds, packet.Canvas), bounds);
        }
        // All changes still occupy their original relative positions, but the
        // large view fits their union rather than the empty full canvas.
        var union = packet.Images.Select(i => DiffViewPacket.Box(i.Bounds)).Aggregate(Rectangle.Union);
        var image = new Bitmap(union.Width, union.Height, PixelFormat.Format32bppArgb);
        try
        {
            foreach (var patch in packet.Images)
            {
                token.ThrowIfCancellationRequested(); using var pixels = Load(packet, patch, mode);
                CopyPixels(pixels, image, patch.Bounds.Left - union.Left, patch.Bounds.Top - union.Top, token);
            }
            return new(image, Context(union, packet.Canvas), union);
        }
        catch { image.Dispose(); throw; }
    }
    private static unsafe void CopyPixels(Bitmap source, Bitmap destination, int left, int top, CancellationToken token)
    {
        var read = source.LockBits(new(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var write = destination.LockBits(new(Point.Empty, destination.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < source.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var a = (uint*)((byte*)read.Scan0 + y * read.Stride);
                    var b = (uint*)((byte*)write.Scan0 + (y + top) * write.Stride) + left;
                    for (int x = 0; x < source.Width; x++) if ((a[x] >> 24) != 0) b[x] = a[x];
                }
            }
            finally { destination.UnlockBits(write); }
        }
        finally { source.UnlockBits(read); }
    }
    private static Rectangle Context(Rectangle bounds, Size canvas)
    {
        var context = bounds; int padding = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 10, 2, 24);
        context.Inflate(padding, padding); context.Intersect(new Rectangle(Point.Empty, canvas)); return context;
    }
    public static DiffDisplayFrame Locator(DiffViewPacket packet, CancellationToken token)
    {
        if (packet.CanvasImagePath is { } path)
        {
            try
            {
                var canvas = new Rectangle(Point.Empty, packet.Canvas);
                return new(CanvasThumbnail.Load(path, packet.Canvas, token), canvas, canvas);
            }
            // A retained legacy snapshot can rotate out during rendering.
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return Render(packet, 0, DiffDisplayMode.Now, token);
    }
    private static Bitmap Load(DiffViewPacket packet, DiffImage patch, DiffDisplayMode mode)
    {
        var image = LayerDiff.Load(packet.ImagePath(patch, mode));
        if (image.Width == patch.Bounds.Width && image.Height == patch.Bounds.Height) return image;
        image.Dispose(); throw new InvalidDataException("差异 PNG 的像素尺寸与影响位置不一致");
    }
}

internal sealed class DiffViewer : UserControl
{
    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205, DropDownWidth = 520 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 138 };
    private readonly CheckBox _ranges = new() { Text = "总览显示脏矩阵", AutoSize = true, Margin = new(8, 6, 3, 3) };
    private readonly Preview _preview = new() { Dock = DockStyle.Fill, Message = "第一张图层作为基准\n从第二张开始，在这里显示图像差异" };
    private readonly Preview _locator = new() { Dock = DockStyle.Fill, Message = "完整画布上的更改位置" };
    private readonly GroupBox _focusTitle = new() { Text = "更改部分 · 修改后", Dock = DockStyle.Fill, Padding = new(6, 8, 6, 6) };
    private readonly Label _location = new() { Dock = DockStyle.Bottom, Height = 62, TextAlign = ContentAlignment.MiddleLeft, Padding = new(3) };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, Text = "等待图像差异" };
    private DiffViewPacket? _packet;
    private Bitmap? _image, _canvasImage;
    private CancellationTokenSource? _renderStop;
    private bool _binding, _fit = true;
    private int _version;
    public event Action<string>? Error;
    public event Action? PacketOpened;
    internal Task Rendering { get; private set; } = Task.CompletedTask;
    internal Exception? LastError { get; private set; }
    internal int RegionCount => _region.Items.Count;
    internal Bitmap? DisplayedImage => _image;
    internal Bitmap? DisplayedCanvas => _canvasImage;
    internal Rectangle? LocatedBounds => _locator.Overlays.LastOrDefault()?.Bounds;
    internal bool RangesShown => _ranges.Checked;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Fit { get => _fit; set { _fit = value; _preview.Fit = value; } }

    public DiffViewer()
    {
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(4) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 76));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var open = new Button { Text = "打开差异…", AutoSize = true };
        open.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "图像差异包|manifest.json|JSON|*.json", Title = "打开已有图像差异包" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { var packet = await Task.Run(() => DiffViewPacket.Read(dialog.FileName)); if (IsDisposed) return; Display(packet); PacketOpened?.Invoke(); }
            catch (Exception ex) { Error?.Invoke("无法打开差异包：" + ex.Message); }
        };
        _mode.Items.AddRange(["通道差值", "修改前原像素", "更改图像", "变化遮罩"]); _mode.SelectedIndex = (int)DiffDisplayMode.Now;
        _region.Enabled = _mode.Enabled = _ranges.Enabled = false;
        toolbar.Controls.AddRange([_region, _mode, _ranges, open]);
        var panels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        panels.ColumnStyles.Add(new(SizeType.Percent, 68)); panels.ColumnStyles.Add(new(SizeType.Percent, 32));
        _focusTitle.Controls.Add(_preview);
        var locatorTitle = new GroupBox { Text = "完整画布 · 位置总览", Dock = DockStyle.Fill, Padding = new(6, 8, 6, 6) };
        locatorTitle.Controls.Add(_locator); locatorTitle.Controls.Add(_location);
        panels.Controls.Add(_focusTitle, 0, 0); panels.Controls.Add(locatorTitle, 1, 0);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(panels, 0, 1); layout.Controls.Add(_details, 0, 2); Controls.Add(layout);
        _region.SelectedIndexChanged += (_, _) => RefreshImage(); _mode.SelectedIndexChanged += (_, _) => RefreshImage();
        _ranges.CheckedChanged += (_, _) => UpdateOverlays();
    }
    public void Display(DiffViewPacket packet)
    {
        _packet = packet; _binding = true; ClearCanvas();
        _region.Items.Clear(); _region.Items.Add("全部更改 · 局部图像");
        for (int i = 0; i < packet.Images.Length; i++)
        {
            var image = packet.Images[i]; var b = image.Bounds;
            _region.Items.Add($"区域 {i + 1} · {b.Width}×{b.Height}");
        }
        _region.SelectedIndex = packet.Images.Length > 0 ? 1 : 0; _mode.SelectedIndex = (int)DiffDisplayMode.Now;
        _region.Enabled = _mode.Enabled = _ranges.Enabled = packet.Images.Length > 0;
        _binding = false; RefreshImage();
    }
    private void RefreshImage()
    {
        if (_binding || _packet is null) return;
        _renderStop?.Cancel(); _renderStop?.Dispose(); _renderStop = new();
        int version = ++_version; var token = _renderStop.Token;
        var packet = _packet; int selection = Math.Max(0, _region.SelectedIndex); var mode = (DiffDisplayMode)_mode.SelectedIndex;
        LastError = null; ClearImage(); _details.Text = Describe(packet, selection, mode);
        _focusTitle.Text = "更改部分 · " + (mode switch { DiffDisplayMode.Now => "修改后原像素", DiffDisplayMode.After => "修改前原像素",
            DiffDisplayMode.Mask => "变化遮罩", _ => "通道差值" });
        if (packet.Images.Length == 0)
        {
            _preview.Message = packet.Baseline ? "已建立 after 基准\n等待下一张图层进行比较" : "本轮未检测到像素变化";
            _focusTitle.Text = "更改部分";
        }
        else _preview.Message = "正在读取更改像素…";
        if (_canvasImage is null) _locator.Message = "正在读取完整画布…";
        Rendering = RenderAsync(packet, selection, mode, version, token);
    }
    private async Task RenderAsync(DiffViewPacket packet, int selection, DiffDisplayMode mode, int version, CancellationToken token)
    {
        Bitmap? pending = null, pendingCanvas = null;
        try
        {
            bool needCanvas = _canvasImage is null;
            var frames = await Task.Run(() =>
            {
                DiffDisplayFrame? focus = null;
                try
                {
                    if (packet.Images.Length > 0) focus = DiffDisplay.Focus(packet, selection, mode, token);
                    return (Focus: focus, Canvas: needCanvas ? DiffDisplay.Locator(packet, token) : null);
                }
                catch { focus?.Image.Dispose(); throw; }
            }, token);
            pending = frames.Focus?.Image; pendingCanvas = frames.Canvas?.Image;
            if (IsDisposed || version != _version || token.IsCancellationRequested) return;
            if (frames.Focus is { } frame)
            {
                _preview.CanvasBounds = frame.CanvasBounds; _preview.ImageBounds = frame.ImageBounds; _preview.Fit = _fit;
                _image = pending; pending = null; _preview.Image = _image;
            }
            if (frames.Canvas is { } canvas)
            {
                _locator.CanvasBounds = canvas.CanvasBounds; _locator.ImageBounds = canvas.ImageBounds;
                _canvasImage = pendingCanvas; pendingCanvas = null; _locator.Image = _canvasImage;
            }
            UpdateOverlays();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && version == _version)
            { LastError = ex; _preview.Message = "差异图像读取失败\n" + ex.Message; Error?.Invoke("差异显示失败：" + ex.Message); }
        }
        finally { pending?.Dispose(); pendingCanvas?.Dispose(); }
    }
    private void ClearImage()
    {
        _preview.Image = null; _preview.Overlays = []; _preview.CanvasBounds = _preview.ImageBounds = null;
        _image?.Dispose(); _image = null;
    }
    private void ClearCanvas()
    {
        _locator.Image = null; _locator.Overlays = []; _locator.CanvasBounds = _locator.ImageBounds = null;
        _location.Text = ""; _canvasImage?.Dispose(); _canvasImage = null;
    }
    private void UpdateOverlays()
    {
        if (_packet is null || _canvasImage is null) return;
        var selected = _region.SelectedIndex > 0 ? _packet.Images[_region.SelectedIndex - 1] : null;
        var matrix = _ranges.Checked ? _packet.ImpactBoxes(selected).Select(b => new PreviewOverlay(b, Color.FromArgb(0, 190, 210), true)) : [];
        Rectangle? bounds = selected is null ? _packet.Images.Length == 0 ? null
            : _packet.Images.Select(i => DiffViewPacket.Box(i.Bounds)).Aggregate(Rectangle.Union) : DiffViewPacket.Box(selected.Bounds);
        // The patch stays unobscured. Only the small locator gets a position
        // frame; all-change selection uses one union frame instead of a grid.
        _preview.Overlays = [];
        _locator.Overlays = bounds is { } b ? matrix.Append(new PreviewOverlay(b, Color.FromArgb(255, 64, 140), MinimumScreenSize: 10)).ToArray() : [];
        _location.Text = bounds is { } box ? $"粉色框：{(selected is null ? "全部更改" : "区域 " + _region.SelectedIndex)}\n位置 ({box.Left}, {box.Top})"
            : $"完整画布\n{_packet.Canvas.Width}×{_packet.Canvas.Height} px";
    }
    private static string Describe(DiffViewPacket packet, int selection, DiffDisplayMode mode)
    {
        var selected = selection > 0 ? packet.Images[selection - 1] : null;
        var labels = packet.LabelsFor(selected); var stateIds = labels.SelectMany(l => l.StateIds).ToHashSet();
        var states = packet.States.Where(s => stateIds.Contains(s.Id)).ToArray();
        var brushes = states.Where(s => s.Channel == "core.brushState").Select(s => J.Text(J.Get(s.Data, "state"), "name"))
            .OfType<string>().Distinct().ToArray();
        var colors = states.Where(s => s.Channel == "core.colorState").Select(s => J.Text(J.Get(s.Data, "state"), "hex"))
            .OfType<string>().Distinct().ToArray();
        string Source(string source) => source switch { "tablet" => "数位笔", "mouse" => "鼠标兼容输入", "low-resolution-fallback" => "范围外补查", _ => source };
        string range = selected is null ? $"{packet.Images.Length} 个区域 · {packet.Images.Sum(i => i.ChangedPixels)} 个变化像素"
            : $"区域 {selection} · 画布位置 ({selected.Bounds.Left}, {selected.Bounds.Top}) · {selected.Bounds.Width}×{selected.Bounds.Height} px · {selected.ChangedPixels} 个变化像素";
        string context = string.Join(" · ", new[] { labels.Length > 0 ? $"影响标签 {labels.Length}（{string.Join("、", labels.Select(l => Source(l.Source)).Distinct())}）" : "",
            brushes.Length > 0 ? "笔刷：" + string.Join("、", brushes) : "", colors.Length > 0 ? "颜色：" + string.Join("、", colors) : "" }.Where(t => t.Length > 0));
        return $"{packet.LayerName} · 完整画布 {packet.Canvas.Width}×{packet.Canvas.Height} px · {range}\r\n{context}\r\n"
            + (packet.Images.Length == 0 ? packet.Baseline ? "首张为比较基准；下一张会生成图像差异。" : "本轮比较完成，没有检测到像素变化。"
                : mode == DiffDisplayMode.Now ? "左侧只显示发生更改的 now 原像素，未更改处透明；擦除的旧内容可切到「修改前」查看。右侧粉色框对应左侧的位置。"
                : mode == DiffDisplayMode.After ? "左侧只显示发生更改的 after 原像素；右侧粉色框对应它在完整画布上的位置。"
                : mode == DiffDisplayMode.Mask ? "白色为发生更改的像素，包含擦除；右侧粉色框标明画布位置。"
                : "通道差值仅用于检查；查看更改的原图像请选择「修改后」或「修改前」。")
            + (packet.CanvasImagePath is null && packet.Images.Length > 0 ? " 旧包未保留完整图层，总览以画布尺寸和更改像素定位。" : "");
    }
    internal void Select(int region, DiffDisplayMode mode) { _region.SelectedIndex = region; _mode.SelectedIndex = (int)mode; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _version++; _renderStop?.Cancel(); _renderStop?.Dispose(); ClearImage(); ClearCanvas(); }
        base.Dispose(disposing);
    }
}
