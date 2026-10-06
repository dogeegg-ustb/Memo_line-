using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PacketReplay;

internal static partial class PacketChecks
{
    internal static async Task CheckPublishedConfigAsync(string executable)
    {
        var config = await ShortcutConfiguration.QueryAsync(executable, null, default);
        var route = config.RouteForBrush("G筆");
        Check(config.Nodes.Count == 264 && J.Array(J.Get(config.Raw, "brushPackages")).Count() == 37, "Consume the published Memoline inventory and brush packages intact.");
        Check(route.Binding.Shortcut == "P" && route.Binding.Id == "tool_22" && route.SubtoolId == "tool_24"
            && route.GroupId == "tool_23", "The real saved config resolves G-pen through P, the pen class, and its actual installed group.");
        Check(!config.Bindings.Any(b => b.Id == route.SubtoolId), "The real G-pen needs no direct subtool shortcut.");
        Console.WriteLine($"Published config verified: {config.Bindings.Count} bindings, {config.Nodes.Count} tool nodes, 37 installed groups; G筆 → {route.Binding.Name} ({route.Binding.Shortcut}), {route.SubtoolId}/{route.GroupId}.");
    }
    internal static async Task CheckPublishedSubtoolsAsync(string executable, string payload)
    {
        var config = await ShortcutConfiguration.QueryAsync(executable, null, default);
        using var json = JsonDocument.Parse(File.ReadAllText(payload));
        var panel = SubtoolSnapshot.Parse(json.RootElement) ?? throw new InvalidDataException("Published OCR payload is missing capture metadata.");
        Check(panel.Entries.Count == 7 && panel.CaptureId == "published-consumer-check", "Consume the actual published OCR model's seven visible brush entries and projected screenshot identity.");
        foreach (var entry in panel.Entries)
        {
            var route = config.RouteForBrush(entry.Name);
            Check(route.Binding.Id == "tool_22" && route.GroupId == "tool_23", "Actual OCR names resolve to their real configured parent category and group.");
            Check(panel.Find(route, entry.Name) is { } box && panel.Panel.Contains(box), "Actual projected brush positions are uniquely selectable within the same-frame panel ROI.");
        }
        Console.WriteLine($"Published OCR consumer verified: {panel.Entries.Count} names, tool/group identities and absolute click rectangles.");
    }
    private static object Tool(string id, string name, string kind, string toolId = "tool_pen", string? groupId = "group_pen") => new {
        id, name, kind, toolId, groupId, hidden = false, pathIds = new[] { toolId, groupId ?? toolId, id } };
    private static ShortcutConfiguration Hierarchy(bool duplicate = false)
    {
        var pen = Tool("tool_pen", "沾水筆", "tool", groupId: null);
        var group = Tool("group_pen", "沾水筆", "group");
        var target = Tool("brush_g", "G筆", "subtool");
        var round = Tool("brush_round", "圓筆", "subtool");
        var markerGroup = Tool("group_marker", "麥克筆", "group", groupId: "group_marker");
        var marker = Tool("brush_marker", "麥克筆", "subtool", groupId: "group_marker");
        var pencil = Tool("brush_pencil", "鉛筆", "subtool", "tool_pencil", "group_pencil");
        var subtools = new[] { target, round, marker };
        return ShortcutConfiguration.Parse(Json(new { toolCatalog = new { nodes = new object[] { pen, group, target, round, markerGroup, marker, pencil }
            .Concat(duplicate ? [Tool("duplicate", "G笔", "subtool")] : []).ToArray() }, brushPackages = new[] { new { id = "group_pen", subtools } },
            bindings = new object[] { new { id = "tool_pen", action_name = "沾水筆", shortcut = "P", tool = pen, subtools },
                new { id = "tool_pencil", action_name = "鉛筆", shortcut = "P", tool = Tool("tool_pencil", "鉛筆", "tool", "tool_pencil", null), subtools = new[] { pencil } } } }));
    }
    private static JsonElement SubtoolData(string[] names, long ticks = 0, string captureId = "capture", string tool = "tool_pen",
        string group = "group_pen", bool locations = true, bool includeGroup = false) => Json(new {
        module = "subtoolState", status = names.Length == 0 ? "unknown" : "changed", state = new { entries = names },
        evidence = new { capturedTicks = ticks, captureId, panelRoi = new[] { -300, -100, 200, 240 } },
        ocrEntries = names.Select((name, i) => new { name, kind = "subtool", coordinateSpace = "panel",
            bbox = locations ? new[] { 40, 60 + i * 24, 90, 16 } : (int[]?)null,
            matches = new[] { new { id = name switch { "G笔" or "G筆" => "brush_g", "铅笔" => "brush_pencil", "麦克笔" => "brush_marker", _ => "brush_round" }, toolId = tool, groupId = group } } }).ToArray(),
        groupEntries = includeGroup ? new[] { new { name = "沾水筆", kind = "group", coordinateSpace = "panel", bbox = new[] { 20, 10, 70, 16 },
            matches = new[] { new { id = "group_pen", toolId = "tool_pen", groupId = "group_pen" } } } } : [],
    });

