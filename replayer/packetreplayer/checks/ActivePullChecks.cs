using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using PacketReplay;

internal static partial class PacketChecks
{
    private sealed class PullControl(MemolineWriter writer) : IPacketStateControl
    {
        internal int Calls;
        internal bool Old, Unknown;
        public Task<IReadOnlyDictionary<string, JsonElement>> RequestAsync(string sessionId, string requestId, string[] modules, string? saveId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Check(sessionId == writer.SessionId && requestId.StartsWith("packet-state-"), "Active requests bind their unique identity to the connected session.");
            long ticks = Old ? 0 : writer.NowTicks;
            IReadOnlyDictionary<string, JsonElement> data = modules.ToDictionary(m => m, m => m switch {
                "brushState" => Unknown ? Json(new { module = m, status = "unknown", state = (object?)null, evidence = new { capturedTicks = ticks } }) : Brush(21, ticks: ticks),
                "subtoolState" => SubtoolData([], ticks, "pull-" + Calls),
                "currentLayerState" => Layer("图层 2", ticks),
                "canvasViewState" => Json(new { module = m, status = "changed", state = new { ocrScalePercent = 75, ocrRotationDegrees = 0,
                    canvasOriginScreenPx = new { x = 10, y = 20 } }, evidence = new { capturedTicks = ticks }, rawResult = new { success = true } }),
                "initializationConfiguration" => Json(new { clipPath = "D:\\test\\current.clip", canvasPixelSize = new[] { 500, 600 } }),
                "shortcuts" => Hierarchy().Raw,
                "colorState" => Json(new { module = m, status = "unchanged", state = new { kind = "rgb", rgb = new[] { 1, 2, 3 } }, evidence = new { capturedTicks = ticks } }),
                "clipState" => Json(new { module = m, status = "unchanged", state = new { canvas = new { current_layer_id = 19 }, layers = new[] { new { id = 19, name = "图层 2" } } },
                    evidence = new { observedTicks = ticks, saveId } }),
                _ => throw new Exception("Unexpected module") });
            return Task.FromResult(data);
        }
    }

