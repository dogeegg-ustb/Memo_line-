using System.Diagnostics;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using StrokeReplay;

namespace PacketReplay;

internal interface IPacketStateFeed
{
    BrushSnapshot? Brush { get; }
    string? Layer { get; }
    JsonElement Layers { get; }
    JsonElement LayersEvidence { get; }
    SubtoolSnapshot? Subtools { get; }
    JsonElement Color { get; }
    ShortcutConfiguration? Shortcuts { get; }
    long Revision { get; }
    void EnsureConnected();
    Task RequestAsync(string[] modules, CancellationToken token, string? saveId = null);
}

internal sealed class LivePacketFeed : IPacketStateFeed, IAsyncDisposable
{
    internal RecognizerViewMonitor View { get; }
    private readonly object _sync = new();
    private TaskCompletionSource _changed = NewSignal();
    private readonly Dictionary<string, long> _barriers = new();
    private string? _subtoolCaptureBarrier;
    private readonly Dictionary<string, long> _moduleRevisions = new();
    private long _revision, _generation;
    private bool _connected;
    private BrushSnapshot? _brush;
    private string? _layer;
    private JsonElement _layers, _layersEvidence, _color;
    private SubtoolSnapshot? _subtools;
    private int? _recorderProcessId;
    private ShortcutConfiguration? _shortcuts;
    private string? _sessionId;
    private CanvasViewSnapshot? _requestedView;
    private readonly IPacketStateControl? _controlOverride;
    private readonly SemaphoreSlim _requestGate = new(1);
    internal LivePacketFeed(string? endpoint = null, IPacketStateControl? stateControl = null)
    {
        View = new(endpoint, additionalChannels: ["shortcuts", "subtools", "layers", "layerstage", "core.brushState", "core.colorState"]);
        View.MessageReceived += Apply;
        View.ConnectionReset += Reset;
        _controlOverride = stateControl;
    }
    public BrushSnapshot? Brush { get { lock (_sync) return _brush; } }
    public string? Layer { get { lock (_sync) return _layer; } }
    public JsonElement Layers { get { lock (_sync) return _layers; } }
    public JsonElement LayersEvidence { get { lock (_sync) return _layersEvidence; } }
    public SubtoolSnapshot? Subtools { get { lock (_sync) return _subtools; } }
    internal int? RecorderProcessId { get { lock (_sync) return _recorderProcessId; } }
    internal long Generation { get { lock (_sync) return _generation; } }
    internal CanvasViewSnapshot? RequestedView { get { lock (_sync) return _requestedView; } }
    public async Task RequestAsync(string[] modules, CancellationToken token, string? saveId = null)
    {
        await _requestGate.WaitAsync(token);
        try
        {
            EnsureConnected();
            long generation = Generation;
            string session;
            int pid;
            lock (_sync)
            {
                session = _sessionId ?? throw new IOException("缺少当前 Memoline 会话身份。");
                pid = _recorderProcessId ?? throw new IOException("缺少当前 Memoline 控制进程。");
            }
            long requested = Stopwatch.GetTimestamp() - View.ClockOriginTicks!.Value;
            lock (_sync)
            {
                foreach (string module in modules.Where(m => m is not ("shortcuts" or "initializationConfiguration"))) _barriers[module] = requested;
                if (modules.Contains("subtoolState")) _subtoolCaptureBarrier = _subtools?.CaptureId;
            }
            var results = await (_controlOverride ?? new PacketRecorderControl(pid)).RequestAsync(session,
                "packet-state-" + Guid.NewGuid().ToString("N"), modules, saveId, token);
            lock (_sync)
            {
                if (!_connected || _generation != generation) throw new IOException("主动请求期间 Memoline 会话已变化。");
                foreach (string module in modules.OrderBy(m => m == "initializationConfiguration" ? 0 : 1))
                {
                    var data = results[module];
                    var evidence = J.Get(data, "evidence");
                    if (module is not ("shortcuts" or "initializationConfiguration"))
                    {
                        var timestamp = J.Get(evidence, module == "clipState" ? "observedTicks" : "capturedTicks");
                        if (timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out long ticks) || ticks < requested)
                            throw new IOException($"主动请求的 {module} 缺少本次采集时间：" + (J.Text(data, "error") ?? "结果过旧"));
                        _barriers[module] = ticks;
                    }
                    switch (module)
                    {
                        case "brushState":
                            _brush = BrushSnapshot.Parse(data, live: true);
                            if (_brush is null) throw new InvalidOperationException("本次主动请求未确认笔刷：" + BrushFailureReason(data));
                            break;
                        case "currentLayerState":
                            _layer = J.Confirmed(data) ? J.Get(data, "state").ValueKind == JsonValueKind.String ? J.Get(data, "state").GetString() : null : null;
                            if (string.IsNullOrWhiteSpace(_layer)) throw new InvalidOperationException("本次主动请求未确认当前图层：" + J.Text(data, "error"));
                            break;
                        case "clipState":
                            _layers = J.Confirmed(data) ? J.Get(data, "state").Clone() : default; _layersEvidence = evidence.Clone();
                            if (_layers.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("本次主动请求未取得图层结构：" + J.Text(data, "error"));
                            break;
                        case "subtoolState":
                            _subtools = SubtoolSnapshot.Parse(data);
                            if (_subtools is null || _subtools.CaptureId == _subtoolCaptureBarrier)
                                throw new InvalidOperationException("本次主动请求未取得新的子工具面板采集信息。");
                            break;
                        case "colorState":
                            _color = J.Confirmed(data) ? J.Get(data, "state").Clone() : default;
                            if (_color.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("本次主动请求未确认颜色：" + J.Text(data, "error"));
                            break;
                        case "shortcuts": _shortcuts = ShortcutConfiguration.Parse(data); break;
                        case "initializationConfiguration":
                            if (J.Get(data, "error").ValueKind == JsonValueKind.String) throw new InvalidOperationException(J.Text(data, "error"));
                            View.ApplyRequestedConfiguration(data); break;
                        case "canvasViewState": _requestedView = View.ApplyRequestedView(data); break;
                        default: throw new ArgumentException("未知状态模块：" + module);
                    }
                    _revision++; _moduleRevisions[module] = _revision;
                }
                Pulse();
            }
        }
        finally { _requestGate.Release(); }
    }
    public JsonElement Color { get { lock (_sync) return _color; } }
    internal static string BrushFailureReason(JsonElement data)
    {
        if (J.Text(data, "error") is { Length: > 0 } error) return error;
        var brush = J.Get(J.Get(data, "rawResult"), "brush");
        if (J.Text(J.Get(brush, "name_resolution"), "reason") is { Length: > 0 } reason) return reason;
        var incomplete = J.Array(J.Get(brush, "properties")).Where(p => J.Text(p, "status") is "partial" or "unknown" or "ambiguous")
            .Select(p => J.Text(p, "label") ?? J.Text(p, "key") ?? "未知属性").Distinct().ToArray();
        if (incomplete.Length > 0) return "以下属性尚未确认：" + string.Join("、", incomplete);
        return "接口返回 " + (J.Text(data, "status") ?? "未知状态") + "，没有可确认的当前笔刷名和属性。";
    }
    public ShortcutConfiguration? Shortcuts { get { lock (_sync) return _shortcuts; } }
    public long Revision { get { lock (_sync) return _revision; } }
    internal void SetShortcuts(ShortcutConfiguration configuration) { lock (_sync) { _shortcuts = configuration; Pulse(); } }
    public void EnsureConnected()
    {
        View.EnsureConnected();
        lock (_sync) if (!_connected) throw new IOException("Memoline 接口已断开。");
    }
    public void MarkInput(string module)
    {
        EnsureConnected();
        var origin = View.ClockOriginTicks ?? throw new IOException("实时接口缺少会话时钟。");
        lock (_sync)
        {
            _barriers[module] = Stopwatch.GetTimestamp() - origin;
            if (module == "subtoolState") _subtoolCaptureBarrier = _subtools?.CaptureId;
        }
    }
    internal void Apply(RecorderRealtimeEvent message)
    {
        if (message.Channel == "system")
        {
            if (message.Kind == "hello") { Reset(); lock (_sync) { _connected = true; _sessionId = message.SessionId; _recorderProcessId = ReadProcessId(message); Pulse(); } }
            else if (message.Kind == "sessionEnded") Reset();
            return;
        }
        lock (_sync)
        {
            if (!_connected) return;
            if (message.Channel == "shortcuts" && message.Kind == "configurationUpdated")
            { _shortcuts = ShortcutConfiguration.Parse(message.Data); _revision++; Pulse(); return; }
            var module = J.Text(message.Data, "module");
            if (message.Kind is not ("stateUpdated" or "currentLayerUpdated" or "layerStructureUpdated" or "ocrUpdated") || module is null) return;
            if (module != "subtoolState" && !J.Confirmed(message.Data)) return;
            if (_barriers.TryGetValue(module, out long barrier))
            {
                var evidence = J.Get(message.Data, "evidence");
                var capture = J.Get(evidence, module == "clipState" ? "observedTicks" : "capturedTicks");
                if (capture.ValueKind != JsonValueKind.Number || !capture.TryGetInt64(out var ticks) || ticks < barrier) return;
            }
            if (module == "brushState")
            {
                var brush = BrushSnapshot.Parse(message.Data, live: true);
                if (brush is null) return;
                _brush = brush;
            }
            else if (module == "currentLayerState")
            {
                var state = J.Get(message.Data, "state");
                if (state.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(state.GetString())) return;
                _layer = state.GetString();
            }
            else if (module == "clipState")
            {
                _layers = J.Get(message.Data, "state").Clone();
                var evidence = J.Get(message.Data, "evidence");
                _layersEvidence = evidence.ValueKind == JsonValueKind.Undefined ? default : evidence.Clone();
            }
            else if (module == "subtoolState")
            {
                var snapshot = SubtoolSnapshot.Parse(message.Data);
                if (snapshot is null || snapshot.CaptureId == _subtoolCaptureBarrier
                    || _subtools is { } old && snapshot.CapturedTicks < old.CapturedTicks) return;
                _subtools = snapshot;
            }
            else if (module == "colorState") _color = J.Get(message.Data, "state").Clone();
            else return;
            _revision++; _moduleRevisions[module] = _revision; Pulse();
        }
    }
    private void Reset()
    {
        lock (_sync) { _connected = false; _brush = null; _layer = null; _layers = default; _layersEvidence = default; _color = default; _subtools = null; _recorderProcessId = null; _sessionId = null; _requestedView = null;
            _subtoolCaptureBarrier = null; _barriers.Clear(); _moduleRevisions.Clear(); _generation++; _revision++; Pulse(); }
    }
    public async Task WaitAsync(long after, string module, Func<bool> matches, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        long generation;
        lock (_sync) generation = _generation;
        try
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    if (after >= 0 && (!_connected || generation != _generation)) throw new IOException("Memoline 会话中断，已停止回放。");
                    if (_connected && _moduleRevisions.GetValueOrDefault(module, -1) > after && matches()) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException($"等待 Memoline 确认 {module} 状态超时。"); }
    }
    private static int? ReadProcessId(RecorderRealtimeEvent hello)
    {
        // Bind control to the same live recording that supplied the state table.
        var file = J.Text(hello.Data, "filePath");
        if (file is null) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file + ".live.json"));
            var manifest = json.RootElement;
            var pid = J.Get(manifest, "processId");
            return J.Text(manifest, "sessionId") == hello.SessionId && J.Text(manifest, "pipeName") == J.Text(hello.Data, "pipeName")
                && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out int id) && id > 0 ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var old = _changed; _changed = NewSignal(); old.TrySetResult(); }
    public async ValueTask DisposeAsync() => await View.DisposeAsync();
}
