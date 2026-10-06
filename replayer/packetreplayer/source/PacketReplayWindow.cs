using System.Text.Json;
using StrokeReplay;

namespace PacketReplay;

internal sealed class PacketReplayWindow : Form
{
    private readonly TextBox _file = new() { Dock = DockStyle.Fill, ReadOnly = true, TabStop = false };
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "留空自动寻找；或填写 live.json／管道名" };
    private readonly TextBox _memoline = new() { Dock = DockStyle.Fill, PlaceholderText = "留空自动寻找 Memoline.exe" };
    private readonly TextBox _configRoot = new() { Dock = DockStyle.Fill, PlaceholderText = "留空使用本机 CSP 配置" };
    private readonly ComboBox _packets = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _speed = new() { Minimum = .1m, Maximum = 10, DecimalPlaces = 1, Increment = .1m, Value = 1, Width = 85 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly NumericUpDown _gap = new() { Minimum = 0, Maximum = 200, Value = 200, Width = 80 };
    private readonly Button _open = new() { Text = "打开录制…", AutoSize = true };
    private readonly Button _connect = new() { Text = "连接／刷新配置", AutoSize = true };
    private readonly Button _play = new() { Text = "从头回放选中包", AutoSize = true };
    private readonly Button _next = new() { Text = "下一笔", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Label _connection = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "等待 Memoline 实时接口" };
    private readonly Label _notes = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly ListView _steps = new() { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, GridLines = true };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private PacketDocument? _document;
    private PacketPlan? _plan;
    private LivePacketFeed? _feed;
    private CancellationTokenSource? _running;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;
    private int _cursor;
    private bool _closing;
    private bool _savedBeforeReplay;
    private long _savedGeneration = -1;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PacketReplay", "settings.json");

    internal PacketReplayWindow(string? path, bool previewOnly = false)
    {
        Text = "PacketReplay · Memoline 聚集包逐笔回放";
        ClientSize = new Size(1040, 820);
        MinimumSize = new Size(860, 700);
        Font = new Font("Microsoft YaHei UI", 9);
        AllowDrop = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 10, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 145));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < 7; i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 62));
        layout.RowStyles.Add(new(SizeType.Absolute, 24));
        layout.RowStyles.Add(new(SizeType.Percent, 38));
        AddRow(0, "录制文件", _file, _open);
        AddRow(1, "实时接口", _endpoint, _connect);
        AddRow(2, "Memoline 程序", _memoline);
        AddRow(3, "CSP 配置目录", _configRoot);
        AddRow(4, "选择聚集包", _packets);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _mode.Items.AddRange(["集中模式", "离散模式"]); _mode.SelectedIndex = 0;
        controls.Controls.AddRange([new Label { Text = "倍速", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, _speed, _mode,
            new Label { Text = "笔间上限 ms", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, _gap, _play, _next, _stop]);
        layout.Controls.Add(controls, 0, 5); layout.SetColumnSpan(controls, 3);
        var description = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2 };
        description.Controls.Add(_connection, 0, 0); description.Controls.Add(_notes, 0, 1);
        layout.Controls.Add(description, 0, 6); layout.SetColumnSpan(description, 3);
        _steps.Columns.Add("事件", 55); _steps.Columns.Add("类型", 80); _steps.Columns.Add("内容", 720);
        layout.Controls.Add(_steps, 0, 7); layout.SetColumnSpan(_steps, 3);
        layout.Controls.Add(_progress, 0, 8); layout.SetColumnSpan(_progress, 3);
        layout.Controls.Add(_log, 0, 9); layout.SetColumnSpan(_log, 3);
        Controls.Add(layout);
        LoadSettings();
        _open.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Memoline 聚集包 (*.memoline)|*.memoline|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) await LoadFileAsync(dialog.FileName);
        };
        _packets.SelectedIndexChanged += (_, _) => SelectPacket();
        _connect.Click += async (_, _) => await ConnectAsync();
        _play.Click += (_, _) => Start(false);
        _next.Click += (_, _) => Start(true);
        _stop.Click += (_, _) => _running?.Cancel();
        DragEnter += (_, e) => { if (_running is null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (_running is null && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadFileAsync(files[0]); };
        if (!previewOnly) Shown += async (_, _) => { if (path is not null) await LoadFileAsync(path); await ConnectAsync(); };
        FormClosing += ClosingAsync;
        SetBusy(false);
        void AddRow(int row, string caption, Control input, Control? button = null)
        {
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 7, 3, 7) }, 0, row);
            layout.Controls.Add(input, 1, row);
            if (button is null) layout.SetColumnSpan(input, 2); else layout.Controls.Add(button, 2, row);
        }
    }

    private async Task LoadFileAsync(string path)
    {
        SetBusy(true);
        try
        {
            Log("正在验证容器、哈希和原生事件指针…");
            var document = await Task.Run(() => PacketArchiveReader.Read(path, _lifetime.Token));
            SetDocument(document);
            Log(Program.Describe(document));
        }
        catch (Exception ex) { Log("读取失败：" + ex.Message); }
        finally { SetBusy(false); }
    }
    internal void SetDocument(PacketDocument document)
    {
        _document = document; _file.Text = document.Path;
        _packets.Items.Clear();
        foreach (var p in document.Packets)
        {
            var plan = PacketArchiveReader.Plan(document, p);
            _packets.Items.Add($"{p.Number}. {p.FromTicks / (double)document.Mechanical.Frequency:0.###}–{p.ToTicks / (double)document.Mechanical.Frequency:0.###} 秒 · {plan.Strokes.Count} 笔 · {plan.Steps.Count} 事件 · {p.Status}");
        }
        if (document.Packets.Count > 0) _packets.SelectedIndex = document.Packets.Select((p, i) => (p, i)).FirstOrDefault(x => PacketArchiveReader.Plan(document, x.p).Strokes.Count > 0).i;
    }
    private void SelectPacket()
    {
        _cursor = 0; _savedBeforeReplay = false; _plan = null; _steps.Items.Clear(); _progress.Value = 0;
        if (_document is null || _packets.SelectedIndex < 0) return;
        _plan = PacketArchiveReader.Plan(_document, _document.Packets[_packets.SelectedIndex]);
        foreach (var step in _plan.Steps)
            _steps.Items.Add(new ListViewItem([(_steps.Items.Count + 1).ToString(), step.Kind switch {
                "stroke" => "笔画", "brush" => "笔刷", "layer" => "图层", "color" => "颜色", "view" => "视图", "command" => "命令",
                "unconfirmed" => "未确认", _ => "未支持" }, step.Description]));
        _notes.Text = "按文件状态变化回放；同状态笔画沿用状态表，修改后主动请求确认。\n" + string.Join("\n", _plan.Notes);
        _progress.Maximum = Math.Max(1, _plan.Steps.Count);
        if (_running is null) SetBusy(false);
    }
    private async Task ConnectAsync()
    {
        SetBusy(true);
        try
        {
            if (_feed is not null) await _feed.DisposeAsync();
            _feed = new(_endpoint.Text); _savedBeforeReplay = false;
            _feed.View.StatusChanged += s => Ui(() => _connection.Text = s);
            _ = _feed.View.RunAsync(_lifetime.Token);
            try
            {
                var config = await ShortcutConfiguration.QueryAsync(_memoline.Text, _configRoot.Text, _lifetime.Token);
                _feed.SetShortcuts(config);
                Log($"Memoline 配置接口读取到 {config.Bindings.Count} 条快捷键。");
            }
            catch (Exception ex) { Log("配置查询：" + ex.Message + "；实时 shortcuts 频道可继续提供配置。"); }
            SaveSettings();
        }
        catch (Exception ex) { Log("连接失败：" + ex.Message); }
        finally { SetBusy(false); }
    }
    private void Start(bool oneStroke)
    {
        if (_document is null || _plan is null || _feed is null || _running is not null) return;
        if (!oneStroke)
        {
            _cursor = 0; _savedBeforeReplay = false; _progress.Value = 0;
            foreach (ListViewItem item in _steps.Items) item.BackColor = _steps.BackColor;
        }
        if (_cursor >= _plan.Steps.Count) { Log("选中包已回放完，可从头回放。"); return; }
        int end = _plan.Steps.Count;
        if (oneStroke)
            for (int i = _cursor; i < _plan.Steps.Count; i++)
                if (_plan.Steps[i].Kind == "stroke") { end = i + 1; break; }
        _running = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var options = new ReplayOptions(_mode.SelectedIndex == 1 ? ReplayMode.Discrete : ReplayMode.Concentrated, (double)_gap.Value / 1000);
        double speed = (double)_speed.Value;
        SetBusy(true); SaveSettings();
        _runTask = RunAsync(_document, _plan, _feed, options, speed, end, _running.Token);
    }
    private async Task RunAsync(PacketDocument document, PacketPlan plan, LivePacketFeed feed, ReplayOptions options, double speed, int end, CancellationToken token)
    {
        try
        {
            await Task.Run(() => PacketReplayRunner.RunAsync(document, plan, feed, speed, options, s => Ui(() => Log(s)),
                count => Ui(() => { _cursor = count; _progress.Value = count; if (count > 0) { _steps.Items[count - 1].BackColor = Color.Honeydew; _steps.Items[count - 1].EnsureVisible(); } }), token, _cursor, end,
                prepareSession: !_savedBeforeReplay || _savedGeneration != feed.Generation,
                prepared: () => { _savedGeneration = feed.Generation; _savedBeforeReplay = true; }), token);
        }
        catch (OperationCanceledException) { _savedBeforeReplay = false; Log("已停止；笔接触与快捷键已释放。"); }
        catch (Exception ex) { _savedBeforeReplay = false; Log("回放停止：" + ex.Message); }
        finally { _running?.Dispose(); _running = null; SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        foreach (var c in new Control[] { _open, _packets, _connect, _endpoint, _memoline, _configRoot, _speed, _mode, _gap }) c.Enabled = !busy;
        _play.Enabled = _next.Enabled = !busy && _plan is not null && _feed is not null;
        _stop.Enabled = _running is not null;
    }
    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }
    private void Log(string message) { if (!IsDisposed && !Disposing) _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}\r\n"); }
    private void LoadSettings()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var p = json.RootElement;
            _endpoint.Text = J.Text(p, "endpoint") ?? ""; _memoline.Text = J.Text(p, "memoline") ?? ""; _configRoot.Text = J.Text(p, "configRoot") ?? "";
            _speed.Value = Math.Clamp((decimal)(J.Number(J.Get(p, "speed")) ?? 1), _speed.Minimum, _speed.Maximum);
            _gap.Value = Math.Clamp((decimal)(J.Number(J.Get(p, "gapMs")) ?? 200), 0, 200);
            _mode.SelectedIndex = J.Text(p, "mode") == "discrete" ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or OverflowException) { }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { endpoint = _endpoint.Text, memoline = _memoline.Text, configRoot = _configRoot.Text,
                speed = _speed.Value, gapMs = _gap.Value, mode = _mode.SelectedIndex == 1 ? "discrete" : "concentrated" }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("设置保存失败：" + ex.Message); }
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true; _closing = true;
        _lifetime.Cancel(); _running?.Cancel();
        if (_runTask is not null) await _runTask;
        if (_feed is not null) await _feed.DisposeAsync();
        SaveSettings(); _lifetime.Dispose(); Close();
    }
}