    private sealed class SubtoolInput(FakeFeed feed, string[][] pages, int startingPage = 0, bool cycle = false,
        bool groupClick = false, bool acceptClick = true) : IPacketInput
    {
        internal readonly List<string> Events = [];
        private int _page = startingPage, _shortcuts, _captures;
        private void Refresh(string tool = "tool_pen", string group = "group_pen", bool groups = false)
        {
            feed.Subtools = SubtoolSnapshot.Parse(SubtoolData(pages[_page], ++_captures, "capture-" + _captures, tool, group, includeGroup: groups));
            feed.Revision++;
        }
        public void EnsureTarget() { }
        public Task ShortcutAsync(ShortcutBinding binding, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add("shortcut:" + binding.Shortcut);
            bool pencil = cycle && ++_shortcuts == 1;
            feed.Brush = BrushSnapshot.Parse(Brush(10, pencil ? "铅笔" : groupClick ? "麦克笔" : "圆笔"), true);
            Refresh(pencil ? "tool_pencil" : "tool_pen", groupClick ? "group_marker" : "group_pen", groupClick);
            return Task.CompletedTask;
        }
        public Task ClickAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (area.Y == -90) { Events.Add("group"); feed.Brush = BrushSnapshot.Parse(Brush(10, "圆笔"), true); Refresh(); }
            else
            {
                Events.Add("click");
                Check(area == new Rectangle(-260, -40, 90, 16), "Brush click uses the target OCR value's same-frame screen rectangle.");
                if (acceptClick) feed.Brush = BrushSnapshot.Parse(Brush(10, "G筆"), true);
                Refresh();
            }
            return Task.CompletedTask;
        }
        public Task ScrollAsync(Rectangle panel, int delta, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(panel == new Rectangle(-300, -100, 200, 240), "Wheel input must move into the current Memoline subtools ROI, including negative coordinates.");
            Events.Add("scroll:" + delta);
            _page = Math.Clamp(_page + (delta < 0 ? 1 : -1), 0, pages.Length - 1); Refresh(); return Task.CompletedTask;
        }
        public Task NumberAsync(Rectangle area, double value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add("number");
            Check(BrushSnapshot.SameName(feed.Brush!.Name, "G笔"), "Numeric changes follow the confirmed child brush selection.");
            feed.Brush = BrushSnapshot.Parse(Brush(value), true); feed.Revision++; return Task.CompletedTask;
        }
    }
    private static async Task SubtoolRestoreAsync()
    {
        var config = Hierarchy(); var route = config.RouteForBrush(" G笔 ");
        Check(route.Binding.Name == "沾水筆" && route.SubtoolId == "brush_g" && route.GroupId == "group_pen", "An unbound G-pen resolves through the configured parent tool and group hierarchy.");
        Check(config.ShortcutCycleCount(route) == 2, "Shared parent shortcuts have a bounded category cycle.");
        Check(config.Raw.GetProperty("brushPackages").GetArrayLength() == 1, "Preserve the complete producer config, including package and source metadata.");
        Reject<InvalidOperationException>(() => Hierarchy(true).RouteForBrush("G笔"));
        var panel = SubtoolSnapshot.Parse(SubtoolData(["G笔"]))!;
        Check(panel.Find(route, "G筆") == new Rectangle(-260, -40, 90, 16), "Child selection consumes projected or same-frame panel-local OCR positions.");
        Check(SubtoolSnapshot.Parse(SubtoolData(["G笔"], locations: false))!.Find(route, "G笔") is null, "Missing OCR positions must never become estimated clicks.");
        Check(SubtoolSnapshot.Parse(SubtoolData([])) is { Entries.Count: 0 }, "Fresh empty/unknown OCR retains only the panel ROI for further scrolling.");
        Reject<InvalidOperationException>(() => SubtoolSnapshot.Parse(SubtoolData(["G笔", "G笔"]))!.Find(route, "G笔"));
        Check(BrushSnapshot.SameName("擬真G筆", "拟真G笔"), "Use the same common UI glyph normalization as Memoline's tool catalog.");

        var feed = new FakeFeed { Brush = BrushSnapshot.Parse(Brush(10, "铅笔"), true), Shortcuts = config };
        var input = new SubtoolInput(feed, [["G笔"]], cycle: true);
        await new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(20))!, default);
        Check(input.Events.SequenceEqual(new[] { "shortcut:P", "shortcut:P", "click", "number" }), "Cycle the parent categories, choose the OCR child, confirm it, then change numeric properties.");

        feed = new FakeFeed { Brush = BrushSnapshot.Parse(Brush(10, "铅笔"), true), Shortcuts = config };
        input = new(feed, [["圆笔"], [], ["G笔"]]);
        await new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(10))!, default);
        Check(input.Events.SequenceEqual(new[] { "shortcut:P", "scroll:-120", "scroll:-120", "click" }), "Scroll over an unrecognized panel page and wait for new OCR until the target becomes visible.");

        feed = new FakeFeed { Brush = BrushSnapshot.Parse(Brush(10, "铅笔"), true), Shortcuts = config };
        input = new(feed, [["G笔"], ["圆笔"]], startingPage: 1);
        await new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(10))!, default);
        Check(input.Events.Contains("scroll:120") && input.Events[^1] == "click", "At the lower boundary reverse direction and search above the initial viewport.");

        feed = new FakeFeed { Brush = BrushSnapshot.Parse(Brush(10, "铅笔"), true), Shortcuts = config };
        input = new(feed, [["G笔"]], groupClick: true);
        await new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(10))!, default);
        Check(input.Events.SequenceEqual(new[] { "shortcut:P", "group", "click" }), "Select a matching subtool group tab before clicking the target child.");

        feed = new FakeFeed { Brush = BrushSnapshot.Parse(Brush(10, "铅笔"), true), Shortcuts = config };
        input = new(feed, [["圆笔"]]);
        await RejectAsync<InvalidOperationException>(() => new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(10))!, default));
        Check(input.Events.Count == 5 && !input.Events.Contains("click"), "End-of-list observations bound unsuccessful scrolling without speculative selection.");
        input = new(feed, [["G笔"]], acceptClick: false);
        await RejectAsync<InvalidOperationException>(() => new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(20))!, default));
        Check(!input.Events.Contains("number"), "An unconfirmed child selection blocks numeric edits and drawing.");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await RejectAsync<OperationCanceledException>(() => new PacketStateRestorer(feed, input, _ => { }).RestoreBrushAsync(BrushSnapshot.Parse(Brush(20))!, stopped.Token));
    }

    private sealed class FakeSaveControl(FakeFeed feed, bool rightId = true, bool hasCurrent = true) : IClipSaveControl
    {
        internal int Calls;
        public Task<ClipSaveDispatch> SaveAsync(string path, string id, long ticks, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            feed.Layers = Json(new { canvas = new { current_layer_id = hasCurrent ? 19 : 999 }, layers = new[] { new { id = 19, name = "图层 1" } } });
            feed.LayersEvidence = Json(new { saveId = rightId ? id : "other-save", observedTicks = ticks + 10 });
            feed.Revision++;
            return Task.FromResult(new ClipSaveDispatch(id, ticks + 1, false));
        }
    }
    private static async Task SavePreparationAsync()
    {
        var feed = new FakeFeed(); var control = new FakeSaveControl(feed); var messages = new List<string>();
        await PacketSessionPreparation.PrepareAsync(feed, control, "D:\\test\\current.clip", 100, messages.Add, default);
        Check(control.Calls == 1 && J.Text(feed.LayersEvidence, "saveId")!.StartsWith("packet-replay-"), "Opening reproduction requests a save and obtains a correlated structure snapshot.");
        Check(messages[^1].Contains("未提供 CSP 落盘完成确认"), "A dispatched save and stable parse must not falsely claim CSP completion.");
        await RejectAsync<IOException>(() => PacketSessionPreparation.PrepareAsync(feed, new FakeSaveControl(feed, rightId: false), "D:\\test\\current.clip", 100, _ => { }, default));
        await RejectAsync<InvalidOperationException>(() => PacketSessionPreparation.PrepareAsync(feed, new FakeSaveControl(feed, hasCurrent: false), "D:\\test\\current.clip", 100, _ => { }, default));
        feed.Layer = "图层 2";
        await RejectAsync<InvalidOperationException>(() => PacketSessionPreparation.PrepareAsync(feed, control, "D:\\test\\current.clip", 100, _ => { }, default));
        int before = control.Calls;
        await RejectAsync<InvalidOperationException>(() => PacketSessionPreparation.PrepareAsync(feed, control, "relative.clip", 100, _ => { }, default));
        Check(control.Calls == before, "An uncalibrated document cannot dispatch a save to an inferred path.");
        var result = Json(new { success = true, requestId = "id", saveInputDispatched = true, saveInputDispatchedTicks = 123, saveCompletionConfirmed = false });
        Check(PacketRecorderControl.Parse(result, "id", 100).DispatchedTicks == 123, "Use the control API's dispatch acknowledgment and original session clock.");
        Reject<IOException>(() => PacketRecorderControl.Parse(result, "other", 100));
        Reject<IOException>(() => PacketRecorderControl.Parse(result, "id", 150));
        Reject<IOException>(() => PacketRecorderControl.Parse(Json(new { success = false, error = "busy" }), "id", 100));
    }
    private static async Task SaveControlProtocolAsync()
    {
        int id = Random.Shared.Next(1000000, 2000000);
        var control = new PacketRecorderControl(id);
        await using var server = new NamedPipeServerStream(control.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(stop.Token);
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var json = JsonDocument.Parse((await reader.ReadLineAsync(stop.Token))!);
            var request = json.RootElement;
            Check(J.Text(request, "command") == "requestClipSave" && J.Text(request, "expectedClipPath") == "D:\\test\\当前.clip"
                && J.Text(request, "requestId") == "test-save" && J.I64(request, "triggerTicks") == 120
                && J.Get(request, "activateCsp").ValueKind == JsonValueKind.False, "Control writes one UTF-8 JSON line using the documented current-document save contract.");
            await writer.WriteLineAsync(Json(new { success = true, requestId = "test-save", saveInputDispatched = true,
                saveInputDispatchedTicks = 130, saveCompletionConfirmed = false }).GetRawText().AsMemory(), stop.Token);
        });
        var result = await control.SaveAsync("D:\\test\\当前.clip", "test-save", 120, stop.Token);
        await serving;
        Check(result.RequestId == "test-save" && result.DispatchedTicks == 130 && !result.CompletionConfirmed, "Read and validate a real local control pipe response without sending CSP input.");
    }
}
