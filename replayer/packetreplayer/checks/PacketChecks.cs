using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using MemolineDemo;
using PacketReplay;
using StrokeReplay;

internal static partial class PacketChecks
{
    private static int _assertions;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); _assertions++; }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { _assertions++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { _assertions++; return; } throw new Exception("Expected " + typeof(T).Name); }

    internal static async Task RunAsync(string? sample)
    {
        NumericRegions();
        CausalHistory();
        PacketBoundaries();
        await BrushAndLayerRestoreAsync();
        await RecordedStateTableAsync();
        await SubtoolRestoreAsync();
        await SavePreparationAsync();
        await SaveControlProtocolAsync();
        await ActivePullAsync();
        await StateControlProtocolAsync();
        await LiveProtocolAsync();
        if (sample is not null) RealSample(sample);
        Console.WriteLine($"PacketReplay checks passed: {_assertions} assertions. No CSP input was injected.");
    }

    internal static void RenderUi(string sample, string destination)
    {
        WindowsViewInput.PrepareDpiAwareness();
        Application.EnableVisualStyles();
        using var window = new PacketReplayWindow(null, previewOnly: true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000), Opacity = 0 };
        window.SetDocument(PacketArchiveReader.Read(sample));
        window.Show();
        window.PerformLayout();
        using var bitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        bitmap.Save(destination);
        Console.WriteLine("Rendered UI: " + Path.GetFullPath(destination));
    }

    private static JsonElement Brush(double size = 12.34567, string name = "G笔", long ticks = 0, bool locations = true) => Json(new
    {
        module = "brushState", status = "changed", state = new { name, properties = new object[] {
            new { key = "brush_size", type = "number", value = size, status = "ok", enabled = "unknown" },
            new { key = "antialiasing", type = "highlight_index", value = 2, status = "ok", enabled = "unknown" },
            new { key = "disabled", type = "number", value = 10, status = "disabled", enabled = "disabled" },
            new { key = "speed", type = "checkbox", value = "checked", status = "ok", enabled = "unknown" } } },
        evidence = new { capturedTicks = ticks, panelRoi = new[] { -300, -100, 200, 200 }, causalAmbiguous = false },
        valueRegions = locations ? new object[] {
            new { propertyKey = "brush_size", category = "number", value = size, status = "ok", bbox = new[] { 100, 20, 30, 14 }, coordinateSpace = "panel" },
            new { propertyKey = "antialiasing", category = "text", value = 2, status = "ok", screenBbox = new[] { -170, -50, 20, 12 } } } : [],
        rawResult = new { raw_ocr = new[] { new { text = "笔刷尺寸", bbox = new[] { 10, 20, 60, 14 } } } }
    });
    private static JsonElement Layer(string name, long ticks = 0) => Json(new { module = "currentLayerState", status = "changed", state = name, evidence = new { capturedTicks = ticks } });
    private static JsonElement Structure(params string[] names) => Json(new { module = "clipState", status = "changed", state = new {
        layers = names.Select((n, i) => new { id = i + 1, name = n, parent_id = (int?)null }).ToArray() } });

    private static void NumericRegions()
    {
        var brush = BrushSnapshot.Parse(Brush(), live: true)!;
        Check(brush.Numbers.Count == 1 && brush.IgnoredProperties == 3, "Only true numeric editors may be changed; numeric option indexes must be excluded.");
        Check(brush.Numbers[0].Input == new Rectangle(-200, -80, 30, 14), "Use the same capture's panel origin, including negative monitors.");
        Check(BrushSnapshot.Parse(Brush(locations: false), live: true)!.Numbers[0].Input is null, "Legacy OCR labels must never become click positions.");
        Check(BrushSnapshot.SameName(" G筆 ", "G笔"), "CSP OCR and configuration names must match common equivalent glyphs/spacing.");
        Check(brush.Numbers[0].Value == 12.34567, "Do not truncate recorded numeric precision.");
        var invalid = Json(new { module = "brushState", status = "unknown", state = new { name = "G笔" }, lastConfirmedState = Brush() });
        Check(BrushSnapshot.Parse(invalid, live: true) is null, "Unknown observations must not invent locations from lastConfirmedState.");
        var screen = Json(new { module = "brushState", status = "unchanged", state = J.Get(Brush(), "state"),
            valueRegions = new[] { new { propertyKey = "brush_size", category = "number", value = 12.34567, status = "ok", screenBbox = new[] { -50, 20, 40, 12 } } } });
        Check(BrushSnapshot.Parse(screen, true)!.Numbers[0].Input == new Rectangle(-50, 20, 40, 12), "screenBbox is absolute and unchanged observations update positions.");
    }

    private static JsonElement Frame(string kind, ulong append, ulong eventId, object data) => Json(new { kind, appendId = append, eventId, data });
    private static JsonElement Reserved(string id, ulong append, ulong anchor, ulong order) => Frame("statePackageReserved", append, 0, new { packageId = id, afterEventId = anchor, reservationOrder = order });
    private static JsonElement Result(string id, ulong append, ulong anchor, params JsonElement[] updates) => Frame("statePackageResult", append, 0, new { packageId = id, afterEventId = anchor, result = new { updates } });
    private static void CausalHistory()
    {
        var frames = new[] { Reserved("initial", 1, 0, 1), Reserved("later", 2, 5, 2), Result("later", 3, 5, Brush(40)),
            Result("initial", 4, 0, Brush(60), Layer("图层 1")), Reserved("same-first", 5, 5, 3), Reserved("same-last", 6, 5, 4),
            Result("same-last", 7, 5, Brush(20)), Result("same-first", 8, 5, Brush(30)) };
        var history = StateHistory.Resolve(frames);
        Check(StateHistory.At(history, 5).Brush!.Numbers[0].Value == 60, "A state anchored after a penBegin cannot affect that begin.");
        Check(StateHistory.At(history, 6).Brush!.Numbers[0].Value == 20, "Use reservation order, even when results are appended in reverse.");
        Check(StateHistory.At(history, 6).Layer == "图层 1", "Unrelated modules must retain their own last causally valid state.");
        var bad = history.Append(new HistorySlot(6, 9, "brushState", Json(new { status = "unknown", module = "brushState" }))).ToArray();
        Check(StateHistory.At(bad, 7).Brush is null, "Historical unknown brush state must invalidate preceding confirmation.");
        Reject<InvalidDataException>(() => StateHistory.Resolve([Result("missing", 1, 3, Brush())]));
    }

    private static PacketDocument Fixture(IReadOnlyList<MemolineReplayStroke> strokes, params PacketInfo[] packets) => new("fixture",
        new("test", 1000, "m", "a", "f", packets.Length, 0, 0), [], new("fixture", "test", 1000, strokes), packets,
        [new(0, 0, "brushState", Brush()), new(0, 0, "currentLayerState", Layer("图层 1"))]);
    private static void PacketBoundaries()
    {
        var samples = new[] { new MemolineReplaySample(10, 100, 100, .2, 0, 0, true, 1, 1),
            new MemolineReplaySample(20, 110, 110, .3, 0, 0, true, 2, 2),
            new MemolineReplaySample(30, 120, 120, 0, 0, 0, false, 3, 3) };
        var stroke = new MemolineReplayStroke(1, 10, 30, new(100, 0, 0, 0), samples);
        var p1 = new PacketInfo(1, "one", 0, 20, "empty", new HashSet<ulong> { 1, 2 }, default);
        var p2 = new PacketInfo(2, "two", 20, 40, "captured", new HashSet<ulong> { 3 }, default);
        var doc = Fixture([stroke], p1, p2);
        var first = PacketArchiveReader.Plan(doc, p1);
        Check(first.Strokes.Count == 1 && first.Strokes[0].Clipped, "An image-empty packet still contains replayable mechanical input.");
        Check(first.Strokes[0].Stroke.EndTicks == 20 && !first.Strokes[0].Stroke.Samples[^1].InContact, "A crossing stroke must end at the selected packet boundary.");
        Check(PacketArchiveReader.Plan(doc, p2).Strokes.Count == 0, "An UP-only next packet must never redraw the preceding stroke.");
        var mid = new PacketInfo(3, "middle", 15, 25, "empty", new HashSet<ulong> { 2 }, default);
        var middle = PacketArchiveReader.Plan(doc, mid).Strokes[0];
        Check(middle.Stroke.StartTicks == 20 && middle.Stroke.Samples[0].AppendId == 2 && middle.Stroke.Samples.Count == 2, "Replay only selected non-context samples; never prepend another packet's begin.");
        PacketReplayRunner.Validate(first);
        var capabilities = new FakeFeed();
        PacketReplayRunner.ValidateCapabilities(first, capabilities);
        Check(true, "Matching current brush and unique layer need no invented shortcuts.");
        capabilities.Layers = J.Get(Structure("图层 1", "图层1"), "state");
        Reject<InvalidOperationException>(() => PacketReplayRunner.ValidateCapabilities(first, capabilities));
        Check(first.Strokes[0].State.Brush!.Numbers.Count == 1, "Old files use semantic numeric types without needing old OCR click coordinates.");
    }

    private sealed class FakeFeed : IPacketStateFeed
    {
        public BrushSnapshot? Brush { get; set; } = BrushSnapshot.Parse(PacketChecks.Brush(10), true);
        public string? Layer { get; set; } = "图层 1";
        public JsonElement Layers { get; set; } = J.Get(Structure("纸张", "图层 1", "图层 2"), "state");
        public JsonElement LayersEvidence { get; set; }
        public SubtoolSnapshot? Subtools { get; set; }
        public JsonElement Color { get; set; }
        public ShortcutConfiguration? Shortcuts { get; set; } = new([new("tool_1", "G笔", null, "G"), new("tool_2", "钢笔", null, "P"),
            new("menu_1", "上图层", "layerselectupperlayer", "Alt + UP"), new("menu_2", "下图层", "layerselectlowerlayer", "Alt + DOWN")]);
        public long Revision { get; set; }
        internal readonly List<string[]> Requests = [];
        internal Action<string[]>? OnRequest;
        internal int Marks;
        internal bool Connected = true;
        public void EnsureConnected() { if (!Connected) throw new IOException("disconnected"); }
        public void MarkInput(string module) { EnsureConnected(); Marks++; }
        public Task WaitAsync(long after, string module, Func<bool> matches, CancellationToken token)
        { token.ThrowIfCancellationRequested(); EnsureConnected(); if (Revision <= after || !matches()) throw new TimeoutException(); return Task.CompletedTask; }
        public Task RequestAsync(string[] modules, CancellationToken token, string? saveId = null)
        { token.ThrowIfCancellationRequested(); EnsureConnected(); Requests.Add(modules); OnRequest?.Invoke(modules); Revision++; return Task.CompletedTask; }
    }
    private sealed class FakeInput(FakeFeed feed) : IPacketInput
    {
        internal readonly List<string> Events = [];
        public void EnsureTarget() { }
        public Task ShortcutAsync(ShortcutBinding binding, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add(binding.Shortcut);
            if (binding.Id.StartsWith("tool_")) feed.Brush = BrushSnapshot.Parse(Brush(10, binding.Name), true);
            if (binding.Command == "layerselectupperlayer") feed.Layer = feed.Layer == "纸张" ? "图层 1" : "图层 2";
            if (binding.Command == "layerselectlowerlayer") feed.Layer = feed.Layer == "图层 2" ? "图层 1" : "纸张";
            feed.Revision++; return Task.CompletedTask;
        }
        public Task NumberAsync(Rectangle area, double value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add($"number:{value}");
            Check(area == new Rectangle(-200, -80, 30, 14), "Number restoration must use the current exact value rectangle.");
            feed.Brush = BrushSnapshot.Parse(Brush(value, feed.Brush!.Name), true); feed.Revision++; return Task.CompletedTask;
        }
        public Task ClickAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public Task ScrollAsync(Rectangle panel, int delta, CancellationToken token) => throw new NotSupportedException();
    }
    private static async Task BrushAndLayerRestoreAsync()
    {
        var feed = new FakeFeed(); var input = new FakeInput(feed); var restorer = new PacketStateRestorer(feed, input, _ => { });
        await restorer.RestoreBrushAsync(BrushSnapshot.Parse(Brush(12.34567, "钢笔"))!, default);
        Check(input.Events.SequenceEqual(new[] { "P", "number:12.34567" }) && feed.Marks == 0 && feed.Requests.Count == 2, "Switch tools, then write numeric properties, requesting post-input confirmation only.");
        int requests = feed.Requests.Count;
        await restorer.RestoreBrushAsync(feed.Brush!, default);
        Check(input.Events.Count == 2 && feed.Requests.Count == requests, "Already matching brush and values need neither input nor another state request.");
        await restorer.RestoreLayerAsync("图层2", default);
        Check(feed.Layer == "图层 2" && input.Events[^1] == "Alt + UP", "Layer selection must use configured commands and feedback.");
        await restorer.RestoreLayerAsync("纸张", default);
        Check(feed.Layer == "纸张" && input.Events.Contains("Alt + DOWN"), "At a directional boundary, the reverse configured route can reach the target.");
        feed.Layers = J.Get(Structure("图层 1", "图层1"), "state");
        await RejectAsync<InvalidOperationException>(() => restorer.RestoreLayerAsync("图层1", default));
        feed = new FakeFeed(); input = new FakeInput(feed); restorer = new(feed, input, _ => { });
        feed.Brush = BrushSnapshot.Parse(Brush(10, locations: false), true);
        await RejectAsync<InvalidOperationException>(() => restorer.RestoreBrushAsync(BrushSnapshot.Parse(Brush(20))!, default));
        Check(input.Events.Count == 0, "Missing current numeric coordinates must stop before sending input.");
        await RejectAsync<InvalidOperationException>(() => restorer.RestoreBrushAsync(new("未配置笔刷", [], 0), default));
        feed.Connected = false;
        await RejectAsync<IOException>(() => restorer.RestoreBrushAsync(new("G笔", [], 0), default));
        using var stop = new CancellationTokenSource(); stop.Cancel(); feed.Connected = true;
        await RejectAsync<OperationCanceledException>(() => restorer.RestoreBrushAsync(BrushSnapshot.Parse(Brush(20))!, stop.Token));
        Check(ShortcutConfiguration.Keys("Ctrl + Shift + F12").SequenceEqual(new ushort[] { 17, 16, 123 }), "Parse configured modifiers and function keys.");
        Check(ShortcutConfiguration.Keys("Ctrl + NUM+").SequenceEqual(new ushort[] { 17, 107 }), "Numpad plus must not be confused with the separator.");
        Reject<ArgumentException>(() => ShortcutConfiguration.Keys("Key_999"));
    }

    private static async Task LiveProtocolAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packetreplay-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = new MemolineWriter(directory, new { test = "packet live consumer" });
            await using var hub = new RecorderRealtimeHub(writer);
            await using var server = new RecorderRealtimePipeServer(hub);
            writer.AppendState("initializationConfiguration", writer.NowTicks, [], new { clipPath = Path.Combine(directory, "current.clip"), canvasPixelSize = new[] { 500, 500 } });
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Brush(ticks: writer.NowTicks), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], SubtoolData(["G筆"], writer.NowTicks, "initial-panel"), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Layer("图层 1", writer.NowTicks), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Structure("纸张", "图层 1", "图层 2"), "delayed");
            writer.AppendState("shortcutConfiguration", writer.NowTicks, [], new { bindings = new[] { new { id = "tool_1", action_name = "G笔", shortcut = "G" } } });
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Json(new { module = "canvasViewState", status = "changed", state = new {
                ocrScalePercent = 100, ocrRotationDegrees = 0, canvasOriginScreenPx = new { x = 0, y = 0 } }, rawResult = new {
                    success = true, canvasWindowRoiScreenPx = new { left = 0, top = 0, right = 500, bottom = 500 } }, evidence = new { capturedTicks = writer.NowTicks } }), "delayed");
            await using var feed = new LivePacketFeed(server.ManifestPath);
            var running = feed.View.RunAsync();
            await feed.View.WaitForViewAsync(-1, _ => true, TimeSpan.FromSeconds(5));
            await feed.WaitAsync(-1, "brushState", () => feed.Brush is not null && feed.Layer is not null && feed.Shortcuts is not null && feed.Layers.ValueKind == JsonValueKind.Object, default);
            await feed.WaitAsync(-1, "subtoolState", () => feed.Subtools is not null, default);
            await feed.View.WaitForViewAsync(-1, _ => feed.View.InitializationConfiguration.ValueKind == JsonValueKind.Object, TimeSpan.FromSeconds(5));
            Check(feed.RecorderProcessId == Environment.ProcessId && J.Text(feed.View.InitializationConfiguration, "clipPath") == Path.Combine(directory, "current.clip"), "Save control binds to the current realtime manifest and calibration, not the historical replay file.");
            Check(feed.Subtools!.CaptureId == "initial-panel", "The new subtools projection retains captured panel metadata in connection snapshots.");
            Check(feed.Brush!.Numbers[0].Input == new Rectangle(-200, -80, 30, 14), "Snapshot consumers receive usable numeric screen positions and independent layer/shortcut streams.");
            long revision = feed.Revision;
            long oldTicks = writer.NowTicks;
            feed.MarkInput("brushState");
            var waiting = feed.WaitAsync(revision, "brushState", () => feed.Brush is not null, default);
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Brush(90, ticks: oldTicks), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Layer("图层 2", writer.NowTicks), "delayed");
            await Eventually(() => feed.Layer == "图层 2");
            Check(!waiting.IsCompleted && feed.Brush.Numbers[0].Value == 12.34567, "Queued old brush OCR and unrelated layer updates must not confirm a brush input.");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Brush(40, ticks: writer.NowTicks), "delayed");
            await waiting;
            Check(feed.Brush.Numbers[0].Value == 40, "A fresh same-module observation can confirm input delivered before the wait began.");
            var unknown = Json(new { module = "brushState", status = "unknown", state = (object?)null, evidence = new { capturedTicks = writer.NowTicks } });
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], unknown, "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Layer("图层 1", writer.NowTicks), "delayed");
            await Eventually(() => feed.Layer == "图层 1");
            Check(feed.Brush.Numbers[0].Value == 40, "Unknown intermediate observations preserve the last confirmed table.");
            long panelRevision = feed.Revision, oldPanelTicks = writer.NowTicks;
            feed.MarkInput("subtoolState");
            var panelWaiting = feed.WaitAsync(panelRevision, "subtoolState", () => feed.Subtools is not null, default);
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], SubtoolData(["圆笔"], oldPanelTicks, "queued-panel"), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], SubtoolData(["圆笔"], writer.NowTicks, "initial-panel"), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Brush(50, ticks: writer.NowTicks), "delayed");
            await Eventually(() => feed.Brush?.Numbers[0].Value == 50);
            Check(!panelWaiting.IsCompleted && feed.Subtools.CaptureId == "initial-panel", "Old panel screenshots and fresh brush messages cannot confirm a subtools wheel operation.");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], SubtoolData([], writer.NowTicks, "empty-panel"), "delayed");
            await panelWaiting;
            Check(feed.Subtools.Entries.Count == 0 && feed.Subtools.CaptureId == "empty-panel", "A newly captured empty/unknown OCR frame clears old clickable rows while allowing scroll search to continue.");
            long layerRevision = feed.Revision;
            feed.MarkInput("clipState");
            var layerWaiting = feed.WaitAsync(layerRevision, "clipState", () => J.Text(feed.LayersEvidence, "saveId") == "our-save", default);
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Json(new { module = "clipState", status = "changed", state = J.Get(Structure("图层 1"), "state"),
                evidence = new { saveId = "other-save", observedTicks = writer.NowTicks } }), "delayed");
            await Eventually(() => J.Text(feed.LayersEvidence, "saveId") == "other-save");
            Check(!layerWaiting.IsCompleted, "A fresh structure from another save cannot start reproduction.");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Json(new { module = "clipState", status = "unchanged", state = J.Get(Structure("图层 1"), "state"),
                evidence = new { saveId = "our-save", observedTicks = writer.NowTicks } }), "delayed");
            await layerWaiting;
            Check(J.Text(feed.LayersEvidence, "saveId") == "our-save", "The structure wait uses save identity plus observedTicks, independently of current-layer OCR.");
            var disconnected = feed.WaitAsync(feed.Revision, "brushState", () => false, default);
            await writer.DisposeAsync();
            await hub.DisposeAsync();
            await RejectAsync<IOException>(() => disconnected);
            Check(feed.Brush is null && feed.Layer is null && feed.Layers.ValueKind == JsonValueKind.Undefined && feed.Subtools is null && feed.RecorderProcessId is null, "Session end clears brushes, layers, subtool locations, and save control identity and stops confirmation waits.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static async Task Eventually(Func<bool> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, stop.Token); }

    private static void RealSample(string path)
    {
        var doc = PacketArchiveReader.Read(path);
        Check(doc.Verification.PacketCount == 6 && doc.Packets.Count == 6, "The provided compact bundle has six verified packets.");
        Check(doc.Packets.Zip(doc.Packets.Skip(1)).All(p => p.First.FromTicks <= p.Second.FromTicks), "Packet numbering must use time order, never JSON append order.");
        foreach (var packet in doc.Packets)
        {
            var plan = PacketArchiveReader.Plan(doc, packet);
            foreach (var drawing in plan.Strokes)
            {
                Check(drawing.Stroke.Samples.Where(s => s.AppendId > 0).All(s => packet.EventAppends.Contains(s.AppendId)), "All replay samples must resolve to this packet's non-context native pointers.");
                Check(drawing.Stroke.EndTicks <= packet.ToTicks && !drawing.Stroke.Samples[^1].InContact, "Every selected segment ends within the package and releases contact.");
                Check(drawing.State.Brush is not null && drawing.State.Layer is not null, "The provided sample's replayable drawings have causally confirmed brush and layer state.");
                Check(drawing.State.Brush!.Numbers.All(n => n.Key != "antialiasing"), "Weak/medium/strong remains excluded for the actual user's file.");
            }
            PacketReplayRunner.Validate(plan);
        }
        Check(PacketArchiveReader.Plan(doc, doc.Packets[5]).Strokes.Count == 1, "The empty-image final packet must still expose its drawing.");
        Console.WriteLine(PacketReplay.Program.Describe(doc));
    }
}
