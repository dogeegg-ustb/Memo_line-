using System.Diagnostics;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

internal static class InterfaceChecks
{
    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool value, string description)
        { checks++; if (!value) throw new InvalidDataException(description); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var writer = new MemolineWriter(Path.Combine(root, "interfaces"), new { }, new() { SuccessfulStatesOnly = true });
        await using var hub = new RecorderRealtimeHub(writer);
        await using var server = new RecorderRealtimePipeServer(hub);
        await using var shortcuts = RecorderRealtimeClient.FollowShortcutsAsync(server.PipeName, timeout.Token).GetAsyncEnumerator();
        await using var layers = RecorderRealtimeClient.FollowLayersAsync(server.PipeName, timeout.Token).GetAsyncEnumerator();
        await using var stage = RecorderRealtimeClient.FollowLayerStageAsync(server.PipeName, timeout.Token).GetAsyncEnumerator();
        await using var subtools = RecorderRealtimeClient.FollowSubtoolsAsync(server.PipeName, timeout.Token).GetAsyncEnumerator();
        Check(await shortcuts.MoveNextAsync() && shortcuts.Current.Kind == "hello", "Shortcut pipe handshake failed.");
        Check(await layers.MoveNextAsync() && layers.Current.Kind == "hello", "Layer pipe handshake failed.");
        Check(await stage.MoveNextAsync() && stage.Current.Kind == "hello", "Current-layer pipe handshake failed.");
        Check(await subtools.MoveNextAsync() && subtools.Current.Kind == "hello", "Subtool pipe handshake failed.");
        using (var manifest = JsonDocument.Parse(File.ReadAllText(server.ManifestPath)))
        {
            var channels = manifest.RootElement.GetProperty("channels").EnumerateArray().Select(x => x.GetString()).ToArray();
            Check(channels.Contains("shortcuts") && channels.Contains("layers") && channels.Contains("layerstage")
                && channels.Contains("subtools") && channels.Contains("core.subtoolState"), "Manifest must advertise all interfaces.");
        }
        object Configuration(string key) => new { configRoot = "CSP 配置", bindings = new[] {
            new { id = "menu_1", shortcut = key, action_name = "重做", sourceFile = "default.khc" },
            new { id = "menu_2", shortcut = "Ctrl + Shift + Z", action_name = "重做", sourceFile = "default.khc" }
        }, warnings = Array.Empty<string>(), source = "savedCspConfiguration" };
        writer.AppendState("shortcutConfiguration", writer.NowTicks, [], Configuration("Ctrl + G"));
        Check(await shortcuts.MoveNextAsync() && shortcuts.Current.Channel == "shortcuts", "Shortcut configuration was not routed.");
        Check(shortcuts.Current.Kind == "configurationUpdated" && shortcuts.Current.Data.GetProperty("bindings").GetArrayLength() == 2,
            "Shortcut interface must retain multiple keys per function.");
        writer.AppendState("shortcutConfiguration", writer.NowTicks, [], Configuration("Ctrl + Y"));
        Check(await shortcuts.MoveNextAsync() && shortcuts.Current.Data.GetProperty("bindings")[0].GetProperty("shortcut").GetString() == "Ctrl + Y",
            "Shortcut configuration refresh was lost.");
        await using (var late = RecorderRealtimeClient.FollowShortcutsAsync(server.PipeName, timeout.Token).GetAsyncEnumerator())
        {
            Check(await late.MoveNextAsync() && late.Current.Kind == "hello", "Late shortcut handshake failed.");
            Check(await late.MoveNextAsync() && late.Current.IsSnapshot
                && late.Current.Data.GetProperty("bindings")[0].GetProperty("shortcut").GetString() == "Ctrl + Y",
                "Late shortcut subscribers must receive only the newest configuration.");
        }

        void Core(string status, object? state, string module = "clipState") => writer.AppendState("coreStateUpdated", writer.NowTicks, [],
            new { module, status, state, error = status == "error" ? "Parse failed" : null,
                evidence = new { saveId = "fixture", triggerTicks = writer.NowTicks }, rawResult = state });
        var table = new { canvas = new { width_raw = 100, current_layer_id = 2 }, layer_count = 2, layers = new[] {
            new { id = 1, parent_id = 0, depth = 0, name = "组", uuid = "folder", visible = true },
            new { id = 2, parent_id = 1, depth = 1, name = "绘图层", uuid = "layer", visible = false }
        } };
        await using var original = hub.SubscribeCore("clipState", false);
        await using var originalReader = original.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Core("changed", table);
        Check(await layers.MoveNextAsync() && layers.Current.Channel == "layers" && layers.Current.Kind == "layerStructureUpdated", "Parsed layer table was not routed.");
        var parsed = layers.Current;
        Check(parsed.Data.GetProperty("state").GetProperty("layers")[1].GetProperty("parent_id").GetInt32() == 1,
            "Layer hierarchy must be preserved.");
        Check(parsed.Data.GetProperty("state").GetProperty("canvas").GetProperty("current_layer_id").GetInt32() == 2,
            "The saved document's current layer identity must be preserved.");
        Check(await originalReader.MoveNextAsync() && originalReader.Current.AppendId == parsed.AppendId
            && originalReader.Current.Ticks == parsed.Ticks && originalReader.Current.SessionId == parsed.SessionId,
            "Dedicated layer interface must preserve the original record identity.");
        Core("error", null);
        Check(await layers.MoveNextAsync() && layers.Current.Data.GetProperty("status").GetString() == "error"
            && layers.Current.Data.GetProperty("state").ValueKind == JsonValueKind.Null
            && layers.Current.Data.GetProperty("lastConfirmedState").GetProperty("layer_count").GetInt32() == 2,
            "Failed parsing must retain the last successful table without marking it current.");
        Check(layers.Current.AppendId == 0, "Diagnostic layer failures must not invent native record pointers.");
        await using (var late = RecorderRealtimeClient.FollowLayersAsync(server.PipeName, timeout.Token).GetAsyncEnumerator())
        {
            Check(await late.MoveNextAsync() && late.Current.Kind == "hello", "Late layer handshake failed.");
            Check(await late.MoveNextAsync() && late.Current.IsSnapshot && late.Current.Data.GetProperty("status").GetString() == "changed",
                "Late layer subscribers need the last successful table.");
            Check(await late.MoveNextAsync() && late.Current.IsSnapshot && late.Current.Data.GetProperty("status").GetString() == "error",
                "Late layer subscribers need the most recent parsing status.");
        }
        Core("changed", new { layer_count = 0, layers = Array.Empty<object>() });
        Check(await layers.MoveNextAsync() && layers.Current.Data.GetProperty("lastConfirmedState").GetProperty("layers").GetArrayLength() == 0,
            "A successfully parsed empty layer table must replace the previous table.");
        Core("changed", "当前图层名称", "currentLayerState");
        Check(await stage.MoveNextAsync() && stage.Current.Channel == "layerstage" && stage.Current.Kind == "currentLayerUpdated"
            && stage.Current.Data.GetProperty("state").GetString() == "当前图层名称",
            "Current-layer observations must be available through layerstage.");
        Core("unknown", null, "currentLayerState");
        Check(await stage.MoveNextAsync() && stage.Current.Data.GetProperty("state").ValueKind == JsonValueKind.Null
            && stage.Current.Data.GetProperty("lastConfirmedState").GetString() == "当前图层名称",
            "Unknown current-layer observations must retain the last confirmed name.");

        var subtoolState = new { entries = new[] { new { name = "G筆", nodeIds = new[] { "tool_24" }, selectionState = "unknown" } } };
        foreach (int x in new[] { 200, 210 })
        {
            writer.AppendState("coreStateUpdated", writer.NowTicks, [], new { module = "subtoolState",
                status = x == 200 ? "changed" : "unchanged", state = subtoolState,
                evidence = new { captureId = "subtool-" + x, panelRoi = new[] { -300, 500, 300, 220 } },
                ocrEntries = new[] { new { name = "G筆", text = "G笔", screenBbox = new[] { -300+x, 540, 30, 18 },
                    bbox = new[] { x, 40, 30, 18 }, coordinateSpace = "panel", selectionState = "unknown" } }, rawResult = new { schemaVersion = 1 }
            });
            Check(await subtools.MoveNextAsync() && subtools.Current.Kind == "ocrUpdated", "Subtool OCR was not routed.");
            Check(subtools.Current.Data.GetProperty("ocrEntries")[0].GetProperty("screenBbox")[0].GetInt32() == -300+x,
                "Subtool screen positions must refresh even when names are unchanged.");
        }
        await using (var late = RecorderRealtimeClient.FollowSubtoolsAsync(server.PipeName, timeout.Token).GetAsyncEnumerator())
        {
            Check(await late.MoveNextAsync() && late.Current.Kind == "hello", "Late subtool handshake failed.");
            Check(await late.MoveNextAsync() && late.Current.IsSnapshot
                && late.Current.Data.GetProperty("evidence").GetProperty("captureId").GetString() == "subtool-210",
                "Subtool snapshots must retain the most recent parsed frame.");
        }

        // Exercise the desktop executable's subscription entry without CSP or input hooks.
        using var cli = new Process { StartInfo = new ProcessStartInfo("dotnet") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        cli.StartInfo.ArgumentList.Add(typeof(BundleArchive).Assembly.Location);
        foreach (string arg in new[] { "--subscribe", "shortcuts,layers,layerstage,subtools", "--endpoint", server.ManifestPath })
            cli.StartInfo.ArgumentList.Add(arg);
        cli.Start();
        try
        {
            string? hello = await cli.StandardOutput.ReadLineAsync(timeout.Token);
            using var helloJson = JsonDocument.Parse(hello ?? throw new IOException("Demo subscription exited without a handshake."));
            Check(helloJson.RootElement.GetProperty("kind").GetString() == "hello", "Demo CLI must output only JSONL.");
            await writer.DisposeAsync();
            await hub.DisposeAsync();
            string output = await cli.StandardOutput.ReadToEndAsync(timeout.Token);
            string error = await cli.StandardError.ReadToEndAsync(timeout.Token);
            await cli.WaitForExitAsync(timeout.Token);
            Check(cli.ExitCode == 0 && error.Length == 0, "Demo CLI subscription failed: " + error);
            var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Check(rows.Count(row => row.GetProperty("channel").GetString() == "layers") == 1
                && rows.Count(row => row.GetProperty("channel").GetString() == "layerstage") == 2
                && rows.Count(row => row.GetProperty("channel").GetString() == "shortcuts") == 1
                && rows.Count(row => row.GetProperty("channel").GetString() == "subtools") == 1
                && rows[^1].GetProperty("kind").GetString() == "sessionEnded", "Demo CLI replay or session termination failed.");
            Check(rows.Any(row => row.GetProperty("kind").GetString() == "layerStructureUpdated"
                    && row.GetProperty("channel").GetString() == "layers"
                    && row.GetProperty("data").GetProperty("state").GetProperty("layers").GetArrayLength() == 0)
                && rows.Any(row => row.GetProperty("kind").GetString() == "currentLayerUpdated"
                    && row.GetProperty("channel").GetString() == "layerstage"
                    && row.GetProperty("data").GetProperty("status").GetString() == "changed"
                    && row.GetProperty("data").GetProperty("state").GetString() == "当前图层名称"),
                "Current-layer updates must not overwrite layer table snapshots.");
            Check(rows.Where(row => row.GetProperty("channel").GetString() == "layers")
                    .All(row => row.GetProperty("data").GetProperty("module").GetString() == "clipState")
                && rows.Where(row => row.GetProperty("channel").GetString() == "layerstage")
                    .All(row => row.GetProperty("data").GetProperty("module").GetString() == "currentLayerState"),
                "The two layer interfaces must isolate their corresponding cores.");
        }
        finally { if (!cli.HasExited) { cli.Kill(entireProcessTree: true); await cli.WaitForExitAsync(); } }
        Check(await shortcuts.MoveNextAsync() && shortcuts.Current.Kind == "sessionEnded" && !await shortcuts.MoveNextAsync(),
            "Shortcut subscription did not close gracefully.");
        Check(await layers.MoveNextAsync() && layers.Current.Kind == "sessionEnded" && !await layers.MoveNextAsync(),
            "Layer subscription did not close gracefully.");
        Check(await stage.MoveNextAsync() && stage.Current.Kind == "sessionEnded" && !await stage.MoveNextAsync(),
            "Current-layer subscribers must not receive structure table updates.");
        Check(await subtools.MoveNextAsync() && subtools.Current.Kind == "sessionEnded" && !await subtools.MoveNextAsync(),
            "Subtool subscribers must not receive unrelated core updates.");
        return checks;
    }
}
