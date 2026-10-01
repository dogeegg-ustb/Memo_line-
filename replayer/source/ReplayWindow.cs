using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using BehaviorRecognizer.Abstractions.Stroke;

namespace StrokeReplay;

internal sealed class ReplayWindow : Form
{
    private readonly TextBox _filePath = new();
    private readonly Label _summary = new();
    private readonly Label _status = new();
    private readonly Label _driverSummary = new();
    private readonly CheckBox _enableDriverCurve = new();
    private readonly Button _changeDriverBtn = new();
    private readonly Button _clearDriverBtn = new();
    private readonly NumericUpDown _pressureMax = new();
    private readonly NumericUpDown _speed = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();

    private string? _selectedFile;
    private CancellationTokenSource? _replayCancellation;
    private bool _busy;
    private readonly ReplaySettings _settings;
    private DriverProfile? _driverProfile;

    public ReplayWindow(string? initialFile)
    {
        _settings = ReplaySettings.Load();

        Text = "StrokeReplay · Windows 笔迹复现器（支持驱动特性）";
        MinimumSize = new Size(720, 620);
        Size = new Size(820, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);
        AllowDrop = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); // 标题
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); // 笔迹文件
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110)); // 驱动特性配置
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 笔迹信息
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90)); // 回放设置
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); // 底部控制栏
        Controls.Add(root);

        // 0. 标题栏
        var title = new Label
        {
            Text = "笔迹复现器",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 20, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 44, 61),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

        // 1. 笔迹文件区域
        var fileGroup = new GroupBox { Text = "笔迹文件", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        _filePath.Dock = DockStyle.Fill;
        _filePath.ReadOnly = true;
        _filePath.BorderStyle = BorderStyle.FixedSingle;
        var browse = new Button { Text = "选择 JSON…", Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
        browse.Click += (_, _) => ChooseFile();
        fileRow.Controls.Add(_filePath, 0, 0);
        fileRow.Controls.Add(browse, 1, 0);
        fileGroup.Controls.Add(fileRow);
        root.Controls.Add(fileGroup, 0, 1);

        // 2. 驱动特性配置区域（新增）
        var driverGroup = new GroupBox { Text = "数位板驱动特性（压感与硬件校准）", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var driverTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        driverTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        driverTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        driverTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        driverTable.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        driverTable.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        _driverSummary.Dock = DockStyle.Fill;
        _driverSummary.TextAlign = ContentAlignment.MiddleLeft;
        _driverSummary.Font = new Font(Font.FontFamily, 8.5F);

        _changeDriverBtn.Text = "更换配置…";
        _changeDriverBtn.Dock = DockStyle.Fill;
        _changeDriverBtn.Margin = new Padding(4);
        _changeDriverBtn.Click += (_, _) => ChooseDriverConfigFile();

        _clearDriverBtn.Text = "清除";
        _clearDriverBtn.Dock = DockStyle.Fill;
        _clearDriverBtn.Margin = new Padding(4);
        _clearDriverBtn.Click += (_, _) => ClearDriverConfig();

        _enableDriverCurve.Text = "启用驱动层压感曲线（还原原厂笔触软硬手感）";
        _enableDriverCurve.Dock = DockStyle.Fill;
        _enableDriverCurve.Checked = _settings.EnableDriverCurve;
        _enableDriverCurve.CheckedChanged += (_, _) =>
        {
            _settings.EnableDriverCurve = _enableDriverCurve.Checked;
            _settings.Save();
        };

        driverTable.Controls.Add(_driverSummary, 0, 0);
        driverTable.Controls.Add(_changeDriverBtn, 1, 0);
        driverTable.Controls.Add(_clearDriverBtn, 2, 0);
        driverTable.Controls.Add(_enableDriverCurve, 0, 1);
        driverTable.SetColumnSpan(_enableDriverCurve, 3);
        driverGroup.Controls.Add(driverTable);
        root.Controls.Add(driverGroup, 0, 2);

        // 3. 笔迹信息区域
        var infoGroup = new GroupBox { Text = "笔迹信息", Dock = DockStyle.Fill, Padding = new Padding(12) };
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.ForeColor = Color.FromArgb(58, 70, 88);
        _summary.Text = "选择笔迹 JSON 后显示设备、笔划数和坐标作用范围。\r\n也可以把 JSON 文件拖到这个窗口。";
        infoGroup.Controls.Add(_summary);
        root.Controls.Add(infoGroup, 0, 3);

        // 4. 回放设置区域
        var settingsGroup = new GroupBox { Text = "回放设置", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var settingsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settingsGrid.Controls.Add(new Label { Text = "压力上限", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _pressureMax.Minimum = 1;
        _pressureMax.Maximum = 65535;
        _pressureMax.Value = (decimal)Math.Clamp(_settings.PressureMax > 0 ? _settings.PressureMax : 16383, 1, 65535);
        _pressureMax.Dock = DockStyle.Fill;
        settingsGrid.Controls.Add(_pressureMax, 1, 0);
        settingsGrid.Controls.Add(new Label { Text = "回放速度", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) }, 2, 0);
        _speed.Minimum = 0.1M;
        _speed.Maximum = 10;
        _speed.DecimalPlaces = 1;
        _speed.Increment = 0.1M;
        _speed.Value = (decimal)Math.Clamp(_settings.Speed > 0 ? _settings.Speed : 1.0, 0.1, 10.0);
        _speed.Dock = DockStyle.Fill;
        settingsGrid.Controls.Add(_speed, 3, 0);
        settingsGroup.Controls.Add(settingsGrid);
        root.Controls.Add(settingsGroup, 0, 4);

        // 5. 底部控制栏
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _status.Text = "等待选择笔迹文件";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(88, 100, 116);
        _stop.Text = "停止回放";
        _stop.Dock = DockStyle.Fill;
        _stop.Enabled = false;
        _stop.Click += (_, _) => _replayCancellation?.Cancel();
        _start.Text = "定位并重放";
        _start.Dock = DockStyle.Fill;
        _start.Enabled = false;
        _start.BackColor = Color.FromArgb(37, 99, 235);
        _start.ForeColor = Color.White;
        _start.FlatStyle = FlatStyle.Flat;
        _start.FlatAppearance.BorderSize = 0;
        _start.Click += StartReplay;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_stop, 1, 0);
        footer.Controls.Add(_start, 2, 0);
        root.Controls.Add(footer, 0, 5);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += (_, _) => _replayCancellation?.Cancel();

        // 加载已保存的驱动配置，若无则在窗口显示后提示选择
        InitDriverProfile();
        Shown += (_, _) => CheckInitialDriverConfig();

        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
            LoadFile(initialFile);
    }

    private void InitDriverProfile()
    {
        if (!string.IsNullOrWhiteSpace(_settings.DriverConfigPath))
        {
            if (File.Exists(_settings.DriverConfigPath) || Directory.Exists(_settings.DriverConfigPath))
            {
                try
                {
                    _driverProfile = DriverProfileParser.Parse(_settings.DriverConfigPath);
                    UpdateDriverDisplay();
                    return;
                }
                catch
                {
                    // 损坏或不可解析，重置
                }
            }

            _settings.DriverConfigPath = null;
            _settings.Save();
        }

        UpdateDriverDisplay();
    }

    private void CheckInitialDriverConfig()
    {
        if (_driverProfile != null) return;

        // 如果没有配置，且本电脑上存在默认的高漫驱动配置文件，提示用户自动关联
        string defaultGaomonPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GAOMON", "data", "EKeySetting.dt");

        string promptText = File.Exists(defaultGaomonPath)
            ? "检测到系统当前未关联数位板驱动配置，但在您的电脑上找到了高漫驱动配置：\r\n" +
              defaultGaomonPath + "\r\n\r\n" +
              "是否直接使用此配置？（点击“是”自动关联并沿用；点击“否”可手动浏览文件；“取消”跳过）"
            : "当前尚未配置数位板驱动配置文件。\r\n\r\n" +
              "复现器支持读取驱动配置以还原原厂压感曲线（如三次贝塞尔曲线或 Wacom TipFeel 软硬度调节）及物理量程。\r\n\r\n" +
              "支持格式：\r\n" +
              "1. 高漫 / 绘王配置（*.dt, *.json，如 EKeySetting.dt）\r\n" +
              "2. Wacom 备份文件（*.wacomprefs, *.wacomxs, *.xml, *.prefs）\r\n\r\n" +
              "是否现在选择您的驱动配置文件？（选择后将自动保存并一直沿用）";

        if (File.Exists(defaultGaomonPath))
        {
            var res = MessageBox.Show(this, promptText, "配置数位板驱动特性", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (res == DialogResult.Yes)
            {
                LoadDriverConfigFile(defaultGaomonPath);
            }
            else if (res == DialogResult.No)
            {
                ChooseDriverConfigFile();
            }
        }
        else
        {
            var res = MessageBox.Show(this, promptText, "配置数位板驱动特性", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res == DialogResult.Yes)
            {
                ChooseDriverConfigFile();
            }
        }
    }

    private void ChooseDriverConfigFile()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择数位板驱动配置文件（支持高漫/绘王及 Wacom 路径1 备份）",
            Filter = "数位板配置文件 (*.dt;*.json;*.wacomprefs;*.wacomxs;*.xml;*.prefs)|*.dt;*.json;*.wacomprefs;*.wacomxs;*.xml;*.prefs|" +
                     "高漫/绘王驱动配置 (*.dt;*.json)|*.dt;*.json|" +
                     "Wacom 备份配置 (*.wacomprefs;*.wacomxs;*.xml;*.prefs)|*.wacomprefs;*.wacomxs;*.xml;*.prefs|" +
                     "所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            LoadDriverConfigFile(picker.FileName);
        }
    }

    private void LoadDriverConfigFile(string path)
    {
        try
        {
            var profile = DriverProfileParser.Parse(path);
            _driverProfile = profile;
            _settings.DriverConfigPath = Path.GetFullPath(path);
            _settings.Save();

            if (profile.RecommendedPressureMax.HasValue && profile.RecommendedPressureMax.Value > 0)
            {
                _pressureMax.Value = (decimal)Math.Clamp(profile.RecommendedPressureMax.Value, 1, 65535);
            }

            UpdateDriverDisplay();
            MessageBox.Show(this,
                $"成功载入驱动配置！\r\n\r\n" +
                $"厂商：{profile.Vendor}\r\n" +
                $"设备：{profile.DeviceName}\r\n" +
                $"特性：{profile.CurveSummary}\r\n" +
                $"推荐压力上限：{profile.RecommendedPressureMax ?? 16383}\r\n\r\n" +
                $"该配置路径已保存，后续运行将一直沿用。",
                "驱动配置已生效", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法解析该驱动配置文件：\r\n{ex.Message}", "加载驱动配置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearDriverConfig()
    {
        _driverProfile = null;
        _settings.DriverConfigPath = null;
        _settings.Save();
        UpdateDriverDisplay();
    }

    private void UpdateDriverDisplay()
    {
        if (_driverProfile != null)
        {
            _driverSummary.Text = $"当前驱动：{_driverProfile.DeviceName} ({_driverProfile.Vendor})\r\n" +
                                  $"特性曲线：{_driverProfile.CurveSummary}\r\n" +
                                  $"配置路径：{_driverProfile.ConfigPath}";
            _driverSummary.ForeColor = Color.FromArgb(20, 83, 45); // 深绿色
            _changeDriverBtn.Text = "更换配置…";
            _clearDriverBtn.Enabled = true;
            _enableDriverCurve.Enabled = true;
        }
        else
        {
            _driverSummary.Text = "未配置驱动文件（当前使用默认纯线性映射）。\r\n点击右侧“选择配置…”关联高漫/绘王或 Wacom 配置文件。";
            _driverSummary.ForeColor = Color.FromArgb(100, 116, 139); // 灰色
            _changeDriverBtn.Text = "选择配置…";
            _clearDriverBtn.Enabled = false;
            _enableDriverCurve.Enabled = false;
        }
    }

    private void ChooseFile()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择笔迹 JSON 文件",
            Filter = "笔迹 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            LoadFile(picker.FileName);
    }

    private void LoadFile(string path)
    {
        try
        {
            if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择笔迹 JSON 文件。");
            var session = Program.ReadSession(path);
            var strokes = session.Segments.SelectMany(s => s.Strokes).Where(s => s.Points.Count > 0).ToArray();
            var points = strokes.SelectMany(s => s.Points).Where(p => p.InContact).ToArray();
            if (points.Length == 0)
                throw new InvalidDataException("这个文件没有可重放的接触点。");

            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            var first = points.Min(p => p.TimestampMs);
            var last = points.Max(p => p.TimestampMs);
            _selectedFile = Path.GetFullPath(path);
            _filePath.Text = _selectedFile;
            _summary.Text = string.Join(Environment.NewLine,
                $"设备：{session.Header.Device.Name}（{session.Header.Device.Id}）",
                $"笔划：{strokes.Length}    接触点：{points.Length}    时长：{(last - first) / 1000d:0.##} 秒",
                FormattableString.Invariant($"作用范围：X {minX:0.###}–{maxX:0.###}    Y {minY:0.###}–{maxY:0.###}"),
                "确认位置后会关闭冻结画面，并将笔迹发送到 CSP。");
            _start.Enabled = !_busy;
            _status.Text = "文件已载入；确认 CSP 画布和画笔状态后开始。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法读取笔迹文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void StartReplay(object? sender, EventArgs e)
    {
        if (_selectedFile is null || _busy) return;
        _busy = true;
        _start.Enabled = false;
        _stop.Enabled = true;
        _pressureMax.Enabled = false;
        _speed.Enabled = false;
        _changeDriverBtn.Enabled = false;
        _clearDriverBtn.Enabled = false;
        _enableDriverCurve.Enabled = false;

        _settings.PressureMax = (double)_pressureMax.Value;
        _settings.Speed = (double)_speed.Value;
        _settings.Save();

        _replayCancellation = new CancellationTokenSource();
        WindowState = FormWindowState.Minimized;

        try
        {
            bool completed = await Program.PlaceAndReplayAsync(
                _selectedFile,
                (double)_pressureMax.Value,
                (double)_speed.Value,
                message => _status.Text = message,
                _replayCancellation.Token,
                _driverProfile,
                _enableDriverCurve.Checked);
            _status.Text = completed ? "回放完成。" : "已取消定位，没有回放。";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "回放已停止。";
        }
        catch (Exception ex)
        {
            _status.Text = "操作失败。";
            MessageBox.Show(this, ex.Message, "StrokeReplay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _replayCancellation?.Dispose();
            _replayCancellation = null;
            _busy = false;
            _stop.Enabled = false;
            _pressureMax.Enabled = true;
            _speed.Enabled = true;
            _changeDriverBtn.Enabled = true;
            _clearDriverBtn.Enabled = _driverProfile != null;
            _enableDriverCurve.Enabled = _driverProfile != null;
            _start.Enabled = _selectedFile is not null;
            if (!IsDisposed)
            {
                WindowState = FormWindowState.Normal;
                Activate();
            }
        }
    }

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            LoadFile(files[0]);
    }
}
