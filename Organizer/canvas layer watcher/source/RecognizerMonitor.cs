using System.Diagnostics;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;

namespace CanvasLayerWatcher;

internal sealed class RecognizerMonitor
{
    private sealed record Endpoint(string PipeName, int ProcessId);
    private readonly WatchState _state;
    private readonly RecognitionEvidence _evidence = new();
    private readonly object _sync = new();
    private readonly HashSet<string> _ended = [];
    public event Action<string>? Status;
    public event Action<string>? Diagnostic;
    public event Action<CaptureRequest>? Capture;
    public event Action<ViewportObservation>? ViewportObserved;
    public event Action<RecorderRealtimeEvent>? MessageReceived;
    private readonly bool _requireConfirmedViewChange;
    private bool _captureRequestsPaused;
    public RecognizerMonitor(bool requireConfirmedViewChange = false)
    {
        _requireConfirmedViewChange = requireConfirmedViewChange;
        _state = new(requireConfirmedViewChange);
        _state.Diagnostic += text => Diagnostic?.Invoke(text);
        _state.ViewportObserved += observation => ViewportObserved?.Invoke(observation);
    }
    public bool CaptureRequestsPaused
    {
        get { lock (_sync) return _captureRequestsPaused; }
        set { lock (_sync) _captureRequestsPaused = value; }
    }
    private string? _clipPath;
    private Size? _pixelSize;
    private string? _controlPipe;
    private string _session = "";
    private bool _earlyEvidence;
    public string SessionId { get { lock (_sync) return _session; } }
    public EvidenceWindow GetEvidence(long from, long to) { lock (_sync) return _evidence.Snapshot(from, to); }
    public string? ControlPipe { get { lock (_sync) return _controlPipe; } }
    public Size? PixelSize { get { lock (_sync) return _pixelSize; } }
    public bool Current(CaptureRequest request) { lock (_sync) return _state.Valid(request); }
    public bool InContact { get { lock (_sync) return _state.InContact; } }
    public void Complete(CaptureRequest request, bool success)
    {
        lock (_sync)
        {
            _state.Complete(request, success);
            if (success && request.Generation == _state.Generation) _evidence.Trim(request.TriggerTicks);
        }
    }
    public string? ClipPath { get { lock (_sync) return _clipPath; } }

