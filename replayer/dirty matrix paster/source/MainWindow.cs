using System.Drawing.Drawing2D;
using System.Text.Json;

namespace DirtyMatrixPaster;

internal sealed record PacketItem(string Path, string Id, int Images, string Layer, long? TriggerTicks)
{
    public override string ToString() => $"{Id}  ·  {(Images == 0 ? "基准 / 无 image" : $"{Images} 个 image")}  ·  {Layer}  ·  t={TriggerTicks}";
    internal static PacketItem Read(string input)
    {
        string path = System.IO.Path.GetFullPath(input);
        if (Directory.Exists(path)) path = System.IO.Path.Combine(path, "manifest.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement;
        if (root.GetProperty("schema").GetString() != "dirty-matrix-image-diff/v1") throw new InvalidDataException("不支持的 schema");
        var descriptor = root.GetProperty("now"); if (descriptor.ValueKind == JsonValueKind.Null) descriptor = root.GetProperty("after");
        string layer = descriptor.TryGetProperty("layer", out var l) ? l.GetProperty("name").GetString() ?? "" : "";
        long? ticks = descriptor.TryGetProperty("triggerTicks",out var time) && time.TryGetInt64(out long value) ? value : null;
        return new(path, root.GetProperty("id").GetString()!, root.GetProperty("images").GetArrayLength(), layer, ticks);
    }
}

internal sealed class MainWindow : Form
{
    private readonly CheckedListBox _packets = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, CheckOnClick = true };
    private readonly Button _generate = new() { Text = "合成预览", AutoSize = true };
    private readonly Button _copy = new() { Text = "合成并写入剪贴板", AutoSize = true };
    private readonly Button _folder = new() { Text = "打开生成目录", AutoSize = true, Enabled = false };
    private readonly CheckBox _compatibility = new() { Text = "同时提供普通图像格式", Checked = true, AutoSize = true, Margin = new(12,8,3,3) };
    private readonly ToolStripStatusLabel _status = new() { Text = "勾选多个脏矩阵包，按发生时间合成后一次写入剪贴板", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _selection = new() { AutoSize = true, Margin = new(12,8,3,3), Text = "已选 0 个包" };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
    private readonly LayerPreview _preview = new() { Dock = DockStyle.Fill };
    private readonly LayerPreview _mask = new() { Dock = DockStyle.Fill, IsMask = true };
    private string? _outputDirectory;
    private bool _busy;

    internal MainWindow(string[]? args = null)
    {
        Text = "dirty matrix paster"; ClientSize = new(1120,780); MinimumSize = new(920,660);
        StartPosition = FormStartPosition.CenterScreen; Font = new("Microsoft YaHei UI",9);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), RowCount = 6, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute,40)); layout.RowStyles.Add(new(SizeType.Absolute,185));
        layout.RowStyles.Add(new(SizeType.Absolute,42)); layout.RowStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.Absolute,112)); layout.RowStyles.Add(new(SizeType.Absolute,32));
        var open = new Button { Text = "选择多个脏矩阵…", AutoSize = true };
        open.Click += (_,_) => { if (_busy) return; using var d = new OpenFileDialog { Filter = "差异包 manifest|manifest.json|JSON|*.json", Multiselect = true }; if (d.ShowDialog(this) == DialogResult.OK) foreach (var p in d.FileNames) Add(p); };
        var scan = new Button { Text = "扫描目录…", AutoSize = true };
        scan.Click += (_,_) =>
        {
            if (_busy) return; using var d = new FolderBrowserDialog { InitialDirectory = Backend.DefaultPacketsRoot };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (File.Exists(Path.Combine(d.SelectedPath,"manifest.json"))) Add(d.SelectedPath);
            else foreach (var dir in Directory.GetDirectories(d.SelectedPath).Order()) if (File.Exists(Path.Combine(dir,"manifest.json"))) Add(dir);
        };
        var selectAll = new Button {Text="全选",AutoSize=true};
        var clear = new Button {Text="清空选择",AutoSize=true};
        selectAll.Click += (_,_) => {if (!_busy) for(int i=0;i<_packets.Items.Count;i++) _packets.SetItemChecked(i,true);};
        clear.Click += (_,_) => {if (!_busy) for(int i=0;i<_packets.Items.Count;i++) _packets.SetItemChecked(i,false);};
        var intro = new FlowLayoutPanel { Dock = DockStyle.Fill }; intro.Controls.AddRange([open,scan,selectAll,clear,
            new Label { Text = "按发生时间 → RGBA 像素替换 → 合成图像", AutoSize = true, Margin = new(16,8,3,3) }]);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill }; commands.Controls.AddRange([_generate,_copy,_folder,_compatibility,_selection]);
        _packets.ItemCheck += (_,e) =>
        {
            if (_busy) {e.NewValue=e.CurrentValue;return;}
            int count=_packets.CheckedItems.Count+(e.NewValue==CheckState.Checked?1:0)-(e.CurrentValue==CheckState.Checked?1:0);
            _selection.Text=$"已选 {count} 个包";
        };
        _generate.Click += async (_,_) => await ProcessAsync(false); _copy.Click += async (_,_) => await ProcessAsync(true);
        _folder.Click += (_,_) => { if (_outputDirectory is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_outputDirectory) { UseShellExecute = true }); };
        var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        previews.ColumnStyles.Add(new(SizeType.Percent,65)); previews.ColumnStyles.Add(new(SizeType.Percent,35));
        previews.RowStyles.Add(new(SizeType.Absolute,26)); previews.RowStyles.Add(new(SizeType.Percent,100));
        previews.Controls.Add(new Label { Text = "合成 RGBA 在完整画布中的位置", Dock = DockStyle.Fill },0,0);
        previews.Controls.Add(new Label { Text = "累计更新范围（含透明擦除）", Dock = DockStyle.Fill },1,0);
        previews.Controls.Add(_preview,0,1); previews.Controls.Add(_mask,1,1);
        var note = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Text = "后发生的 RGBA 完整替换先前像素，包括 alpha=0 的擦除。未选包涉及的其他范围保持透明。" };
        layout.Controls.Add(intro,0,0); layout.Controls.Add(_packets,0,1); layout.Controls.Add(commands,0,2);
        layout.Controls.Add(previews,0,3); layout.Controls.Add(_details,0,4); layout.Controls.Add(note,0,5);
        Controls.Add(layout); var statusBar = new StatusStrip(); statusBar.Items.Add(_status); Controls.Add(statusBar);
        AllowDrop = true; DragEnter += (_,e) => { if (!_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_,e) => { if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) foreach (var p in paths) Add(p); };
        if (args is {Length: >0}) foreach (var p in args) Add(p);
        else foreach (var id in Backend.ExampleIds) { var p = Path.Combine(Backend.DefaultPacketsRoot,id); if (Directory.Exists(p)) Add(p); }
        if (_packets.Items.Count > 0) _packets.SelectedIndex = Math.Min(1,_packets.Items.Count-1);
    }
    private void Add(string path)
    {
        try { var item = PacketItem.Read(path); if (!_packets.Items.Cast<PacketItem>().Any(p => p.Path.Equals(item.Path,StringComparison.OrdinalIgnoreCase))) _packets.Items.Add(item,true); }
        catch (Exception ex) { _status.Text = "无法加载差异包：" + ex.Message; }
    }
    private async Task ProcessAsync(bool copy)
    {
        if (_busy) return;
        var inputs=_packets.CheckedItems.Cast<PacketItem>().Select(p=>p.Path).ToArray();
        if (inputs.Length==0) {_status.Text="请勾选至少一个脏矩阵包";return;}
        bool compatibleImage=_compatibility.Checked;
        _busy = true; _generate.Enabled = _copy.Enabled = _packets.Enabled = _compatibility.Enabled = false;
        _status.Text = $"正在将 {inputs.Length} 个包按发生时间排序，并执行 RGBA 替换合成…";
        try
        {
            using var result = await Backend.ComposeAsync(inputs);
            if (!result.Ready) { _status.Text = result.String("reason"); return; }
            ShowResult(result);
            if (copy)
            {
                LayerClipboard.Write(result,compatibleImage);
                _status.Text = $"已将合成 RGBA 写入持久剪贴板：{result.Number("changedPixels"):N0} 个更新像素，最终透明 {result.Number("erasedPixels"):N0} 个";
            }
            else _status.Text = "按时间替换合成完成，可以查看结果或写入剪贴板";
        }
        catch (Exception ex) { _status.Text = "失败：" + ex.Message; }
        finally { _busy = false; _generate.Enabled = _copy.Enabled = _packets.Enabled = _compatibility.Enabled = true; }
    }
    internal void ShowResult(ExportResult result)
    {
        if (!result.Ready) { _status.Text = result.String("reason"); return; }
        var canvas = result.Root.GetProperty("canvas"); var bounds = result.Root.GetProperty("bounds");
        var size = new Size(canvas.GetProperty("width").GetInt32(),canvas.GetProperty("height").GetInt32());
        var rectangle = new Rectangle(bounds.GetProperty("left").GetInt32(),bounds.GetProperty("top").GetInt32(),result.Number("width"),result.Number("height"));
        _preview.Display(result.String("imagePath"),size,rectangle); _mask.Display(result.String("maskImagePath"),size,rectangle);
        _outputDirectory = Path.GetDirectoryName(result.String("rgbaPath")); _folder.Enabled = true;
        var c=result.Root.GetProperty("composition");
        var order=string.Join("\r\n",c.GetProperty("order").EnumerateArray().Select(p=>$"{p.GetProperty("triggerTicks").GetInt64()}  {p.GetProperty("id").GetString()}"));
        _details.Text = $"已合成 {c.GetProperty("appliedCount").GetInt32()} 个包，跳过 {c.GetProperty("skippedPackets").GetArrayLength()} 个无 image 包\r\n"+
            $"画布 {size.Width} × {size.Height}    位置 ({rectangle.Left}, {rectangle.Top})    合成范围 {rectangle.Width} × {rectangle.Height}\r\n" +
            $"更新 {result.Number("changedPixels"):N0} 像素    最终透明 {result.Number("erasedPixels"):N0} 像素    覆盖替换 {c.GetProperty("overwrittenPixels").GetInt64():N0} 次\r\n"+
            $"发生顺序：\r\n{order}\r\n{result.String("clipboardImagePath")}";
        _status.Text="按发生时间进行 RGBA 替换合成完成";
    }
    internal void RenderSmoke(string path)
    {
        StartPosition = FormStartPosition.Manual; Location = new(-20000,-20000); Show(); Application.DoEvents();
        PerformLayout(); using var image = new Bitmap(Width,Height); DrawToBitmap(image,new(Point.Empty,Size));
        image.Save(path,System.Drawing.Imaging.ImageFormat.Png); Hide();
    }
}

