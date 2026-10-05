using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DirtyMatrix.Core;
using DirtyMatrix.IO;
using DirtyMatrix.Windows;

namespace DirtyMatrix.UI;

public sealed record DirtyStroke(string Id, string Source, int PointCount, PixelBox Box,
    double BrushRadius, double AntiAliasRadius, double SafetyRadius, double CoveragePaddingRadius, IReadOnlyList<StrokeSegment> Segments)
{
    [JsonIgnore] public StrokeCoverage Coverage => new(Segments, BrushRadius + AntiAliasRadius + SafetyRadius + CoveragePaddingRadius);
}

public sealed class MainWindow : Form
{
    private readonly Dictionary<string, NumericUpDown> _numbers = [];
    private readonly Dictionary<string, DirtyStroke> _strokes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (CoverageSettings Settings, InputSpace Space)> _fileTransforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly GreenOverlay _overlay = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 90, Padding = new(16, 8, 16, 4) };
    private readonly TextBox _log = new() { Dock = DockStyle.Bottom, Height = 75, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox _unit = Choice("px（画布像素）", "mm（按画布 DPI 换算）");
    private readonly ComboBox _space = Choice("数位板原始坐标", "画布像素坐标", "屏幕物理像素坐标");
    private readonly CheckBox _screenSize = new() { Text = "按屏幕尺寸指定（CSP）", AutoSize = true };
    private readonly CheckBox _confirmed = new() { Text = "已核对驱动坐标映射", AutoSize = true };
    private readonly CheckBox _tiles = new() { Text = "以矩阵单元显示覆盖", AutoSize = true };
    private readonly CheckBox _clipToCanvas = new() { Text = "按画布宽高裁剪覆盖", AutoSize = true };
    private readonly Button _liveButton;
    private readonly Button _watchButton;
    private CoverageSettings _settings;
    private ScreenInputMonitor? _monitor;
    private StrokeFolderFeed? _feed;
    private StrokeExtent? _active;
    private string? _activeId;
    private readonly string _liveSession = Guid.NewGuid().ToString("N");
    private int _liveSequence;
    private nint _lastWindow;
    private bool _lastWindowIsCsp;
    private bool _overlayVisible = true;
    private bool _visualDirty = true;
    private bool _closing;
    private bool _picking;
    private int _generation;
    private int _feedGeneration;
    private long _lastPaint;
    private const int MaxStrokes = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "dirty-matrix-settings.json");

    public MainWindow()
    {
        Text = "dirty matrix · 笔触覆盖范围";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new(990, 830); MinimumSize = new(820, 690);
        StartPosition = FormStartPosition.CenterScreen;
        _settings = LoadSettings();

        var header = new Label { Text = "dirty matrix", Dock = DockStyle.Top, Height = 58,
            Font = new Font(Font.FontFamily, 23, FontStyle.Bold), ForeColor = Color.FromArgb(0, 140, 67), Padding = new(16, 10, 0, 0) };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var canvas = Page(tabs, "画布与笔刷");
        AddNumber(canvas, "width", "画布宽度", 2000, 1, 100000, 0, "画布像素 px");
        AddNumber(canvas, "height", "画布高度", 1500, 1, 100000, 0, "画布像素 px");
        AddNumber(canvas, "brush", "CSP 笔刷尺寸（直径）", 20, .01m, 100000, 2, "输入 CSP 当前显示的尺寸值");
        AddControl(canvas, "笔刷单位", _unit, "与 CSP 的首选项 > 标尺／单位保持一致");
        AddNumber(canvas, "dpi", "画布分辨率", 300, 1, 10000, 2, "mm 单位需要；px 单位不受 DPI 影响");
        AddNumber(canvas, "zoom", "缩放比例", 50, .01m, 10000, 2, "百分比 %；例如 50 表示 50%");
        AddNumber(canvas, "padding", "覆盖额外扩展", 12, 0, 1000, 1, "屏幕 px；在线两侧各加余量，默认 12");
        AddControl(canvas, "按屏幕尺寸指定", _screenSize, "与 CSP 的笔刷尺寸子工具设置保持一致");
        AddNumber(canvas, "factor", "笔刷包络系数", 1, .01m, 100, 2, "普通圆笔默认 1；复杂笔尖可增加");
        AddNumber(canvas, "aa", "R_AA · 抗锯齿半径", 2, 0, 10000, 2, "画布 px；可调保守余量，并非 CSP 内部常数");
        AddNumber(canvas, "safety", "R_safety · 安全半径", 4, 0, 10000, 2, "画布 px；稳定器偏移／散布等需额外余量");
        AddNumber(canvas, "alpha", "绿色层不透明度", 28, 5, 80, 0, "%；覆盖层允许笔和鼠标穿透");
        AddNumber(canvas, "tile", "矩阵单元尺寸", 64, 4, 4096, 0, "画布 px；矩阵沿笔迹记录笔刷覆盖的单元");
        AddControl(canvas, "覆盖层显示方式", _tiles, "重叠范围保持相同透明度");

        var position = Page(tabs, "画布位置与可见区域");
        AddNumber(position, "originX", "画布原点 · 屏幕 X", 0, -10000000, 10000000, 2, "完整画布 (0,0) 的屏幕物理像素位置，可为负值");
        AddNumber(position, "originY", "画布原点 · 屏幕 Y", 0, -10000000, 10000000, 2, "完整画布 (0,0) 的屏幕物理像素位置，可为负值");
        AddNumber(position, "anchorX", "校准点 · 画布 X", 0, 0, 100000, 2, "默认 0；原点不可见时可指定其他已知画布坐标");
        AddNumber(position, "anchorY", "校准点 · 画布 Y", 0, 0, 100000, 2, "随后点击该画布点在屏幕上的位置");
        AddControl(position, "校准画布位置", ActionButton("点击已知画布点", PickOrigin), "按所填缩放比例计算画布屏幕原点");
        AddControl(position, "限制绿色层范围", ActionButton("框选可见画布区域", PickViewport), "只选择绘图区，排除 CSP 面板和工具栏");
        AddControl(position, "按画布尺寸限制", _clipToCanvas, "默认关闭，避免推算的画布边界截断笔迹；显示仍限制在框选绘图区");
        AddNumber(position, "viewL", "可见区域 · 左", 0, -100000, 100000, 0, "屏幕物理像素；支持副屏负坐标");
        AddNumber(position, "viewT", "可见区域 · 上", 0, -100000, 100000, 0, "屏幕物理像素");
        AddNumber(position, "viewR", "可见区域 · 右", 1920, -100000, 100000, 0, "右边界不包含自身");
        AddNumber(position, "viewB", "可见区域 · 下", 1080, -100000, 100000, 0, "下边界不包含自身");
        AddControl(position, "画布显示条件", new Label { Text = "未旋转／未镜像", AutoSize = true }, "平移／缩放后更新校准");

        var input = Page(tabs, "文件输入与原始坐标映射");
        AddControl(input, "文件坐标类型", _space, "原始笔迹不是画布坐标；不得按本笔轨迹自动缩放");
        AddNumber(input, "rawL", "原始范围 · 最小 X", 0, -100000000, 100000000, 2, "使用录制时的数位板有效区域参数");
        AddNumber(input, "rawT", "原始范围 · 最小 Y", 0, -100000000, 100000000, 2, "使用录制时的数位板有效区域参数");
        AddNumber(input, "rawR", "原始范围 · 最大 X", 32767, -100000000, 100000000, 2, "默认值仅为占位；设备的实际范围可能不同");
        AddNumber(input, "rawB", "原始范围 · 最大 Y", 32767, -100000000, 100000000, 2, "保留长宽比／映射裁剪须体现在有效范围中");
        var screens = Choice(Screen.AllScreens.Select(s => $"{s.DeviceName} · {s.Bounds.Width}×{s.Bounds.Height}").Append("整个虚拟桌面").ToArray());
        screens.SelectedIndexChanged += (_, _) =>
        {
            var bounds = screens.SelectedIndex < Screen.AllScreens.Length ? Screen.AllScreens[screens.SelectedIndex].Bounds : SystemInformation.VirtualScreen;
            Set("mapL", bounds.Left); Set("mapT", bounds.Top); Set("mapR", bounds.Right); Set("mapB", bounds.Bottom);
        };
        AddControl(input, "选择驱动映射屏幕", screens, "与数位板驱动设置一致，也可手工输入映射范围");
        AddNumber(input, "mapL", "映射屏幕 · 左", 0, -100000, 100000, 0, "屏幕物理像素");
        AddNumber(input, "mapT", "映射屏幕 · 上", 0, -100000, 100000, 0, "屏幕物理像素");
        AddNumber(input, "mapR", "映射屏幕 · 右", 1920, -100000, 100000, 0, "屏幕物理像素");
        AddNumber(input, "mapB", "映射屏幕 · 下", 1080, -100000, 100000, 0, "屏幕物理像素");
        AddControl(input, "映射核对", _confirmed, "只有原始坐标输入需要核对；屏幕／画布坐标不需要");

        var usage = new TabPage("使用说明");
        usage.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, Text = HelpText });
        tabs.TabPages.Add(usage);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 95, Padding = new(12, 8, 12, 4), WrapContents = true };
        actions.Controls.Add(ActionButton("应用参数", () => ApplyParameters()));
        _liveButton = ActionButton("开始实时监视 · F9", ToggleLive); actions.Controls.Add(_liveButton);
        actions.Controls.Add(ActionButton("显示／隐藏 · F10", ToggleOverlay));
        actions.Controls.Add(ActionButton("清空覆盖 · F8", ClearCoverage));
        var import = ActionButton("导入笔迹文件", () => { });
        import.Click += async (_, _) => await ImportFileAsync(); actions.Controls.Add(import);
        _watchButton = ActionButton("监视 Recognizer 输出目录", ToggleWatch); actions.Controls.Add(_watchButton);
        actions.Controls.Add(ActionButton("导出 dirty matrix JSON", ExportMatrix));

        Controls.Add(tabs); Controls.Add(actions); Controls.Add(_log); Controls.Add(_summary); Controls.Add(header);
        Populate(_settings);
        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) =>
        {
            _timer.Start();
            var failed = new List<string>();
            foreach (var pair in new[] { (1, Keys.F9), (2, Keys.F8), (3, Keys.F10) })
                if (!Native.RegisterHotKey(Handle, pair.Item1, 0x4000, (uint)pair.Item2)) failed.Add(pair.Item2.ToString());
            Log(failed.Count == 0 ? "先输入参数，再校准位置及可见绘图区。F9 开始／暂停，F8 清空，F10 隐藏／显示。" :
                $"热键被占用：{string.Join("、", failed)}。仍可使用窗口按钮。");
            RefreshVisual();
        };
    }

    private CoverageSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            { var saved = JsonSerializer.Deserialize<CoverageSettings>(File.ReadAllText(_settingsPath)); saved?.Validate(); if (saved is not null) return saved; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
        var screen = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        return new() { OriginX = screen.Left + (screen.Width - 1000) / 2.0,
            OriginY = screen.Top + (screen.Height - 750) / 2.0,
            Viewport = new(screen.Left, screen.Top, screen.Right, screen.Bottom),
            MappedScreen = new(screen.Left, screen.Top, screen.Right, screen.Bottom) };
    }
    private static ComboBox Choice(params string[] items)
    { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 }; c.Items.AddRange(items); c.SelectedIndex = 0; return c; }
    private TableLayoutPanel Page(TabControl tabs, string title)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new(12) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 230)); table.ColumnStyles.Add(new(SizeType.Absolute, 285));
        table.ColumnStyles.Add(new(SizeType.Percent, 100)); page.Controls.Add(table); tabs.TabPages.Add(page); return table;
    }
    private static void AddControl(TableLayoutPanel table, string label, Control editor, string hint)
    {
        int row = table.RowCount++; table.RowStyles.Add(new(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new(3, 9, 3, 9) }, 0, row);
        editor.Margin = new(3, 5, 3, 5); editor.MaximumSize = new(275, 0); table.Controls.Add(editor, 1, row);
        table.Controls.Add(new Label { Text = hint, AutoSize = true, MaximumSize = new(360, 0),
            ForeColor = Color.DimGray, Margin = new(3, 9, 3, 9) }, 2, row);
    }
    private void AddNumber(TableLayoutPanel table, string key, string label, decimal value, decimal min, decimal max, int decimals, string hint)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals,
            Width = 155, ThousandsSeparator = true, Increment = decimals > 0 ? .1m : 1 };
        _numbers[key] = n; AddControl(table, label, n, hint);
    }
    private Button ActionButton(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new(110, 34), Margin = new(4), Padding = new(4, 0, 4, 0) };
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "dirty matrix", MessageBoxButtons.OK, MessageBoxIcon.Information); } };
        return b;
    }
    private void Set(string key, double value) => _numbers[key].Value = Math.Clamp((decimal)value, _numbers[key].Minimum, _numbers[key].Maximum);
    private double Value(string key) => (double)_numbers[key].Value;
    private RectD Rect(string prefix) => new(Value(prefix + "L"), Value(prefix + "T"), Value(prefix + "R"), Value(prefix + "B"));
    private void SetRect(string prefix, RectD r) { Set(prefix + "L", r.Left); Set(prefix + "T", r.Top); Set(prefix + "R", r.Right); Set(prefix + "B", r.Bottom); }
    private void Populate(CoverageSettings s)
    {
        Set("width", s.CanvasWidth); Set("height", s.CanvasHeight); Set("brush", s.BrushSize); Set("dpi", s.Dpi);
        Set("zoom", s.ZoomPercent); Set("factor", s.BrushEnvelopeFactor); Set("aa", s.AntiAliasRadius); Set("safety", s.SafetyRadius);
        Set("alpha", s.OpacityPercent); Set("tile", s.TileSize); Set("originX", s.OriginX); Set("originY", s.OriginY);
        Set("padding", s.CoveragePaddingScreenPixels);
        SetRect("view", s.Viewport); SetRect("raw", s.RawArea); SetRect("map", s.MappedScreen);
        _unit.SelectedIndex = (int)s.BrushUnit; _space.SelectedIndex = (int)s.FileInputSpace;
        _screenSize.Checked = s.SpecifyBySizeOnScreen; _confirmed.Checked = s.RawMappingConfirmed; _tiles.Checked = s.ShowTiles;
        _clipToCanvas.Checked = s.ClipToCanvasBounds;
    }
    private bool ApplyParameters()
    {
        var next = new CoverageSettings { CanvasWidth = (int)Value("width"), CanvasHeight = (int)Value("height"),
            BrushSize = Value("brush"), BrushUnit = (BrushUnit)_unit.SelectedIndex, Dpi = Value("dpi"), ZoomPercent = Value("zoom"),
            SpecifyBySizeOnScreen = _screenSize.Checked, BrushEnvelopeFactor = Value("factor"), AntiAliasRadius = Value("aa"), SafetyRadius = Value("safety"),
            CoveragePaddingScreenPixels = Value("padding"), ClipToCanvasBounds = _clipToCanvas.Checked,
            OriginX = Value("originX"), OriginY = Value("originY"), Viewport = Rect("view"), RawArea = Rect("raw"), MappedScreen = Rect("map"),
            RawMappingConfirmed = _confirmed.Checked, FileInputSpace = (InputSpace)_space.SelectedIndex, TileSize = (int)Value("tile"),
            OpacityPercent = (int)Value("alpha"), ShowTiles = _tiles.Checked };
        next.Validate();
        FinishActive();
        if (next.CanvasWidth != _settings.CanvasWidth || next.CanvasHeight != _settings.CanvasHeight)
        { ClearCoverage(); Log("画布像素尺寸已改变，已清空旧覆盖。请重新校准位置。"); }
        _settings = next; _visualDirty = true;
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, JsonOptions)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"参数已生效，但未能保存设置：{ex.Message}"); }
        RefreshVisual(); return true;
    }
    private void PickOrigin()
    {
        ApplyParameters();
        var p = Pick(false, $"点击画布坐标 ({Value("anchorX"):0.##}, {Value("anchorY"):0.##}) 对应的屏幕位置");
        if (p is null) return;
        Set("originX", p.Value.X - Value("anchorX") * _settings.Zoom);
        Set("originY", p.Value.Y - Value("anchorY") * _settings.Zoom);
        ApplyParameters(); Log("画布位置已校准。缩放／平移后需更新此位置。");
    }
    private void PickViewport()
    {
        ApplyParameters();
        _picking = true; _overlay.Hide();
        try
        {
            using var picker = new ScreenPicker(true, "拖动框选 CSP 中的可见画布，排除面板和工具栏");
            if (picker.ShowDialog() == DialogResult.OK)
            {
                var r = picker.Selection; SetRect("view", new(r.Left, r.Top, r.Right, r.Bottom));
                ApplyParameters(); Log("可见画布区域已更新。");
            }
        }
        finally { _picking = false; _visualDirty = true; }
    }
    private Point? Pick(bool rectangle, string instruction)
    {
        _picking = true; _overlay.Hide();
        try { using var picker = new ScreenPicker(rectangle, instruction); return picker.ShowDialog() == DialogResult.OK ? picker.SelectedPoint : null; }
        finally { _picking = false; _visualDirty = true; }
    }
    private void ToggleLive()
    {
        if (_monitor is not null) { StopLive(); return; }
        if (_feed is not null) throw new InvalidOperationException("请先停止目录监视，避免同一笔迹被两种输入重复计入。");
        ApplyParameters();
        if (_settings.VisibleCanvas.IsEmpty) throw new InvalidOperationException("画布与可见区域没有交集，请校准位置。");
        _monitor = new(); _liveButton.Text = "暂停实时监视 · F9";
        Log("实时监视已开始：只记录 CSP 前台、绘图区内开始的左键／笔尖接触。按住 Space／Ctrl／Alt 时暂停该笔。");
    }
    private void StopLive()
    {
        _monitor?.Dispose(); _monitor = null; FinishActive(); _liveButton.Text = "开始实时监视 · F9";
        Log("实时监视已暂停；已有覆盖保留。");
    }
    private bool IsCsp(nint window)
    {
        if (window == 0) return false;
        if (window == _lastWindow) return _lastWindowIsCsp;
        _lastWindow = window; _lastWindowIsCsp = false;
        Native.GetWindowThreadProcessId(window, out var pid);
        try { using var process = Process.GetProcessById((int)pid); _lastWindowIsCsp = process.ProcessName.Contains("CLIPStudioPaint", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return _lastWindowIsCsp;
    }
    private void Tick()
    {
        if (_closing) return;
        int count = 0;
        while (_monitor is not null && count++ < 8192 && _monitor.TryRead(out var sample))
        {
            if (_picking) { FinishActive(); continue; }
            if (sample.Action == PointerAction.Reset) { FinishActive(); Log("输入积压，已结束当前笔划以避免跨笔连接。"); continue; }
            // The hook runs before an activating click is delivered. By the time
            // the UI drains it, CSP may have become foreground for that first down.
            bool csp = IsCsp(sample.Foreground) ||
                (sample.Action == PointerAction.Down && IsCsp(Native.GetForegroundWindow()));
            ProcessPointerSample(sample, csp);
        }
        if (_visualDirty && Environment.TickCount64 - _lastPaint >= 100 && !_picking) RefreshVisual();
    }
    private void ProcessPointerSample(PointerSample sample, bool csp)
    {
        if (sample.ModifierHeld || !csp) { FinishActive(); return; }
        if (sample.Action == PointerAction.Down)
        {
            FinishActive();
            if (!_settings.VisibleCanvas.Contains(sample.Screen)) return;
            if (_strokes.Count >= MaxStrokes) { StopLive(); Log("已达到 10000 笔，请先导出或清空覆盖。"); return; }
            _active = new(); _activeId = $"live:{_liveSession}:{++_liveSequence}";
        }
        if (_active is null) return;
        // Mouse/pen release may carry a newer position than the last move. Include it
        // before ending the stroke, even when it is beyond the visible canvas edge.
        _active.Add(_settings.ScreenToCanvas(sample.Screen));
        if (sample.Action == PointerAction.Up) FinishActive();
        else UpdateActive();
    }
    private void UpdateActive()
    {
        if (_activeId is null || _active is null) return;
        var box = _active.Expand(_settings with { ClipToCanvasBounds = false });
        if (box is PixelBox b) _strokes[_activeId] = new(_activeId, "CSP 屏幕输入", _active.PointCount, b,
            _settings.BrushRadius, _settings.AntiAliasRadius, _settings.SafetyRadius, _settings.CoveragePaddingRadius, _active.Segments);
        _visualDirty = true;
    }
    private void FinishActive() { UpdateActive(); _active = null; _activeId = null; }
    private void ToggleOverlay() { _overlayVisible = !_overlayVisible; _visualDirty = true; RefreshVisual(); }
    private void ClearCoverage()
    {
        _active = null; _activeId = null; _strokes.Clear(); _fileTransforms.Clear(); _generation++;
        if (_feed is not null) { _feed.Dispose(); _feed = null; _feedGeneration++; _watchButton.Text = "监视 Recognizer 输出目录"; }
        _visualDirty = true; RefreshVisual(); Log("覆盖已清空。目录监视已停止，重新开启时只接收之后开始的笔迹。");
    }

    private void CheckMapping(InputSpace space)
    { if (space == InputSpace.TabletRaw && !_settings.RawMappingConfirmed) throw new InvalidOperationException("请在“文件输入与原始坐标映射”中填写实际数位板范围、屏幕映射，并勾选确认。"); }
    private async Task ImportFileAsync()
    {
        try
        {
            ApplyParameters();
            using var dialog = new OpenFileDialog { Filter = "笔迹文件|*.strokebin;*.part;*.json|所有文件|*.*", Multiselect = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            int generation = _generation; string path = dialog.FileName;
            Log($"正在读取 {Path.GetFileName(path)}…");
            var file = await Task.Run(() => StrokeFiles.Read(path));
            if (_closing || generation != _generation) return;
            AddFile(path, file); RefreshVisual();
            Log($"已读取 {file.Strokes.Count} 笔，只有接触点及与画布相交的范围被计入。");
        }
        catch (Exception ex) { if (!_closing) { Log(ex.Message); MessageBox.Show(this, ex.Message, "导入笔迹", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
    }
    private void AddFile(string path, StrokeFile file)
    {
        var space = file.DeclaredSpace ?? _settings.FileInputSpace; CheckMapping(space);
        // Build first so malformed data cannot partly replace an existing source.
        string source = Path.GetFullPath(path);
        if (source.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) source = source[..^5];
        var additions = new List<DirtyStroke>();
        var transforms = new List<(string Id, CoverageSettings Settings, InputSpace Space)>();
        foreach (var stroke in file.Strokes)
        {
            string id = $"{source}|{stroke.Id}";
            var transform = _fileTransforms.TryGetValue(id, out var frozen) ? frozen : (_settings, space);
            var extent = new StrokeExtent();
            foreach (var point in stroke.Points)
                extent.Add(transform.Item1.InputToCanvas(new(point.X, point.Y), transform.Item2), point.InContact);
            // Mapping and radius freeze per stroke; clipping is a current display/export choice.
            var box = extent.Expand(transform.Item1 with { ClipToCanvasBounds = false });
            if (box is not PixelBox b) continue;
            if (_strokes.TryGetValue(id, out var prior) && extent.PointCount < prior.PointCount) continue;
            additions.Add(new(id, source, extent.PointCount, b, transform.Item1.BrushRadius, transform.Item1.AntiAliasRadius,
                transform.Item1.SafetyRadius, transform.Item1.CoveragePaddingRadius, extent.Segments));
            transforms.Add((id, transform.Item1, transform.Item2));
        }
        if (_strokes.Count + additions.Count(a => !_strokes.ContainsKey(a.Id)) > MaxStrokes)
            throw new InvalidOperationException("累计笔迹超过 10000 笔，请先导出或清空覆盖。");
        foreach (var a in additions) _strokes[a.Id] = a;
        foreach (var t in transforms) _fileTransforms[t.Id] = (t.Settings, t.Space);
        _visualDirty = true;
    }
    private void ToggleWatch()
    {
        if (_feed is not null) { _feed.Dispose(); _feed = null; _feedGeneration++; _watchButton.Text = "监视 Recognizer 输出目录"; Log("目录监视已停止。"); return; }
        if (_monitor is not null) throw new InvalidOperationException("请先暂停实时屏幕监视。");
        ApplyParameters(); CheckMapping(_settings.FileInputSpace);
        using var dialog = new FolderBrowserDialog { Description = "选择 BehaviorRecognizer 的 procedure/stroke 目录" };
        var suggested = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../recognizer/behavior_recognizer/publish/win-x64/procedure/stroke"));
        if (Directory.Exists(suggested)) dialog.InitialDirectory = suggested;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        int generation = ++_feedGeneration;
        _feed = new(dialog.SelectedPath, (path, file) => Post(() =>
        {
            if (_feed is null || generation != _feedGeneration) return;
            try { AddFile(path, file); } catch (Exception ex) { Log($"文件输入：{ex.Message}"); }
        }), message => Post(() => { if (_feed is not null && generation == _feedGeneration) Log(message); }));
        _watchButton.Text = "停止目录监视";
        Log("目录监视已开始：只接收此刻之后开始的笔迹；需等待 Recognizer 分段提交及文件缓冲写出。长笔划须等待提交。");
    }
    private void Post(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!_closing) action(); })); } catch (InvalidOperationException) { }
    }
    private DirtyTileMatrix Matrix()
    {
        var matrix = DirtyTileMatrix.ForCoverage(_settings, _strokes.Values.Select(s => s.Box));
        foreach (var stroke in _strokes.Values) matrix.AddCoverage(stroke.Coverage);
        return matrix;
    }
    private void RefreshVisual()
    {
        if (_closing || _picking) return;
        var matrix = Matrix();
        var boxes = _settings.ShowTiles ? matrix.Boxes.ToArray() : [];
        _overlay.UpdateCoverage(_settings, _strokes.Values.Select(s => s.Coverage).ToArray(), boxes);
        if (_overlayVisible && matrix.DirtyCellCount > 0) { if (!_overlay.Visible) _overlay.Show(); }
        else _overlay.Hide();
        _summary.Text = $"画布 {_settings.CanvasWidth} × {_settings.CanvasHeight} px  ·  缩放 {_settings.ZoomPercent:0.##}%  ·  笔刷直径 {_settings.NominalBrushPixels:0.###} px\r\n" +
            $"覆盖半径 {_settings.Radius:0.###} 画布 px → {_settings.Radius * _settings.Zoom:0.###} 屏幕 px（含额外扩展 {_settings.CoveragePaddingScreenPixels:0.#} 屏幕 px） · {(_settings.ClipToCanvasBounds ? "按画布宽高裁剪" : "按框选绘图区显示，矩阵自动扩展")}\r\n" +
            $"{_strokes.Count} 笔 · {matrix.DirtyCellCount:N0} 个脏单元 / {matrix.Columns}×{matrix.Rows} · 覆盖层{(_overlayVisible ? "显示" : "隐藏")} · 当前参数应用于后续笔划";
        _visualDirty = false; _lastPaint = Environment.TickCount64;
    }
    private void ExportMatrix()
    {
        FinishActive();
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"dirty-matrix-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, SerializeMatrix());
        Log($"已导出 {dialog.FileName}");
    }
    private string SerializeMatrix()
    {
        var matrix = Matrix();
        var export = new { schema = "dirty-matrix/v2", coordinateSpace = "canvas", coverageMode = "swept-polyline", boundsConvention = "[left,right) x [top,bottom)",
            settings = _settings, strokes = _strokes.Values.ToArray(), matrix = new { columns = matrix.Columns, rows = matrix.Rows,
                originX = matrix.Bounds.Left, originY = matrix.Bounds.Top, bounds = matrix.Bounds,
                tileSize = matrix.TileSize, dirtyCells = matrix.DirtyCellCount,
                rowRuns = matrix.Runs.Select(r => new { row = r.Row, startColumn = r.Start, endColumnExclusive = r.End }).ToArray() } };
        return JsonSerializer.Serialize(export, JsonOptions);
    }
    private void Log(string message)
    {
        if (_closing) return;
        if (_log.TextLength > 12000) _log.Text = _log.Text[^6000..];
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312 && !_closing && !_picking)
        {
            try { switch ((int)message.WParam) { case 1: ToggleLive(); break; case 2: ClearCoverage(); break; case 3: ToggleOverlay(); break; } }
            catch (Exception ex) { Log(ex.Message); }
        }
        base.WndProc(ref message);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true; _timer.Stop(); _timer.Dispose(); _monitor?.Dispose(); _feed?.Dispose(); _overlay.Close(); _overlay.Dispose();
        for (int id = 1; id <= 3; id++) Native.UnregisterHotKey(Handle, id);
        base.OnFormClosed(e);
    }
    public void SavePreview(string path)
    {
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new(0, 0, Width, Height)); bitmap.Save(path);
    }
    public void RunSmokeChecks(string directory)
    {
        Directory.CreateDirectory(directory);
        if (_unit.SelectedIndex < 0 || _space.SelectedIndex < 0) throw new Exception("Missing selected input controls.");
        _settings.Validate();
        var original = _settings;
        var screen = SystemInformation.VirtualScreen;
        _settings = original with { CanvasWidth = 1000, CanvasHeight = 800, ZoomPercent = 100,
            OriginX = screen.Left + 40, OriginY = screen.Top + 40,
            Viewport = new(screen.Left, screen.Top, screen.Right, screen.Bottom),
            BrushSize = 20, BrushEnvelopeFactor = 1, BrushUnit = BrushUnit.Px,
            SpecifyBySizeOnScreen = false, AntiAliasRadius = 2, SafetyRadius = 4,
            CoveragePaddingScreenPixels = 0, ClipToCanvasBounds = true, ShowTiles = false };
        var checkSettings = _settings;
        var data = new StrokeFile(InputSpace.CanvasPixels, new[] { new InputStroke("1", 0,
            new[] { new InputPoint(200,200,true), new InputPoint(400,350,true), new InputPoint(99999,99999,false) }) });
        var source = Path.Combine(directory,"ui-sample.json");
        AddFile(source,data); AddFile(source,data);
        if (_strokes.Count != 1 || _strokes.Values.Single().PointCount != 2) throw new Exception("Import deduplication or hover filtering failed.");
        var frozenBox = _strokes.Values.Single().Box;
        _settings = checkSettings with { BrushSize = checkSettings.BrushSize * 2, CoveragePaddingScreenPixels=24 };
        AddFile(source,data);
        if (_strokes.Values.Single().Box != frozenBox) throw new Exception("Existing stroke radius was not preserved.");
        _settings = checkSettings;
        RefreshVisual();
        if (!_overlay.Visible || Math.Abs(_overlay.Opacity - _settings.OpacityPercent / 100.0) > .01)
            throw new Exception("Overlay failed to show with the requested alpha.");
        const long requiredStyles = 0x80000 | 0x20 | 0x8000000;
        if (((long)Native.GetWindowLongPtr(_overlay.Handle,-20) & requiredStyles) != requiredStyles)
            throw new Exception("Overlay is missing layered, click-through or no-activate styles.");
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap, new(300,275), true);
            AssertPixel(bitmap, new(210,335), false); // Empty corner inside the old bounding box.
            bitmap.Save(Path.Combine(directory,"overlay.png"));
        }
        _strokes.Clear();
        Sample(PointerAction.Down, new(50,60)); Sample(PointerAction.Move, new(100,60)); Sample(PointerAction.Up, new(500,60));
        var released = _strokes.Values.Single();
        if (_active is not null || released.PointCount != 3 || released.Segments.Last().End != new PointD(500,60))
            throw new Exception("Release endpoint was truncated.");
        Sample(PointerAction.Down, new(50,160)); Sample(PointerAction.Up, new(1200,160));
        if (Matrix().Bounds.Right != _settings.CanvasWidth)
            throw new Exception("Release outside the canvas did not reach the canvas edge.");
        var lPath = new StrokeFile(InputSpace.CanvasPixels, new[] { new InputStroke("L",0,
            new[] { new InputPoint(80,250,true), new InputPoint(450,250,true), new InputPoint(450,600,true) }) });
        AddFile(Path.Combine(directory,"polyline.json"),lPath);
        RefreshVisual();
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap,new(500,60),true); AssertPixel(bitmap,new(350,60),true);
            AssertPixel(bitmap,new(998,160),true); AssertPixel(bitmap,new(1010,160),false);
            AssertPixel(bitmap,new(450,250),true); AssertPixel(bitmap,new(450,600),true);
            AssertPixel(bitmap,new(200,400),false);
            bitmap.Save(Path.Combine(directory,"along-line-overlay.png"));
        }
        _settings = _settings with { ShowTiles = true }; RefreshVisual();
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap,new(500,60),true); AssertPixel(bitmap,new(450,600),true);
            AssertPixel(bitmap,new(200,400),false);
            bitmap.Save(Path.Combine(directory,"along-line-tiles.png"));
        }
        _settings = checkSettings;
        RefreshVisual();
        _strokes.Clear();
        _settings = checkSettings with { CanvasWidth=200,CanvasHeight=200,ClipToCanvasBounds=false,CoveragePaddingScreenPixels=12 };
        // Reproduce a fixed inferred right edge, including a new stroke starting past it.
        Sample(PointerAction.Down,new(90,120)); Sample(PointerAction.Move,new(300,120)); Sample(PointerAction.Up,new(600,120));
        Sample(PointerAction.Down,new(500,250)); Sample(PointerAction.Up,new(700,250));
        Sample(PointerAction.Down,new(-10,330)); Sample(PointerAction.Up,new(120,330));
        RefreshVisual();
        if (_strokes.Count != 3 || Matrix().Bounds.Right <= 200 || Matrix().Bounds.Left >= 0)
            throw new Exception("Coverage did not expand beyond the inferred canvas rectangle.");
        var exported = SerializeMatrix();
        File.WriteAllText(Path.Combine(directory,"expanded-matrix.json"),exported);
        using (var document = JsonDocument.Parse(exported))
        {
            if (document.RootElement.GetProperty("schema").GetString() != "dirty-matrix/v2")
                throw new Exception("Expanded matrix export has the wrong schema.");
            var grid = document.RootElement.GetProperty("matrix");
            int tile = grid.GetProperty("tileSize").GetInt32(), originX = grid.GetProperty("originX").GetInt32();
            int row = (250-grid.GetProperty("originY").GetInt32())/tile, column = (700-originX)/tile;
            if (originX >= 0 || !grid.GetProperty("rowRuns").EnumerateArray().Any(run =>
                run.GetProperty("row").GetInt32()==row && run.GetProperty("startColumn").GetInt32()<=column &&
                run.GetProperty("endColumnExclusive").GetInt32()>column))
                throw new Exception("Export lost a dirty cell beyond the inferred right edge.");
        }
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap,new(190,120),true); AssertPixel(bitmap,new(300,120),true); AssertPixel(bitmap,new(600,120),true);
            AssertPixel(bitmap,new(500,250),true); AssertPixel(bitmap,new(700,250),true); AssertPixel(bitmap,new(-10,330),true);
            AssertPixel(bitmap,new(300,143),true); AssertPixel(bitmap,new(300,150),false);
            bitmap.Save(Path.Combine(directory,"expanded-coverage.png"));
        }
        _settings = _settings with { ShowTiles=true }; RefreshVisual();
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap,new(600,120),true); AssertPixel(bitmap,new(700,250),true); AssertPixel(bitmap,new(-10,330),true);
            bitmap.Save(Path.Combine(directory,"expanded-matrix.png"));
        }
        _settings = _settings with { ShowTiles=false,ClipToCanvasBounds=true }; RefreshVisual();
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap,new(190,120),true); AssertPixel(bitmap,new(300,120),false);
        }
        _settings = _settings with { ClipToCanvasBounds=false,Viewport=new(screen.Left+40,screen.Top+40,screen.Left+440,screen.Top+440) };
        RefreshVisual();
        using (var bitmap = new Bitmap(_overlay.Width,_overlay.Height))
        {
            _overlay.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            AssertPixel(bitmap,new(300,120),true); AssertPixel(bitmap,new(500,120),false);
        }
        _settings = checkSettings with { ClipToCanvasBounds=false }; RefreshVisual();
        using (var monitor = new ScreenInputMonitor()) { }
        var tabs = Controls.OfType<TabControl>().Single();
        for (int i = 0; i < tabs.TabCount; i++)
        {
            tabs.SelectedIndex = i; PerformLayout(); Refresh();
            SavePreview(Path.Combine(directory,$"window-{i+1}.png"));
        }
        File.WriteAllText(Path.Combine(directory,"ui-check.json"), JsonSerializer.Serialize(new {
            result = "PASS", unit = _unit.SelectedItem?.ToString(), inputSpace = _space.SelectedItem?.ToString(),
            strokeCount = _strokes.Count, box = frozenBox, dirtyCells = Matrix().DirtyCellCount,
            overlayOpacity = _overlay.Opacity, checks = new[] { "form tabs", "input selection", "import and hover", "deduplication",
                "preserved per-stroke radius and padding", "visible overlay alpha", "layered/click-through/no-activate styles",
                "along-line green coverage raster", "release endpoint without a final move",
                "release past canvas edge", "polyline joins and empty interior", "along-line matrix display",
                "coverage continues across inferred right edge", "stroke starts beyond inferred canvas",
                "negative canvas coordinates", "wider screen-pixel margin", "expanded matrix display",
                "expanded matrix export with origin", "optional canvas clipping", "explicit viewport clipping",
                "native hook startup and cleanup" }
        },JsonOptions));
        ClearCoverage();
        if (_overlay.Visible || _strokes.Count != 0) throw new Exception("Clear failed to remove the overlay.");
        _settings = original;

        void Sample(PointerAction action, PointD canvasPoint) => ProcessPointerSample(new(action,
            new(_settings.OriginX + canvasPoint.X * _settings.Zoom, _settings.OriginY + canvasPoint.Y * _settings.Zoom),0,false),true);
        void AssertPixel(Bitmap bitmap, PointD p, bool green)
        {
            int x=(int)(_settings.OriginX+p.X*_settings.Zoom)-_overlay.Left;
            int y=(int)(_settings.OriginY+p.Y*_settings.Zoom)-_overlay.Top;
            if ((bitmap.GetPixel(x,y).ToArgb() == Color.FromArgb(0,230,90).ToArgb()) != green)
                throw new Exception($"Coverage pixel at {p} did not match expected green={green}.");
        }
    }
    private const string HelpText = """
        dirty matrix 使用方式

        1. 填写画布像素宽高、CSP 笔刷尺寸、px／mm 单位和缩放百分比。
           mm 单位还需要画布 DPI。按“应用参数”。
        2. 在“画布位置与可见区域”中校准画布：默认点击完整画布左上角。
           左上角不可见时，填写一个已知画布点的像素坐标，再点击它。
           框选可见绘图区；缩放、平移或移动窗口后重新校准。画布须未旋转、未镜像。
           默认只用框选绘图区限制显示，矩阵自动扩展，不在推算的画布右边界截止。
           校准准确后，可勾选“按画布宽高裁剪覆盖”以严格限制在标称画布内。
        3. 按 F9 开始实时监视，切回 CSP 绘画。绿色层沿笔迹实时显示扩展后的覆盖范围。
           F9 暂停，F8 清空，F10 隐藏／显示；也可以按窗口按钮。
           实时输入采用系统笔／鼠标左键消息，保留 CSP 原有驱动。
           某些 Wintab 配置不生成这些消息时，请改用 Recognizer 文件目录监视。
           按住 Space／Ctrl／Alt 时不记录；其他工具、选区、撤销无法从轨迹自动区分。

        文件输入
        · 直接读取 BehaviorRecognizer 的 STRO v1 .strokebin、.strokebin.part 和导出 JSON。
        · 原始 STRO 的 x/y 是数位板 PreTransform 坐标，不是画布像素！
          在“文件输入与原始坐标映射”中配置实际有效区域及驱动映射屏幕，再勾选确认。
        · 不按单笔的最小／最大坐标自动适配画布，那会扭曲轨迹覆盖范围。
        · 目录监视只接收开启监视后开始的新笔迹；需等待分段提交和文件缓冲写出。
          正在进行的长笔划须等待提交。清空会停止目录监视，避免旧文件重新出现。
        · 可直接输入声明 coordinateSpace 为 canvas／screen／tablet 的 JSON，示例见 examples。
        · 原始映射为轴对齐线性映射；相对模式、驱动旋转、额外曲线须预先转成屏幕／画布坐标。

        覆盖计算
        · 相邻接触点连线，以 R = R_brush + R_AA + R_safety + R_padding 扩展为两端为圆形的带状范围。
          单点显示圆形范围；悬浮断开连线；实时输入补入抬笔位置，完整保留最后一段。
        · px：笔刷像素直径 = CSP 笔刷尺寸；mm：直径 = 尺寸 × DPI / 25.4。
        · R_brush = 直径 × 包络系数 / 2；按屏幕尺寸指定时再除以缩放因子。
        · 抗锯齿和安全半径以画布像素填写，默认 2、4 是本程序的保守估计。
        · “覆盖额外扩展”默认在线两侧各加 12 屏幕 px，想更宽可调大；换算为画布半径后记录。
        · 沿线记录被触及的矩阵单元；屏幕覆盖按缩放投影，并向外覆盖到屏幕像素。
          默认矩阵自动扩展，显示只裁剪到框选的可见绘图区。开始接触后离开区域仍采集到抬笔。
          开启“按画布宽高裁剪覆盖”时，两者才严格限制在设定画布内。
        · 采用全笔刷半径，不按压力缩小覆盖；这是范围估计，不是实际涂色像素检测。
          复杂笔尖、散布、双笔刷、稳定器、标尺、对称、后校正可能超出该估计，需加大包络／余量。
        · 每笔记录当时的半径；更改笔刷参数只影响之后输入的笔划。
          更改画布像素宽高会清空旧覆盖；更改缩放／位置会重新投影已有画布范围。

        dirty matrix
        · 默认沿线显示扩展范围并集；可选显示沿线触及的矩阵单元。
        · v2 导出包含每笔包围盒、接触线段、半径、矩阵原点及稀疏行区间。
          矩阵原点可为负值，行列索引相对该原点；不分配整张画布的像素数组。
        · 当前最多保留 10000 笔，单输入文件最多 256 MB。

        CSP 官方说明与计算依据见程序目录 README.md。
        """;
}