    public async Task RunAsync(string endpoint, string clipPath, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task? configuration = null;
            Task? controlStatus = null;
            int viewEvents = 0, layerEvents = 0, penBegins = 0, mousePresses = 0;
            try
            {
                Endpoint? selected = Discover(endpoint);
                if (selected is null) { Status?.Invoke("等待 Recognizer 开始录制…"); await Task.Delay(1000, token); continue; }
                string pipe = selected.PipeName;
                lock (_sync)
                {
                    _state.Reset(); _clipPath = null; _pixelSize = null; _session = "";
                    _controlPipe = selected.ProcessId > 0 ? RecorderControlClient.PipeNameFor(selected.ProcessId) : null;
                }
                Status?.Invoke("连接 Recognizer：" + pipe);
                Diagnostic?.Invoke($"实时订阅：pipe={pipe}，processId={selected.ProcessId}，channels=keyboard,mouse,tablet,cores，controlPipe={ControlPipe}");
                if (ControlPipe is { } controlPipe) controlStatus = CheckControlAsync(controlPipe, connection.Token);
                await foreach (var message in RecorderRealtimeClient.SubscribeAsync(pipe,
                    ["keyboard", "mouse", "tablet", "cores"], true, connection.Token))
                {
                    MessageReceived?.Invoke(message);
                    if (message.Channel == "system")
                    {
                        if (message.Kind == "hello")
                        {
                            lock (_sync)
                            {
                                _session = message.SessionId;
                                _earlyEvidence = J.Get(message.Data, "evidenceCapturedNotifications").ValueKind == JsonValueKind.True;
                                _evidence.Reset(_session, J.Tick(message.Data, "frequency", 0), message.Ticks);
                            }
                            string? recording = J.Text(message.Data, "liveFilePath") ?? J.Text(message.Data, "filePath");
                            if (recording is null) throw new InvalidDataException("接口未提供录制文件，无法核对 .clip 路径");
                            Status?.Invoke("已连接；等待画布、图层和录制配置");
                            Diagnostic?.Invoke(_requireConfirmedViewChange ? "聚集模式使用已确认的视口变化作为封包边界"
                                : _earlyEvidence ? "Recognizer 支持存证即时消息，保存不等待视口解析结果" : "Recognizer 未声明存证即时消息能力，当前连接回退到解析结果触发保存；请启动新版 Recognizer");
                            configuration = ReadConfigurationAsync(recording, clipPath, connection.Token);
                        }
                        if (message.Kind == "sessionEnded") { _ended.Add(pipe); Status?.Invoke("录制已结束，等待新会话"); }
                        continue;
                    }
                    if (configuration is { IsFaulted: true }) await configuration;
                    if (message.Channel == "core.canvasViewState") viewEvents++;
                    if (message.Channel == "core.currentLayerState") layerEvents++;
                    if (message.Channel == "tablet" && message.Kind == "penBegin" && !message.IsSnapshot) penBegins++;
                    if (message.Channel == "mouse" && message.Kind == "mouseDown" && !message.IsSnapshot) mousePresses++;
                    CaptureRequest? request;
                    lock (_sync)
                    {
                        _evidence.Accept(message);
                        if (message.Channel == "core.canvasViewState" && J.Confirmed(message.Data)
                            && ViewArea(message.Data) is { } area)
                            _state.SetCanvasArea(area);
                        request = _state.Accept(message);
                        if (request is not null && _captureRequestsPaused)
                        {
                            // Accept may reserve a capture while the host is sealing.
                            // Release that reservation without consuming any input.
                            _state.Complete(request, false); request = null;
                        }
                        if (request is not null && _clipPath is not null && _pixelSize is { } pixels && _controlPipe is { } control)
                            request = request with { Context = new(_session, pixels, control, _evidence.Freeze(request.TriggerTicks)) };
                    }
                    if (request is not null)
                    {
                        if (request.Context is null) { Complete(request, false); Status?.Invoke("录制配置尚未就绪；本次切换不保存"); }
                        else Capture?.Invoke(request);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Status?.Invoke("接口不可用，将重连：" + ex.Message); }
            finally
            {
                await connection.CancelAsync();
                if (configuration is not null) try { await configuration; } catch (Exception) { }
                if (controlStatus is not null) await controlStatus;
                Diagnostic?.Invoke($"本次订阅统计：视口事件={viewEvents}，图层事件={layerEvents}，penBegin={penBegins}，mouseDown={mousePresses}");
                lock (_sync) { _state.Reset(); _clipPath = null; _pixelSize = null; _controlPipe = null; _session = ""; }
            }
            await Task.Delay(1000, token);
        }
    }
    private async Task CheckControlAsync(string pipe, CancellationToken token)
    {
        try
        {
            var result = await new RecorderControlClient(pipe).StatusAsync(token);
            Diagnostic?.Invoke("Recognizer 控制接口已连接：" + result.GetRawText());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostic?.Invoke("Recognizer 控制接口检查失败：" + ex.Message); }
    }
    internal static Rectangle? ViewArea(JsonElement data)
    {
        var raw = J.Get(data, "rawResult");
        return ScreenArea(J.Get(raw, "canvasWindowRoiScreenPx")) ?? ScreenArea(J.Get(raw, "workspaceRoiScreenPx"))
            ?? ScreenArea(J.Get(J.Get(raw, "snapshot"), "workspaceRoi"));
    }
    internal static Rectangle? ScreenArea(JsonElement value)
    {
        static bool Integer(JsonElement item, out int number)
        { number = 0; return item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out number); }
        int x, y, width, height;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 4)
        {
            if (!Integer(value[0], out x) || !Integer(value[1], out y)
                || !Integer(value[2], out width) || !Integer(value[3], out height)) return null;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (!Integer(J.Get(value, "left"), out x) || !Integer(J.Get(value, "top"), out y)
                || !Integer(J.Get(value, "width"), out width) || !Integer(J.Get(value, "height"), out height)) return null;
        }
        else return null;
        return width > 0 && height > 0 && width <= 100000 && height <= 100000 ? new(x, y, width, height) : null;
    }
    private async Task ReadConfigurationAsync(string path, string selectedClip, CancellationToken token)
    {
        using var reader = MemolineReader.Open(path);
        while (!token.IsCancellationRequested)
        {
            foreach (var frame in reader.ReadAvailable())
                if (J.Text(frame, "kind") == "initializationConfiguration")
                {
                    string? configured = J.Text(J.Get(frame, "data"), "clipPath");
                    if (string.IsNullOrWhiteSpace(configured) || !Path.GetFullPath(configured).Equals(Path.GetFullPath(selectedClip), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("选择的 .clip 必须与 Recognizer 本次录制配置的 clipPath 相同");
                    var size = J.Get(J.Get(frame, "data"), "canvasPixelSize");
                    if (size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2
                        || !size[0].TryGetInt32(out int width) || !size[1].TryGetInt32(out int height)
                        || width <= 0 || height <= 0 || (long)width * height > 64_000_000)
                        throw new InvalidOperationException("Recognizer 配置缺少有效的完整画布像素尺寸（上限 6400 万像素）");
                    var settings = J.Get(J.Get(frame, "data"), "settings");
                    var regions = J.Get(J.Get(J.Get(settings, "navigatorOcrSelection"), "layout"), "regions");
                    lock (_sync)
                    {
                        var append = J.Get(frame, "appendId");
                        _evidence.Configuration(J.Tick(frame, "ticks", 0), J.Get(frame, "data"),
                            append.ValueKind == JsonValueKind.Number && append.TryGetUInt64(out var appendId) ? appendId : 0);
                        _pixelSize = new(width, height); _clipPath = Path.GetFullPath(configured);
                        if (!_state.HasCanvasArea && ScreenArea(J.Get(regions, "画布视口")) is { } area) _state.SetCanvasArea(area);
                    }
                    Diagnostic?.Invoke($"录制配置：clip={configured}，完整画布={width}×{height}，mouseFallbackArea={J.Get(regions, "画布视口")}");
                    Status?.Invoke(_requireConfirmedViewChange ? "持续监听中；仅按已确认的画布视口变化封包"
                        : _earlyEvidence ? "持续监听中；画布存证后立即请求保存，不等待视口解析"
                        : "持续监听中；当前为旧版 Recognizer，保存仍由视口解析结果触发");
                    return;
                }
            if (reader.IsComplete) throw new InvalidDataException("录制缺少 initializationConfiguration");
            await Task.Delay(100, token);
        }
    }
    private Endpoint? Discover(string specified)
    {
        if (!string.IsNullOrWhiteSpace(specified) && specified.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return Manifest(specified);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? parent = new(start); parent is not null; parent = parent.Parent)
            {
                directories.Add(Path.Combine(parent.FullName, "procedure", "stroke"));
                directories.Add(Path.Combine(parent.FullName, "recognizer", "Recognizer", "publish", "win-x64", "procedure", "stroke"));
                directories.Add(Path.Combine(parent.FullName, "recognizer", "Recognizer", "publish", "win-x64-injected-input", "procedure", "stroke"));
            }
        foreach (var process in Process.GetProcessesByName("BehaviorRecognizer"))
            using (process) try
            {
                if (process.MainModule?.FileName is { } exe) directories.Add(Path.Combine(Path.GetDirectoryName(exe)!, "procedure", "stroke"));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        var manifests = new List<string>();
        foreach (string dir in directories)
            try { if (Directory.Exists(dir)) manifests.AddRange(Directory.GetFiles(dir, "*.memoline.live.json")); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        var candidates = manifests.OrderByDescending(p => p.Contains("win-x64-injected-input", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc).Select(Manifest);
        return string.IsNullOrWhiteSpace(specified) ? candidates.FirstOrDefault(p => p is not null)
            : candidates.FirstOrDefault(p => p?.PipeName == specified) ?? new Endpoint(specified, 0);
    }
    private Endpoint? Manifest(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
            if (J.Tick(root, "schemaVersion", 0) != 1) return null;
            string? pipe = J.Text(root, "pipeName");
            if (pipe is null || _ended.Contains(pipe)) return null;
            int pid = checked((int)J.Tick(root, "processId", 0));
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() <= File.GetLastWriteTimeUtc(path).AddSeconds(1) ? new(pipe, pid) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
