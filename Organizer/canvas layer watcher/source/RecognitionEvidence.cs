using System.Text.Json;
using BehaviorRecognizer.Realtime;
using DirtyMatrix.Core;

namespace CanvasLayerWatcher;

internal sealed record RecognizerState(string Id, string Channel, long Ticks, long PublishedTicks, JsonElement Data,
    ulong AppendId = 0);
internal sealed record InputObservation(long Ticks, string Channel, string Kind, long OperationId, JsonElement Data,
    JsonElement DeviceSource, bool Continuity = false, ulong EventId = 0, ulong[]? RelatedEventIds = null, ulong AppendId = 0);
internal sealed record EvidenceWindow(string SessionId, long Frequency, long FromTicks, long ToTicks, bool Complete,
    RecognizerState[] States, InputObservation[] Inputs, string[] CaptureStateIds);
internal sealed record DirtyLabel(string Id, long OperationId, long FromTicks, long ToTicks, string Source,
    string[] StateIds, StrokeCoverage Coverage, string[] Warnings);
internal sealed record DirtyEvidence(EvidenceWindow Recognizer, DirtyLabel[] Labels, string[] Warnings);
internal sealed class FrozenEvidence(string session, long frequency, long connectedTicks, long droppedThrough,
    long triggerTicks, RecognizerState[] states, InputObservation[] inputs)
{
    public EvidenceWindow Snapshot(long from, long to)
    {
        if (to > triggerTicks) throw new InvalidOperationException("取证窗口不能超过已固定的 triggerTicks");
        return RecognitionEvidence.Window(session, frequency, connectedTicks, droppedThrough, states, inputs, from, to);
    }
}

