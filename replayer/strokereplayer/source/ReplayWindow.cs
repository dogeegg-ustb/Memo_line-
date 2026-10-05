using System.Drawing;
using System.Text.Json;

namespace StrokeReplay;

internal sealed class ReplayWindow : Form
{
    private readonly TextBox _filePath = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "留空自动寻找 Recognizer；也可输入 .live.json 路径或管道名" };
    private readonly Label _live = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, Text = "选择 Recognizer 生成的 .memoline 文件，或将文件拖入窗口。", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "等待选择 memoline 文件" };
    private readonly NumericUpDown _speed = new() { Minimum = .1m, Maximum = 10, DecimalPlaces = 1, Increment = .1m, Dock = DockStyle.Fill };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _strokeGap = new() { Minimum = 0, Maximum = 200, DecimalPlaces = 0, Increment = 10, Dock = DockStyle.Fill };
    private readonly Label _modeHint = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _start = new() { Text = "恢复视图并重放", Enabled = false, Dock = DockStyle.Fill };
    private readonly Button _stop = new() { Text = "停止回放", Enabled = false, Dock = DockStyle.Fill };
    private readonly Button _connect = new() { Text = "连接 / 自动寻找", Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 500 };
    private readonly ReplaySettings _settings = ReplaySettings.Load();
    private MemolineReplayDocument? _document;
    private RecognizerViewMonitor? _monitor;
    private CancellationTokenSource? _listenStop, _replayStop;
    private Task? _listenTask;
    private TaskCompletionSource? _replayFinished;
    private bool _busy, _closing;

    internal ReplayWindow(string? initialFile)
    {
        Text = "StrokeReplay · memoline 复现器";
        Font = new Font("Microsoft YaHei UI", 9F);
        Size = new Size(920, 720);
        MinimumSize = new Size(840, 660);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 248, 251);
        AllowDrop = true;
        _endpoint.Text = _settings.RecognizerEndpoint ?? "";
        _speed.Value = (decimal)Math.Clamp(_settings.Speed, .1, 10);
        _mode.Items.AddRange(["集中模式", "离散模式"]);
        _mode.SelectedIndex = _settings.Mode == ReplayMode.Discrete ? 1 : 0;
        _strokeGap.Value = (decimal)(double.IsFinite(_settings.StrokeGapSeconds) ? Math.Clamp(_settings.StrokeGapSeconds * 1000, 0, 200) : 200);
        _mode.SelectedIndexChanged += (_, _) => UpdateMode();
        _strokeGap.ValueChanged += (_, _) => UpdateMode();
        UpdateMode();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "笔迹复现器", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 20, FontStyle.Bold) }, 0, 0);

        var fileGroup = Group("Recognizer 笔记文件");
        var fileRow = Row(140);
        var browse = new Button { Text = "选择 memoline…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => ChooseFile();
        fileRow.Controls.Add(_filePath, 0, 0);
        fileRow.Controls.Add(browse, 1, 0);
        fileGroup.Controls.Add(fileRow);
        root.Controls.Add(fileGroup, 0, 1);

        var liveGroup = Group("Recognizer 实时接口");
        var liveLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        liveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        liveLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var endpointRow = Row(140);
        endpointRow.Controls.Add(_endpoint, 0, 0);
        endpointRow.Controls.Add(_connect, 1, 0);
        _connect.Click += async (_, _) => await RestartMonitorAsync();
        liveLayout.Controls.Add(endpointRow, 0, 0);
        liveLayout.Controls.Add(_live, 0, 1);
        liveGroup.Controls.Add(liveLayout);
        root.Controls.Add(liveGroup, 0, 2);

        var info = Group("历史数据");
        info.Controls.Add(_summary);
        root.Controls.Add(info, 0, 3);
        var speedRow = Row(180);
        speedRow.Controls.Add(new Label { Text = "回放速度（视图恢复期间暂停笔点计时）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        speedRow.Controls.Add(_speed, 1, 0);
        var playback = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        playback.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        playback.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        playback.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        playback.Controls.Add(speedRow, 0, 0);
        var modeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        modeRow.Controls.Add(new Label { Text = "回放模式", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        modeRow.Controls.Add(_mode, 1, 0);
        modeRow.Controls.Add(new Label { Text = "离散间隔上限（ms）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        modeRow.Controls.Add(_strokeGap, 3, 0);
        playback.Controls.Add(modeRow, 0, 1);
        playback.Controls.Add(_modeHint, 0, 2);
        root.Controls.Add(playback, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_stop, 1, 0);
        footer.Controls.Add(_start, 2, 0);
        _start.BackColor = Color.FromArgb(37, 99, 235);
        _start.ForeColor = Color.White;
        _start.Click += StartReplay;
        _stop.Click += (_, _) => _replayStop?.Cancel();
        root.Controls.Add(footer, 0, 5);
        _liveTimer.Tick += (_, _) => UpdateLive();
        Shown += async (_, _) => { await RestartMonitorAsync(); _liveTimer.Start(); };
        FormClosing += CloseWindow;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadFile(files[0]); };
        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile)) LoadFile(initialFile);
    }

    private void UpdateMode()
    {
        bool discrete = _mode.SelectedIndex == 1;
        _strokeGap.Enabled = discrete && !_busy;
        _modeHint.Text = discrete
            ? "参考 memoline 的笔间间隔，最多等待 200 ms；最后一笔结束后直接完成。"
            : "保持当前连续重放方式，一笔结束后立即继续下一笔。";
    }

    private static GroupBox Group(string text) => new() { Text = text, Dock = DockStyle.Fill, Padding = new Padding(12) };
    private static TableLayoutPanel Row(int trailingWidth)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, trailingWidth));
        return row;
    }
    private void ChooseFile()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog { Title = "选择 memoline 笔记", Filter = "Memoline 文件 (*.memoline;*.memoline.part)|*.memoline;*.memoline.part", CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) LoadFile(picker.FileName);
    }
    private void LoadFile(string path)
    {
        try
        {
            _document = MemolineReplayReader.Read(path);
            _filePath.Text = _document.FilePath;
            _summary.Text = Program.Describe(_document);
            _status.Text = "文件已载入；请让 Recognizer 完成当前 CSP 画布初始化。";
            _start.Enabled = !_busy;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法读取 memoline", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private async Task RestartMonitorAsync()
    {
        if (_busy || _closing) return;
        _connect.Enabled = false;
        try
        {
            await StopMonitorAsync();
            _settings.RecognizerEndpoint = string.IsNullOrWhiteSpace(_endpoint.Text) ? null : _endpoint.Text.Trim();
            _settings.Save();
            _listenStop = new CancellationTokenSource();
            _monitor = new RecognizerViewMonitor(_settings.RecognizerEndpoint);
            _listenTask = _monitor.RunAsync(_listenStop.Token);
            UpdateLive();
        }
        catch (Exception ex) { _live.Text = ex.Message; }
        finally { if (!_closing) _connect.Enabled = true; }
    }
    private void UpdateLive()
    {
        if (_monitor is null) { _live.Text = "尚未连接 Recognizer。"; return; }
        _live.Text = _monitor.Status + Environment.NewLine + (_monitor.Current is { } view
            ? $"当前缩放 {view.ScalePercent:0.###}%    旋转 {view.RotationDegrees:0.###}°    原点 ({view.OriginX:0.##}, {view.OriginY:0.##})"
            : "等待已确认的当前画布视图；接口只监听，不启动第二个录制器。");
        if (_monitor.LastUpdateTiming is { } timing)
            _live.Text += Environment.NewLine + $"消息序号 {timing.Sequence}    取证到结果 {timing.CaptureToResultMilliseconds:0.#} ms    接口接收 {timing.DeliveryMilliseconds:0.#} ms"
                + (timing.Accepted ? "" : $"    未更新表：{timing.Reason}");
        if (_listenTask?.IsFaulted == true) _live.Text = _listenTask.Exception?.GetBaseException().Message;
    }
    private async void StartReplay(object? sender, EventArgs e)
    {
        if (_document is null || _monitor is null || _busy) return;
        _busy = true;
        _replayFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _start.Enabled = false;
        _stop.Enabled = true;
        _connect.Enabled = _endpoint.Enabled = _speed.Enabled = _mode.Enabled = _strokeGap.Enabled = false;
        _settings.Speed = (double)_speed.Value;
        _settings.Mode = _mode.SelectedIndex == 1 ? ReplayMode.Discrete : ReplayMode.Concentrated;
        _settings.StrokeGapSeconds = (double)_strokeGap.Value / 1000;
        _settings.Save();
        _replayStop = new CancellationTokenSource();
        WindowState = FormWindowState.Minimized;
        try
        {
            await Program.ReplayDocumentAsync(_document, _monitor, _settings.Speed,
                message => { if (!IsDisposed) _status.Text = message; }, _replayStop.Token, new ReplayOptions(_settings.Mode, _settings.StrokeGapSeconds));
        }
        catch (OperationCanceledException) { _status.Text = "回放已停止。"; }
        catch (Exception ex)
        {
            _status.Text = "回放停止：" + ex.Message;
            if (!_closing) MessageBox.Show(this, ex.Message, "StrokeReplay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _replayStop.Dispose();
            _replayStop = null;
            _busy = false;
            _replayFinished.TrySetResult();
            if (!_closing)
            {
                _start.Enabled = _document is not null;
                _stop.Enabled = false;
                _connect.Enabled = _endpoint.Enabled = _speed.Enabled = _mode.Enabled = true;
                UpdateMode();
                WindowState = FormWindowState.Normal;
                Activate();
            }
        }
    }
    private async Task StopMonitorAsync()
    {
        _listenStop?.Cancel();
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch (OperationCanceledException) { } catch (Exception) { }
        }
        if (_monitor is not null) await _monitor.DisposeAsync();
        _listenStop?.Dispose();
        _listenTask = null;
        _listenStop = null;
        _monitor = null;
    }
    private async void CloseWindow(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        _liveTimer.Stop();
        _replayStop?.Cancel();
        if (_replayFinished is { } finished) await finished.Task;
        await StopMonitorAsync();
        Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _liveTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class ReplaySettings
{
    public string? RecognizerEndpoint { get; set; }
    public double Speed { get; set; } = 1;
    public ReplayMode Mode { get; set; } = ReplayMode.Concentrated;
    private double _strokeGapSeconds = .2;
    public double StrokeGapSeconds
    {
        get => _strokeGapSeconds;
        set => _strokeGapSeconds = double.IsFinite(value) ? Math.Clamp(value, 0, .2) : .2;
    }
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrokeReplay", "settings.json");
    internal static ReplaySettings Load()
    {
        try { return JsonSerializer.Deserialize<ReplaySettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
