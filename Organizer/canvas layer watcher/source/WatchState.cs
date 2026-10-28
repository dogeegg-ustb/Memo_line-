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

/// <summary>Layers a saved CLIP shows for the first time in this session, with that save's own copy; without one
/// (null) the layers start empty at <paramref name="TriggerTicks"/>.</summary>
internal sealed record LayerAppearance(long Generation, long TriggerTicks, string? SnapshotPath, IReadOnlyList<LayerReference> Layers);
internal sealed record CaptureContext(string SessionId, Size PixelSize, string ControlPipe, FrozenEvidence Evidence);
internal sealed record ViewportObservation(RecorderRealtimeEvent Message, long TriggerTicks, int Changed,
    bool IsBaseline, bool HasEditingInput);
internal sealed record CaptureRequest(long Generation, long Ticks, string LayerName, View View, long[] Operations,
    LayerReference? Layer = null, string TimeSource = "triggerTicks", string TriggerKind = "stateUpdated", bool ViewPending = false)
{
    public long TriggerTicks => Ticks;
    public string? BoundaryRequestId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public CaptureContext? Context { get; init; }
}

// Called under the monitor lock. Recognition completions can arrive out of capture order.
internal sealed class WatchState(bool requireConfirmedViewChange = false)
{
    private readonly SortedDictionary<long, string?> _layers = [];
    private readonly SortedDictionary<long, LayerReference[]> _clipLayers = [];
    private readonly SortedDictionary<long, string[]> _paperLayers = [];
    private readonly SortedDictionary<long, string> _clipSnapshots = [];
    private readonly Dictionary<long, long> _dirty = [];
    private readonly Dictionary<long, long> _lastDirty = [];
    private readonly Dictionary<long, long> _strokeBegins = [];
    private readonly Dictionary<long, long> _strokeLast = [];
    private readonly HashSet<long> _canvasStrokeOperations = [];
    private readonly Dictionary<string, long> _capturedLayers = new(StringComparer.Ordinal);
    // Boundary refreshes follow layer identity: a captured layer that was deleted stays gone even when
    // CSP later gives another layer its name (a paste becomes the next free "图层 1").
    private readonly HashSet<string> _capturedLayerUuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _capturedUuidTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _pendingBoundaryLayers = [];
    // Every layer UUID any CLIP state of this session has listed; null until the first one arrives.
    private HashSet<string>? _knownLayerUuids;
    private long _lastEditBoundaryTicks = -1;
    private long? _mouseOperation, _penOperation;
    private View? _view;
    private long _viewTicks = -1, _evidenceTicks = -1, _completedTriggerTicks = -1, _syntheticOperation;
    private bool _reserved;
    private bool _penContact, _mouseContact;
    private bool _penEditable = true;
    private Rectangle? _canvasArea;
    public event Action<string>? Diagnostic;
    public event Action<ViewportObservation>? ViewportObserved;
    public event Action<LayerAppearance>? LayerAppeared;
    public event Action<LayerVanishing>? LayersVanished;
    public bool InContact => _penContact || _mouseContact;
    public void SetCanvasArea(Rectangle area) => _canvasArea = area;
    public bool HasCanvasArea => _canvasArea is not null;
    public long Generation { get; private set; }
    public string? LayerName => _layers.Count == 0 ? null : _layers.Last().Value;
    public bool Dirty => _dirty.Count > 0;
    /// <summary>Counts input samples that may have changed the document (canvas pen and mouse contacts).</summary>
    public long Edits { get; private set; }
    public void Reset()
    {
        Generation++; _layers.Clear(); _clipLayers.Clear(); _paperLayers.Clear(); _clipSnapshots.Clear(); _dirty.Clear(); _lastDirty.Clear(); _view = null;
        _strokeBegins.Clear(); _strokeLast.Clear(); _canvasStrokeOperations.Clear(); _capturedLayers.Clear(); _capturedLayerUuids.Clear(); _capturedUuidTicks.Clear(); _pendingBoundaryLayers.Clear(); _lastEditBoundaryTicks = -1;
        _knownLayerUuids = null;
        _mouseOperation = _penOperation = null;
        _viewTicks = _evidenceTicks = _completedTriggerTicks = -1;
        _reserved = _penContact = _mouseContact = false; _penEditable = true; _canvasArea = null;
    }
    public CaptureRequest? Accept(RecorderRealtimeEvent message)
    {
        long ticks = J.ObservationTicks(message);
        if (message.Channel == "system" && message.Kind == "editBoundaryRequested")
        { _lastEditBoundaryTicks = message.Ticks; return null; }
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
                _strokeBegins.TryAdd(_penOperation.Value, message.Ticks);
                var location = PanelRegionMap.ReadLocation(message.Data);
                _penEditable = location is not null ? location.Region == "canvasViewport"
                    || !(location.Status == "matched" && location.Region != "other" || location.Status == "outsideRegions")
                    : _canvasArea is not { } area || J.Number(message.Data, "x") is not { } x || J.Number(message.Data, "y") is not { } y
                        || area.Contains((int)x, (int)y);
                if (_penEditable && J.Get(message.Data, "inCsp").ValueKind != JsonValueKind.False && !NavigationHeld(message.Data))
                    _canvasStrokeOperations.Add(_penOperation.Value);
                Diagnostic?.Invoke($"数位笔下笔区域：{location?.Name ?? (_penEditable ? "画布区域或旧接口未分类" : "画布外")}，operation={_penOperation}，位置=({J.Number(message.Data, "x")},{J.Number(message.Data, "y")})");
            }
            if (message.Kind is "penBegin" or "penSample") _penContact = true;
            else if (message.Kind is "penEnd" or "penInterrupted" or "leave" or "outOfRange")
            {
                var operation = message.OperationId is { } endId ? (long)endId : _penOperation;
                if (operation is { } op && _dirty.ContainsKey(op)) { _lastDirty[op] = message.Ticks; Edits++; }
                _penContact = false; _penOperation = null;
            }
            if (message.Kind is "penBegin" or "penSample" or "penEnd" or "penInterrupted"
                && (message.OperationId is { } strokeId ? (long)strokeId : _penOperation) is { } strokeOperation
                && _strokeBegins.ContainsKey(strokeOperation))
                _strokeLast[strokeOperation] = message.Ticks;
        }
        if (message.Channel == "tablet" && !message.IsSnapshot && message.Kind is "penBegin" or "penSample")
        {
            var data = message.Data;
            if (J.Get(data, "inCsp").ValueKind == JsonValueKind.False) return null;
            if (!_penEditable) return null;
            if (NavigationHeld(data)) return null;
            long operation = message.OperationId is { } id ? checked((long)id) : _penOperation ??= --_syntheticOperation;
            if (_dirty.TryAdd(operation, message.Ticks)) Diagnostic?.Invoke($"编辑输入：tablet/{message.Kind}，operation={operation}");
            _lastDirty[operation] = message.Ticks; Edits++;
            return null;
        }
        if (message.Channel == "mouse" && !message.IsSnapshot)
        {
            if (message.Kind == "mouseInterrupted") { _mouseContact = false; _mouseOperation = null; }
            if (J.Text(message.Data, "button") != "left") return null;
            if (message.Kind is "mouseDrag" or "mouseUp")
            {
                var activeOperation = message.OperationId is { } mouseId ? (long)mouseId : _mouseOperation;
                if (activeOperation is { } tracked && _strokeBegins.ContainsKey(tracked)) _strokeLast[tracked] = message.Ticks;
                if (activeOperation is { } op && _dirty.ContainsKey(op)) { _lastDirty[op] = message.Ticks; Edits++; }
                if (message.Kind == "mouseUp") { _mouseContact = false; _mouseOperation = null; }
                return null;
            }
            // Native mouse events have already passed the CSP point filter.
            // An unavailable canvas ROI cannot discard a possibly dirty layer.
            double? x = J.Number(message.Data, "x"), y = J.Number(message.Data, "y");
            if (message.Kind != "mouseDown" || J.Get(message.Data, "inCsp").ValueKind == JsonValueKind.False
                || _canvasArea is { } area && x is not null && y is not null && !area.Contains((int)x.Value, (int)y.Value)) return null;
            _mouseContact = true;
            if (NavigationHeld(message.Data)) return null;
            long operation = message.OperationId is { } id ? checked((long)id)
                : message.EventId > 0 ? checked((long)message.EventId) : --_syntheticOperation;
            _mouseOperation = operation;
            _strokeBegins.TryAdd(operation, message.Ticks); _strokeLast[operation] = message.Ticks;
            _canvasStrokeOperations.Add(operation);
            if (_dirty.TryAdd(operation, message.Ticks)) Diagnostic?.Invoke($"编辑输入：mouse/left，operation={operation}，位置=({x},{y})");
            _lastDirty[operation] = message.Ticks; Edits++;
            return null;
        }
        if (message.Channel == "core.clipState" && message.Kind == "stateUpdated")
        {
            if (J.Confirmed(message.Data))
            {
                var previous = _clipLayers.LastOrDefault(pair => pair.Key < ticks);
                bool latest = _clipLayers.Count == 0 || ticks > _clipLayers.Keys.Last();
                _clipLayers[ticks] = LayerMapping.ReadLayers(J.Get(message.Data, "state"));
                string? copy = J.ObservationClock(message) == "triggerTicks" ? J.Text(J.Get(message.Data, "rawResult"), "snapshotPath") : null;
                if (copy is { Length: > 0 }) _clipSnapshots[ticks] = copy;
                while (_clipSnapshots.Count > 64) _clipSnapshots.Remove(_clipSnapshots.First().Key);
                _paperLayers[ticks] = LayerMapping.ReadPaperNames(J.Get(message.Data, "state"));
                while (_clipLayers.Count > 64) _clipLayers.Remove(_clipLayers.First().Key);
                while (_paperLayers.Count > 64) _paperLayers.Remove(_paperLayers.First().Key);
                Diagnostic?.Invoke($"图层属性核心：ticks={ticks}，图层数={_clipLayers[ticks].Length}，snapshot={message.IsSnapshot}");
                ObserveLayerUuids(message, ticks, _clipLayers[ticks], _paperLayers[ticks]);
                if (latest && previous.Value is { } before) ObserveVanished(message, ticks, previous.Key, before, _clipLayers[ticks], copy);
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
        if (view is null)
        {
            _canvasArea = null; // Do not use a stale ROI to discard input while the layout is unknown.
            return Skip("状态未确认或画布字段不完整");
        }
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
        // Aggregate packets point at native frames; an observation the recorder kept out of its file
        // (states read inside a Memoline edit transaction) only updates the view.
        if (requireConfirmedViewChange && message.AppendId == 0) return Skip("原生记录未收录该观测（编辑事务内），只更新视口状态");
        if (_reserved) return Skip("已有保存任务，合并本次变化");
        return Reserve(message, ticks, view, pending: false);
    }
    // A layer that appears in a save made after the recording baseline has no proven starting pixels yet.
    // The save that first lists it is exactly such a proof, provided the recorder kept that save's file;
    // a save taken right after a layer operation shows a new layer before anything is drawn on it.
    private void ObserveLayerUuids(RecorderRealtimeEvent message, long ticks, LayerReference[] layers, string[] paper)
    {
        var uuids = layers.Select(layer => layer.Uuid).OfType<string>().Where(uuid => uuid.Length > 0);
        if (_knownLayerUuids is null) { _knownLayerUuids = new(uuids, StringComparer.OrdinalIgnoreCase); return; }
        var appeared = layers.Where(layer => layer.Uuid is { Length: > 0 } uuid && _knownLayerUuids.Add(uuid)
            && !paper.Contains(LayerMapping.Compact(layer.Name), StringComparer.Ordinal)).ToArray();
        if (appeared.Length == 0) return;
        string names = string.Join("、", appeared.Select(layer => $"{layer.Name}({layer.Uuid})"));
        // States read inside a Memoline edit transaction are not in the native file; that transaction
        // registers its own reproduction baseline.
        if (message.IsSnapshot || message.AppendId == 0)
        { Diagnostic?.Invoke($"新图层 {names} 出现在编辑事务内的状态中（snapshot={message.IsSnapshot}，appendId={message.AppendId}），由事务登记复现层基线"); return; }
        if (J.ObservationClock(message) != "triggerTicks" || J.Text(J.Get(message.Data, "rawResult"), "snapshotPath") is not { Length: > 0 } snapshot)
        {
            // No save copy shows it before anything was drawn on it. A new layer starts empty: it did not exist at the
            // previous structure, so every contact on it comes after that.
            long born = _clipLayers.Keys.Where(key => key < ticks).DefaultIfEmpty(-1).Max() + 1;
            Diagnostic?.Invoke($"新图层 {names} 出现时没有可用的保存副本，以空白画布（triggerTicks={born}）作为基线");
            LayerAppeared?.Invoke(new(Generation, born, null, appeared));
            return;
        }
        Diagnostic?.Invoke($"新图层 {names} 首次出现在 triggerTicks={ticks} 的保存中，以该保存建立基线");
        LayerAppeared?.Invoke(new(Generation, ticks, snapshot, appeared));
    }
    // A layer the user removed is either deleted or merged into another one; which, only the pixels of the two
    // saves around it tell. Memoline's own deletions happen inside its transactions and are recorded by them.
    private void ObserveVanished(RecorderRealtimeEvent message, long ticks, long previousTicks, LayerReference[] before,
        LayerReference[] after, string? afterCopy)
    {
        var present = after.Select(layer => layer.Uuid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vanished = before.Where(layer => layer.Uuid is { Length: > 0 } uuid && !present.Contains(uuid)).ToArray();
        if (vanished.Length == 0 || message.IsSnapshot || message.AppendId == 0) return;
        string names = string.Join("、", vanished.Select(layer => $"{layer.Name}({layer.Uuid})"));
        // Ctrl+Z past a Memoline edit removes its reproduction and brings the deleted original back; the editing
        // index reads that as the edit undone (LayerLineage: the original's return revokes the replacement).
        var previous = before.Select(layer => layer.Uuid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var returned = after.Where(layer => layer.Uuid is { Length: > 0 } uuid && !previous.Contains(uuid)
            && _knownLayerUuids?.Contains(uuid) == true).ToArray();
        if (returned.Length > 0)
            Diagnostic?.Invoke($"图层 {names} 消失，此前的图层 {string.Join("、", returned.Select(layer => $"{layer.Name}({layer.Uuid})"))} 重新出现：" +
                "消失的若是记艺线复现层，即用户撤销了那次编辑，编辑索引视其从未发生");
        if (afterCopy is null || !_clipSnapshots.TryGetValue(previousTicks, out var beforeCopy))
        { Diagnostic?.Invoke($"图层 {names} 已从 CSP 中消失；前后保存副本不全，按用户删除处理"); return; }
        Diagnostic?.Invoke($"图层 {names} 已从 CSP 中消失，比较前后两次保存判断是删除还是合并");
        LayersVanished?.Invoke(new(Generation, ticks, beforeCopy, afterCopy, before, after, vanished));
    }
    public CaptureRequest? ReserveRecordingEnd(RecorderRealtimeEvent message)
    {
        if (message.Kind != "recordingEndRequested" || message.AppendId == 0 || message.Ticks < 0)
            throw new InvalidDataException("结束保存需要 Recognizer 的原生结束边界。");
        if (_reserved || _view is null) return null;
        return Reserve(message, message.Ticks, _view, pending: false, force: true) is { } request
            ? request with { TimeSource = "triggerTicks", TriggerKind = "recordingEnd" } : null;
    }
    public IReadOnlyList<CaptureRequest> ReserveRecordingEndLayers(RecorderRealtimeEvent message)
    {
        if (message.Kind != "recordingEndRequested" || message.AppendId == 0 || message.Ticks < 0)
            throw new InvalidDataException("结束保存需要 Recognizer 的原生结束边界。");
        if (_view is null || _viewTicks > message.Ticks)
            throw new InvalidDataException("末尾画布视口未在结束边界前确认。");
        var requests = ReserveLayerBoundary("recording-end-" + message.AppendId,
            KnownLayersAt(message.Ticks), message.Ticks, final: true);
        return requests.Select((request, index) => request with {
            TriggerKind = index == 0 ? "recordingEnd" : "recordingEndLayer"
        }).ToArray();
    }
    public IReadOnlyList<LayerReference> KnownLayersAt(long triggerTicks)
        => _clipLayers.LastOrDefault(pair => pair.Key <= triggerTicks).Value ?? [];

    public IReadOnlyList<CaptureRequest> ReserveEditBoundary(string requestId, IReadOnlyList<LayerReference> layers)
        => ReserveEditBoundary(requestId, layers, _lastEditBoundaryTicks);

    public IReadOnlyList<CaptureRequest> ReserveEditBoundary(string requestId, IReadOnlyList<LayerReference> layers, long triggerTicks)
        => ReserveLayerBoundary(requestId, layers, triggerTicks, final: false);

    private IReadOnlyList<CaptureRequest> ReserveLayerBoundary(string requestId, IReadOnlyList<LayerReference> layers,
        long triggerTicks, bool final)
    {
        if (string.IsNullOrWhiteSpace(requestId) || triggerTicks < 0)
            throw new InvalidDataException("编辑边界缺少 requestId 或原生 triggerTicks");
        if (_reserved || InContact) throw new InvalidOperationException("编辑边界必须等待捕获和完整笔接触结束");
        if (_view is null) throw new InvalidDataException("编辑边界画布视图未确认");
        string current = _layers.LastOrDefault(pair => pair.Key <= triggerTicks).Value
            ?? throw new InvalidDataException("编辑边界当前图层未确认");
        bool Paper(string key) => PaperAt(key, triggerTicks, layers);
        LayerReference[] Named(string name) => layers.Where(layer => LayerMapping.SameName(layer.Name, name)
            && !string.IsNullOrWhiteSpace(layer.Uuid)).ToArray();
        // Keyed by UUID. A layer the saved structure does not list yet (one brought back by Ctrl+Z, which
        // saves nothing) is keyed by its name and resolved in the boundary's own save.
        var touched = new Dictionary<string, (LayerReference Layer, List<long> Operations)>(StringComparer.OrdinalIgnoreCase);
        if (!Paper(LayerMapping.Compact(current)))
        {
            var named = Named(current);
            if (named.Length > 1) throw new InvalidDataException($"当前图层名「{current}」在保存的结构中对应多个图层，无法确定 UUID");
            var layer = named.Length == 1 ? named[0] : new LayerReference(-1, current, null);
            touched.Add(Key(layer), (layer, []));
        }
        // Previous captures have consumed their contact tracking. They still
        // need fresh full-layer pixels at this boundary for cross-layer selection.
        foreach(string captured in _capturedLayerUuids)
        {
            var matches=layers.Where(layer=>string.Equals(layer.Uuid,captured,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length==1)touched.TryAdd(Key(matches[0]),(matches[0],[]));
        }
        var contacts = final ? _strokeBegins.Where(pair => pair.Value <= triggerTicks && _canvasStrokeOperations.Contains(pair.Key))
            .Concat(_dirty.Where(pair => pair.Value <= triggerTicks && !_strokeBegins.ContainsKey(pair.Key)))
            : _strokeBegins.Where(pair => pair.Value <= triggerTicks);
        foreach (var stroke in contacts)
        {
            string name = _layers.LastOrDefault(pair => pair.Key <= stroke.Value).Value
                ?? throw new InvalidDataException("笔接触开始时的历史图层未确认");
            if (Paper(LayerMapping.Compact(name))) continue;
            // The layer the contact landed on is the one its name meant then; a later layer that took the name
            // (CSP reuses "图层 1" after a deletion) is a different layer.
            var then = (_clipLayers.LastOrDefault(pair => pair.Key <= stroke.Value).Value ?? [])
                .Where(layer => LayerMapping.SameName(layer.Name, name) && !string.IsNullOrWhiteSpace(layer.Uuid)).ToArray();
            LayerReference? target;
            if (then.Length == 1)
            {
                target = layers.FirstOrDefault(layer => string.Equals(layer.Uuid, then[0].Uuid, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                { Diagnostic?.Invoke($"笔接触 {stroke.Key} 所在图层「{name}」({then[0].Uuid}) 已不在当前图层结构中（已删除或已替换），不再捕获"); continue; }
            }
            else
            {
                var named = Named(name);
                if (named.Length > 1) throw new InvalidDataException($"笔接触所在图层名「{name}」对应多个图层，无法确定 UUID");
                if (named.Length == 0 && !LayerMapping.SameName(name, current))
                { Diagnostic?.Invoke($"笔接触 {stroke.Key} 所在图层「{name}」已不在当前图层结构中，不再捕获"); continue; }
                target = named.Length == 1 ? named[0] : touched.Values.Select(t => t.Layer).First(l => l.Uuid is null);
            }
            long captured = target.Uuid is { } uuid ? _capturedUuidTicks.GetValueOrDefault(uuid, -1) : -1;
            if (_strokeLast.GetValueOrDefault(stroke.Key, stroke.Value) <= captured) continue;
            if (!touched.TryGetValue(Key(target), out var entry)) touched[Key(target)] = entry = (target, []);
            entry.Operations.Add(stroke.Key);
        }
        if (touched.Count == 0)
            throw new InvalidDataException($"当前图层「{current}」是纸张，边界没有需要保存的图层；请先选中要编辑的图层");
        var requests = new List<CaptureRequest>();
        foreach (var (layer, operations) in touched.Values)
            requests.Add(new(Generation, triggerTicks, layer.Name, _view, operations.Order().ToArray(), layer,
                "triggerTicks", "editBoundary") { BoundaryRequestId = requestId });
        _reserved = true;
        foreach (var request in requests) _pendingBoundaryLayers.Add(request.Layer!.Id);
        return requests;
    }
    private static string Key(LayerReference layer) => layer.Uuid ?? "name:" + LayerMapping.Compact(layer.Name);
    private CaptureRequest? Reserve(RecorderRealtimeEvent message, long ticks, View view, bool pending, bool force = false)
    {
        CaptureRequest? Skip(string reason) { ViewDiagnostic(message, ticks, reason, view); return null; }
        string? layer = _layers.LastOrDefault(p => p.Key <= ticks).Value;
        if (string.IsNullOrWhiteSpace(layer)) return Skip("切换时图层未确认");
        if (PaperAt(LayerMapping.Compact(layer), ticks, _clipLayers.LastOrDefault(p => p.Key <= ticks).Value ?? []))
            return Skip("切换时图层是纸张，不接受笔画，不保存");
        var operations = _dirty.Where(pair => pair.Value <= ticks
            && LayerMapping.SameName(_layers.LastOrDefault(observation => observation.Key
                <= _strokeBegins.GetValueOrDefault(pair.Key, pair.Value)).Value, layer))
            .Select(pair => pair.Key).ToArray();
        if (operations.Length == 0 && !force) return Skip("切换前没有符合条件的当前图层编辑输入");
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
    // A raster layer of the same name wins: only a name that is nothing but CSP's
    // paper layer proves the contact changed no pixels.
    private bool PaperAt(string key, long ticks, IReadOnlyList<LayerReference> layers)
        => (_paperLayers.LastOrDefault(pair => pair.Key <= ticks).Value ?? []).Contains(key, StringComparer.Ordinal)
            && !layers.Any(layer => LayerMapping.Compact(layer.Name) == key);
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
        && (request.TriggerKind == "editBoundary" || request.TriggerKind is "recordingEnd" or "recordingEndLayer" && request.BoundaryRequestId is not null
            ? request.Layer is { } layer && _pendingBoundaryLayers.Contains(layer.Id)
            : LayerMapping.SameName(_layers.LastOrDefault(p => p.Key <= request.TriggerTicks).Value, request.LayerName));
    public void Complete(CaptureRequest request, bool success)
    {
        if (request.Generation != Generation) return;
        if (success) _completedTriggerTicks = Math.Max(_completedTriggerTicks, request.TriggerTicks);
        if (success)
        {
            string layerKey = LayerMapping.Compact(request.LayerName);
            _capturedLayers[layerKey] = Math.Max(_capturedLayers.GetValueOrDefault(layerKey, -1), request.TriggerTicks);
            if (request.Layer is { Uuid: { Length: > 0 } uuid })
            {
                _capturedLayerUuids.Add(uuid);
                _capturedUuidTicks[uuid] = Math.Max(_capturedUuidTicks.GetValueOrDefault(uuid, -1), request.TriggerTicks);
            }
            foreach (long operation in _strokeBegins.Where(pair => pair.Value <= request.TriggerTicks
                && _strokeLast.GetValueOrDefault(pair.Key, pair.Value) <= request.TriggerTicks
                && LayerMapping.SameName(_layers.LastOrDefault(layer => layer.Key <= pair.Value).Value, request.LayerName))
                .Select(pair => pair.Key).ToArray())
            { _strokeBegins.Remove(operation); _strokeLast.Remove(operation); _canvasStrokeOperations.Remove(operation); }
        }
        if (success) foreach (long operation in request.Operations)
        {
            if (_lastDirty.GetValueOrDefault(operation) <= request.TriggerTicks) { _dirty.Remove(operation); _lastDirty.Remove(operation); }
            else _dirty[operation] = request.TriggerTicks + 1;
        }
        if ((request.TriggerKind == "editBoundary" || request.TriggerKind is "recordingEnd" or "recordingEndLayer")
            && request.Layer is { } completedLayer)
            _pendingBoundaryLayers.Remove(completedLayer.Id);
        _reserved = _pendingBoundaryLayers.Count > 0;
    }
}
