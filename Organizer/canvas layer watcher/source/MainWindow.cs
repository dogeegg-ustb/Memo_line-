using System.Text.Json;

namespace CanvasLayerWatcher;

internal sealed record Settings(string Clip = "", string Endpoint = "");

internal sealed class MainWindow : Form
{
    private readonly TextBox _clip = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "自动发现，也可填写 .live.json 路径或管道名" };
    private readonly Button _start = new() { Text = "开始监听", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "先选择与 Recognizer 录制配置一致的 .clip 文件" };
    private readonly Label _info = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "等待图层图像" };
    private readonly Preview _preview = new() { Dock = DockStyle.Fill };
    private readonly DiffViewer _diff = new();
    private readonly TabControl _views = new() { Dock = DockStyle.Fill };
    private readonly TabPage _layerPage = new("完整图层");
    private readonly TabPage _diffPage = new("图像差异");
    private CancellationTokenSource? _stopSource;
    private Task? _monitorTask;
    private readonly HashSet<Task> _captureTasks = [];
    private readonly SemaphoreSlim _captureSerial = new(1, 1);
    private readonly List<Control> _configurationControls = [];
    private RecognizerMonitor? _monitor;
    private Bitmap? _image;
    private SnapshotHistory? _history;
    private readonly IWatcherHost? _host;
    private string _diffRoot => _host?.DiffRoot ?? Path.Combine(AppContext.BaseDirectory, "layer-diffs");
    private bool _closing;
    private bool _closeReady;
    private bool _stopping;
    private CancellationTokenSource? _startingSource;
    private Task? _startingTask, _stoppingTask;
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "watcher-settings.json");
    private readonly object _logSync = new();
    protected override bool ShowWithoutActivation => true;

    public MainWindow(string[] args, IWatcherHost? host = null)
    {
        _host = host;
        if (host is not null)
        {
            _settingsPath = host.SettingsPath;
            _start.Text = "开始录制"; _stop.Text = "停止并封盘";
            _status.Text = "选择在 CSP 中打开的 .clip 文件，然后开始录制";
            _endpoint.ReadOnly = true;
            _endpoint.PlaceholderText = "开始录制后自动连接集成的 Recognizer";
            host.Status += Status;
        }
        Text = host?.Title ?? "画布视图切换 · 图层与差异"; Size = new(980, 780); MinimumSize = new(740, 680);
        StartPosition = FormStartPosition.CenterScreen; Font = new("Microsoft YaHei UI", 9);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new(12) };
        layout.RowStyles.Add(new(SizeType.Absolute, 78));
        layout.RowStyles.Add(host is null ? new(SizeType.Absolute, 38) : new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Absolute, 40)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 32));
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        paths.ColumnStyles.Add(new(SizeType.Absolute, 85)); paths.ColumnStyles.Add(new(SizeType.Percent, 100)); paths.ColumnStyles.Add(new(SizeType.Absolute, 86));
        paths.RowStyles.Add(new(SizeType.Percent, 50)); paths.RowStyles.Add(new(SizeType.Percent, 50));
        var choose = new Button { Text = "文件…", Dock = DockStyle.Fill };
        choose.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = "CLIP 文件|*.clip", FileName = _clip.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) _clip.Text = dialog.FileName; };
        var endpointChoose = new Button { Text = "接口…", Dock = DockStyle.Fill };
        _configurationControls.AddRange([choose, _clip, _endpoint]);
        if (host is null) _configurationControls.Add(endpointChoose);
        else endpointChoose.Enabled = false;
        endpointChoose.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = "Recognizer 实时接口|*.memoline.live.json|JSON|*.json" }; if (dialog.ShowDialog(this) == DialogResult.OK) _endpoint.Text = dialog.FileName; };
        paths.Controls.Add(new Label { Text = ".clip", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        paths.Controls.Add(_clip, 1, 0); paths.Controls.Add(choose, 2, 0);
        paths.Controls.Add(new Label { Text = "接口", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        paths.Controls.Add(_endpoint, 1, 1); paths.Controls.Add(endpointChoose, 2, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = host is not null, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var fit = new CheckBox { Text = "适应窗口", Checked = true, AutoSize = true, Margin = new(12, 8, 3, 3) };
        fit.CheckedChanged += (_, _) => { _preview.Fit = fit.Checked; _diff.Fit = fit.Checked; };
        var top = new CheckBox { Text = "置顶", AutoSize = true, Margin = new(12, 8, 3, 3) };
        top.CheckedChanged += (_, _) => TopMost = top.Checked;
        var export = new Button { Text = "另存 PNG", AutoSize = true };
        export.Click += (_, _) =>
        {
            bool diffView = _views.SelectedTab == _diffPage;
            var selected = diffView ? _diff.DisplayedImage : _image; if (selected is null) return;
            // Freeze the image before the modal dialog can process another capture.
            using var image = (Bitmap)selected.Clone();
            using var dialog = new SaveFileDialog { Filter = "PNG|*.png", FileName = diffView ? "difference.png" : "layer.png" };
            if (dialog.ShowDialog(this) == DialogResult.OK) image.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
        };
        var packets = new Button { Text = "差异包", AutoSize = true };
        packets.Click += (_, _) =>
        {
            Directory.CreateDirectory(_diffRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_diffRoot) { UseShellExecute = true });
        };
        buttons.Controls.AddRange([_start, _stop, fit, top, export, packets]);
        if (host is not null)
        {
            var recordings = new Button { Text = "录制文件", AutoSize = true };
            recordings.Click += (_, _) =>
            {
                Directory.CreateDirectory(host.OutputDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(host.OutputDirectory) { UseShellExecute = true });
            };
            buttons.Controls.Add(recordings);
        }
        _layerPage.Controls.Add(_preview); _diffPage.Controls.Add(_diff); _views.TabPages.AddRange([_layerPage, _diffPage]);
        _diff.Error += Status; _diff.PacketOpened += () => { _views.SelectedTab = _diffPage; _info.Text = "正在查看已保存的差异包"; };
        _start.Click += async (_, _) => await (_startingTask = StartAsync()); _stop.Click += async (_, _) => await StopAsync();
        layout.Controls.Add(paths, 0, 0); layout.Controls.Add(buttons, 0, 1); layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_views, 0, 3); layout.Controls.Add(_info, 0, 4); Controls.Add(layout);
        try { var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)); _clip.Text = saved?.Clip ?? ""; _endpoint.Text = saved?.Endpoint ?? ""; } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        for (int i = 0; i + 1 < args.Length; i++) { if (args[i] == "--clip") _clip.Text = args[++i]; else if (args[i] == "--endpoint") _endpoint.Text = args[++i]; }
        int diffArgument = Array.IndexOf(args, "--diff");
        if (diffArgument >= 0 && diffArgument + 1 < args.Length)
            Shown += async (_, _) =>
            {
                try { var packet = await Task.Run(() => DiffViewPacket.Read(args[diffArgument + 1])); if (IsDisposed) return; _diff.Display(packet); _views.SelectedTab = _diffPage; }
                catch (Exception ex) { Status("无法打开差异包：" + ex.Message); }
            };
        FormClosing += async (_, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _startingSource?.Cancel();
            if (_startingTask is not null) await _startingTask;
            await StopAsync(); _image?.Dispose(); _closeReady = true; Close();
        };
    }
    private async Task StartAsync()
    {
        if (_stopSource is not null) return;
        try
        {
            string clip = Path.GetFullPath(_clip.Text), bridge = Path.Combine(AppContext.BaseDirectory, "clip-layer-bridge.exe");
            if (!File.Exists(clip) || !clip.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择已保存的 .clip 文件");
            if (!File.Exists(bridge)) throw new IOException("缺少 clip-layer-bridge.exe，请先运行 Build.ps1");
            // Parse once before enabling automatic saves, so unsupported files fail without input injection.
            _start.Enabled = false;
            using var validation = new CancellationTokenSource(_host is null ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(3));
            _startingSource = validation;
            await Bridge.RunAsync(bridge, ["inspect", clip], validation.Token);
            if (_host is not null) _endpoint.Text = await _host.PrepareAsync(clip, validation.Token);
            _startingSource = null;
            if (_closing || IsDisposed) return;
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings(clip, _endpoint.Text.Trim())));
            _history = new SnapshotHistory(_diffRoot);
            _stopSource = new(); _stop.Enabled = true;
            foreach (var control in _configurationControls) control.Enabled = false;
            _monitor = new(requireConfirmedViewChange: _host is not null); _monitor.Status += Status; _monitor.Diagnostic += WriteLog;
            _host?.Attach(_monitor);
            var monitor = _monitor; var token = _stopSource.Token;
            monitor.Capture += request =>
            {
                _captureTasks.RemoveWhere(t => t.IsCompleted);
                _host?.CaptureQueued(request);
                _captureTasks.Add(HandleCaptureAsync(monitor, clip, bridge, request, token));
            };
            _monitorTask = RunMonitorAsync(monitor, _endpoint.Text.Trim(), clip, token);
        }
        catch (Exception ex)
        {
            _startingSource = null; _start.Enabled = true;
            if (_host is not null)
                try { await _host.FinishRecordingAsync(CancellationToken.None); _stop.Enabled = _host.HasPendingSeal; }
                catch (Exception cleanup) { WriteLog("停止初始化中的 Recognizer：" + cleanup.Message); }
            Status("无法开始：" + ex.Message);
        }
    }
    private async Task RunMonitorAsync(RecognizerMonitor monitor, string endpoint, string clip, CancellationToken token)
    { try { await monitor.RunAsync(endpoint, clip, token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { } catch (Exception ex) { Status(ex.Message); } }
    private async Task HandleCaptureAsync(RecognizerMonitor monitor, string clip, string bridge, CaptureRequest request, CancellationToken token)
    {
        bool success = false;
        bool acquired = false;
        string failure = "保存或解析未完成";
        string? pendingPng = null;
        try
        {
            await _captureSerial.WaitAsync(token); acquired = true;
            var context = request.Context ?? throw new InvalidOperationException("缺少已固定的 triggerTicks 上下文");
            var control = new RecorderControlClient(context.ControlPipe, WriteLog);
            var result = await SaveCapture.CaptureAsync(clip, bridge, control, context.PixelSize,
                request, () => monitor.Current(request), () => monitor.InContact, Status, token);
            pendingPng = result.PngPath;
            if (token.IsCancellationRequested || !monitor.Current(request)) { SaveCapture.TryDelete(result.PngPath); return; }
            string session = context.SessionId;
            var history = _history ?? throw new InvalidOperationException("图层快照历史尚未建立");
            Status("图层已解析，生成脏矩阵与图像差异包…");
            var update = await Task.Run(() => history.Advance(result, request, session, context.Evidence.Snapshot,
                () => monitor.Current(request) && monitor.SessionId == session, token), token);
            if (_host is not null) await _host.PacketCommittedAsync(request, update, token);
            success = true; pendingPng = null;
            WriteLog($"差异包已提交：kind={(update.Baseline ? "baseline/after" : "diff/after+now")}，images={update.DiffImages}，labels={update.DirtyLabels}，triggerTicks={request.TriggerTicks}，saveDispatchedTicks={result.SaveDispatchedTicks}，path={update.PacketDirectory}");
            var packet = await Task.Run(() => DiffViewPacket.Read(Path.Combine(update.PacketDirectory, "manifest.json")), token);
            await InvokeAsync(() =>
            {
                    using var decoded = Image.FromFile(update.ImagePath); var bitmap = new Bitmap(decoded);
                    _preview.Image = bitmap; _image?.Dispose(); _image = bitmap;
                    using var metadata = JsonDocument.Parse(result.Metadata);
                    _info.Text = $"{result.LayerName} · ID {J.Tick(metadata.RootElement, "layerId", 0)} · {bitmap.Width} × {bitmap.Height} px · 差异 {update.DiffImages} / 标签 {update.DirtyLabels}";
                    _diff.Display(packet); _views.SelectedTab = update.Baseline ? _layerPage : _diffPage;
            });
            Status(update.Baseline ? $"已保存「{result.LayerName}」并建立 after 基准；继续监听"
                : $"已保存「{result.LayerName}」；生成 {update.DiffImages} 份图像差异，{update.DirtyLabels} 个影响范围标签；继续监听");
        }
        catch (OperationCanceledException) { failure = "等待文件更新、读取超时或取消"; if (!token.IsCancellationRequested) Status("等待文件更新或读取超时；保留上次图像，下次视图变化重试"); }
        catch (Exception ex) { failure = ex.Message; Status("本次未完成：" + ex.Message + "；下次视图变化重试"); }
        finally
        {
            if (!success && pendingPng is not null) SaveCapture.TryDelete(pendingPng);
            try { if (!success) _host?.CaptureFailed(request, failure); }
            finally
            {
                monitor.Complete(request, success);
                if (acquired) _captureSerial.Release();
            }
        }
    }
    private Task StopAsync()
        => _stoppingTask is { IsCompleted: false } ? _stoppingTask : _stoppingTask = StopCoreAsync();
    private async Task StopCoreAsync()
    {
        if (_stopping) return;
        if (_stopSource is null && _host?.HasPendingSeal != true) return;
        _stopping = true; _stop.Enabled = false; _start.Enabled = false;
        var stop = _stopSource;
        try
        {
            if (_host is not null)
            {
                if (_monitor is not null) _monitor.CaptureRequestsPaused = true;
                Status("停止接收新封包，等待已经接受的保存与差异任务…");
                Exception? captureError = null;
                try { await Task.WhenAll(_captureTasks); }
                catch (Exception ex) { captureError = ex; }
                await _host.FinishRecordingAsync(CancellationToken.None);
                if (captureError is not null) throw new IOException("聚集事件写入失败，保留两份中间文件以供恢复。", captureError);
            }
            if (stop is not null) await stop.CancelAsync();
            if (_monitorTask is not null) await _monitorTask;
            await Task.WhenAll(_captureTasks);
            if (_host is not null)
            {
                Status("校验原生事件与脏矩阵指针，封盘中…");
                string bundle = await _host.SealAsync(CancellationToken.None);
                Status("已封盘：" + bundle);
            }
            else Status("监听已停止");
        }
        catch (Exception ex) { Status("封盘未完成，原始记录与聚集文件已保留：" + ex.Message); }
        finally
        {
            if (stop is not null)
            {
                await stop.CancelAsync();
                if (_monitorTask is not null) await _monitorTask;
                stop.Dispose();
            }
            _captureTasks.Clear(); _stopSource = null; _monitor = null; _monitorTask = null;
            _start.Enabled = true; _stop.Enabled = _host?.HasPendingSeal == true; _stopping = false;
            foreach (var control in _configurationControls) control.Enabled = true;
        }
    }
    private void Ui(Action action)
    { if (IsDisposed || Disposing) return; if (InvokeRequired) BeginInvoke(action); else action(); }
    private void Status(string text) => Ui(() =>
    {
        _status.Text = text;
        WriteLog(text);
    });
    private void WriteLog(string text)
    {
        lock (_logSync)
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "watcher.log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    internal void RenderSmoke(string directory, string? manifest = null)
    {
        Directory.CreateDirectory(directory);
        void Render(string name)
        {
            using var image = new Bitmap(Width, Height); DrawToBitmap(image, new(Point.Empty, Size));
            image.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
        }
        Show(); Render("empty.png");
        _image = new Bitmap(320, 240);
        using (var g = Graphics.FromImage(_image))
        { g.Clear(Color.Transparent); using var brush = new SolidBrush(Color.FromArgb(192, 32, 128, 240)); g.FillEllipse(brush, 30, 30, 240, 170); }
        _preview.Image = _image; _info.Text = "测试图层 α · 320 × 240 px";
        _status.Text = "已保存并读取图层；继续监听"; Render("preview.png");
        Size = MinimumSize; Render("minimum.png");
        Size = new(980, 780);
        RenderDiffSmoke(directory, Render, manifest);
        _image.Dispose(); _image = null; _preview.Image = null; Hide();
    }
    private void RenderDiffSmoke(string directory, Action<string> render, string? manifest)
    {
        void WaitForImage()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!_diff.Rendering.IsCompleted)
            {
                Application.DoEvents(); Thread.Sleep(10);
                if (elapsed.ElapsedMilliseconds > 10000) throw new TimeoutException("差异窗口渲染超时");
            }
            if (_diff.LastError is { } error) throw new InvalidOperationException("差异窗口渲染失败", error);
            Application.DoEvents();
        }
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        string inputs = Path.Combine(directory, "diff-inputs"); Directory.CreateDirectory(inputs);
        string after = Path.Combine(inputs, "after.png"), now = Path.Combine(inputs, "now.png");
        using (var a = new Bitmap(640, 480))
        using (var b = new Bitmap(640, 480))
        {
            using (var g = Graphics.FromImage(a)) { g.Clear(Color.Transparent); g.FillEllipse(Brushes.Crimson, 70, 70, 90, 60); }
            using (var g = Graphics.FromImage(b))
            { g.Clear(Color.Transparent); g.FillEllipse(Brushes.DodgerBlue, 80, 78, 90, 60); g.FillRectangle(Brushes.LimeGreen, 410, 310, 75, 50); }
            a.Save(after, System.Drawing.Imaging.ImageFormat.Png); b.Save(now, System.Drawing.Imaging.ImageFormat.Png);
        }
        var states = new[] { new RecognizerState("brush", "core.brushState", 1, 1, JsonSerializer.SerializeToElement(new { state = new { name = "示例笔刷" } })),
            new RecognizerState("color", "core.colorState", 1, 1, JsonSerializer.SerializeToElement(new { state = new { hex = "#1E90FF" } })) };
        var window = new EvidenceWindow("smoke", 1000, 0, 10, true, states, [], ["brush", "color"]);
        var evidence = new DirtyEvidence(window, [new("stroke", 1, 1, 10, "tablet", ["brush", "color"],
            new([new(new(60, 60), new(175, 145))], 24), [])], []);
        var difference = LayerDiff.Compare(after, now, evidence, inputs, CancellationToken.None);
        var packet = new DiffViewPacket(inputs, false, new(640, 480), "测试图层", difference.Images, difference.Labels, states, now);
        _views.SelectedTab = _diffPage; _diff.Display(packet); WaitForImage();
        _info.Text = $"本轮差异 · {difference.Images.Length} 个区域 · {difference.ChangedPixels} 个变化像素";
        _status.Text = "已保存图层并显示本轮图像差异；继续监听";
        Check(_diff.RegionCount == difference.Images.Length + 1 && difference.Images.Length >= 2, "差异区域列表不完整");
        using (var expected = LayerDiff.Load(Path.Combine(inputs, difference.Images[0].Image)))
            Check(_diff.DisplayedImage?.Size == expected.Size && _diff.DisplayedImage.GetPixel(30, 30).ToArgb() == expected.GetPixel(30, 30).ToArgb(), "默认未显示实际更改图像");
        Check(_diff.DisplayedCanvas?.Size == new Size(640, 480) && _diff.LocatedBounds == DiffViewPacket.Box(difference.Images[0].Bounds)
            && !_diff.RangesShown, "完整画布总览或默认定位框不符");
        render("diff-primary.png");
        var canvas = _diff.DisplayedCanvas;
        _diff.Select(0, DiffDisplayMode.Now); WaitForImage(); render("diff-all.png");
        Check(ReferenceEquals(canvas, _diff.DisplayedCanvas), "切换区域时完整画布不应改变");
        _diff.Select(1, DiffDisplayMode.Difference); WaitForImage(); render("diff-region.png");
        _diff.Select(1, DiffDisplayMode.After); WaitForImage(); render("diff-after.png");
        using (var expected = (Bitmap)Image.FromFile(Path.Combine(inputs, difference.Images[0].AfterImage)))
            Check(_diff.DisplayedImage?.GetPixel(0, 0).ToArgb() == expected.GetPixel(0, 0).ToArgb(), "修改前图像显示不符");
        _diff.Select(1, DiffDisplayMode.Now); WaitForImage(); render("diff-now.png");
        _diff.Select(1, DiffDisplayMode.Mask); WaitForImage(); render("diff-mask.png");
        _diff.Select(1, DiffDisplayMode.Now); WaitForImage(); Size = MinimumSize; render("diff-minimum.png");
        _diff.Display(packet with { Images = [], Labels = [] }); WaitForImage(); render("diff-unchanged.png");
        Check(_diff.DisplayedImage is null, "没有变化时仍显示旧差异");
        _diff.Display(packet with { Baseline = true, Images = [], Labels = [] }); WaitForImage(); render("diff-baseline.png");
        Check(_diff.DisplayedImage is null, "基准快照伪造了差异图像");
        _diff.Display(packet); _diff.Select(1, DiffDisplayMode.After); _diff.Select(1, DiffDisplayMode.Now);
        WaitForImage(); Check(_diff.LastError is null && _diff.DisplayedImage is not null, "快速切换差异视图失败");
        if (manifest is not null)
        {
            Size = new(980, 780); var recorded = DiffViewPacket.Read(manifest);
            _diff.Display(recorded); WaitForImage();
            _info.Text = $"{recorded.LayerName} · 完整画布 {recorded.Canvas.Width}×{recorded.Canvas.Height} px · 更改 {recorded.Images.Length} 个区域";
            _status.Text = "已打开已有差异包"; render("diff-recorded.png");
            Check(recorded.Images.Length == 0 || _diff.LocatedBounds == DiffViewPacket.Box(recorded.Images[0].Bounds), "实际差异包的完整画布定位不符");
        }
    }
}
