using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;

namespace CanvasLayerWatcher;

internal static class J
{
    public static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string? Text(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static long Tick(JsonElement e, string key, long fallback) => Get(e, key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : fallback;
    public static double? Number(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static long ObservationTicks(RecorderRealtimeEvent message) => Tick(Get(message.Data, "evidence"), "triggerTicks",
        Tick(Get(message.Data, "evidence"), "capturedTicks", message.Ticks));
    public static string ObservationClock(RecorderRealtimeEvent message)
    {
        var evidence = Get(message.Data, "evidence");
        if (Get(evidence, "triggerTicks") is { ValueKind: JsonValueKind.Number } trigger && trigger.TryGetInt64(out _)) return "triggerTicks";
        return Get(evidence, "capturedTicks") is { ValueKind: JsonValueKind.Number } captured && captured.TryGetInt64(out _) ? "capturedTicks" : "ticks";
    }
    // Newer Recognizer builds report causalAmbiguous as input attribution
    // diagnostics. The status field determines whether the observed value is valid.
    public static bool Confirmed(JsonElement e) => Text(e, "status") is "changed" or "unchanged";
}

internal sealed record View(double X, double Y, double Scale, double Rotation)
{
    public static View? Parse(JsonElement payload)
    {
        if (!J.Confirmed(payload)) return null;
        var state = J.Get(payload, "state"); var origin = J.Get(state, "canvasOriginScreenPx");
        return J.Number(origin, "x") is { } x && J.Number(origin, "y") is { } y
            && J.Number(state, "ocrScalePercent") is > 0 and var s && J.Number(state, "ocrRotationDegrees") is { } r
            ? new(x, y, s, r) : null;
    }
    public bool Differs(View other) => Math.Abs(X - other.X) >= 1 || Math.Abs(Y - other.Y) >= 1
        || Math.Abs(Scale - other.Scale) >= .01 || Math.Abs(Math.IEEERemainder(Rotation - other.Rotation, 360)) >= .01;
}

internal sealed record CaptureContext(string SessionId, Size PixelSize, string ControlPipe, FrozenEvidence Evidence);
internal sealed record ViewportObservation(RecorderRealtimeEvent Message, long TriggerTicks, int Changed,
    bool IsBaseline, bool HasEditingInput);
internal sealed record CaptureRequest(long Generation, long Ticks, string LayerName, View View, long[] Operations,
    LayerReference? Layer = null, string TimeSource = "triggerTicks", string TriggerKind = "stateUpdated", bool ViewPending = false)
{
    public long TriggerTicks => Ticks;
    [System.Text.Json.Serialization.JsonIgnore]
    public CaptureContext? Context { get; init; }
}

// Called under the monitor lock. Recognition completions can arrive out of capture order.
internal sealed class WatchState(bool requireConfirmedViewChange = false)
{
    private readonly SortedDictionary<long, string?> _layers = [];
    private readonly SortedDictionary<long, LayerReference[]> _clipLayers = [];
    private readonly Dictionary<long, long> _dirty = [];
    private readonly Dictionary<long, long> _lastDirty = [];
    private long? _mouseOperation, _penOperation;
    private View? _view;
    private long _viewTicks = -1, _evidenceTicks = -1, _completedTriggerTicks = -1, _syntheticOperation;
    private bool _reserved;
    private bool _penContact, _mouseContact;
    private bool _penEditable = true;
    private Rectangle? _canvasArea;
    public event Action<string>? Diagnostic;
    public event Action<ViewportObservation>? ViewportObserved;
    public bool InContact => _penContact || _mouseContact;
    public void SetCanvasArea(Rectangle area) => _canvasArea = area;
    public bool HasCanvasArea => _canvasArea is not null;
    public long Generation { get; private set; }
    public string? LayerName => _layers.Count == 0 ? null : _layers.Last().Value;
    public bool Dirty => _dirty.Count > 0;
    public void Reset()
    {
        Generation++; _layers.Clear(); _clipLayers.Clear(); _dirty.Clear(); _lastDirty.Clear(); _view = null;
        _mouseOperation = _penOperation = null;
        _viewTicks = _evidenceTicks = _completedTriggerTicks = -1;
        _reserved = _penContact = _mouseContact = false; _penEditable = true; _canvasArea = null;
    }
    public CaptureRequest? Accept(RecorderRealtimeEvent message)
    {
        long ticks = J.ObservationTicks(message);
        if (message.Channel == "core.canvasViewState" && message.Kind == "evidenceCaptured")
        {
            CaptureRequest? EarlySkip(string reason) { ViewDiagnostic(message, ticks, reason, _view); return null; }
            if (requireConfirmedViewChange) return EarlySkip("聚集模式等待确认视口变化，存证消息不作为封包边界");
            if (message.IsSnapshot || J.Get(message.Data, "initial").ValueKind == JsonValueKind.True) return EarlySkip("初始画布存证，不保存");
            if (J.ObservationClock(message) != "triggerTicks") return EarlySkip("存证消息缺少 triggerTicks");
            if (ticks <= _evidenceTicks || ticks <= _viewTicks || ticks <= _completedTriggerTicks) return EarlySkip("该触发的存证已处理");
            _evidenceTicks = ticks;
            if (_view is null) return EarlySkip("画布视口基线尚未就绪");
            if (_reserved) return EarlySkip("已有保存任务，合并本次存证");
            return Reserve(message, ticks, _view, pending: true);
        }
        if (message.Channel == "tablet" && !message.IsSnapshot)
        {
            if (message.Kind == "penBegin")
            {
                _penOperation = message.OperationId is { } penId ? (long)penId : --_syntheticOperation;
                var location = PanelRegionMap.ReadLocation(message.Data);
                _penEditable = location is not null ? location.Region == "canvasViewport"
                    : _canvasArea is not { } area || J.Number(message.Data, "x") is not { } x || J.Number(message.Data, "y") is not { } y
                        || area.Contains((int)x, (int)y);
                Diagnostic?.Invoke($"数位笔下笔区域：{location?.Name ?? (_penEditable ? "画布区域或旧接口未分类" : "画布外")}，operation={_penOperation}，位置=({J.Number(message.Data, "x")},{J.Number(message.Data, "y")})");
            }
            if (message.Kind is "penBegin" or "penSample") _penContact = true;
            else if (message.Kind is "penEnd" or "penInterrupted" or "leave" or "outOfRange")
            {
                var operation = message.OperationId is { } endId ? (long)endId : _penOperation;
                if (operation is { } op && _dirty.ContainsKey(op)) _lastDirty[op] = message.Ticks;
                _penContact = false; _penOperation = null;
            }
        }
        if (message.Channel == "tablet" && !message.IsSnapshot && message.Kind is "penBegin" or "penSample")
        {
            var data = message.Data;
            if (J.Get(data, "inCsp").ValueKind == JsonValueKind.False) return null;
            if (!_penEditable) return null;
            if (NavigationHeld(data)) return null;
            long operation = message.OperationId is { } id ? checked((long)id) : _penOperation ??= --_syntheticOperation;
            if (_dirty.TryAdd(operation, message.Ticks)) Diagnostic?.Invoke($"编辑输入：tablet/{message.Kind}，operation={operation}");
            _lastDirty[operation] = message.Ticks;
            return null;
        }
        if (message.Channel == "mouse" && !message.IsSnapshot)
        {
            if (message.Kind == "mouseInterrupted") { _mouseContact = false; _mouseOperation = null; }
            if (J.Text(message.Data, "button") != "left") return null;
            if (message.Kind is "mouseDrag" or "mouseUp")
            {
                var activeOperation = message.OperationId is { } mouseId ? (long)mouseId : _mouseOperation;
                if (activeOperation is { } op && _dirty.ContainsKey(op)) _lastDirty[op] = message.Ticks;
                if (message.Kind == "mouseUp") { _mouseContact = false; _mouseOperation = null; }
                return null;
            }
            // Some tablet drivers expose drawing through the Windows mouse route.
            // Restrict the fallback to primary presses inside the calibrated canvas.
            if (message.Kind != "mouseDown" || _canvasArea is not { } area
                || J.Number(message.Data, "x") is not { } x || J.Number(message.Data, "y") is not { } y
                || !area.Contains((int)x, (int)y)) return null;
            _mouseContact = true;
            if (NavigationHeld(message.Data)) return null;
            long operation = message.OperationId is { } id ? checked((long)id)
                : message.EventId > 0 ? checked((long)message.EventId) : --_syntheticOperation;
            _mouseOperation = operation;
            if (_dirty.TryAdd(operation, message.Ticks)) Diagnostic?.Invoke($"编辑输入：mouse/left，operation={operation}，位置=({x},{y})");
            _lastDirty[operation] = message.Ticks;
            return null;
        }
        if (message.Channel == "core.clipState" && message.Kind == "stateUpdated")
        {
            if (J.Confirmed(message.Data))
            {
                _clipLayers[ticks] = LayerMapping.ReadLayers(J.Get(message.Data, "state"));
                while (_clipLayers.Count > 64) _clipLayers.Remove(_clipLayers.First().Key);
                Diagnostic?.Invoke($"图层属性核心：ticks={ticks}，图层数={_clipLayers[ticks].Length}，snapshot={message.IsSnapshot}");
            }
            return null;
        }
        if (message.Channel == "core.currentLayerState" && message.Kind == "stateUpdated")
        {
            if (J.Text(J.Get(message.Data, "evidence"), "reason") == "supersededBeforeAnalysis") return null;
            var state = J.Get(message.Data, "state");
            _layers[ticks] = J.Confirmed(message.Data) && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
            while (_layers.Count > 1024) _layers.Remove(_layers.First().Key);
            Diagnostic?.Invoke($"图层事件：ticks={ticks}，status={J.Text(message.Data, "status")}，layer={_layers[ticks] ?? "未确认"}，snapshot={message.IsSnapshot}");
            return null;
        }
        if (message.Channel != "core.canvasViewState" || message.Kind != "stateUpdated") return null;
        var view = View.Parse(message.Data);
        CaptureRequest? Skip(string reason)
        { ViewDiagnostic(message, ticks, reason, view); return null; }
        if (view is null) return Skip("状态未确认或画布字段不完整");
        if (ticks <= _viewTicks) return Skip("观察时间早于已处理视口");
        bool first = _view is null;
        bool changed = _view is not null && view.Differs(_view);
        _viewTicks = ticks; _view = view;
        bool baseline = message.IsSnapshot || J.Get(message.Data, "initial").ValueKind == JsonValueKind.True || first;
        ViewportObserved?.Invoke(new(message, ticks, baseline ? 0 : changed ? 1 : 0,
            baseline, _dirty.Any(operation => operation.Value <= ticks)));
        if (baseline) return Skip("建立视口基线");
        if (ticks <= _completedTriggerTicks) return Skip("该 triggerTicks 已由存证消息完成保存，只更新视口状态");
        if (!changed) return Skip("视口值未变化");
        if (_reserved) return Skip("已有保存任务，合并本次变化");
        return Reserve(message, ticks, view, pending: false);
    }
    public CaptureRequest? ReserveRecordingEnd(RecorderRealtimeEvent message)
    {
        if (message.Kind != "recordingEndRequested" || message.AppendId == 0 || message.Ticks < 0)
            throw new InvalidDataException("结束保存需要 Recognizer 的原生结束边界。");
        if (_reserved || _view is null) return null;
        return Reserve(message, message.Ticks, _view, pending: false, force: true) is { } request
            ? request with { TimeSource = "triggerTicks", TriggerKind = "recordingEnd" } : null;
    }
    private CaptureRequest? Reserve(RecorderRealtimeEvent message, long ticks, View view, bool pending, bool force = false)
    {
        CaptureRequest? Skip(string reason) { ViewDiagnostic(message, ticks, reason, view); return null; }
        var operations = _dirty.Where(p => p.Value <= ticks).Select(p => p.Key).ToArray();
        string? layer = _layers.LastOrDefault(p => p.Key <= ticks).Value;
        if (operations.Length == 0 && !force) return Skip("切换前没有符合条件的编辑输入");
        if (string.IsNullOrWhiteSpace(layer)) return Skip("切换时图层未确认");
        // Later layer changes do not replace the layer at triggerTicks, even
        // when the viewport recognition result arrives after those changes.
        LayerReference? identity = null;
        try { identity = LayerMapping.FromCore(_clipLayers.LastOrDefault(p => p.Key <= ticks).Value ?? [], layer); }
        catch (InvalidDataException ex) { Diagnostic?.Invoke(ex.Message + "；保存后以当前 CLIP 的名称映射为准"); }
        _reserved = true;
        Diagnostic?.Invoke($"图层名称已固定：接口={layer}，初始编号提示={identity?.Id}；保存后将名称映射为当前 CLIP 编号");
        ViewDiagnostic(message, ticks, pending ? "存证已发生，立即触发保存，不等待解析" : "触发保存", view);
        return new(Generation, ticks, layer, view, operations, identity, J.ObservationClock(message), message.Kind, pending);
    }
    private static bool NavigationHeld(JsonElement data)
    {
        var keys = J.Get(data, "heldKeys");
        return keys.ValueKind == JsonValueKind.Array && keys.EnumerateArray().Any(k => k.TryGetInt32(out int vk)
            && vk is 32 or 17 or 18 or 0xA2 or 0xA3 or 0xA4 or 0xA5);
    }
    private void ViewDiagnostic(RecorderRealtimeEvent message, long ticks, string reason, View? view)
        => Diagnostic?.Invoke("视口事件：" + JsonSerializer.Serialize(new {
            triggerTicks = ticks, timeSource = J.ObservationClock(message),
            capturedTicks = J.Tick(J.Get(message.Data, "evidence"), "capturedTicks", message.Ticks),
            message.PublishedTicks, message.Kind, message.IsSnapshot, status = J.Text(message.Data, "status"),
            causalAmbiguous = J.Get(J.Get(message.Data, "evidence"), "causalAmbiguous").ValueKind == JsonValueKind.True,
            evidenceReason = J.Text(J.Get(message.Data, "evidence"), "reason"), view,
            dirtyOperations = _dirty.Count, layer = LayerName, decision = reason,
            error = J.Text(message.Data, "error")
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    public bool Valid(CaptureRequest request) => request.Generation == Generation
        && LayerMapping.SameName(_layers.LastOrDefault(p => p.Key <= request.TriggerTicks).Value, request.LayerName);
    public void Complete(CaptureRequest request, bool success)
    {
        if (request.Generation != Generation) return;
        if (success) _completedTriggerTicks = Math.Max(_completedTriggerTicks, request.TriggerTicks);
        if (success) foreach (long operation in request.Operations)
        {
            if (_lastDirty.GetValueOrDefault(operation) <= request.TriggerTicks) { _dirty.Remove(operation); _lastDirty.Remove(operation); }
            else _dirty[operation] = request.TriggerTicks + 1;
        }
        _reserved = false;
    }
}