// Owned by the monitor lock. Keep the original state payloads, including status
// and evidence; core observations use triggerTicks rather than completion time.
internal sealed class RecognitionEvidence
{
    private readonly List<RecognizerState> _states = [];
    private readonly List<InputObservation> _inputs = [];
    private string _session = "";
    private long _frequency, _connectedTicks, _droppedThrough = -1;
    private long _syntheticId;
    private readonly Dictionary<string, long> _active = [];
    public void Reset(string session, long frequency, long connectedTicks)
    {
        _states.Clear(); _inputs.Clear(); _active.Clear(); _session = session; _frequency = frequency;
        _connectedTicks = connectedTicks; _droppedThrough = -1; _syntheticId = 0;
    }
    public void Configuration(long ticks, JsonElement data, ulong appendId = 0)
        => _states.Add(new("configuration:" + ticks, "configuration", ticks, ticks, data.Clone(), appendId));
    public void Accept(RecorderRealtimeEvent message)
    {
        bool state = message.Channel.StartsWith("core.") || message.Kind is "driverConfiguration" or "tabletDeviceChanged"
            || (message.Channel == "keyboard" && message.Kind is "keyDown" or "keyUp" or "reset");
        if (state)
        {
            long ticks = J.ObservationTicks(message);
            string channel = message.Channel == "tablet" ? "tablet." + message.Kind : message.Channel;
            if (message.Kind == "evidenceCaptured") channel += ".evidence";
            if (message.Kind == "tabletDeviceChanged") channel += "." + J.Text(message.Data, "deviceId");
            _states.Add(new($"{channel}:{message.Sequence}:{ticks}", channel, ticks, message.PublishedTicks, message.Data.Clone(), message.AppendId));
            if (_states.Count > 2048) { _droppedThrough = Math.Max(_droppedThrough, _states[0].Ticks); _states.RemoveAt(0); }
        }
        if (message.IsSnapshot) return;
        bool pen = message.Channel == "tablet" && message.Kind is "penBegin" or "penSample" or "penEnd" or "penInterrupted";
        bool mouse = message.Channel == "mouse" && (J.Text(message.Data, "button") == "left"
            || message.Kind == "mouseInterrupted") && message.Kind is "mouseDown" or "mouseDrag" or "mouseUp" or "mouseInterrupted";
        if (!pen && !mouse) return;
        string key = message.Channel;
        if (message.Kind is "penBegin" or "mouseDown") _active[key] = message.OperationId is { } id ? (long)id
            : message.EventId > 0 ? (long)message.EventId : --_syntheticId;
        long operation = message.OperationId is { } op ? (long)op
            : _active.TryGetValue(key, out long current) ? current : --_syntheticId;
        _inputs.Add(new(message.Ticks, message.Channel, message.Kind, operation, message.Data.Clone(),
            JsonSerializer.SerializeToElement(message.DeviceSource, SnapshotHistory.Json), false, message.EventId, message.RelatedEventIds, message.AppendId));
        if (message.Kind is "penEnd" or "penInterrupted" or "mouseUp" or "mouseInterrupted") _active.Remove(key);
        if (_inputs.Count > 50000) { _droppedThrough = Math.Max(_droppedThrough, _inputs[999].Ticks); _inputs.RemoveRange(0, 1000); }
    }
    public FrozenEvidence Freeze(long triggerTicks) => new(_session, _frequency, _connectedTicks, _droppedThrough, triggerTicks,
        _states.Where(s => s.Ticks <= triggerTicks).ToArray(), _inputs.Where(p => p.Ticks <= triggerTicks).ToArray());
    public EvidenceWindow Snapshot(long from, long to) => Window(_session, _frequency, _connectedTicks, _droppedThrough, _states, _inputs, from, to);
    internal static EvidenceWindow Window(string session, long frequency, long connectedTicks, long droppedThrough,
        IEnumerable<RecognizerState> observations, IEnumerable<InputObservation> contacts, long from, long to)
    {
        if (from < 0 || to < from) throw new InvalidDataException("Recognizer 取证时间范围无效");
        var allStates = observations.ToArray(); var allInputs = contacts.ToArray();
        var before = allStates.Where(s => s.Ticks <= from).GroupBy(s => s.Channel)
            .SelectMany(g => new[] { g.MaxBy(s => s.Ticks), g.Where(s => J.Confirmed(s.Data)).MaxBy(s => s.Ticks) })
            .OfType<RecognizerState>().ToArray();
        var states = before.Concat(allStates.Where(s => s.Ticks > from && s.Ticks <= to)).DistinctBy(s => s.Id)
            .OrderBy(s => s.Ticks).ToArray();
        var capture = states.GroupBy(s => s.Channel).Select(g => g.MaxBy(s => s.Ticks)!.Id).ToArray();
        var inputs = allInputs.Where(p => p.Ticks > from && p.Ticks <= to).ToArray();
        var continuing = inputs.Select(p => (p.Channel, p.OperationId)).ToHashSet();
        var context = allInputs.Where(p => p.Ticks <= from && continuing.Contains((p.Channel, p.OperationId)))
            .GroupBy(p => (p.Channel, p.OperationId)).Select(g => g.MaxBy(p => p.Ticks)!)
            .Where(p => p.Kind is "penBegin" or "penSample" or "mouseDown" or "mouseDrag")
            .Select(p => p with { Continuity = true }).ToArray();
        long stateFrom = context.Length == 0 ? from : Math.Min(from, context.Min(p => p.Ticks));
        var continuityStates = allStates.Where(s => s.Ticks <= stateFrom).GroupBy(s => s.Channel).Select(g => g.MaxBy(s => s.Ticks)!);
        states = states.Concat(continuityStates).DistinctBy(s => s.Id).OrderBy(s => s.Ticks).ToArray();
        return new(session, frequency, from, to, from >= connectedTicks && from > droppedThrough,
            states, context.Concat(inputs).ToArray(), capture);
    }
    public void Trim(long ticks)
    {
        // Retain contact continuity and one preceding state for each channel.
        var last = _inputs.Where(p => p.Ticks <= ticks).GroupBy(p => (p.Channel, p.OperationId)).Select(g => g.Last())
            .Where(p => p.Kind is "penBegin" or "penSample" or "mouseDown" or "mouseDrag").ToArray();
        var keep = _states.Where(s => s.Ticks <= ticks).GroupBy(s => s.Channel)
            .SelectMany(g => new[] { g.MaxBy(s => s.Ticks), g.Where(s => J.Confirmed(s.Data)).MaxBy(s => s.Ticks) })
            .OfType<RecognizerState>().Select(s => s.Id).ToHashSet();
        foreach (long contextTicks in last.Select(p => p.Ticks).Distinct())
            foreach (var state in _states.Where(s => s.Ticks <= contextTicks).GroupBy(s => s.Channel)
                .SelectMany(g => new[] { g.MaxBy(s => s.Ticks), g.Where(s => J.Confirmed(s.Data)).MaxBy(s => s.Ticks) })
                .OfType<RecognizerState>()) keep.Add(state.Id);
        _states.RemoveAll(s => s.Ticks <= ticks && !keep.Contains(s.Id));
        _inputs.RemoveAll(p => p.Ticks <= ticks);
        _inputs.InsertRange(0, last);
    }

