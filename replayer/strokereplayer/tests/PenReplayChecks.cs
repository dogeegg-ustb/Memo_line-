using StrokeReplay;
using static StrokeReplay.PenReplayEngine;

internal static class PenReplayChecks
{
    internal static async Task RunAsync()
    {
        ChecksRecordedGaps();
        await PreservesConcentratedAsync();
        await FinishesLastStrokeWithoutIdleAsync();
        await SeparatesContactsAsync();
        await LiftsMissingEndAsync();
        await ReleasesOnGuardFailureAsync();
        await CancelsIdleAsync();
        await RejectsInvalidBeforeDeviceAsync();
        Console.WriteLine("Pen mode and cancellation checks passed (simulated injection).");
    }

    private static MemolineReplayStroke Stroke(params bool[] contacts) => new(1, 0, 0,
        new CanvasViewSnapshot(100, 0, 0, 0),
        contacts.Select((contact, i) => new MemolineReplaySample(0, 100 + i, 200, .5, 10, -15, contact)).ToArray());

    private static Task Replay(MemolineReplayStroke stroke, FakePen pen, ReplayOptions options,
        Func<MemolineReplaySample, Task>? guard = null, CancellationToken token = default, TimeSpan followingGap = default)
        => ReplayStrokeCoreAsync(stroke, 1000, 10, guard ?? (_ => Task.CompletedTask), token, options,
            () => pen, pen.IdleAsync, followingGap: followingGap);

    private static void ChecksRecordedGaps()
    {
        var discrete = new ReplayOptions(ReplayMode.Discrete);
        foreach (var (end, next, expectedMs) in new (long End, long? Next, double Ms)[]
        {
            (1000, 1080, 80), (1000, 1200, 200), (1000, 5000, 200),
            (1000, 1000, 0), (1000, 900, 0), (1000, null, 0)
        })
        {
            Check(discrete.GapBetween(end, next, 1000) == TimeSpan.FromMilliseconds(expectedMs),
                "Gap must follow original end-to-next-start ticks, capped at 200 ms, with no gap after the last operation.");
            Check(new ReplayOptions().GapBetween(end, next, 1000) == TimeSpan.Zero,
                "Concentrated mode must ignore all recorded interstroke gaps.");
        }
        Check(discrete.GapBetween(10_000_000, 10_800_000, 10_000_000) == TimeSpan.FromMilliseconds(80),
            "Gap conversion must use the memoline clock frequency.");
        Check(new ReplayOptions(ReplayMode.Discrete, .05).GapBetween(0, 1000, 1000) == TimeSpan.FromMilliseconds(50),
            "A lower selected limit may reduce, but never enlarge, the recorded gap.");
    }

    private static async Task FinishesLastStrokeWithoutIdleAsync()
    {
        foreach (bool explicitUp in new[] { true, false })
        {
            var pen = new FakePen();
            await Replay(explicitUp ? Stroke(true, false) : Stroke(true), pen, new(ReplayMode.Discrete));
            Check(pen.Events.SequenceEqual(new[] { "Down", "UpAndLeave", "Dispose" }),
                "The final stroke must leave range and finish immediately without an artificial trailing gap.");
        }
    }

    private static async Task PreservesConcentratedAsync()
    {
        var pen = new FakePen();
        await Replay(Stroke(true, true, false), pen, new());
        Check(pen.Events.SequenceEqual(new[] { "Down", "Update", "Up", "Dispose" }),
            "The default must retain the old contact sequence without any idle gap or leave event.");
        Check(pen.Frames[0].Point.Pressure == 512 && pen.Frames[^1].Point.Pressure == 0,
            "Pressure must remain normalized during drawing and zero at pen up.");
    }

