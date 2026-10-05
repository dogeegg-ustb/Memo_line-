using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
RecorderRealtimeEvent Event(string channel, string kind, long ticks, object data, bool snapshot = false, ulong? op = null)
    => new(1, "test", ticks, channel, kind, snapshot, 0, 0, ticks, ticks, ticks, op, null, null, JsonSerializer.SerializeToElement(data));
RecorderRealtimeEvent Layer(long t, string? name = "Ink", string status = "changed") => Event("core.currentLayerState", "stateUpdated", t, new { status, state = name, evidence = new { capturedTicks = t } });
RecorderRealtimeEvent ViewEvent(long t, double x = 0, bool snapshot = false, string status = "changed", bool initial = false)
    => Event("core.canvasViewState", "stateUpdated", t, new { status, initial, state = new { canvasOriginScreenPx = new { x, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 }, evidence = new { capturedTicks = t } }, snapshot);
RecorderRealtimeEvent Pen(long t, ulong op = 1, int[]? heldKeys = null, bool snapshot = false, string kind = "penBegin")
    => Event("tablet", kind, t, new { heldKeys = heldKeys ?? [], inCsp = true }, snapshot, op);
WatchState Ready()
{ var state = new WatchState(); state.Reset(); state.Accept(Layer(1)); state.Accept(ViewEvent(2, snapshot: true)); return state; }

if (args.Length == 3 && args[0] == "--probe-endpoint")
{
    var probe = new RecognizerMonitor();
    probe.Status += Console.WriteLine; probe.Diagnostic += Console.WriteLine;
    probe.Capture += request => { Console.WriteLine("Probe observed a capture; no save requested."); probe.Complete(request, false); };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    try { await probe.RunAsync(args[1], args[2], timeout.Token); } catch (OperationCanceledException) { }
    var observed = probe.GetEvidence(0, long.MaxValue);
    Console.WriteLine("PROBE STATE CHANNELS: " + string.Join(',', observed.States.Select(s => s.Channel).Distinct()));
    return;
}

if (args.Length == 4 && args[0] == "--diff-recorded")
{
    using var recorded = JsonDocument.Parse(File.ReadAllText(args[1]));
    var evidence = recorded.RootElement.GetProperty("dirty").Deserialize<DirtyEvidence>(SnapshotHistory.Json)!;
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    var diff = LayerDiff.Compare(args[2], args[2], evidence, args[3], CancellationToken.None);
    File.WriteAllText(Path.Combine(args[3], "comparison.json"), JsonSerializer.Serialize(new { evidence, diff }, SnapshotHistory.Json));
    Console.WriteLine($"FULL CANVAS DIFF: {diff.PredictedMatrix.CanvasWidth}x{diff.PredictedMatrix.CanvasHeight}, labels={diff.Labels.Length}, finePixels={diff.FullResolutionComparedPixels}, coarsePixels={diff.CoarseComparedPixels}, changes={diff.ChangedPixels}, elapsedMs={elapsed.ElapsedMilliseconds}; identical real export, no CSP input.");
    if (diff.ChangedPixels != 0 || diff.FullResolutionComparedPixels <= 0) throw new Exception("Full canvas comparison failed");
    return;
}

// Read-only replay of a real recording; no control requests or native input.
if (args.Length == 2 && args[0] is "--replay-session" or "--replay-dirty")
{
    bool replayDirty = args[0] == "--replay-dirty";
    var replay = new WatchState(); replay.Reset(); replay.Diagnostic += Console.WriteLine;
    var evidence = new RecognitionEvidence(); long from = 0; Size canvas = new(1, 1);
    int captures = 0, views = 0, mouse = 0, pen = 0;
    foreach (var frame in MemolineReader.Read(args[1]))
    {
        string? kind = J.Text(frame, "kind"); var data = J.Get(frame, "data");
        if (kind == "header") evidence.Reset(J.Text(frame, "sessionId") ?? "replay", J.Tick(frame, "frequency", 0), 0);
        if (kind == "initializationConfiguration")
        {
            evidence.Configuration(J.Tick(frame, "ticks", 0), data);
            var size = J.Get(data, "canvasPixelSize");
            if (size.ValueKind == JsonValueKind.Array && size.GetArrayLength() == 2) canvas = new(size[0].GetInt32(), size[1].GetInt32());
            var regions = J.Get(J.Get(J.Get(J.Get(data, "settings"), "navigatorOcrSelection"), "layout"), "regions");
            if (RecognizerMonitor.ScreenArea(J.Get(regions, "画布视口")) is { } area) replay.SetCanvasArea(area);
        }
        string? channel = kind == "coreStateUpdated" ? "core." + J.Text(data, "module")
            : kind is "mouseDown" or "mouseUp" or "mouseDrag" or "mouseInterrupted" ? "mouse"
            : kind is "penBegin" or "penSample" or "penEnd" or "penInterrupted" or "driverConfiguration" or "tabletDeviceChanged" ? "tablet"
            : kind is "keyboardStateChanged" or "keyInput" ? "keyboard" : null;
        if (channel is null) continue;
        if (channel == "core.canvasViewState") views++;
        if (kind == "mouseDown") mouse++;
        if (kind == "penBegin") pen++;
        if (channel == "core.canvasViewState" && J.Confirmed(data)
            && RecognizerMonitor.ViewArea(data) is { } workspace)
            replay.SetCanvasArea(workspace);
        var message = Event(channel, kind == "coreStateUpdated" ? "stateUpdated" : kind == "keyboardStateChanged" ? J.Text(data, "action")! : kind!, J.Tick(frame, "ticks", 0), data,
            op: J.Get(frame, "operationId").ValueKind == JsonValueKind.Number ? J.Get(frame, "operationId").GetUInt64() : null) with {
                EventId = (ulong)J.Tick(frame, "eventId", 0), PublishedTicks = J.Tick(frame, "appendedTicks", 0),
                DeviceSource = J.Get(frame, "deviceSource").ValueKind == JsonValueKind.Object
                    ? J.Get(frame, "deviceSource").Deserialize<HardwareDeviceSource>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null
            };
        evidence.Accept(message);
        if (replay.Accept(message) is { } request)
        {
            captures++; Console.WriteLine($"REPLAY CAPTURE: ticks={request.Ticks}, layer={request.LayerName}, id={request.Layer?.Id}, uuid={request.Layer?.Uuid}");
            if (replayDirty)
            {
                var dirty = RecognitionEvidence.Build(evidence.Snapshot(from, request.Ticks), request, canvas);
                var matrix = LayerDiff.Predicted(dirty, canvas);
                string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/real-dirty-replay")); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "capture-" + captures + ".json"), JsonSerializer.Serialize(new { request, canvas, dirty, matrix }, SnapshotHistory.Json));
                Console.WriteLine($"REPLAY DIRTY: inputs={dirty.Recognizer.Inputs.Length}, labels={dirty.Labels.Length}, cells={matrix.RowRuns.Sum(r => r.EndColumnExclusive - r.StartColumn)}, states={string.Join(',', dirty.Recognizer.States.Select(s => s.Channel).Distinct())}, warnings={string.Join(',', dirty.Warnings)}");
                from = request.Ticks; evidence.Trim(from);
            }
            replay.Complete(request, true);
        }
    }
    Console.WriteLine($"REPLAY: views={views}, mousePresses={mouse}, penBegins={pen}, captures={captures}; no save input dispatched.");
    if (views == 0 || captures == 0) throw new Exception("Recording did not produce a capture");
    return;
}

RecorderRealtimeEvent Mouse(long t, int x = 100, int y = 100, string kind = "mouseDown", string button = "left", int[]? keys = null, bool snapshot = false)
    => Event("mouse", kind, t, new { x, y, button, heldKeys = keys ?? [] }, snapshot, 200);
var compatibility = Ready(); compatibility.SetCanvasArea(new(10, 10, 200, 200));
var decisions = new List<string>(); compatibility.Diagnostic += decisions.Add;
compatibility.Accept(Mouse(3, keys: [32])); compatibility.Accept(Mouse(4, kind: "mouseUp"));
Check(!compatibility.Dirty && !compatibility.InContact, "Mouse navigation is excluded and released");
compatibility.Accept(Mouse(5, x: 500));
Check(!compatibility.Dirty, "Panel mouse presses do not count as drawing");
compatibility.Accept(Mouse(6, button: "middle"));
Check(!compatibility.Dirty, "Middle mouse navigation excluded");
compatibility.Accept(Mouse(7, snapshot: true));
Check(!compatibility.Dirty, "Mouse snapshot is not an edit");
compatibility.Accept(Mouse(8));
Check(compatibility.Dirty && compatibility.InContact, "Canvas mouse input supplies edit fallback");
compatibility.Accept(Mouse(9, kind: "mouseUp"));
Check(!compatibility.InContact, "Mouse release permits saving");
compatibility.Accept(Event("core.currentLayerState", "stateUpdated", 10, new {
    status = "unknown", state = (string?)null, evidence = new { reason = "supersededBeforeAnalysis" }
}));
Check(compatibility.LayerName == "Ink", "Superseded intermediate layer observation preserves confirmed layer");
var causalView = Event("core.canvasViewState", "stateUpdated", 11, new {
    status = "changed", state = new { canvasOriginScreenPx = new { x = 40, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
    evidence = new { capturedTicks = 11, causalAmbiguous = true }
});
var causalCapture = compatibility.Accept(causalView);
Check(causalCapture is { LayerName: "Ink" }, "Confirmed view with causal attribution diagnostics triggers save");
Check(decisions.Any(s => s.Contains("触发保存") && s.Contains("\"causalAmbiguous\":true")), "View decision and causal diagnostics logged");
compatibility.Complete(causalCapture!, true);
compatibility = Ready(); compatibility.Accept(Pen(3)); compatibility.Accept(Layer(7));
Check(compatibility.Accept(ViewEvent(6, 40)) is not null, "Later same-name layer confirmation permits delayed view");
compatibility = Ready(); compatibility.Accept(Pen(3)); compatibility.Accept(Layer(7, "Other"));
Check(compatibility.Accept(ViewEvent(6, 40)) is { LayerName: "Ink" }, "Later layer selection does not replace or block the trigger-time layer");
var triggerClock = Ready(); triggerClock.Accept(Pen(3)); triggerClock.Accept(Pen(9, 2));
triggerClock.Accept(Event("core.currentLayerState", "stateUpdated", 20, new {
    status = "changed", state = "Other", evidence = new { triggerTicks = 7, capturedTicks = 18, completedTicks = 25 }
}));
var triggerRequest = triggerClock.Accept(Event("core.canvasViewState", "stateUpdated", 30, new {
    status = "changed", state = new { canvasOriginScreenPx = new { x = 40, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
    evidence = new { triggerTicks = 5, capturedTicks = 15, completedTicks = 29 }
}));
Check(triggerRequest is { TriggerTicks: 5, LayerName: "Ink", TimeSource: "triggerTicks" }
    && triggerRequest.Operations.SequenceEqual(new long[] { 1 }) && triggerClock.Valid(triggerRequest),
    "API triggerTicks fixes the layer and excludes input between trigger, screenshot and completion");
var earlyState = Ready(); earlyState.Accept(Pen(3));
var early = earlyState.Accept(Event("core.canvasViewState", "evidenceCaptured", 8, new {
    initial = false, evidence = new { triggerTicks = 4, capturedTicks = 7, analysisPending = true }
}));
Check(early is { TriggerTicks: 4, TriggerKind: "evidenceCaptured", ViewPending: true, LayerName: "Ink" },
    "Canvas evidence requests a save before any viewport analysis result exists");
earlyState.Accept(Pen(5, 2)); earlyState.Complete(early!, true);
Check(earlyState.Accept(Event("core.canvasViewState", "stateUpdated", 10, new {
    status = "changed", state = new { canvasOriginScreenPx = new { x = 40, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
    evidence = new { triggerTicks = 4, capturedTicks = 7, completedTicks = 10 }
})) is null && earlyState.Dirty, "Late viewport result does not resave the completed trigger or clear its later inputs");
Check(earlyState.Accept(Event("core.canvasViewState", "evidenceCaptured", 11, new {
    initial = false, evidence = new { triggerTicks = 6, capturedTicks = 11, analysisPending = true }
})) is { TriggerTicks: 6 }, "Next evidence trigger saves edits retained after the previous trigger");
var confirmedState = new WatchState(requireConfirmedViewChange: true);
var observations = new List<ViewportObservation>(); confirmedState.ViewportObserved += observations.Add;
confirmedState.Reset(); confirmedState.Accept(Layer(1)); confirmedState.Accept(ViewEvent(2, snapshot: true));
Check(observations is [{ Changed: 0, IsBaseline: true, HasEditingInput: false }], "Confirmed mode emits a zero-valued baseline observation");
confirmedState.Accept(Pen(3));
Check(confirmedState.Accept(Event("core.canvasViewState", "evidenceCaptured", 8, new {
    initial = false, evidence = new { triggerTicks = 4, capturedTicks = 7, analysisPending = true }
})) is null && observations.Count == 1 && confirmedState.Dirty,
    "Aggregation waits for confirmed viewport geometry and retains edits after early evidence");
var confirmedMessage = Event("core.canvasViewState", "stateUpdated", 30, new {
    status = "changed", state = new { canvasOriginScreenPx = new { x = 40, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
    evidence = new { triggerTicks = 4, capturedTicks = 20 }
}) with { AppendId = 501 };
var confirmedCapture = confirmedState.Accept(confirmedMessage);
Check(confirmedCapture is { TriggerTicks: 4, TriggerKind: "stateUpdated", ViewPending: false }
    && observations[^1] is { TriggerTicks: 4, Changed: 1, IsBaseline: false, HasEditingInput: true }
    && observations[^1].Message.AppendId == 501, "Confirmed geometry emits one boundary with its native record pointer and trigger ticks");
confirmedState.Accept(ViewEvent(5, 40));
Check(observations[^1] is { TriggerTicks: 5, Changed: 0, IsBaseline: false }, "Unchanged geometry emits zero even while a capture is reserved");
confirmedState.Accept(ViewEvent(4, 80)); confirmedState.Accept(ViewEvent(6, 80, status: "unknown"));
Check(observations.Count == 3, "Stale and unconfirmed viewport results do not enter the dimension timeline");
confirmedState.Accept(ViewEvent(7, 100));
Check(observations[^1] is { Changed: 1 } && observations.Count == 4, "Every confirmed boundary is observed while older capture work is pending");
confirmedState.Complete(confirmedCapture!, false);
var emptyState = new WatchState(requireConfirmedViewChange: true);
ViewportObservation? emptyObservation = null; emptyState.ViewportObserved += value => emptyObservation = value;
emptyState.Reset(); emptyState.Accept(ViewEvent(1));
Check(emptyState.Accept(ViewEvent(2, 20)) is null && emptyObservation is { Changed: 1, IsBaseline: false, HasEditingInput: false },
    "A true viewport change without edits or a known layer still exposes an empty-package boundary without requesting a save");
emptyState.Accept(ViewEvent(3, 30, initial: true));
Check(emptyObservation is { Changed: 0, IsBaseline: true }, "Explicit initial geometry establishes a zero-valued baseline");
var pointerEvidence = new RecognitionEvidence(); pointerEvidence.Reset("test", 1000, 0);
pointerEvidence.Configuration(1, JsonSerializer.SerializeToElement(new { clipPath = "test.clip" }), 500);
pointerEvidence.Accept(confirmedMessage);
pointerEvidence.Accept(Pen(3) with { AppendId = 600, EventId = 900, RelatedEventIds = [899] });
var pointerWindow = pointerEvidence.Freeze(4).Snapshot(0, 4);
Check(pointerWindow.States.Any(state => state.Channel == "configuration" && state.AppendId == 500)
    && pointerWindow.States.Any(state => state.Channel == "core.canvasViewState" && state.AppendId == 501)
    && pointerWindow.Inputs is [{ AppendId: 600, EventId: 900 }] && pointerWindow.Inputs[0].RelatedEventIds!.SequenceEqual(new ulong[] { 899 }),
    "Frozen evidence preserves native append pointers for configuration, core states, and hardware inputs");
Check(RecognizerMonitor.ScreenArea(JsonSerializer.SerializeToElement(new { missing = true })) is null, "Incomplete canvas area is rejected without crashing");
Check(SaveCapture.ResolveLayerName(JsonSerializer.SerializeToElement(new { layers = new[] { new { id = 3, name = "图层 1" }, new { id = 9, name = "图层 2" } } }), "图层1") == "图层 1", "OCR whitespace resolved to unique stored layer name");
try
{
    SaveCapture.ResolveLayerName(JsonSerializer.SerializeToElement(new { layers = new[] { new { id = 3, name = "AB" }, new { id = 9, name = "A B" } } }), "AB");
    throw new Exception("Whitespace-colliding names were accepted");
}
catch (InvalidDataException) { checks++; }

var coreProperties = JsonSerializer.SerializeToElement(new {
    canvas = new { current_layer_id = 9 }, // Saved selection can differ from the live OCR selection.
    layers = new[] { new { id = 3, name = "图层 1", uuid = "aa-bb" }, new { id = 9, name = "图层 2", uuid = "cc-dd" } }
});
compatibility = Ready(); compatibility.Accept(Layer(3, "图层1"));
compatibility.Accept(Event("core.clipState", "stateUpdated", 4, new { status = "changed", state = coreProperties }));
compatibility.Accept(Pen(5));
var mapped = compatibility.Accept(ViewEvent(6, 20));
Check(mapped?.Layer is { Id: 3, Name: "图层 1", Uuid: "aa-bb" }, "Live current layer maps to layer property core ID rather than stale saved selection");
var renamed = JsonSerializer.SerializeToElement(new { layers = new[] { new { id = 20, name = "重命名", uuid = "AABB" }, new { id = 3, name = "图层 1", uuid = "other" } } });
Check(LayerMapping.FromFile(renamed, "图层1", mapped!.Layer).Id == 20, "Layer UUID preserves identity through rename and ID change");
try
{
    LayerMapping.FromFile(JsonSerializer.SerializeToElement(new { layers = new[] { new { id = 3, name = "图层 1", uuid = "replacement" } } }), "图层1", mapped.Layer);
    throw new Exception("Replaced same-name layer accepted");
}
catch (InvalidDataException) { checks++; }
try
{
    LayerMapping.FromCore([new(3, "图层1", "a"), new(9, "图层 1", "b")], "图层1");
    throw new Exception("Ambiguous property core names accepted");
}
catch (InvalidDataException) { checks++; }
Check(LayerMapping.FromFile(JsonSerializer.SerializeToElement(new { layers = new[] { new { id = 3, name = "Ink" } } }), "Ink", new(3, "Ink", null)).Id == 3, "Core ID and canonical name used when UUID unavailable");
try
{
    SaveCapture.ResolveLayerName(JsonSerializer.SerializeToElement(new { layers = new[] { new { id = 3, name = "Ink" } } }), "Missing");
    throw new Exception("Missing OCR layer was accepted");
}
catch (InvalidDataException) { checks++; }

var state = Ready();
Check(state.Accept(ViewEvent(3, 10)) is null, "No pen means no save");
state.Accept(Pen(4, kind: "hover"));
Check(!state.Dirty && state.Accept(ViewEvent(5, 20)) is null, "Hover excluded");
state.Accept(Pen(6, heldKeys: [32]));
Check(!state.Dirty, "Space navigation excluded");
state.Accept(Pen(7, snapshot: true));
Check(!state.Dirty, "Pen snapshot excluded");
state.Accept(Pen(8));
Check(state.InContact && state.Dirty, "Contact marked dirty");
state.Accept(Pen(9, kind: "penEnd"));
Check(!state.InContact, "Pen release observed");
Check(state.Accept(ViewEvent(10, 20)) is null, "Unchanged geometry excluded");
var capture = state.Accept(ViewEvent(11, 30));
Check(capture is { LayerName: "Ink" }, "Trigger freezes layer name");
Check(state.Accept(ViewEvent(12, 40)) is null, "Pending save coalesced");
state.Complete(capture!, false);
Check(state.Dirty, "Failure retains dirty operations");
capture = state.Accept(ViewEvent(13, 50));
Check(capture is not null, "Next switch retries");
state.Accept(Pen(14, 2));
state.Complete(capture!, true);
Check(state.Dirty, "New operation during save retained");
capture = state.Accept(ViewEvent(15, 60));
state.Complete(capture!, true);
Check(!state.Dirty, "Successful save clears only reserved operations");
state = Ready(); state.Accept(Pen(20));
Check(state.Accept(ViewEvent(10, 10)) is null, "Future input cannot dirty earlier capture");
Check(state.Accept(ViewEvent(9, 20)) is null, "Out of order view ignored");
Check(state.Accept(ViewEvent(21, 20, status: "unknown")) is null, "Unknown view excluded");
state.Accept(Layer(22, null, "unknown"));
Check(state.Accept(ViewEvent(23, 20)) is null, "Unknown layer cannot use old name");
state = Ready(); state.Accept(Pen(3)); capture = state.Accept(ViewEvent(4, 10));
state.Accept(Layer(4, "Other"));
Check(!state.Valid(capture!), "Late same-capture layer invalidates earlier name");
state.Reset(); state.Complete(capture!, true);
Check(!state.Valid(capture!), "Old session result invalidated");

string artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts")); Directory.CreateDirectory(artifacts);
DiffChecks.Run(Check, artifacts);
string source = Path.Combine(artifacts, "source.tmp"), target = Path.Combine(artifacts, "snapshot.tmp");
await File.WriteAllTextAsync(source, "before"); var signature = SaveCapture.Stat(source);
using (var timeout = new CancellationTokenSource(250))
{
    try { await SaveCapture.StableSnapshotAsync(source, target, signature, timeout.Token); throw new Exception("Unchanged file was accepted"); }
    catch (OperationCanceledException) { checks++; }
}
await File.WriteAllTextAsync(source, "after-save");
using (var timeout = new CancellationTokenSource(3000)) await SaveCapture.StableSnapshotAsync(source, target, signature, timeout.Token);
Check(await File.ReadAllTextAsync(target) == "after-save", "Only changed stable file copied");

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
string bridge = Path.Combine(root, "bridge/target/release/clip-layer-bridge.exe");
string fixture = Path.GetFullPath(Path.Combine(root, "../../replayer/status change/csp_blank_template.clip"));
if (File.Exists(fixture) && File.Exists(bridge))
{
    using var timeout = new CancellationTokenSource(10000);
    string inspect = await Bridge.RunAsync(bridge, ["inspect", fixture], timeout.Token);
    using var doc = JsonDocument.Parse(inspect);
    Check(doc.RootElement.GetProperty("layers").GetArrayLength() == 3, "Real CLIP document parsed");
    string png = Path.Combine(artifacts, "blank-layer.png"); SaveCapture.TryDelete(png);
    await Bridge.RunAsync(bridge, ["export", fixture, "レイヤー 1", png], timeout.Token);
    using var image = new System.Drawing.Bitmap(png);
    Check(image.Width == 1600 && image.Height == 1200 && image.GetPixel(0, 0).A == 0, "Blank raster preserves full canvas and transparency");
    string byId = Path.Combine(artifacts, "blank-layer-by-id.png"); SaveCapture.TryDelete(byId);
    string idMetadata = await Bridge.RunAsync(bridge, ["export-id", fixture, "3", byId, "1600", "1200"], timeout.Token);
    using var idDoc = JsonDocument.Parse(idMetadata);
    Check(J.Tick(idDoc.RootElement, "layerId", 0) == 3 && J.Get(idDoc.RootElement, "fullCanvas").ValueKind == JsonValueKind.True, "Layer ID export preserves identity and full canvas");
    try { await Bridge.RunAsync(bridge, ["export", fixture, "missing", Path.Combine(artifacts, "bad.png")], timeout.Token); throw new Exception("Missing layer accepted"); }
    catch (InvalidDataException) { checks++; }
}
else throw new Exception("CLIP bridge integration fixture missing");

// Exercise the actual current-user named pipe, initial snapshots, and recorded clipPath binding.
var writer = new MemolineWriter(Path.Combine(artifacts, "ipc"), new { });
var hub = new RecorderRealtimeHub(writer);
var server = new RecorderRealtimePipeServer(hub);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var monitor = new RecognizerMonitor();
var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var captured = new TaskCompletionSource<CaptureRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
monitor.Status += text => { Console.WriteLine("IPC: " + text); if (text.StartsWith("持续监听中")) configured.TrySetResult(); };
monitor.Capture += request => captured.TrySetResult(request);
writer.AppendState("initializationConfiguration", writer.NowTicks, [], new { clipPath = fixture, canvasPixelSize = new[] { 1600, 1200 } });
long layerTicks = writer.NowTicks;
long viewTicks = writer.NowTicks;
writer.AppendState("coreStateUpdated", viewTicks, [], JsonSerializer.SerializeToElement(new {
    module = "canvasViewState", status = "changed", initial = true,
    state = new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 }, evidence = new { capturedTicks = viewTicks }
}));
writer.AppendState("coreStateUpdated", writer.NowTicks, [], new {
    module = "currentLayerState", status = "changed", state = "Ink", evidence = new { capturedTicks = layerTicks }
});
writer.AppendState("coreStateUpdated", writer.NowTicks, [], new { module = "brushState", status = "changed", state = new { name = "IPC brush", properties = new[] { new { key = "brush_size", value = 10, unit = "px" } } } });
writer.AppendState("coreStateUpdated", writer.NowTicks, [], new { module = "colorState", status = "changed", state = new { hex = "#123456" } });
writer.AppendState("coreStateUpdated", writer.NowTicks, [], new { module = "clipState", status = "changed", state = new { layers = new[] { new { id = 3, name = "Ink", uuid = "ink" } } } });
writer.AppendState("driverConfiguration", writer.NowTicks, [], new { snapshotId = "IPC-driver" });
await writer.FlushAsync();
var running = monitor.RunAsync(server.ManifestPath, fixture, stop.Token);
try
{
    await configured.Task.WaitAsync(stop.Token);
    await Task.Delay(150, stop.Token);
    var began = writer.AppendHardware("penBegin", new { heldKeys = Array.Empty<int>() }, new("tablet", "test", "test", "deviceId"));
    writer.AppendHardware("penEnd", new { heldKeys = Array.Empty<int>() }, new("tablet", "test", "test", "deviceId"), began.EventId);
    long switchedTicks = writer.NowTicks;
    writer.AppendState("coreEvidenceCaptured", switchedTicks, [], new {
        module = "canvasViewState", initial = false,
        evidence = new { triggerTicks = switchedTicks, capturedTicks = writer.NowTicks, analysisPending = true, encodingPending = true }
    }, "immediate");
    var request = await captured.Task.WaitAsync(stop.Token);
    Check(request.LayerName == "Ink" && monitor.Current(request), "Real IPC drives trigger-time layer capture");
    Check(request.TriggerTicks == switchedTicks && request.Context is { SessionId: var frozenSession, PixelSize: var frozenSize }
        && frozenSession == writer.SessionId && frozenSize == new Size(1600, 1200), "Real IPC freezes triggerTicks, canvas size and session before asynchronous capture work");
    Check(request.TriggerKind == "evidenceCaptured" && request.ViewPending,
        "Real named pipe delivers the save trigger before viewport analysis is published");
    writer.AppendState("coreStateUpdated", writer.NowTicks, [], new {
        module = "canvasViewState", status = "changed", initial = false,
        state = new { canvasOriginScreenPx = new { x = 20, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
        evidence = new { triggerTicks = switchedTicks, capturedTicks = writer.NowTicks, completedTicks = writer.NowTicks }
    });
    Check(monitor.ClipPath == fixture, "Recording clipPath bound to selected file");
    Check(monitor.PixelSize == new Size(1600, 1200), "Full canvas dimensions from Recognizer configuration");
    Check(monitor.ControlPipe == RecorderControlClient.PipeNameFor(Environment.ProcessId), "Save control bound to realtime process");
    var bundled = monitor.GetEvidence(0, request.Ticks);
    Check(bundled.SessionId == writer.SessionId && bundled.Frequency > 0, "Evidence clock and session come from the actual realtime hello");
    Check(new[] { "configuration", "core.brushState", "core.colorState", "core.currentLayerState", "core.canvasViewState", "core.clipState", "tablet.driverConfiguration" }
        .All(channel => bundled.States.Any(s => s.Channel == channel)), "Actual pipe subscription collects all core states and driver configuration");
    Check(bundled.Inputs.Length == 2 && J.Text(bundled.Inputs[0].DeviceSource, "deviceType") == "tablet", "Raw contact and device source survive realtime packaging");
    Check(bundled.Inputs.All(input => input.AppendId > 0) && bundled.States.All(state => state.AppendId > 0),
        "Real native append pointers survive both configuration reads and realtime state/input packaging");
    writer.AppendState("coreStateUpdated", writer.NowTicks, [], new { module = "colorState", status = "changed",
        state = new { hex = "#ABCDEF" }, evidence = new { triggerTicks = switchedTicks - 1, capturedTicks = writer.NowTicks } });
    var lateDeadline = System.Diagnostics.Stopwatch.StartNew();
    while (!monitor.GetEvidence(0, switchedTicks).States.Any(s => J.Text(J.Get(s.Data, "state"), "hex") == "#ABCDEF"))
    {
        await Task.Delay(10, stop.Token);
        if (lateDeadline.ElapsedMilliseconds > 2000) throw new TimeoutException("Late core event was not delivered");
    }
    Check(!request.Context!.Evidence.Snapshot(0, switchedTicks).States.Any(s => J.Text(J.Get(s.Data, "state"), "hex") == "#ABCDEF"),
        "Late core completion cannot mutate the evidence already frozen by the actual realtime trigger");
}
finally
{
    await stop.CancelAsync();
    try { await running; } catch (OperationCanceledException) { }
    await writer.DisposeAsync(); await hub.DisposeAsync(); await server.DisposeAsync();
}

// The integrated host observes every confirmed boundary while independently pausing capture requests.
var integratedWriter = new MemolineWriter(Path.Combine(artifacts, "integrated-ipc"), new { });
var integratedHub = new RecorderRealtimeHub(integratedWriter);
var integratedServer = new RecorderRealtimePipeServer(integratedHub);
using var integratedStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var integratedMonitor = new RecognizerMonitor(requireConfirmedViewChange: true) { CaptureRequestsPaused = true };
var integratedConfigured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var integratedCaptured = new TaskCompletionSource<CaptureRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
var integratedEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var integratedObservations = new System.Collections.Concurrent.ConcurrentQueue<ViewportObservation>();
var integratedMessages = new System.Collections.Concurrent.ConcurrentQueue<RecorderRealtimeEvent>();
integratedMonitor.Status += text => { if (text.StartsWith("持续监听中")) integratedConfigured.TrySetResult(); };
integratedMonitor.ViewportObserved += integratedObservations.Enqueue;
integratedMonitor.MessageReceived += message => { integratedMessages.Enqueue(message); if (message.Kind == "sessionEnded") integratedEnded.TrySetResult(); };
integratedMonitor.Capture += request => integratedCaptured.TrySetResult(request);
integratedWriter.AppendState("initializationConfiguration", integratedWriter.NowTicks, [], new { clipPath = fixture, canvasPixelSize = new[] { 1600, 1200 } });
integratedWriter.AppendState("coreStateUpdated", integratedWriter.NowTicks, [], new {
    module = "currentLayerState", status = "changed", state = "Ink"
});
integratedWriter.AppendState("coreStateUpdated", integratedWriter.NowTicks, [], new {
    module = "canvasViewState", status = "changed", initial = true,
    state = new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 }
});
await integratedWriter.FlushAsync();
var integratedRunning = integratedMonitor.RunAsync(integratedServer.ManifestPath, fixture, integratedStop.Token);
try
{
    await integratedConfigured.Task.WaitAsync(integratedStop.Token);
    async Task AwaitObservation(long ticks)
    {
        while (!integratedObservations.Any(value => value.TriggerTicks == ticks)) await Task.Delay(10, integratedStop.Token);
    }
    while (!integratedObservations.Any(value => value.IsBaseline)) await Task.Delay(10, integratedStop.Token);
    Check(integratedMessages.Any(message => message.Kind == "hello"), "The integrated raw-message hook receives the actual pipe hello");
    var began = integratedWriter.AppendHardware("penBegin", new { heldKeys = Array.Empty<int>() }, new("tablet", "test", "test", "deviceId"));
    integratedWriter.AppendHardware("penEnd", new { heldKeys = Array.Empty<int>() }, new("tablet", "test", "test", "deviceId"), began.EventId);
    long pausedTicks = integratedWriter.NowTicks;
    integratedWriter.AppendState("coreEvidenceCaptured", pausedTicks, [], new {
        module = "canvasViewState", initial = false, evidence = new { triggerTicks = pausedTicks, analysisPending = true }
    }, "immediate");
    integratedWriter.AppendState("coreStateUpdated", integratedWriter.NowTicks, [], new {
        module = "canvasViewState", status = "changed", initial = false,
        state = new { canvasOriginScreenPx = new { x = 20, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
        evidence = new { triggerTicks = pausedTicks }
    });
    await AwaitObservation(pausedTicks);
    Check(!integratedCaptured.Task.IsCompleted && integratedObservations.Single(value => value.TriggerTicks == pausedTicks)
        is { Changed: 1, HasEditingInput: true }, "Pausing captures keeps confirmed observations and raw input processing active");
    integratedMonitor.CaptureRequestsPaused = false;
    long resumedTicks = integratedWriter.NowTicks;
    integratedWriter.AppendState("coreStateUpdated", integratedWriter.NowTicks, [], new {
        module = "canvasViewState", status = "changed", initial = false,
        state = new { canvasOriginScreenPx = new { x = 40, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
        evidence = new { triggerTicks = resumedTicks }
    });
    var resumed = await integratedCaptured.Task.WaitAsync(integratedStop.Token);
    Check(resumed.TriggerTicks == resumedTicks && resumed.TriggerKind == "stateUpdated" && !resumed.ViewPending
        && resumed.Operations.Contains((long)began.EventId), "A paused reservation is released without losing edits, so the next confirmed change can capture");
    integratedMonitor.Complete(resumed, true);
    long emptyTicks = integratedWriter.NowTicks;
    integratedWriter.AppendState("coreStateUpdated", integratedWriter.NowTicks, [], new {
        module = "canvasViewState", status = "changed", initial = false,
        state = new { canvasOriginScreenPx = new { x = 60, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
        evidence = new { triggerTicks = emptyTicks }
    });
    await AwaitObservation(emptyTicks);
    Check(integratedObservations.Single(value => value.TriggerTicks == emptyTicks) is { Changed: 1, HasEditingInput: false },
        "The real pipe exposes a later empty aggregation boundary without dispatching any save input");
    Check(integratedMessages.Any(message => message.Kind == "evidenceCaptured") && integratedMessages.Any(message => message.Kind == "penBegin"),
        "The raw-message hook includes evidence and hardware messages even in confirmed-only mode");
    await integratedWriter.DisposeAsync(); await integratedHub.DisposeAsync();
    await integratedEnded.Task.WaitAsync(integratedStop.Token);
    Check(integratedMessages.Any(message => message.Kind == "sessionEnded"), "The raw-message hook receives the actual session-ended notification");
}
finally
{
    await integratedStop.CancelAsync();
    try { await integratedRunning; } catch (OperationCanceledException) { }
    await integratedWriter.DisposeAsync(); await integratedHub.DisposeAsync(); await integratedServer.DisposeAsync();
}
// Verify the wire contract without dispatching any real keyboard input.
async Task ControlReply(bool dispatched, bool matchingId, bool success)
{
    string pipeName = "watcher-control-check-" + Guid.NewGuid().ToString("N");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var receiving = Task.Run(async () =>
    {
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var responseWriter = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var doc = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        var request = doc.RootElement;
        Check(request.GetProperty("command").GetString() == "requestClipSave", "Immediate save command used");
        Check(request.GetProperty("expectedClipPath").GetString() == fixture, "Expected document passed to Recognizer");
        Check(request.GetProperty("triggerTicks").GetInt64() == 42, "Save control request carries the original API triggerTicks");
        await responseWriter.WriteLineAsync(JsonSerializer.Serialize(new {
            success, requestId = matchingId ? request.GetProperty("requestId").GetString() : "other",
            saveInputDispatched = dispatched, saveInputDispatchedTicks = 123L, saveCompletionConfirmed = false,
            error = success ? null : "wrong foreground document"
        }));
    });
    bool accepted = false;
    try { await new RecorderControlClient(pipeName).SaveClipAsync(fixture, timeout.Token, 42); accepted = true; }
    catch (IOException) { }
    catch (InvalidDataException) { }
    await receiving;
    Check(accepted == (dispatched && matchingId && success), "Only matching successful save dispatch accepted");
}
await ControlReply(true, true, true);
await ControlReply(false, true, true);
await ControlReply(true, false, true);
await ControlReply(false, true, false);
Console.WriteLine($"Passed {checks} checks; no CSP input was injected.");
