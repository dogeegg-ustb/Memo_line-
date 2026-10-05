using System.Diagnostics;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class RecorderRealtimeTests
{
    [Fact]
    public async Task EvidenceNotificationArrivesBeforeAnalysisAndDoesNotReplaceConfirmedSnapshot()
    {
        await using var s = new Session(successfulStatesOnly: true);
        await using var subscription = s.Hub.SubscribeCore("canvasViewState", false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        s.Core("changed", new { marker = "previous" }, "canvasViewState");
        Assert.True(await reader.MoveNextAsync()); Assert.Equal("stateUpdated", reader.Current.Kind);
        long trigger = s.Writer.NowTicks;
        s.Writer.AppendState("coreEvidenceCaptured", trigger, [], new { module = "canvasViewState", initial = false,
            evidence = new { triggerTicks = trigger, capturedTicks = s.Writer.NowTicks, analysisPending = true } }, "immediate");
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("evidenceCaptured", reader.Current.Kind);
        Assert.Equal(trigger, reader.Current.Data.GetProperty("evidence").GetProperty("triggerTicks").GetInt64());
        Assert.Equal(0ul, reader.Current.AppendId);
        await using var late = s.Hub.SubscribeCore("canvasViewState", true);
        await using var lateReader = late.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await lateReader.MoveNextAsync());
        Assert.True(lateReader.Current.IsSnapshot);
        Assert.Equal("stateUpdated", lateReader.Current.Kind);
        Assert.Equal("previous", lateReader.Current.Data.GetProperty("state").GetProperty("marker").GetString());
        s.Core("changed", new { marker = "recognized" }, "canvasViewState");
        Assert.True(await reader.MoveNextAsync()); Assert.Equal("stateUpdated", reader.Current.Kind);
        await s.EndAsync();
        Assert.DoesNotContain(MemolineReader.Read(s.Writer.FilePath), f => f.GetProperty("kind").GetString() == "coreEvidenceCaptured");
        Assert.Contains("coreEvidenceCaptured", File.ReadAllText(s.Writer.DiagnosticFilePath));
    }
    private sealed class Session : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "memoline-live-" + Guid.NewGuid().ToString("N"));
        public readonly MemolineWriter Writer;
        public readonly RecorderRealtimeHub Hub;
        private bool _ended;
        public Session(bool successfulStatesOnly = false) { Writer = new(DirectoryPath, new { }, new() { SuccessfulStatesOnly = successfulStatesOnly }); Hub = new(Writer); }
        public async Task EndAsync()
        {
            if (_ended) return;
            _ended = true;
            await Writer.DisposeAsync();
            await Hub.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await EndAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
        public void Core(string status, object? state, string module = "colorState") => Writer.AppendState(
            "coreStateUpdated", Writer.NowTicks, [], new { module, status, state, packageId = "p", initial = false,
                error = status == "error" ? "OCR failed" : null, evidence = new { capturedTicks = 0 }, rawResult = state }, "delayed");
    }

    private static async Task<List<RecorderRealtimeEvent>> CollectAsync(IAsyncEnumerable<RecorderRealtimeEvent> stream)
    {
        var items = new List<RecorderRealtimeEvent>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    [Fact]
    public async Task ChannelFilteringPreservesOrderAndRecordingReferences()
    {
        await using var s = new Session();
        await using var keyboard = s.Hub.SubscribeKeyboard(false);
        await using var all = s.Hub.Subscribe(["all"], false);
        var source = new HardwareDeviceSource("keyboard", "test", "keyboard1", "deviceId");
        var stamp = s.Writer.AppendHardware("keyInput", new { key = "S", modifiers = new[] { "Ctrl" } }, source);
        s.Writer.AppendHardware("mouseDown", new { button = "left", x = 1, y = 2 }, new("mouse", "test", null, "classOnly"));
        s.Writer.AppendState("keyboardStateChanged", s.Writer.NowTicks, [], new { action = "keyUp", heldKeys = Array.Empty<int>() });
        // Screenshot evidence remains available on disk without entering the live stream.
        s.Writer.AppendState("screenshot", s.Writer.NowTicks, [], new { pngBase64 = "large-evidence" });
        await s.EndAsync();
        var keyEvents = await CollectAsync(keyboard.ReadAllAsync());
        var allEvents = await CollectAsync(all.ReadAllAsync());
        Assert.Equal(new[] { "keyInput", "keyUp", "sessionEnded" }, keyEvents.Select(e => e.Kind));
        Assert.Equal(new[] { "keyboard", "mouse", "keyboard", "system" }, allEvents.Select(e => e.Channel));
        Assert.Equal(new long[] { 1, 2, 3 }, allEvents.Take(3).Select(e => e.Sequence));
        var disk = MemolineReader.Read(s.Writer.FilePath).Single(e => e.TryGetProperty("kind", out var kind) && kind.GetString() == "keyInput");
        Assert.Equal(disk.GetProperty("appendId").GetUInt64(), keyEvents[0].AppendId);
        Assert.Equal(stamp.EventId, keyEvents[0].EventId);
        Assert.Equal(source, keyEvents[0].DeviceSource);
        Assert.True(keyEvents[0].PublishedTicks >= keyEvents[0].AppendedTicks);
    }

    [Fact]
    public async Task DiagnosticCoreUpdatesRemainLiveWithoutPersistentAppendIds()
    {
        await using var s = new Session(successfulStatesOnly: true);
        await using var subscription = s.Hub.SubscribeCore("colorState", false);
        s.Core("changed", new { kind = "color", hex = "#010203" });
        s.Core("error", null);
        s.Core("changed", new { kind = "color", hex = "#040506" });
        await s.EndAsync();
        var events = (await CollectAsync(subscription.ReadAllAsync())).Where(e => e.Channel != "system").ToArray();
        Assert.Equal(new[] { "changed", "error", "changed" }, events.Select(e => e.Data.GetProperty("status").GetString()));
        Assert.Equal(0ul, events[1].AppendId);
        Assert.Equal("#010203", events[1].Data.GetProperty("lastConfirmedState").GetProperty("hex").GetString());
        Assert.Equal(2, MemolineReader.Read(s.Writer.FilePath).Count(f => f.GetProperty("kind").GetString() == "coreStateUpdated"));
        Assert.Contains("error", File.ReadAllText(s.Writer.DiagnosticFilePath));
    }

    [Fact]
    public async Task CoreUpdatesKeepConfirmedStateAcrossUnknownAmbiguousAndError()
    {
        await using var s = new Session();
        await using var subscription = s.Hub.SubscribeCore("colorState", false);
        s.Core("changed", new { kind = "color", rgb = new[] { 1, 2, 3 }, hex = "#010203" });
        s.Core("unchanged", new { kind = "color", rgb = new[] { 1, 2, 3 }, hex = "#010203" });
        s.Core("unknown", null);
        s.Core("ambiguous", new { kind = "color", rgb = new[] { 9, 9, 9 }, hex = "#090909" });
        s.Core("error", null);
        s.Core("changed", new { kind = "color", rgb = new[] { 4, 5, 6 }, hex = "#040506" });
        await s.EndAsync();
        var events = (await CollectAsync(subscription.ReadAllAsync())).Where(e => e.Channel != "system").ToArray();
        Assert.Equal(6, events.Length);
        Assert.Equal("/", events[0].Data.GetProperty("changedFields")[0].GetString());
        foreach (var update in events.Skip(1).Take(4))
        {
            Assert.Empty(update.Data.GetProperty("changedFields").EnumerateArray());
            Assert.Equal("#010203", update.Data.GetProperty("lastConfirmedState").GetProperty("hex").GetString());
        }
        Assert.Equal(new[] { "/hex", "/rgb" }, events[5].Data.GetProperty("changedFields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("#040506", events[5].Data.GetProperty("lastConfirmedState").GetProperty("hex").GetString());
    }

    [Fact]
    public async Task LateSubscriberReceivesSnapshotIncludingTabletConfiguration()
    {
        await using var s = new Session();
        await using var barrier = s.Hub.SubscribeCore("colorState", false);
        s.Writer.AppendState("tabletDeviceChanged", s.Writer.NowTicks, [], new { deviceId = "dev", name = "Tablet" });
        s.Writer.AppendState("tabletDeviceChanged", s.Writer.NowTicks, [], new { deviceId = "dev2", name = "Tablet2" });
        s.Writer.AppendState("driverConfiguration", s.Writer.NowTicks, [], new { driverSnapshotId = "config1" });
        s.Writer.AppendState("tabletStateChanged", s.Writer.NowTicks, [], new { action = "hover", sample = new { tabletX = 123 } });
        s.Core("changed", new { hex = "#010203" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var first = barrier.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync()); // Projection of all earlier records has completed.
        await using var late = s.Hub.Subscribe(["tablet", "cores"]);
        await s.EndAsync();
        var events = await CollectAsync(late.ReadAllAsync());
        Assert.Equal(new[] { "tabletDeviceChanged", "tabletDeviceChanged", "driverConfiguration", "hover", "stateUpdated", "sessionEnded" }, events.Select(e => e.Kind));
        Assert.All(events.Take(5), e => Assert.True(e.IsSnapshot));
        Assert.False(events[^1].IsSnapshot);
    }

    [Fact]
    public async Task SlowSubscriberGetsExplicitGapWithoutLosingDiskRecords()
    {
        await using var s = new Session();
        await using var slow = s.Hub.Subscribe(["keyboard"], false, capacity: 8);
        for (int i = 0; i < 40; i++)
            s.Writer.AppendState("keyboardStateChanged", s.Writer.NowTicks, [], new { action = "keyDown", vk = i });
        await s.EndAsync();
        var received = new List<RecorderRealtimeEvent>();
        var error = await Assert.ThrowsAsync<RecorderStreamGapException>(async () =>
        { await foreach (var evt in slow.ReadAllAsync()) received.Add(evt); });
        Assert.Equal(8, received.Count);
        Assert.Equal(9, error.LastAvailableSequence);
        Assert.Equal(40, MemolineReader.Read(s.Writer.FilePath).Count(e => e.TryGetProperty("kind", out var k) && k.GetString() == "keyboardStateChanged"));
    }

    [Fact]
    public void TopicSelectionRejectsTyposAndExpandsCores()
    {
        Assert.Equal(5, RecorderRealtimeTopics.Expand(["cores"]).Length);
        Assert.Equal(8, RecorderRealtimeTopics.Expand(["all", "keyboard"]).Length);
        Assert.Throws<ArgumentException>(() => RecorderRealtimeTopics.Expand(["core.typo"]));
        Assert.Throws<ArgumentException>(() => RecorderRealtimeTopics.Expand([]));
    }

    [Fact]
    public async Task LocalPipeServesConcurrentClientsAndEndsGracefully()
    {
        await using var s = new Session();
        await using var server = new RecorderRealtimePipeServer(s.Hub);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var keyboard = RecorderRealtimeClient.FollowKeyboardAsync(server.PipeName, cancellation.Token).GetAsyncEnumerator();
        await using var core = RecorderRealtimeClient.FollowCoreAsync(server.PipeName, "clipState", cancellation.Token).GetAsyncEnumerator();
        Assert.True(await keyboard.MoveNextAsync());
        Assert.Equal("hello", keyboard.Current.Kind);
        Assert.True(await core.MoveNextAsync());
        Assert.Equal("hello", core.Current.Kind);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(server.ManifestPath));
        Assert.Equal(server.PipeName, manifest.RootElement.GetProperty("pipeName").GetString());
        s.Writer.AppendState("keyboardStateChanged", s.Writer.NowTicks, [], new { action = "keyDown", vk = 65 });
        s.Core("changed", new { canvas = new { width = 100 }, layers = new[] { "Layer1" } }, "clipState");
        Assert.True(await keyboard.MoveNextAsync());
        Assert.Equal("keyboard", keyboard.Current.Channel);
        Assert.True(await core.MoveNextAsync());
        Assert.Equal("core.clipState", core.Current.Channel);
        await s.EndAsync();
        Assert.True(await keyboard.MoveNextAsync());
        Assert.Equal("sessionEnded", keyboard.Current.Kind);
        Assert.False(await keyboard.MoveNextAsync());
        Assert.True(await core.MoveNextAsync());
        Assert.Equal("sessionEnded", core.Current.Kind);
        Assert.False(await core.MoveNextAsync());
    }

    [Fact]
    public async Task ClientCancellationDoesNotStopRecordingOrOtherSubscriptions()
    {
        await using var s = new Session();
        await using var server = new RecorderRealtimePipeServer(s.Hub);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = RecorderRealtimeClient.FollowMouseAsync(server.PipeName, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await client.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.MoveNextAsync());
        s.Writer.AppendHardware("mouseDown", new { x = 10 }, new("mouse", "test", null, "classOnly"));
        await s.EndAsync();
        Assert.Contains(MemolineReader.Read(s.Writer.FilePath), e => e.TryGetProperty("kind", out var k) && k.GetString() == "mouseDown");
    }

    [Fact]
    public async Task PipeReportsBackpressureGapRatherThanSilentlyDroppingEvents()
    {
        await using var s = new Session();
        await using var server = new RecorderRealtimePipeServer(s.Hub);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = RecorderRealtimeClient.FollowKeyboardAsync(server.PipeName, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await client.MoveNextAsync()); // hello; pause reads while the bounded queue fills.
        for (int i = 0; i < 5000; i++) s.Writer.AppendState("keyboardStateChanged", s.Writer.NowTicks, [],
            new { action = "keyDown", vk = i, detail = new string('x', 512) });
        await s.EndAsync();
        var gap = await Assert.ThrowsAsync<RecorderStreamGapException>(async () =>
        { while (await client.MoveNextAsync()) { } });
        Assert.True(gap.LastAvailableSequence >= 2048);
        Assert.Equal(5000, MemolineReader.Read(s.Writer.FilePath).Count(e => e.TryGetProperty("kind", out var k) && k.GetString() == "keyboardStateChanged"));
    }

    [Fact]
    public async Task RecorderCliOutputsOnlyJsonLinesThroughEndpointManifest()
    {
        await using var s = new Session();
        await using var server = new RecorderRealtimePipeServer(s.Hub);
        using var process = new Process { StartInfo = new ProcessStartInfo(
            System.Environment.GetEnvironmentVariable("MEMOLINE_REALTIME_TEST_EXE") ??
            Path.ChangeExtension(typeof(BehaviorRecognizer.Program).Assembly.Location, ".exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--subscribe");
        process.StartInfo.ArgumentList.Add("all");
        process.StartInfo.ArgumentList.Add("--endpoint");
        process.StartInfo.ArgumentList.Add(server.ManifestPath);
        Assert.True(process.Start());
        try
        {
            string? hello = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("hello", JsonDocument.Parse(hello!).RootElement.GetProperty("kind").GetString());
            s.Writer.AppendState("keyboardStateChanged", s.Writer.NowTicks, [], new { action = "keyUp", vk = 65 });
            await s.EndAsync();
            string rest = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await process.StandardError.ReadToEndAsync());
            var records = rest.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Equal(new[] { "keyUp", "sessionEnded" }, records.Select(e => e.GetProperty("kind").GetString()));
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
