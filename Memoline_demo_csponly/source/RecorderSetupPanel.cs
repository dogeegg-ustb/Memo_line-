using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal sealed record RecorderInputSelection(bool PassivePen, string? DeviceId, string? DriverPath,
    bool DriverDisabled, int? ScreenIndex)
{
    internal string[] Arguments => PassivePen ? ["--passive-pen"]
        : DeviceId is { Length: > 0 } id ? ["--tablet-device-id", id] : [];
    internal void Apply(JsonObject settings)
    {
        settings["driverConfigPath"] = DriverPath;
        settings["driverMappingDisabled"] = DriverDisabled;
        settings["driverScreenIndex"] = ScreenIndex;
    }
}

internal sealed class RecorderSetupPanel : UserControl
{
    private sealed record Choice(string Text, string? Value = null, int? Index = null)
    { public override string ToString() => Text; }
    private readonly ComboBox _source = Combo(), _device = Combo(), _driver = Combo(), _screen = Combo();
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new(800, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new(800, 0), Text = "正在读取 Recognizer 的设备和驱动…" };
    private readonly Button _refresh = new() { Text = "刷新设备和驱动", AutoSize = true };
    private readonly Button _file = new() { Text = "选择驱动文件…", AutoSize = true };
    private readonly string _recorderRoot, _selectionPath;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loading;
    private bool _ready;
    private bool _renderOnly;
    private RecorderInputSelection _saved;
    internal RecorderSetupPanel(string recorderRoot, bool renderOnly = false)
    {
        _recorderRoot = recorderRoot; _renderOnly = renderOnly;
        _selectionPath = Path.Combine(Path.GetDirectoryName(recorderRoot)!, "input-settings.json");
        _saved = ReadSaved();
        Padding = new(16); AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 8 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 110)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(int row, string name, Control control)
        {
            layout.RowStyles.Add(new(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new(3, 9, 3, 12) }, 0, row);
            control.Margin = new(3, 5, 3, 12); control.Dock = DockStyle.Top; layout.Controls.Add(control, 1, row);
        }
        _source.Items.AddRange([new Choice("OTD 数位板原始输入（压力、倾斜）", "otd"),
            new Choice("Windows 笔输入", "windows")]);
        _source.SelectedIndex = _saved.PassivePen ? 1 : 0;
        Row(0, "笔输入来源", _source); Row(1, "数位板设备", _device);
        Row(2, "驱动配置", _driver); Row(3, "映射屏幕", _screen);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        actions.Controls.AddRange([_refresh, _file]); Row(4, "", actions);
        Row(5, "配置详情", _details); Row(6, "录制状态", _status);
        Row(7, "", new Label { AutoSize = true, MaximumSize = new(800, 0),
            Text = "选择当前 .clip 后点击「开始录制」。随后完成 Recognizer 的画布初始化；初始化完成后才开始记录输入。驱动的压力和坐标映射由原 Recognizer 应用。" });
        Controls.Add(layout);
        _source.SelectedIndexChanged += (_, _) => UpdateDetails();
        _device.SelectedIndexChanged += (_, _) => UpdateDetails();
        _driver.SelectedIndexChanged += (_, _) => UpdateDetails();
        _refresh.Click += async (_, _) => await RefreshSafelyAsync();
        _file.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "选择数位板驱动配置",
                Filter = "驱动配置|*.dt;*.json;*.xml;*.wacomprefs;*.wacomxs;*.prefs;*.dat|所有文件|*.*" };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            {
                var choice = new Choice("手动选择 · " + Path.GetFileName(dialog.FileName), dialog.FileName);
                _driver.Items.Add(choice); _driver.SelectedItem = choice;
            }
        };
        Load += async (_, _) =>
        {
            if (_renderOnly) { ShowRenderPreview(); return; }
            if (_loading is null) await RefreshSafelyAsync();
        };
    }

    private static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private RecorderInputSelection ReadSaved()
    {
        try
        {
            if (File.Exists(_selectionPath))
                return JsonSerializer.Deserialize<RecorderInputSelection>(File.ReadAllText(_selectionPath)) ?? new(false, null, null, false, null);
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(_recorderRoot, "integration", "settings.json")))!;
            return new(false, null, settings["driverConfigPath"]?.GetValue<string>(),
                settings["driverMappingDisabled"]?.GetValue<bool>() ?? false, settings["driverScreenIndex"]?.GetValue<int>());
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { return new(false, null, null, false, null); }
    }

    private async Task RefreshSafelyAsync()
    {
        if (_loading is { IsCompleted: false }) { await _loading; return; }
        _loading = RefreshAsync();
        try { await _loading; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) { _ready = false; _status.Text = "设备查询失败：" + ex.Message; } }
    }
    private async Task RefreshAsync()
    {
        _refresh.Enabled = _file.Enabled = false; _status.Text = "正在查询设备和驱动…";
        try
        {
            var catalog = await QueryAsync(_recorderRoot, ["--input-catalog"], _lifetime.Token);
            if (IsDisposed) return;
            string? previousDevice = _device.SelectedItem is Choice deviceChoice ? deviceChoice.Value : _saved.DeviceId;
            var previousDriver = _driver.SelectedItem as Choice;
            _device.Items.Clear(); _device.Items.Add(new Choice("自动检测数位板"));
            foreach (var device in J.Get(catalog, "devices").EnumerateArray())
                _device.Items.Add(new Choice($"{J.Text(device, "name")} · 压力 {J.Tick(device, "maxPressure", 0)}",
                    J.Text(device, "deviceId")));
            Select(_device, previousDevice);
            if (previousDevice is not null && (_device.SelectedItem as Choice)?.Value != previousDevice)
            { var absent = new Choice("未连接 · " + previousDevice, previousDevice); _device.Items.Add(absent); _device.SelectedItem = absent; }
            _driver.Items.Clear(); _driver.Items.Add(new Choice("不使用驱动映射（保留原始数据）"));
            foreach (var driver in J.Get(J.Get(catalog, "driver"), "choices").EnumerateArray())
                _driver.Items.Add(new Choice($"{J.Text(driver, "vendor")} · {J.Text(driver, "deviceName")}", J.Text(driver, "path")));
            string? path = previousDriver is null ? _saved.DriverPath : previousDriver.Value;
            if (path is not null && !_driver.Items.Cast<Choice>().Any(c => c.Value == path))
                _driver.Items.Add(new Choice("手动配置 · " + Path.GetFileName(path), path));
            if (path is not null || previousDriver is not null || _saved.DriverDisabled) Select(_driver, path);
            else _driver.SelectedIndex = -1;
            int? previousScreen = _screen.SelectedItem is Choice screenChoice ? screenChoice.Index : _saved.ScreenIndex;
            _screen.Items.Clear(); _screen.Items.Add(new Choice("使用驱动配置中的屏幕区域（单屏自动）"));
            foreach (var display in J.Get(J.Get(catalog, "driver"), "displays").EnumerateArray())
                _screen.Items.Add(new Choice($"屏幕 {J.Tick(display, "index", 0) + 1} · {J.Text(display, "name")}",
                    Index: checked((int)J.Tick(display, "index", 0))));
            _screen.SelectedItem = _screen.Items.Cast<Choice>().FirstOrDefault(c => c.Index == previousScreen) ?? _screen.Items[0];
            _ready = true;
            string[] warnings = J.Get(catalog, "warnings").EnumerateArray()
                .Concat(J.Get(J.Get(catalog, "driver"), "warnings").EnumerateArray()).Select(w => w.GetString() ?? "").ToArray();
            _status.Text = $"已检测到 {J.Get(catalog, "devices").GetArrayLength()} 项数位板设备；Recognizer 尚未开始录制。"
                + (warnings.Length > 0 ? "\n" + string.Join("\n", warnings) : "");
            UpdateDetails();
        }
        finally { if (!IsDisposed) _refresh.Enabled = _file.Enabled = true; }
    }
    private static void Select(ComboBox combo, string? value)
        => combo.SelectedItem = combo.Items.Cast<Choice>().FirstOrDefault(c => c.Value == value) ?? combo.Items[0];
    private void UpdateDetails()
    {
        bool passive = _source.SelectedIndex == 1; _device.Enabled = !passive;
        _details.Text = (_driver.SelectedItem as Choice) is { } driver
            ? driver.Value ?? "保留原始压力和 Windows 屏幕坐标，不应用驱动映射。"
            : "请选择本次使用的驱动，或者明确选择不使用驱动映射。";
        if (passive) _details.Text += "\nWindows 笔模式使用系统事件，压力和倾斜可能不可用。";
    }

    internal async Task<RecorderInputSelection> PrepareAsync(CancellationToken token)
    {
        if (_loading is null) await RefreshSafelyAsync();
        else await _loading.WaitAsync(token);
        if (!_ready) throw new InvalidOperationException("设备和驱动查询尚未完成，请刷新后重试。");
        var driver = _driver.SelectedItem as Choice ?? throw new InvalidOperationException("请先在「录制设置」中选择驱动配置。");
        var selection = new RecorderInputSelection(_source.SelectedIndex == 1,
            _source.SelectedIndex == 1 ? null : (_device.SelectedItem as Choice)?.Value,
            driver.Value, driver.Value is null, (_screen.SelectedItem as Choice)?.Index);
        if (selection.DriverPath is { } path)
        {
            var snapshot = await QueryAsync(_recorderRoot, ["--diagnose-driver", "--driver-config", path], token);
            if (J.Text(snapshot, "status") != "selected")
                throw new InvalidOperationException("驱动配置不可用：" + snapshot.GetRawText());
        }
        await File.WriteAllTextAsync(_selectionPath, JsonSerializer.Serialize(selection), token);
        _saved = selection; _status.Text = "配置已确认，正在启动 Recognizer…";
        return selection;
    }
    internal void RecorderStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => RecorderStatus(text)); return; }
        _status.Text = text;
    }
    internal void ShowRenderPreview()
    {
        _device.Items.Clear(); _device.Items.Add(new Choice("自动检测数位板")); _device.SelectedIndex = 0;
        _driver.Items.Clear(); _driver.Items.Add(new Choice("不使用驱动映射（保留原始数据）")); _driver.SelectedIndex = 0;
        _screen.Items.Clear(); _screen.Items.Add(new Choice("使用驱动配置中的屏幕区域（单屏自动）")); _screen.SelectedIndex = 0;
        _status.Text = "界面渲染检查；未启动设备查询或录制。"; UpdateDetails();
    }
    internal static async Task<JsonElement> QueryAsync(string root, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(root, "BehaviorRecognizer.exe"))
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Recognizer 设备查询。");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false), error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Recognizer 查询失败：" + error + text);
            int offset = text.StartsWith('{') ? 0 : text.IndexOf("\n{", StringComparison.Ordinal) + 1;
            if (offset <= 0 && !text.StartsWith('{')) throw new InvalidDataException("Recognizer 未返回设备配置 JSON，请重新构建集成程序。");
            using var document = JsonDocument.Parse(text[offset..]);
            if (arguments[0] == "--input-catalog" && !string.IsNullOrWhiteSpace(error))
            {
                var catalog = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
                var warnings = catalog["warnings"]!.AsArray();
                foreach (string line in error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    warnings.Add(line);
                return JsonSerializer.SerializeToElement(catalog);
            }
            return document.RootElement.Clone();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { _lifetime.Cancel(); _lifetime.Dispose(); } base.Dispose(disposing); }
}
