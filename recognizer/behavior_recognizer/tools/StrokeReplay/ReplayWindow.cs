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
    private readonly NumericUpDown _pressureMax = new();
    private readonly NumericUpDown _speed = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private string? _selectedFile;
    private CancellationTokenSource? _replayCancellation;
    private bool _busy;

    public ReplayWindow(string? initialFile)
    {
        Text = "StrokeReplay · Windows 笔迹复现器";
        MinimumSize = new Size(680, 490);
        Size = new Size(780, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);
        AllowDrop = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        Controls.Add(root);

        var title = new Label
        {
            Text = "笔迹复现器",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 20, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 44, 61),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

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

        var infoGroup = new GroupBox { Text = "笔迹信息", Dock = DockStyle.Fill, Padding = new Padding(12) };
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.ForeColor = Color.FromArgb(58, 70, 88);
        _summary.Text = "选择笔迹 JSON 后显示设备、笔划数和坐标作用范围。\r\n也可以把 JSON 文件拖到这个窗口。";
        infoGroup.Controls.Add(_summary);
        root.Controls.Add(infoGroup, 0, 2);

        var settings = new GroupBox { Text = "回放设置", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var settingsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settingsGrid.Controls.Add(new Label { Text = "压力上限", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _pressureMax.Minimum = 1;
        _pressureMax.Maximum = 65535;
        _pressureMax.Value = 16383;
        _pressureMax.Dock = DockStyle.Fill;
        settingsGrid.Controls.Add(_pressureMax, 1, 0);
        settingsGrid.Controls.Add(new Label { Text = "回放速度", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) }, 2, 0);
        _speed.Minimum = 0.1M;
        _speed.Maximum = 10;
        _speed.DecimalPlaces = 1;
        _speed.Increment = 0.1M;
        _speed.Value = 1;
        _speed.Dock = DockStyle.Fill;
        settingsGrid.Controls.Add(_speed, 3, 0);
        settings.Controls.Add(settingsGrid);
        root.Controls.Add(settings, 0, 3);

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
        root.Controls.Add(footer, 0, 4);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += (_, _) => _replayCancellation?.Cancel();

        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
            LoadFile(initialFile);
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
        _replayCancellation = new CancellationTokenSource();
        WindowState = FormWindowState.Minimized;

        try
        {
            bool completed = await Program.PlaceAndReplayAsync(
                _selectedFile,
                (double)_pressureMax.Value,
                (double)_speed.Value,
                message => _status.Text = message,
                _replayCancellation.Token);
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
            _replayCancellation.Dispose();
            _replayCancellation = null;
            _busy = false;
            _stop.Enabled = false;
            _pressureMax.Enabled = true;
            _speed.Enabled = true;
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