internal sealed class LayerPreview : Control
{
    private Bitmap? _image; private Size _canvas; private Rectangle _bounds;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool IsMask { get; init; }
    internal LayerPreview() { DoubleBuffered = true; BackColor = Color.FromArgb(230,232,236); }
    internal void Display(string path, Size canvas, Rectangle bounds)
    {
        using var source = Image.FromFile(path); var next = new Bitmap(source); _image?.Dispose(); _image = next;
        _canvas = canvas; _bounds = bounds; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (_image is null) return;
        var area = Rectangle.Inflate(ClientRectangle,-18,-18); if (area.Width <= 0 || area.Height <= 0) return;
        var viewSize = IsMask ? _image.Size : _canvas;
        float scale = Math.Min((float)area.Width/viewSize.Width,(float)area.Height/viewSize.Height);
        var target = new RectangleF(area.Left+(area.Width-viewSize.Width*scale)/2,area.Top+(area.Height-viewSize.Height*scale)/2,viewSize.Width*scale,viewSize.Height*scale);
        using var light = new SolidBrush(Color.White); using var dark = new SolidBrush(Color.FromArgb(238,238,238));
        e.Graphics.FillRectangle(light,target);
        for (float y=target.Top;y<target.Bottom;y+=12) for (float x=target.Left;x<target.Right;x+=12)
            if (((int)((x-target.Left)/12)+(int)((y-target.Top)/12))%2 == 0) e.Graphics.FillRectangle(dark,x,y,Math.Min(12,target.Right-x),Math.Min(12,target.Bottom-y));
        e.Graphics.InterpolationMode = scale >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        var imageTarget = IsMask ? target : new RectangleF(target.Left+_bounds.Left*scale,target.Top+_bounds.Top*scale,_bounds.Width*scale,_bounds.Height*scale);
        e.Graphics.DrawImage(_image,imageTarget); using var border = new Pen(Color.FromArgb(180,185,195)); e.Graphics.DrawRectangle(border,target.X,target.Y,target.Width,target.Height);
        if (!IsMask) { using var position = new Pen(Color.FromArgb(230,90,50),1.5f); e.Graphics.DrawRectangle(position,imageTarget.X,imageTarget.Y,imageTarget.Width,imageTarget.Height); }
    }
    protected override void Dispose(bool disposing) { if (disposing) _image?.Dispose(); base.Dispose(disposing); }
}
