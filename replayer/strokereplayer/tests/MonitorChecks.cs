using System.Drawing;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using StrokeReplay;

internal static class MonitorChecks
{
    internal static async Task RunAsync()
    {
        ParseRecognizerContract();
        await InitializeFromConfirmedSnapshotAfterUnknownAsync();
        await FollowRealPipeAsync();
        await DiscoverAndFollowNextSessionAsync();
        Console.WriteLine("Recognizer monitor checks passed.");
    }

    private static async Task InitializeFromConfirmedSnapshotAfterUnknownAsync()
    {
        await using var session = new Session();
        session.Core("changed", session.Writer.NowTicks, originX: 777);
        session.Core("unknown", session.Writer.NowTicks);
        await EventuallyAsync(() => session.LastPublishedSequence >= 2);
        await using var monitor = new RecognizerViewMonitor(session.Server.ManifestPath);
        _ = monitor.RunAsync();
        var view = await monitor.WaitForViewAsync(-1, v => v.OriginX == 777, TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => monitor.LastUpdateTiming is { IsSnapshot: true, Accepted: false });
        Check(monitor.Current == view && view.ZoomInput is not null,
            "A late Replay connection must initialize its complete state table even if the latest packet is unknown.");
    }

    private static async Task DiscoverAndFollowNextSessionAsync()
    {
        await using var first = new Session();
        // This newer descriptor has no owning process and must not hide a viable pipe.
        string stale = Path.Combine(first.DirectoryPath, "stale.memoline.live.json");
        await File.WriteAllTextAsync(stale, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, pipeName = "missing-test-pipe", processId = int.MaxValue
        }));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(5));
        first.Writer.AppendState("tabletDeviceChanged", first.Writer.NowTicks, [], new { deviceId = "gating-test" });
        first.Core("changed", first.Writer.NowTicks, originX: 777);
        await first.Writer.FlushAsync();

        await using var monitor = new RecognizerViewMonitor(discoveryRoot: first.DirectoryPath);
        _ = monitor.RunAsync();
        await EventuallyAsync(() => monitor.TabletDevices.ContainsKey("gating-test"));
        var initialized = await monitor.WaitForViewAsync(-1, v => v.OriginX == 777, TimeSpan.FromSeconds(5));
        Check(initialized.ZoomInput == new Rectangle(-113, -394, 32, 11),
            "Exact live OCR positions must work before optional initialization metadata.");
        first.Writer.AppendState("initializationConfiguration", first.Writer.NowTicks, [], Configuration());
        await first.Writer.FlushAsync();

        long revision = monitor.Revision;
        var interrupted = monitor.WaitForViewAsync(revision, _ => false, TimeSpan.FromSeconds(5));
        await first.EndAsync();
        await ThrowsAsync<IOException>(() => interrupted);

        await using var next = new Session();
        next.Writer.AppendState("initializationConfiguration", next.Writer.NowTicks, [], Configuration());
        next.Core("changed", next.Writer.NowTicks, originX: 888);
        await next.Writer.FlushAsync();
        string discoverable = Path.Combine(first.DirectoryPath, "next.memoline.live.json");
        File.Copy(next.Server.ManifestPath, discoverable);
        File.SetLastWriteTimeUtc(discoverable, DateTime.UtcNow.AddMinutes(1));
        var resumed = await monitor.WaitForViewAsync(-1, v => v.OriginX == 888, TimeSpan.FromSeconds(5));
        Check(resumed.ZoomInput is not null && !monitor.TabletDevices.ContainsKey("gating-test"),
            "Continuous auto discovery must connect to the next session and clear old tablet metadata.");
    }

    private static void ParseRecognizerContract()
    {
        foreach (string status in new[] { "changed", "unchanged" })
        {
            var payload = Payload(status, 10);
            Check(CanvasViewSnapshot.TryParse(payload, out var view), "Confirmed changed and unchanged views must both be usable.");
            Check(view!.Viewport == new Rectangle(-900, -500, 400, 300),
                "Recognizer left/top/right/bottom rectangles must preserve physical screen coordinates.");
            Check(view.ZoomInput == new Rectangle(-113, -394, 32, 11) &&
                  view.RotationInput == new Rectangle(-110, -374, 40, 14),
                "Precise OCR digit rectangles must preserve negative physical screen coordinates.");
            view = view.WithInitializationConfiguration(Configuration());
            Check(view.ZoomInput == new Rectangle(-113, -394, 32, 11) &&
                  view.RotationInput == new Rectangle(-110, -374, 40, 14),
                "Initialization regions must never replace precise digit positions.");
            Check(view.PixelWidth == 1920 && view.PixelHeight == 1080,
                "Canvas dimensions must survive the live payload.");
        }
        foreach (string status in new[] { "unknown", "ambiguous", "error" })
            Check(!CanvasViewSnapshot.TryParse(Payload(status, 10), out _),
                "Unconfirmed observations must not fall back to lastConfirmedState.");
        Check(!CanvasViewSnapshot.TryParse(Payload("changed", 10, ambiguous: true), out _),
            "Causally ambiguous capture must not become a replay confirmation.");
        Check(!CanvasViewSnapshot.TryParse(Payload("changed", 10, success: false), out _),
            "Failed raw transform results must not become a replay confirmation.");
        Check(!CanvasViewSnapshot.TryParse(Payload("changed", 10, module: "colorState"), out _),
            "Other core modules must not be parsed as a canvas view.");
        var missingScale = JsonSerializer.SerializeToElement(new { module = "canvasViewState", status = "changed",
            state = new { canvasOriginScreenPx = new { x = 10, y = 20 }, ocrScalePercent = (double?)null, ocrRotationDegrees = 0 } });
        Check(!CanvasViewSnapshot.TryParse(missingScale, out _), "Missing numeric OCR data must not become zero-valued state.");
        Check(CanvasViewSnapshot.ParseRectangle(JsonSerializer.SerializeToElement(new[] { int.MaxValue, 0, 2, 2 })) is null,
            "Overflowing ROI coordinates must be rejected.");

        var slotOnly = JsonSerializer.SerializeToElement(new {
            ocrLayout = new {
                scaleSlotScreen = new { left = 10, top = 20, right = 50, bottom = 40 },
                rotationSlotScreen = new { left = 10, top = 40, right = 50, bottom = 60 }
            }
        });
        var state = Payload("changed", 10).GetProperty("state");
        var noDigits = CanvasViewSnapshot.ParseState(state, slotOnly)!;
        Check(noDigits.ZoomInput is null && noDigits.RotationInput is null,
            "OCR slots without actual digit rectangles must not become click targets.");

        var calibrated = new CanvasViewSnapshot(50, 0, 0, 0).WithInitializationConfiguration(
            JsonSerializer.SerializeToElement(new { numbersRoi = new[] { 10, 20, 30, 8 } }));
        Check(calibrated.ZoomInput is null && calibrated.RotationInput is null,
            "Initialization number regions must never be split into guessed input positions.");
        var invalid = new CanvasViewSnapshot(50, 0, 0, 0).WithInitializationConfiguration(
            JsonSerializer.SerializeToElement(new { numbersRoi = new[] { 10, 20, 30, 7 } }));
        Check(invalid.ZoomInput is null && invalid.RotationInput is null, "Undersized number regions must stay unavailable.");
    }

    private static async Task FollowRealPipeAsync()
    {
        await using var session = new Session();
        session.Writer.AppendState("initializationConfiguration", session.Writer.NowTicks, [], Configuration());
        session.Writer.AppendState("driverConfiguration", session.Writer.NowTicks, [], new { driverSnapshotId = "driver-test" });
        session.Writer.AppendState("tabletDeviceChanged", session.Writer.NowTicks, [], new { deviceId = "tablet-test", name = "Test Tablet" });
        long initialCapture = session.Writer.NowTicks;
        session.Core("changed", initialCapture);
        await session.Writer.FlushAsync();

        await using var monitor = new RecognizerViewMonitor(session.Server.ManifestPath);
        _ = monitor.RunAsync();
        var initial = await monitor.WaitForViewAsync(-1, v => v.ZoomInput is not null && v.RotationInput is not null,
            TimeSpan.FromSeconds(5));
        Check(initial.Viewport == new Rectangle(-900, -500, 400, 300), "IPC snapshot must retain the canvas viewport.");
        Check(monitor.DriverConfiguration?.GetProperty("driverSnapshotId").GetString() == "driver-test" &&
              monitor.TabletDevices.ContainsKey("tablet-test"), "The tablet channel must retain driver and device snapshots.");
        monitor.EnsureConnected();

        long revision = monitor.Revision;
        monitor.MarkInputStarted();
        session.Core("changed", initialCapture, originX: 123);
        await ThrowsAsync<TimeoutException>(() => monitor.WaitForViewAsync(revision, _ => true,
            TimeSpan.FromMilliseconds(200)));
        Check(monitor.Current == initial && monitor.Revision == revision,
            "A delayed pre-input capture must not overwrite the state table or confirm restoration.");

        session.Core("unchanged", session.Writer.NowTicks, originX: 321);
        var confirmed = await monitor.WaitForViewAsync(revision, v => v.OriginX == 321, TimeSpan.FromSeconds(5));
        Check(confirmed.ZoomInput is not null && confirmed.RotationInput is not null,
            "Later observations must retain their own precise OCR positions.");

        revision = monitor.Revision;
        session.Core("changed", session.Writer.NowTicks, originX: 322, ambiguous: true);
        var latestWithAttributionWarning = await monitor.WaitForViewAsync(revision, v => v.OriginX == 322, TimeSpan.FromSeconds(5));
        Check(latestWithAttributionWarning.OriginX == 322 && monitor.LastUpdateTiming is { Accepted: true, DeliveryMilliseconds: not null },
            "A successful latest live observation must be accepted despite causal attribution diagnostics and expose delivery timing.");
        revision = monitor.Revision;
        session.Core("unchanged", session.Writer.NowTicks, includeDigits: false);
        var noLocations = await monitor.WaitForViewAsync(revision, _ => true, TimeSpan.FromSeconds(5));
        Check(noLocations.ZoomInput is null && noLocations.RotationInput is null,
            "A new observation without digit positions must not reuse prior positions or initialization slots.");

        revision = monitor.Revision;
        var recovering = monitor.WaitForViewAsync(revision, v => v.OriginX == 456, TimeSpan.FromSeconds(5));
        foreach (string intermediate in new[] { "unknown", "ambiguous", "error" })
        {
            long before = monitor.Revision;
            session.Core(intermediate, session.Writer.NowTicks);
            await EventuallyAsync(() => monitor.Revision > before);
            Check(monitor.Current == noLocations && !recovering.IsCompleted,
                "Unconfirmed intermediate observations must preserve the table without confirming a different target.");
            var cached = await monitor.WaitForViewAsync(-1, v => v.OriginX == noLocations.OriginX, TimeSpan.FromMilliseconds(200));
            Check(cached == noLocations, "A matching state table must be usable without another update.");
            monitor.EnsureConnected();
        }
        session.Core("changed", session.Writer.NowTicks, originX: 123);
        await EventuallyAsync(() => monitor.Current?.OriginX == 123);
        Check(!recovering.IsCompleted, "A confirmed view must still meet the caller's condition.");
        session.Core("unchanged", session.Writer.NowTicks, originX: 456);
        Check((await recovering).OriginX == 456,
            "The original wait must resume after a later matching confirmed observation.");
        revision = monitor.Revision;
        session.Core("unknown", session.Writer.NowTicks);
        await EventuallyAsync(() => monitor.Revision > revision);
        Check(monitor.Current?.OriginX == 456, "Unknown observations must not clear the last confirmed state table.");
        await ThrowsAsync<TimeoutException>(() => monitor.WaitForViewAsync(revision, _ => true, TimeSpan.FromMilliseconds(200)));
        session.Core("changed", session.Writer.NowTicks, originX: 456);
        await monitor.WaitForViewAsync(-1, v => v.OriginX == 456, TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => monitor.WaitForViewAsync(monitor.Revision, _ => false,
            TimeSpan.FromSeconds(5), cancelled.Token));

        revision = monitor.Revision;
        var interruptedWait = monitor.WaitForViewAsync(revision, _ => false, TimeSpan.FromSeconds(5));
        await session.EndAsync();
        await ThrowsAsync<IOException>(() => interruptedWait);
        Check(monitor.Current is null, "Ending the Recognizer session must invalidate the active view.");
    }

    private static JsonElement Configuration() => JsonSerializer.SerializeToElement(new
    {
        numbersRoi = new[] { -120, -400, 70, 40 }, canvasPixelSize = new[] { 1920, 1080 }
    });

    private static JsonElement Payload(string status, long capturedTicks, double originX = -400,
        bool ambiguous = false, bool success = true, string module = "canvasViewState", bool includeDigits = true) => JsonSerializer.SerializeToElement(new
    {
        module, status, state = new
        {
            canvasOriginScreenPx = new { x = originX, y = -200 }, ocrScalePercent = 50, ocrRotationDegrees = 0,
            transform = new { canvasPixelWidth = 1920, canvasPixelHeight = 1080 }
        },
        lastConfirmedState = new { ocrScalePercent = 50 },
        rawResult = new { success, canvasWindowRoiScreenPx = new { left = -900, top = -500, right = -500, bottom = -200 },
            ocrLayout = new {
                scaleSlotScreen = new { left = -120, top = -400, right = -50, bottom = -380 },
                rotationSlotScreen = new { left = -120, top = -380, right = -50, bottom = -360 },
                scaleDigitsScreen = includeDigits ? new { left = -113, top = -394, right = -81, bottom = -383 } : null,
                rotationDigitsScreen = includeDigits ? new { left = -110, top = -374, right = -70, bottom = -360 } : null
            } },
        evidence = new { capturedTicks, causalAmbiguous = ambiguous }
    });

    private sealed class Session : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "strokereplayer-monitor-" + Guid.NewGuid().ToString("N"));
        private bool _ended;
        internal MemolineWriter Writer { get; }
        private RecorderRealtimeHub Hub { get; }
        internal RecorderRealtimePipeServer Server { get; }
        internal string DirectoryPath => _directory;
        internal long LastPublishedSequence => Hub.CurrentSequence;
        internal Session()
        {
            Writer = new(_directory, new { test = "headless replay monitor" });
            Hub = new(Writer);
            Server = new(Hub);
        }
        internal void Core(string status, long capturedTicks, double originX = -400, bool includeDigits = true, bool ambiguous = false) =>
            Writer.AppendState("coreStateUpdated", Writer.NowTicks, [], Payload(status, capturedTicks, originX, ambiguous: ambiguous, includeDigits: includeDigits), "delayed");
        internal async Task EndAsync()
        {
            if (_ended) return;
            _ended = true;
            await Writer.DisposeAsync();
            await Hub.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await EndAsync();
            await Server.DisposeAsync();
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
