using System.Drawing;
using PacketReplay;
using StrokeReplay;

internal static partial class PacketChecks
{
    internal static async Task EventReplayAsync()
    {
        // These checks pin the strict colour contract; the editing host runs colour best effort.
        EventReplayRunner.StrictColor = true;
        Check(WindowsViewInput.WithRecorderTag(new() { Type = 0 }).Data.Mouse.Extra == 0x4D4C5243 &&
            WindowsViewInput.WithRecorderTag(new() { Type = 1 }).Data.Keyboard.Extra == 0x4D4C5243,
            "Keyboard, Unicode, button, wheel and pointer-move SendInput share the recorder tag.");
        var feed = new FakeFeed { Layer = "工作层", Color = Color(1) };
        var input = new FakeInput(feed);
        var view = new TableView(feed);
        var environment = new ReplayTestEnvironment(feed, input, view);
        var historical = new HistoricalState(BrushSnapshot.Parse(Brush(10)), "原图层", Color(1), view.Current);
        ScheduledStroke Stroke(ulong id, ulong append, long ticks, BrushSnapshot? changed = null) => new(("session", append),
            new(id, ticks, ticks + 2, view.Current!, [new(ticks, 100, 100, .5, 0, 0, true, append, id),
                new(ticks + 1, 101, 100, .8, 0, 0, true, append + 2, id + 1),
                new(ticks + 2, 102, 100, 0, 0, 0, false, append + 5, id + 2)]), historical, changed);
        var first = Stroke(10, 100, 0, BrushSnapshot.Parse(Brush(25)));
        var second = Stroke(30, 200, 1000);
        var schedule = new EventReplaySchedule(1000, [first, second], "工作层", "work-uuid");
        var result = await EventReplayRunner.RunAsync(schedule, environment, 100000, new(ReplayMode.Discrete), _ => { }, default);
        Check(result.CompletedStrokes == 2, "Arbitrary event replay reports both complete contacts.");
        Check(environment.Appends.SequenceEqual(new ulong[] { 100, 102, 105, 200, 202, 205 }), "Cross-packet append IDs stay complete and in native order.");
        Check(environment.DownSizes.SequenceEqual(new[] { 25d, 10d }), "An override applies at pen down and the next stroke restores its complete historical brush.");
        Check(environment.Transitions.Count(t => t == PenReplayEngine.PointerTransition.UpAndLeave) == 2, "Discrete contacts each release with UP/LEAVE.");
        Check(environment.Gaps.SequenceEqual(new[] { TimeSpan.FromSeconds(.2) }), "Skipped events use the next scheduled native start and the bounded recorded gap.");
        Check(environment.Layers.All(n => n == "工作层") && !input.Events.Any(e => e.Contains("Alt")), "All contacts route to the work layer without navigating to historical layers.");
        Check(environment.Releases == 1, "Runner releases held input on successful completion.");
        environment.Clear();
        await EventReplayRunner.RunAsync(schedule, environment, 100000, new(), _ => { }, default);
        Check(environment.Transitions.Count(t => t == PenReplayEngine.PointerTransition.Up) == 2 && environment.Gaps.Count == 0, "Concentrated replay ends each contact with UP and no idle gap.");
        environment.Clear();
        int verified=0;
        await EventReplayRunner.RunAsync(schedule with {Strokes=[first],VerifyRestoredStateAsync=(drawing,token)=>{
            Check(environment.Transitions.Count==0&&feed.Brush!.Matches(drawing.OverrideBrush!),"Extra raw-state verification runs after numeric restoration and before pen injection.");
            verified++;return Task.CompletedTask;
        }},environment,100000,new(),_=>{},default);
        Check(verified==1,"A reference-control readback callback actually executes before replay.");
        environment.Clear();
        try {await EventReplayRunner.RunAsync(schedule with {Strokes=[first],VerifyRestoredStateAsync=(_,_)=>
            throw new ReplayStateException("BrushUnrestorable","Reference controls are on.")},environment,100000,new(),_=>{},default);
            throw new Exception("Expected reference verification failure");}
        catch(ReplayStateException error){Check(error.Code=="BrushUnrestorable","Extra raw-state verification retains its precise failure code.");}
        Check(environment.Transitions.Count==0&&environment.Releases==1,"A failed reference-control readback never injects a pen and releases inputs.");
        EventReplayRunner.Validate(schedule with {Strokes=[first with {OverrideBrush=new("Confirmed different pen",[new("brush_size",25),new("flow",61)],0)}]});
        Check(true,"A confirmed different subtool carries its own complete numeric table, including target-only properties.");
        Reject<ReplayStateException>(()=>EventReplayRunner.Validate(schedule with {Strokes=[first with {OverrideBrush=
            historical.Brush! with {Numbers=[new("brush_size",25),new("flow",61)]}}]}));
        environment.Clear();
        await EventReplayRunner.RunAsync(schedule with { Strokes = [first], FollowingStrokeStartTicks = second.Stroke.StartTicks },
            environment, 100000, new(ReplayMode.Discrete), _ => { }, default);
        Check(environment.Gaps.SequenceEqual(new[] { TimeSpan.FromSeconds(.2) }),
            "A one-event reference-scene batch preserves the gap to the next selected native contact.");
        environment.Clear();
        await EventReplayRunner.RunAsync(schedule with { Strokes = [first], FollowingStrokeStartTicks = 102 },
            environment, 100000, new(ReplayMode.Discrete), _ => { }, default);
        Check(environment.Gaps.SequenceEqual(new[] { TimeSpan.FromSeconds(.1) }),
            "Reference-scene batches preserve shorter recorded gaps without expanding them to the maximum.");
        Reject<InvalidDataException>(() => EventReplayRunner.Validate(schedule with { Strokes = [second, first] }));
        Reject<InvalidDataException>(() => EventReplayRunner.Validate(schedule with { Strokes = [first, first] }));
        Reject<InvalidDataException>(() => EventReplayRunner.Validate(schedule with { Strokes = [first with { Stroke = first.Stroke with { Samples = first.Stroke.Samples.Take(2).ToArray() } }] }));
        environment.Clear();
        environment.OnSend = count => { if (count == 1) feed.Layer = "意外图层"; };
        await RejectAsync<ReplayStateException>(() => EventReplayRunner.RunAsync(schedule, environment, 100000, new(), _ => { }, default));
        Check(environment.Transitions.SequenceEqual(new[] { PenReplayEngine.PointerTransition.Down, PenReplayEngine.PointerTransition.Up }), "Work-layer drift at the next sample stops drawing and releases the active pen.");
        feed.Layer = "工作层"; environment.Clear();
        using var cancel = new CancellationTokenSource();
        environment.OnSend = count => { if (count == 1) cancel.Cancel(); };
        await RejectAsync<OperationCanceledException>(() => EventReplayRunner.RunAsync(schedule, environment, 100000, new(), _ => { }, cancel.Token));
        Check(environment.Transitions.Last() == PenReplayEngine.PointerTransition.Up && environment.Releases == 1, "Cancellation releases both the pen and held environment input.");
        environment.Clear();
        environment.OnSend = count => { if (count == 1) environment.LiveViewOverride = view.Current! with { ScalePercent = 75 }; };
        try { await EventReplayRunner.RunAsync(schedule, environment, 100000, new(), _ => { }, default); throw new Exception("Expected live view drift"); }
        catch (ReplayStateException ex) { Check(ex.Code == "ViewUnrestorable", "Sampling guards inspect the live view rather than an earlier requested snapshot."); }
        Check(environment.Transitions.Last() == PenReplayEngine.PointerTransition.Up, "View drift releases the active pen.");
        environment.Clear(); feed.Color = Color(9);
        try { await EventReplayRunner.RunAsync(schedule, environment, 100000, new(), _ => { }, default); throw new Exception("Expected color failure"); }
        catch (ReplayStateException ex) { Check(ex.Code == "ColorUnrestorable", "Unverified color writes fail with the defined error code."); }
        Check(feed.Requests.Last().SequenceEqual(new[] { "colorState" }) && environment.Transitions.Count == 0, "A colour that cannot be picked (no picker shortcut here) stops before any pen injection.");
        // Best effort (product default): an unsettable colour is attempted once per target colour,
        // and both strokes still replay in the current colour.
        EventReplayRunner.StrictColor = false; environment.Clear(); feed.Requests.Clear();
        await EventReplayRunner.RunAsync(schedule, environment, 100000, new(), _ => { }, default);
        Check(environment.Transitions.Count(t => t == PenReplayEngine.PointerTransition.Down) == 2 &&
              feed.Requests.Count(r => r.Contains("colorState")) == 1,
            "Consecutive strokes with one target colour request colour state once, not per stroke.");
        // An edit's colour replaces the recorded one and is never best effort.
        environment.Clear(); var statuses = new List<string>();
        await EventReplayRunner.RunAsync(schedule with { Strokes = [second with { OverrideColor = ColorRestorer.State([9, 2, 3]) }] },
            environment, 100000, new(), statuses.Add, default);
        Check(environment.Transitions.Count(t => t == PenReplayEngine.PointerTransition.Down) == 1 && !statuses.Any(s => s.Contains("颜色未还原")),
            "A stroke recoloured by an edit is drawn in the edit's colour, not restored to the recorded one.");
        environment.Clear();
        try
        {
            await EventReplayRunner.RunAsync(schedule with { Strokes = [first, second with { OverrideColor = ColorRestorer.State([200, 100, 50]) }] },
                environment, 100000, new(), _ => { }, default);
            throw new Exception("Expected override colour failure");
        }
        catch (ReplayStateException ex) { Check(ex.Code == "ColorUnrestorable", "An edit's colour that cannot be picked fails even when recorded colours are best effort."); }
        Check(environment.Transitions.Count(t => t == PenReplayEngine.PointerTransition.Down) == 1 && environment.Releases == 1,
            "The recoloured stroke is not drawn in another colour; inputs are released.");
        Reject<ReplayStateException>(() => EventReplayRunner.Validate(schedule with { Strokes = [second with { OverrideColor = Json(new { kind = "transparent" }) }] }));
        EventReplayRunner.StrictColor = true;
        feed.Color = Color(1); environment.Clear();
        feed.Layers = J.Get(Structure("工作层"), "state");
        var packet = new PacketInfo(1, "legacy", 0, 1002, "captured", new HashSet<ulong>(), default);
        var drawings = schedule.Strokes.Select(s => new PacketStroke(s.Stroke, s.State with { Layer = "工作层" }, false)).ToArray();
        var plan = new PacketPlan(packet, drawings, drawings.Select(d => new PacketStep(d.Stroke.OperationId, d.Stroke.OperationId, "stroke", Drawing: d)).ToArray(), [], drawings[0].State);
        await PacketReplayRunner.RunCoreAsync(Fixture(drawings.Select(d => d.Stroke).ToArray(), packet), plan, environment,
            100000, new(), _ => { }, null, default);
        Check(environment.Transitions.Count(t => t == PenReplayEngine.PointerTransition.Down) == 2 && environment.Releases == 1,
            "The old packet runner executes via the same injectable environment without constructing Windows input.");
        Console.WriteLine($"Event replay checks passed: {_assertions} assertions. No CSP input was injected.");
    }

