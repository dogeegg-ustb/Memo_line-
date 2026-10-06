using System.Drawing;
using System.Text.Json;
using PacketReplay;
using StrokeReplay;

internal static partial class PacketChecks
{
    private static JsonElement Color(int red) => Json(new { kind = "rgb", rgb = new[] { red, 2, 3 }, hex = $"#{red:X2}0203" });
    private static JsonElement ViewState(double scale, double x = 0) => Json(new { module = "canvasViewState", status = "changed",
        state = new { ocrScalePercent = scale, ocrRotationDegrees = 0, canvasOriginScreenPx = new { x, y = 0 } } });
    private sealed class TableView(FakeFeed feed) : ICanvasViewFeed, IViewInput
    {
        public CanvasViewSnapshot? Current { get; private set; } = new(100, 0, 0, 0, new Rectangle(0, 0, 800, 600),
            new Rectangle(10, 10, 20, 10), new Rectangle(40, 10, 20, 10), 500, 600);
        private CanvasViewSnapshot? _pending;
        internal int Inputs;
        public long Revision => feed.Revision;
        public void EnsureConnected() => feed.EnsureConnected();
        public void EnsureTarget() { }
        public void MarkInputStarted() { }
        public Task SetNumberAsync(Rectangle area, double value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Inputs++;
            _pending = area == Current!.ZoomInput ? Current with { ScalePercent = value } : Current with { RotationDegrees = value };
            return Task.CompletedTask;
        }
        public Task PanAsync(Point from, Point to, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Inputs++;
            _pending = Current! with { OriginX = Current!.OriginX + to.X - from.X, OriginY = Current.OriginY + to.Y - from.Y };
            return Task.CompletedTask;
        }
        public async Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate, TimeSpan timeout, CancellationToken token)
        {
            await feed.RequestAsync(["canvasViewState"], token);
            Current = _pending ?? Current; _pending = null;
            if (!predicate(Current!)) throw new InvalidOperationException();
            return Current!;
        }
    }

    private static async Task RecordedStateTableAsync()
    {
        var feed = new FakeFeed { Color = Color(1) };
        var input = new FakeInput(feed); var view = new TableView(feed);
        var initial = new HistoricalState(BrushSnapshot.Parse(Brush(10)), "图层 1", Color(1), view.Current);
        var table = new PacketReplayStateTable(feed, input, view, view, _ => { }, initial);
        PacketStroke Drawing(HistoricalState state, ulong eventId) => new(new MemolineReplayStroke(eventId, (long)eventId, (long)eventId + 1,
            state.View!, [new((long)eventId, 100, 100, .5, 0, 0, true, eventId, eventId),
                new((long)eventId + 1, 100, 100, 0, 0, 0, false, eventId + 1, eventId + 1)]), state, false);
        await table.RestoreAsync(default);
        for (ulong n = 1; n <= 40; n++) await table.PrepareStrokeAsync(Drawing(initial, n), default);
        Check(feed.Requests.Count == 0 && input.Events.Count == 0 && view.Inputs == 0,
            "Forty consecutive strokes in the recorded initial state reuse the confirmed table with zero requests or restoration input.");

        await table.ApplyAsync(new(41, 1, "brush", Brush: BrushSnapshot.Parse(Brush(20))), default);
        Check(feed.Requests.Count == 1 && feed.Requests[0].SequenceEqual(new[] { "brushState" }) && input.Events.Count == 1,
            "A recorded numeric update changes only the brush column and requests one post-edit confirmation.");
        Check(table.Target.Layer == initial.Layer && PacketReplayRunner.ColorMatches(table.Target.Color, initial.Color),
            "A brush update preserves independent layer and color columns.");
        await table.ApplyAsync(new(42, 2, "layer", Layer: "图层 2"), default);
        await table.ApplyAsync(new(43, 3, "view", View: initial.View! with { ScalePercent = 75 }), default);
        feed.OnRequest = modules => { if (modules.Contains("colorState")) feed.Color = Color(4); };
        await table.ApplyAsync(new(44, 4, "color", Color: Color(4)), default);
        Check(feed.Requests.SelectMany(m => m).SequenceEqual(new[] { "brushState", "currentLayerState", "canvasViewState", "colorState" }),
            "Recorded layer, view, and color changes query only their affected modules, once per needed confirmation.");
        var changed = table.Target;
        for (ulong n = 45; n <= 84; n++) await table.PrepareStrokeAsync(Drawing(changed, n), default);
        Check(feed.Requests.Count == 4 && input.Events.Count == 2 && view.Inputs == 1,
            "Forty subsequent strokes use the updated table without adding state requests.");
        var resumed = new PacketReplayStateTable(feed, input, view, view, _ => { }, changed);
        await resumed.PrepareStrokeAsync(Drawing(changed, 85), default);
        Check(feed.Requests.Count == 4, "A separately invoked next stroke keeps the previous run's confirmed state.");
        await resumed.ApplyAsync(new(86, 5, "brush", Brush: changed.Brush), default);
        await resumed.ApplyAsync(new(87, 6, "view", View: changed.View), default);
        await resumed.ApplyAsync(new(88, 7, "color", Color: changed.Color), default);
        Check(feed.Requests.Count == 4, "Repeated or unchanged historical observations do not cause redundant queries.");
        feed.OnRequest = null;
        await RejectAsync<InvalidOperationException>(() => resumed.ApplyAsync(new(89, 8, "color", Color: Color(9)), default));
        Check(feed.Requests.Count == 5, "An unmatched color update is requested once and blocks drawing if it remains wrong.");
        feed.Brush = null;
        Reject<InvalidOperationException>(() => table.AssertMatches());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await RejectAsync<OperationCanceledException>(() => table.PrepareStrokeAsync(Drawing(changed, 90), stop.Token));

        // The selected packet starts after initialization and earlier packet updates.
        var packet = new PacketInfo(1, "later-packet", 10, 30, "captured", new HashSet<ulong> { 10, 20 }, default);
        var stroke = Drawing(changed, 20).Stroke;
        var history = new HistorySlot[] { new(0, 0, "brushState", Brush(10)), new(0, 0, "currentLayerState", Layer("图层 1")),
            new(0, 0, "canvasViewState", ViewState(100)), new(0, 0, "colorState", Json(new { module = "colorState", status = "changed", state = Color(1) })),
            new(5, 1, "brushState", Brush(20)), new(10, 2, "currentLayerState", Layer("图层 2")),
            new(10, 3, "canvasViewState", ViewState(75)), new(10, 4, "colorState", Json(new { module = "colorState", status = "changed", state = Color(4) })) };
        var doc = Fixture([stroke], packet) with { History = history,
            Frames = [Json(new { kind = "mouse", path = "hardware", appendId = 10, eventId = 10 })] };
        var plan = PacketArchiveReader.Plan(doc, packet);
        Check(plan.InitialState.Brush!.Numbers[0].Value == 20 && plan.InitialState.Layer == "图层 1"
            && plan.InitialState.View?.ScalePercent == 100 && PacketReplayRunner.ColorMatches(plan.InitialState.Color, Color(1)),
            "The packet's baseline inherits initialization and updates before the selected packet's first input.");
        Check(plan.Steps.Select(s => s.Kind).SequenceEqual(new[] { "layer", "view", "color", "stroke" }),
            "Include recorded color updates in the same causal event sequence as layers, views and drawings.");
        var beforeStroke = plan.StateBefore(3);
        Check(beforeStroke.Brush!.Numbers[0].Value == 20 && beforeStroke.Layer == "图层 2"
            && beforeStroke.View?.ScalePercent == 75 && PacketReplayRunner.ColorMatches(beforeStroke.Color, Color(4)),
            "The state table at a next-stroke cursor accumulates earlier module changes instead of restarting from initialization.");
        Check(PacketReplayRunner.ColorMatches(plan.Strokes.Single().State.Color, Color(4)) && plan.Strokes.Single().State.Layer == "图层 2",
            "Each drawing resolves the accumulated file state at its own causal position.");
    }
}