    private static async Task ActivePullAsync()
    {
        Check(LivePacketFeed.BrushFailureReason(Json(new { status = "unknown" })).Contains("unknown"), "Unknown brush responses always have a readable failure reason.");
        Check(BrushSnapshot.SameName("較硬", "较硬") && BrushSnapshot.SameName("柔軟", "柔软"), "Configured traditional eraser names match their simplified OCR spelling.");
        Check(LivePacketFeed.BrushFailureReason(Json(new { rawResult = new { brush = new { name_resolution = new { reason = "子工具面板没有唯一高亮的子工具行" } } } })).Contains("唯一高亮"), "Preserve the producer's child-name fallback failure reason.");
        Check(LivePacketFeed.BrushFailureReason(Json(new { rawResult = new { brush = new { properties = new[] { new { key = "antialiasing", label = "消除锯齿", status = "partial" } } } } })).Contains("消除锯齿"), "Expose the partial property that blocked confirmation.");
        string directory = Path.Combine(Path.GetTempPath(), "packet-pull-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = new MemolineWriter(directory, new { test = "active state queries without automatic pushes" });
            await using var hub = new RecorderRealtimeHub(writer);
            await using var server = new RecorderRealtimePipeServer(hub);
            var control = new PullControl(writer);
            await using var feed = new LivePacketFeed(server.ManifestPath, control);
            _ = feed.View.RunAsync();
            await feed.View.WaitForConnectionAsync(default);
            Check(feed.Brush is null && feed.View.Current is null && feed.Layer is null, "A connection with no state push can bootstrap active queries.");
            await feed.RequestAsync(["initializationConfiguration", "shortcuts", "canvasViewState", "brushState", "currentLayerState", "colorState", "subtoolState", "clipState"], default, "our-save");
            Check(control.Calls == 1 && feed.Brush?.Numbers[0].Value == 21 && feed.Layer == "图层 2", "Brush and layer state come directly from the requested response, without interface update events.");
            Check(feed.RequestedView is { ScalePercent: 75, PixelWidth: 500, PixelHeight: 600 } && J.Text(feed.LayersEvidence, "saveId") == "our-save", "Active queries return canvas calibration and the requested save identity.");
            Check(feed.Subtools is { Entries.Count: 0 } && feed.Shortcuts?.Nodes.Count > 0 && feed.Color.ValueKind == JsonValueKind.Object,
                "The same request handles empty fresh subtool OCR, saved config, and color state.");
            for (int next = 0; next < 4; next++) await PacketReplayRunner.InitializeStatesAsync(feed, false, true, default);
            Check(control.Calls == 1, "Continuing next-stroke runs reuses the initialized live table without repeating startup requests.");
            long revision = feed.Revision;
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Brush(99, ticks: 0), "delayed");
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], Json(new { module = "colorState", status = "changed",
                state = new { kind = "rgb", rgb = new[] { 9, 2, 3 } }, evidence = new { capturedTicks = writer.NowTicks } }), "delayed");
            await feed.WaitAsync(revision, "colorState", () => J.Get(feed.Color, "rgb")[0].GetInt32() == 9, default);
            Check(feed.Brush?.Numbers[0].Value == 21, "A delayed automatic result cannot overwrite the newer explicitly requested observation.");
            var views = new PacketViewFeed(feed);
            await views.WaitForViewAsync(feed.Revision, _ => true, TimeSpan.FromSeconds(1), default);
            Check(control.Calls == 2 && views.Current?.ScalePercent == 75, "Shared view restoration feedback actively requests a new observation.");
            control.Old = true;
            await RejectAsync<IOException>(() => feed.RequestAsync(["brushState"], default));
            control.Old = false; control.Unknown = true;
            await RejectAsync<InvalidOperationException>(() => feed.RequestAsync(["brushState"], default));
            Check(feed.Brush is null, "An unknown requested result cannot silently reuse the previous confirmed brush.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await RejectAsync<OperationCanceledException>(() => feed.RequestAsync(["brushState"], canceled.Token));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task StateControlProtocolAsync()
    {
        var control = new PacketRecorderControl(Random.Shared.Next(2000000, 3000000));
        await using var server = new NamedPipeServerStream(control.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = Task.Run(async () => {
            await server.WaitForConnectionAsync(stop.Token);
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var json = JsonDocument.Parse((await reader.ReadLineAsync(stop.Token))!);
            Check(J.Text(json.RootElement, "command") == "requestStates" && J.Text(json.RootElement, "sessionId") == "session"
                && J.Text(json.RootElement, "requestId") == "our-request" && J.Text(json.RootElement, "saveId") == "save"
                && J.Get(json.RootElement, "modules").GetArrayLength() == 1, "The consumer sends an explicit state request on the documented control pipe.");
            await writer.WriteLineAsync(Json(new { success = true, requestId = "our-request", sessionId = "session", requestedTicks = 100,
                results = new { brushState = Brush(33, ticks: 150) } }).GetRawText().AsMemory(), stop.Token);
        });
        var response = await control.RequestAsync("session", "our-request", ["brushState"], "save", stop.Token);
        await serving;
        Check(BrushSnapshot.Parse(response["brushState"])?.Numbers[0].Value == 33, "The consumer receives fresh state in the control response without subscribing for completion.");
        var valid = Json(new { success = true, requestId = "r", sessionId = "s", results = new { brushState = Brush() } });
        Reject<IOException>(() => PacketRecorderControl.ParseStates(valid, "other", "r", ["brushState"]));
        Reject<IOException>(() => PacketRecorderControl.ParseStates(valid, "s", "other", ["brushState"]));
        Reject<IOException>(() => PacketRecorderControl.ParseStates(valid, "s", "r", ["brushState", "colorState"]));
    }
}