    private sealed class ReplayTestEnvironment(FakeFeed feed, FakeInput input, TableView view) : IReplayEnvironment
    {
        public IPacketStateFeed Feed => feed;
        public IPacketInput Input => input;
        public ICanvasViewFeed ViewFeed => view;
        public IViewInput ViewInput => view;
        public CanvasViewSnapshot? CurrentView => LiveViewOverride ?? view.Current;
        internal CanvasViewSnapshot? LiveViewOverride;
        public Func<PenReplayEngine.IPenInjector> CreateInjector => () => new TestInjector(this);
        public Func<TimeSpan, CancellationToken, Task> Idle => (gap, token) => { token.ThrowIfCancellationRequested(); Gaps.Add(gap); return Task.CompletedTask; };
        internal readonly List<ulong> Appends = [];
        internal readonly List<string> Layers = [];
        internal readonly List<double> DownSizes = [];
        internal readonly List<PenReplayEngine.PointerTransition> Transitions = [];
        internal readonly List<TimeSpan> Gaps = [];
        internal Action<int>? OnSend;
        internal int Releases;
        public void EnsureCanvasPoint(MemolineReplaySample sample, Rectangle viewport) { Appends.Add(sample.AppendId); Layers.Add(feed.Layer!); if (!viewport.Contains((int)sample.X, (int)sample.Y)) throw new Exception("outside"); }
        public void ReleaseInputs() => Releases++;
        internal void Clear() { Appends.Clear(); Layers.Clear(); DownSizes.Clear(); Transitions.Clear(); Gaps.Clear(); OnSend = null; LiveViewOverride = null; Releases = 0; }
        private sealed class TestInjector(ReplayTestEnvironment owner) : PenReplayEngine.IPenInjector
        {
            public void Send(PenReplayEngine.MappedPoint point, PenReplayEngine.PointerTransition transition)
            { owner.Transitions.Add(transition); if (transition == PenReplayEngine.PointerTransition.Down) owner.DownSizes.Add(owner.Feed.Brush!.Numbers[0].Value); owner.OnSend?.Invoke(owner.Transitions.Count); }
            public void Dispose() { }
        }
    }
}
