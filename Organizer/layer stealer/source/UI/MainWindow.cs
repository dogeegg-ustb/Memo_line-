using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using LayerStealer.Capture;
using LayerStealer.Windows;

namespace LayerStealer.UI;

internal sealed record Settings(double CenterX = .5, double CenterY = .5, bool Calibrated = false, int DelayMs = 180)
{
    public Point CopyPoint(Rectangle bounds) => new(bounds.Left + (int)Math.Round(CenterX * (bounds.Width - 1)),
        bounds.Top + (int)Math.Round(CenterY * (bounds.Height - 1)));
}

internal sealed class MainWindow : Form
{
    private const int HotkeyId = 0x4C53;
    private readonly bool _testMode;
    private readonly ImagePreview _preview = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 72, AutoEllipsis = true, Padding = new(16, 8, 16, 4) };
    private readonly Label _position = new() { AutoSize = true, Padding = new(0, 8, 0, 0) };
    private readonly NumericUpDown _delay = new() { Minimum = 50, Maximum = 2000, Increment = 20, Width = 80 };
    private readonly Button _copy;
    private readonly Button _read;
    private readonly Button _calibrate;
    private readonly Button _save;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _targetTimer = new() { Interval = 250 };
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "layer-stealer-settings.json");
    private Settings _settings;
    private ClipboardFrame? _frame;
    private nint _lastCsp;
    private bool _busy;
    private bool _closing;
    private bool _registered;

    public MainWindow(bool testMode = false)
    {
        _testMode = testMode;
        _settings = testMode ? new() : LoadSettings();
        Text = "layer stealer";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new(1050, 740); MinimumSize = new(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 250);
        var header = new Label { Text = "layer stealer", Dock = DockStyle.Top, Height = 64, Padding = new(16, 12, 0, 0),
            Font = new Font(Font.FontFamily, 24, FontStyle.Bold), ForeColor = Color.FromArgb(46, 79, 124) };
        var subtitle = new Label { Text = "在绘图区中心按住 Ctrl → 右键 → C，将剪贴板中的图层图像呈现到这里。", Dock = DockStyle.Top,
            Height = 36, Padding = new(18, 4, 0, 0) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(12, 4, 12, 4) };
        _copy = Button("复制并呈现", async () => await CopyAsync());
        _read = Button("读取现有剪贴板", async () => await ReadAsync());
        _calibrate = Button("校准绘图区中心", async () => await CalibrateAsync());
        _save = Button("保存 PNG", SavePng); _save.Enabled = false;
        var fit = Button("适应窗口", () => _preview.SetFit(true));
        var original = Button("100%", () => _preview.SetFit(false));
        var topmost = new CheckBox { Text = "置顶预览", AutoSize = true, Margin = new(12, 12, 0, 0) };
        topmost.CheckedChanged += (_, _) => TopMost = topmost.Checked;
        actions.Controls.AddRange([_copy, _read, _calibrate, _save, fit, original, topmost]);
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(18, 3, 18, 10) };
        _delay.Value = _settings.DelayMs;
        _delay.ValueChanged += (_, _) => { _settings = _settings with { DelayMs = (int)_delay.Value }; SaveSettings(); };
        options.Controls.AddRange([_position, new Label { Text = "右键后等待", AutoSize = true, Margin = new(24, 8, 3, 0) }, _delay,
            new Label { Text = "ms", AutoSize = true, Padding = new(0, 8, 0, 0) }]);
        Controls.Add(_preview); Controls.Add(_status); Controls.Add(options); Controls.Add(actions); Controls.Add(subtitle); Controls.Add(header);
        UpdatePosition();
        SetStatus("就绪 · Ctrl + Alt + F8 触发复制 · 复制会覆盖系统剪贴板。");
        _targetTimer.Tick += (_, _) =>
        {
            if (_busy) return;
            nint foreground = Native.GetForegroundWindow();
            if (CspTarget.IsCsp(foreground)) _lastCsp = foreground;
        };
        if (!testMode) _targetTimer.Start();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new(8, 6, 8, 6), Margin = new(4), UseVisualStyleBackColor = true };
        button.Click += (_, _) => action();
        return button;
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_testMode) return;
        _registered = Native.RegisterHotKey(Handle, HotkeyId, 0x4003, 0x77); // Ctrl+Alt+F8, MOD_NOREPEAT
        if (!_registered) SetStatus("Ctrl + Alt + F8 已被其他程序占用，请使用「复制并呈现」按钮。");
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_registered) Native.UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == HotkeyId && !_busy) _ = CopyAsync();
        base.WndProc(ref m);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V)) { if (!_busy) _ = ReadAsync(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { if (!_busy) SavePng(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task CopyAsync()
    {
        if (_busy || _closing) return;
        SetBusy(true);
        try
        {
            var target = CspTarget.Find(_lastCsp);
            CopyTrace.Write($"copy requested target=0x{target.Window:X} pid={target.ProcessId}");
            _lastCsp = target.Window;
            Hide();
            // Calculate the point after restoring a minimized CSP window.
            await target.ActivateAsync(_lifetime.Token);
            var point = _settings.CopyPoint(target.ClientBounds());
            uint previous = await CopyScript.RunAsync(target, point, _settings.DelayMs, _lifetime.Token);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10000)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var frame = ClipboardImageReader.TryRead(Handle, previous, target.ProcessId);
                if (frame is not null)
                {
                    Present(frame);
                    CopyTrace.Write($"clipboard read {frame.Image.Width}x{frame.Image.Height} format={frame.Format} owner={frame.OwnerProcessId} sequence={frame.Sequence}");
                    SetStatus($"已复制并呈现：{frame.Image.Width} × {frame.Image.Height} px · {frame.Format} · {watch.ElapsedMilliseconds} ms · {target.Title}");
                    return;
                }
                await Task.Delay(80, _lifetime.Token);
            }
            SetStatus("未收到 CSP 本次复制的新图像。请检查图层／选区和复制位置；也可手动复制后点「读取现有剪贴板」。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CopyTrace.Write("copy failed: " + ex); SetStatus("复制失败：" + ex.Message); }
        finally { RestoreWindow(); SetBusy(false); }
    }

    private async Task ReadAsync()
    {
        if (_busy || _closing) return;
        SetBusy(true);
        try
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var frame = ClipboardImageReader.TryRead(Handle);
                if (frame is not null) { Present(frame); SetStatus($"已呈现剪贴板：{frame.Image.Width} × {frame.Image.Height} px · {frame.Format}"); return; }
                await Task.Delay(80, _lifetime.Token);
            }
            SetStatus("剪贴板里没有可读取的图像。请先在 CSP 中复制图层。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("读取失败：" + ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task CalibrateAsync()
    {
        if (_busy || _closing) return;
        SetBusy(true);
        try
        {
            var target = CspTarget.Find(_lastCsp);
            _lastCsp = target.Window;
            Hide();
            await target.ActivateAsync(_lifetime.Token);
            using var picker = new CenterPicker();
            if (picker.ShowDialog() != DialogResult.OK) return;
            var bounds = target.ClientBounds();
            var point = picker.SelectedPoint;
            if (!bounds.Contains(point)) throw new InvalidOperationException("请选择 CSP 绘图区内的位置。");
            _settings = _settings with { CenterX = (point.X - bounds.Left) / (double)Math.Max(1, bounds.Width - 1),
                CenterY = (point.Y - bounds.Top) / (double)Math.Max(1, bounds.Height - 1), Calibrated = true };
            SaveSettings(); UpdatePosition();
            SetStatus("已校准复制位置。CSP 窗口移动／缩放后会按相对位置更新；调整面板布局后请重新校准。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("校准失败：" + ex.Message); }
        finally { RestoreWindow(); SetBusy(false); }
    }

    internal void Present(ClipboardFrame frame)
    {
        var previous = _frame;
        _frame = frame;
        _preview.SetImage(frame.Image);
        previous?.Dispose();
        _save.Enabled = !_busy;
    }
    private void SavePng()
    {
        if (_frame is null) return;
        using var dialog = new SaveFileDialog { Filter = "PNG 图像|*.png", FileName = $"layer-{DateTime.Now:yyyyMMdd-HHmmss}.png", DefaultExt = "png", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _frame.Image.Save(dialog.FileName, ImageFormat.Png); SetStatus("已保存：" + dialog.FileName); }
        catch (Exception ex) { SetStatus("保存失败：" + ex.Message); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closing) return;
        _copy.Enabled = _read.Enabled = _calibrate.Enabled = _delay.Enabled = !busy;
        _save.Enabled = !busy && _frame is not null;
    }
    private void RestoreWindow() { if (!_closing) { Show(); Activate(); } }
    private void SetStatus(string text) { if (!_closing) _status.Text = text; }
    private void UpdatePosition() => _position.Text = _settings.Calibrated
        ? $"复制位置：已校准 ({_settings.CenterX:P0}, {_settings.CenterY:P0})" : "复制位置：CSP 客户区中央";
    private Settings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)) is { } settings
                && double.IsFinite(settings.CenterX) && double.IsFinite(settings.CenterY)
                && settings.CenterX is >= 0 and <= 1 && settings.CenterY is >= 0 and <= 1 && settings.DelayMs is >= 50 and <= 2000)
                return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }
    private void SaveSettings()
    {
        if (_testMode) return;
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("设置本次已生效，但未能保存：" + ex.Message); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true; _lifetime.Cancel(); _targetTimer.Stop();
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _targetTimer.Dispose(); _preview.SetImage(null); _frame?.Dispose(); _frame = null; }
        base.Dispose(disposing);
    }
    internal void SaveUiPreview(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path, ImageFormat.Png);
    }
}