    public static DirtyEvidence Build(EvidenceWindow window, CaptureRequest request, Size canvas)
    {
        var labels = new Dictionary<string, (long Operation, long From, long To, string Source, string[] States, double Radius, List<StrokeSegment> Segments, string[] Warnings)>();
        var last = new Dictionary<long, (PointD Point, string Label)>();
        var eligibleMouse = new HashSet<long>();
        var warnings = new HashSet<string>();
        if (!window.Complete) warnings.Add("inputHistoryIncomplete");
        var orderedStates = window.States.OrderBy(s => s.Ticks).ToArray();
        int nextState = 0;
        var at = new Dictionary<string, RecognizerState>();
        RecognizerState? view = null;
        foreach (var input in window.Inputs.OrderBy(p => p.Ticks))
        {
            while (nextState < orderedStates.Length && orderedStates[nextState].Ticks <= input.Ticks)
            {
                var state = orderedStates[nextState++]; at[state.Channel] = state;
                if (state.Channel == "core.canvasViewState" && J.Confirmed(state.Data)) view = state;
            }
            var keys = J.Get(input.Data, "heldKeys");
            if (input.Kind is "penInterrupted" or "mouseInterrupted" || (keys.ValueKind == JsonValueKind.Array
                && keys.EnumerateArray().Any(k => k.TryGetInt32(out int vk) && vk is 32 or 17 or 18 or 0xA2 or 0xA3 or 0xA4 or 0xA5))
                || J.Get(input.Data, "inCsp").ValueKind == JsonValueKind.False)
            { last.Remove(input.OperationId); eligibleMouse.Remove(input.OperationId); continue; }
            var currentLayer = at.GetValueOrDefault("core.currentLayerState");
            if (currentLayer is not null && J.Confirmed(currentLayer.Data)
                && J.Get(currentLayer.Data, "state").ValueKind == JsonValueKind.String
                && !LayerMapping.SameName(J.Get(currentLayer.Data, "state").GetString(), request.LayerName))
            { last.Remove(input.OperationId); continue; }
            if (view is null || View.Parse(view.Data) is not { } parsed)
            { warnings.Add("canvasTransformUnavailable"); last.Remove(input.OperationId); continue; }
            // x/y is the Windows cursor actually used by CSP; passive pen also
            // exposes only this pair. Driver-mapped coordinates are a fallback.
            double? x = J.Number(input.Data, "x") ?? J.Number(input.Data, "screenX");
            double? y = J.Number(input.Data, "y") ?? J.Number(input.Data, "screenY");
            if (x is null || y is null) { warnings.Add("screenCoordinatesUnavailable"); continue; }
            var config = at.GetValueOrDefault("configuration");
            var regions = J.Get(J.Get(J.Get(J.Get(config?.Data ?? default, "settings"), "navigatorOcrSelection"), "layout"), "regions");
            var roi = RecognizerMonitor.ViewArea(view.Data)
                ?? RecognizerMonitor.ScreenArea(J.Get(regions, "画布视口"));
            if (input.Channel == "mouse")
            {
                if (input.Kind == "mouseDown" || input.Continuity)
                {
                    if (roi is not { } area || !area.Contains((int)x.Value, (int)y.Value))
                    { eligibleMouse.Remove(input.OperationId); last.Remove(input.OperationId); continue; }
                    eligibleMouse.Add(input.OperationId);
                }
                if (!eligibleMouse.Contains(input.OperationId)) continue;
            }
            if (!TryCanvasPoint(view.Data, parsed, x.Value, y.Value, canvas, out var point))
            { warnings.Add("rotatedCanvasWithoutAffineMapping"); last.Remove(input.OperationId); continue; }
            if (Math.Abs(point.X) > 1000000 || Math.Abs(point.Y) > 1000000) { warnings.Add("invalidCanvasCoordinates"); continue; }
            var brush = at.GetValueOrDefault("core.brushState");
            var clip = at.GetValueOrDefault("core.clipState");
            var localWarnings = new List<string>();
            double size = 20; BrushUnit unit = BrushUnit.Px;
            var brushData = brush is not null && J.Confirmed(brush.Data) ? J.Get(brush.Data, "state") : default;
            var properties = J.Get(brushData, "properties");
            bool sizeKnown = false, fixedScreenSize = false;
            if (properties.ValueKind == JsonValueKind.Array)
                foreach (var property in properties.EnumerateArray())
                {
                    if (J.Text(property, "key") == "brush_size.specify_by_size_on_screen")
                        fixedScreenSize = J.Get(property, "value").ValueKind == JsonValueKind.True;
                    if (J.Text(property, "key") is "brush_size" or "brushSize" or "笔刷尺寸" or "笔刷大小")
                    {
                        double? value = J.Number(property, "value");
                        if (value is null && double.TryParse(J.Text(property, "value"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double numeric)) value = numeric;
                        if (value is > 0 and <= 100000)
                        { size = value.Value; unit = J.Text(property, "unit") == "mm" ? BrushUnit.Mm : BrushUnit.Px; sizeKnown = true; }
                        if (string.IsNullOrEmpty(J.Text(property, "unit"))) localWarnings.Add("brushUnitAssumedPx");
                    }
                }
            if (!sizeKnown) localWarnings.Add("brushSizeFallback20Px");
            double dpi = J.Number(J.Get(J.Get(clip?.Data ?? default, "state"), "canvas"), "resolution") ?? 300;
            var settings = new CoverageSettings { CanvasWidth = canvas.Width, CanvasHeight = canvas.Height,
                BrushSize = size, BrushUnit = unit, Dpi = dpi > 0 ? dpi : 300, ZoomPercent = parsed.Scale,
                SpecifyBySizeOnScreen = fixedScreenSize, ClipToCanvasBounds = true };
            double radius = settings.Radius;
            var stateIds = at.Values.Select(s => s.Id).Append(view.Id).Distinct().Order().ToArray();
            // Keyboard key-up changes need not fragment otherwise identical brush labels.
            string labelKey = input.OperationId + ":" + string.Join("|", stateIds.Where(id => !id.StartsWith("keyboard:")));
            if (!labels.TryGetValue(labelKey, out var label))
                label = (input.OperationId, Math.Max(input.Ticks, window.FromTicks), input.Ticks, input.Channel, stateIds, radius, [], localWarnings.ToArray());
            var start = last.TryGetValue(input.OperationId, out var previous) ? previous.Point : point;
            if (!input.Continuity) label.Segments.Add(new(start, point)); label.To = input.Ticks;
            labels[labelKey] = label; last[input.OperationId] = (point, labelKey);
            if (input.Kind is "penEnd" or "mouseUp") { last.Remove(input.OperationId); eligibleMouse.Remove(input.OperationId); }
        }
        return new(window, labels.Where(pair => pair.Value.Segments.Count > 0).Select((pair, index) => new DirtyLabel("input-" + index, pair.Value.Operation,
            pair.Value.From, pair.Value.To, pair.Value.Source, pair.Value.States,
            new(pair.Value.Segments.ToArray(), pair.Value.Radius), pair.Value.Warnings)).ToArray(), warnings.ToArray());
    }
    private static bool TryCanvasPoint(JsonElement data, View view, double x, double y, Size canvas, out PointD point)
    {
        // The native ScreenToCanvas matrix maps to normalized canvas coordinates.
        var affine = J.Get(J.Get(J.Get(data, "rawResult"), "snapshot"), "screenToCanvas");
        double?[] m = Enumerable.Range(0, 6).Select(i => J.Number(affine, "m" + i)).ToArray();
        if (m.All(v => v.HasValue) && Math.Abs(m[0]!.Value * m[4]!.Value - m[1]!.Value * m[3]!.Value) > 1e-18)
        {
            point = new((m[0]!.Value * x + m[1]!.Value * y + m[2]!.Value) * canvas.Width,
                (m[3]!.Value * x + m[4]!.Value * y + m[5]!.Value) * canvas.Height);
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }
        // Recognizer publishes native geometry rotation separately from OCR.
        // Its marker is canvas (0,0); positive geometry rotation is clockwise
        // in screen coordinates. Invert the resulting similarity transform.
        double? rotation = J.Number(J.Get(J.Get(data, "state"), "transform"), "rotationDegrees");
        if (rotation is null && Math.Abs(Math.IEEERemainder(view.Rotation, 360)) > .01)
        { point = default; return false; }
        double angle = Math.IEEERemainder(rotation ?? 0, 360) * Math.PI / 180;
        double c = Math.Cos(angle), s = Math.Sin(angle), dx = x - view.X, dy = y - view.Y, zoom = view.Scale / 100;
        point = new((c * dx + s * dy) / zoom, (-s * dx + c * dy) / zoom);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }
}