    private static async Task SeparatesContactsAsync()
    {
        var pen = new FakePen();
        var stroke = Stroke(true, true, false, false, true, false);
        stroke = stroke with { EndTicks = 1000, Samples = stroke.Samples.Select((s, i) => s with { Ticks = i < 3 ? 0 : 1000 }).ToArray() };
        await Replay(stroke, pen, new(ReplayMode.Discrete), followingGap: TimeSpan.FromMilliseconds(100));
        Check(pen.Events.SequenceEqual(new[] { "Down", "Update", "UpAndLeave", "Idle", "Down", "UpAndLeave", "Idle", "Dispose" }),
            "Every discrete contact must end and idle before the next down, including multiple contacts in one operation.");
        Check(pen.Delays.SequenceEqual(new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100) }),
            "The internal recorded hover gap must be capped, not waited again, and the following-operation gap respected.");
        Check(pen.Frames.Where(f => f.Transition == PointerTransition.UpAndLeave).All(f => f.Point.Pressure == 0),
            "Leaving range must never carry contact pressure.");
    }

    private static async Task LiftsMissingEndAsync()
    {
        var pen = new FakePen();
        await Replay(Stroke(true, true), pen, new(ReplayMode.Discrete), followingGap: TimeSpan.FromMilliseconds(100));
        Check(pen.Events.SequenceEqual(new[] { "Down", "Update", "UpAndLeave", "Idle", "Dispose" }),
            "A stroke ending without a recorded noncontact sample must still lift and separate once.");
        Check(pen.Frames[^1].Point.X == 101, "Fallback up must occur at the last recorded contact location.");
    }

    private static async Task ReleasesOnGuardFailureAsync()
    {
        foreach (var mode in Enum.GetValues<ReplayMode>())
        {
            var pen = new FakePen();
            int seen = 0;
            try
            {
                await Replay(Stroke(true, true, false), pen, new(mode), _ =>
                    ++seen == 2 ? Task.FromException(new InvalidOperationException("view changed")) : Task.CompletedTask);
                throw new Exception("Expected guard failure.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "view changed") { }
            var lift = mode == ReplayMode.Discrete ? "UpAndLeave" : "Up";
            Check(pen.Events.SequenceEqual(new[] { "Down", lift, "Dispose" }),
                "Guard failure must release the contact and device without waiting for an undo gap.");
        }
    }

    private static async Task CancelsIdleAsync()
    {
        using var stop = new CancellationTokenSource();
        var pen = new FakePen { OnIdle = token => { stop.Cancel(); token.ThrowIfCancellationRequested(); } };
        try
        {
            var stroke = Stroke(true, false, true);
            stroke = stroke with { EndTicks = 500, Samples = stroke.Samples.Select((s, i) => s with { Ticks = i == 2 ? 500 : 0 }).ToArray() };
            await Replay(stroke, pen, new(ReplayMode.Discrete), token: stop.Token);
            throw new Exception("Expected idle cancellation.");
        }
        catch (OperationCanceledException) { }
        Check(pen.Events.SequenceEqual(new[] { "Down", "UpAndLeave", "Idle", "Dispose" }),
            "Cancel during idle must not duplicate pen up or begin the next stroke.");
    }

    private static async Task RejectsInvalidBeforeDeviceAsync()
    {
        foreach (var gap in new[] { double.NaN, double.PositiveInfinity, -.1, .201, 1 })
        {
            bool created = false;
            try
            {
                await ReplayStrokeCoreAsync(Stroke(true), 1000, 1, _ => Task.CompletedTask, default,
                    new(ReplayMode.Discrete, gap), () => { created = true; return new FakePen(); }, (_, _) => Task.CompletedTask);
                throw new Exception("Expected invalid gap rejection.");
            }
            catch (ArgumentException) { }
            Check(!created, "Invalid mode settings must be rejected before allocating an input device.");
        }
        var oldSettings = System.Text.Json.JsonSerializer.Deserialize<ReplaySettings>("{\"Speed\":2}")!;
        Check(oldSettings.Mode == ReplayMode.Concentrated && oldSettings.StrokeGapSeconds == .2,
            "Existing saved settings must retain concentrated mode by default.");
        var legacySettings = System.Text.Json.JsonSerializer.Deserialize<ReplaySettings>("{\"Mode\":1,\"StrokeGapSeconds\":1}")!;
        Check(legacySettings.Mode == ReplayMode.Discrete && legacySettings.StrokeGapSeconds == .2,
            "Previously saved one-second gaps must migrate to the 200 ms cap.");
        var settings = new ReplaySettings { Mode = ReplayMode.Discrete, StrokeGapSeconds = .15 };
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ReplaySettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Check(roundTrip.Mode == ReplayMode.Discrete && roundTrip.StrokeGapSeconds == .15,
            "The chosen mode and gap must survive a settings round trip.");
    }

    private sealed class FakePen : IPenInjector
    {
        internal List<string> Events { get; } = [];
        internal List<(MappedPoint Point, PointerTransition Transition)> Frames { get; } = [];
        internal List<TimeSpan> Delays { get; } = [];
        internal Action<CancellationToken>? OnIdle { get; init; }
        private bool _disposed;
        public void Send(MappedPoint point, PointerTransition transition)
        {
            Check(!_disposed, "No input may be sent after device disposal.");
            Frames.Add((point, transition));
            Events.Add(transition.ToString());
        }
        internal Task IdleAsync(TimeSpan delay, CancellationToken token)
        {
            Check(!_disposed && Frames[^1].Point.Pressure == 0, "Device must remain alive with the contact released throughout idle.");
            Events.Add("Idle");
            Delays.Add(delay);
            OnIdle?.Invoke(token);
            return Task.CompletedTask;
        }
        public void Dispose() { Check(!_disposed, "Device must be disposed once."); _disposed = true; Events.Add("Dispose"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
