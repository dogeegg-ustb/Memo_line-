using System.Drawing;
using StrokeReplay;

namespace PacketReplay;

internal sealed record ScheduledStroke((string SessionId, ulong BeginAppendId) EditableKey,
    MemolineReplayStroke Stroke, HistoricalState State, BrushSnapshot? OverrideBrush = null);
internal sealed record EventReplaySchedule(long Frequency, IReadOnlyList<ScheduledStroke> Strokes,
    string WorkLayerName, string WorkLayerUuid)
{
    // Per-event historical reference scenes may split a selected sequence into
    // separate calls. Preserve the recorded idle period across that boundary.
    internal long? FollowingStrokeStartTicks { get; init; }
    internal Func<ScheduledStroke,CancellationToken,Task>? VerifyRestoredStateAsync { get; init; }
}
internal sealed record EventReplayResult(int CompletedStrokes, IReadOnlyList<(string SessionId, ulong BeginAppendId)> CompletedEvents);

internal interface IReplayEnvironment
{
    IPacketStateFeed Feed { get; }
    IPacketInput Input { get; }
    ICanvasViewFeed ViewFeed { get; }
    IViewInput ViewInput { get; }
    CanvasViewSnapshot? CurrentView { get; }
    Func<PenReplayEngine.IPenInjector> CreateInjector { get; }
    Func<TimeSpan, CancellationToken, Task> Idle { get; }
    void EnsureCanvasPoint(MemolineReplaySample sample, Rectangle viewport);
    void ReleaseInputs();
}

internal sealed class WindowsReplayEnvironment(LivePacketFeed feed, WindowsViewInput windows) : IReplayEnvironment
{
    public IPacketStateFeed Feed => feed;
    public IPacketInput Input { get; } = new PacketWindowsInput(windows);
    public ICanvasViewFeed ViewFeed { get; } = new PacketViewFeed(feed);
    public IViewInput ViewInput => windows;
    public CanvasViewSnapshot? CurrentView => feed.View.Current;
    public Func<PenReplayEngine.IPenInjector> CreateInjector => PenReplayEngine.CreateSyntheticInjector;
    public Func<TimeSpan, CancellationToken, Task> Idle => (delay, token) => Task.Delay(delay, token);
    public void EnsureCanvasPoint(MemolineReplaySample sample, Rectangle viewport) => windows.EnsureCanvasPoint(sample.X, sample.Y, viewport);
    public void ReleaseInputs() => windows.ReleaseInputs();
}

internal sealed class ReplayStateException(string code, string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    internal string Code { get; } = code;
}

/// <summary>Replays complete native contacts into a transaction-confirmed work layer.</summary>
internal static class EventReplayRunner
{
    internal static void Validate(EventReplaySchedule schedule)
    {
        if (schedule.Frequency <= 0) throw new InvalidDataException("录制时钟频率必须为正数。");
        if (string.IsNullOrWhiteSpace(schedule.WorkLayerName) || string.IsNullOrWhiteSpace(schedule.WorkLayerUuid))
            throw new InvalidDataException("工作图层必须有已确认的名称及 UUID。");
        var keys = new HashSet<(string, ulong)>();
        ulong previousEvent = 0;
        string? session = null;
        foreach (var drawing in schedule.Strokes)
        {
            var samples = drawing.Stroke.Samples;
            if (samples.Count < 2 || !samples[0].InContact || samples[^1].InContact ||
                samples[0].AppendId != drawing.EditableKey.BeginAppendId || drawing.EditableKey.BeginAppendId == 0 ||
                string.IsNullOrWhiteSpace(drawing.EditableKey.SessionId) || !keys.Add(drawing.EditableKey))
                throw new InvalidDataException("事件清单包含重复、裁切或未结束的接触。");
            session ??= drawing.EditableKey.SessionId;
            if (session != drawing.EditableKey.SessionId || samples[0].EventId <= previousEvent ||
                samples[0].EventId != drawing.Stroke.OperationId || drawing.Stroke.StartTicks != samples[0].Ticks ||
                drawing.Stroke.EndTicks != samples[^1].Ticks || drawing.Stroke.EndTicks < drawing.Stroke.StartTicks)
                throw new InvalidDataException("清单必须属于同一会话，并按原生 penBegin eventId 严格递增。");
            previousEvent = samples[0].EventId;
            ulong previousAppend = 0;
            long ticks = drawing.Stroke.StartTicks;
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                bool interruptedRelease = i == samples.Count - 1 && !sample.InContact && sample.AppendId == 0 && sample.EventId == 0;
                if (sample.Ticks < ticks || !double.IsFinite(sample.X) || !double.IsFinite(sample.Y) ||
                    !double.IsFinite(sample.Pressure) || !double.IsFinite(sample.TiltX) || !double.IsFinite(sample.TiltY) ||
                    !interruptedRelease && sample.AppendId <= previousAppend || i < samples.Count - 1 && !sample.InContact)
                    throw new InvalidDataException("完整接触的采样必须按原生 appendId 递增，不能拼接独立接触。");
                ticks = sample.Ticks;
                previousAppend = sample.AppendId;
            }
            if (drawing.State.Brush is null || drawing.State.Layer is null)
                throw new ReplayStateException("BrushUnrestorable", "事件缺少已确认的历史笔刷或图层状态。");
            if (StrictColor && drawing.State.Color.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ReplayStateException("ColorUnrestorable", "事件缺少已确认的历史颜色。");
            if (drawing.OverrideBrush is { } replacement)
            {
                if (string.IsNullOrWhiteSpace(replacement.Name)||replacement.Name is "unknown" or "ambiguous"||
                    replacement.Numbers.Select(n => n.Key).Distinct().Count() != replacement.Numbers.Count ||
                    replacement.Numbers.Any(n => !double.IsFinite(n.Value)) ||
                    BrushSnapshot.SameName(replacement.Name,drawing.State.Brush.Name)&&
                        !replacement.Numbers.Select(n => n.Key).ToHashSet().SetEquals(drawing.State.Brush.Numbers.Select(n => n.Key)))
                    throw new ReplayStateException("UnsupportedOverride", "参数覆盖必须包含已确认子工具的完整数值表；同一子工具不能新增或删除历史属性。");
            }
        }
        if (schedule.FollowingStrokeStartTicks is { } next && (schedule.Strokes.Count == 0 || next < schedule.Strokes[^1].Stroke.EndTicks))
            throw new InvalidDataException("批次后的笔开始时间不能早于本批次最后一笔的结束时间。");
    }

    /// <summary>When false (default), colour restoration is best effort: a colour that cannot be
    /// set or confirmed is reported in the status line and the stroke is replayed in the current colour.</summary>
    internal static bool StrictColor { get; set; }

    internal static async Task<EventReplayResult> RunAsync(EventReplaySchedule schedule, IReplayEnvironment env,
        double speed, ReplayOptions options, Action<string> status, CancellationToken token)
    {
        Validate(schedule);
        if (!double.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        options.Validate();
        var completed = new List<(string, ulong)>();
        var states = new PacketStateRestorer(env.Feed, env.Input, status);
        var views = new ViewRestorer(env.ViewFeed, env.ViewInput, status);
        var colors = new ColorRestorer(env.Feed, env.Input);
        void WorkLayer()
        {
            env.Feed.EnsureConnected(); env.Input.EnsureTarget();
            if (J.Name(env.Feed.Layer ?? "") != J.Name(schedule.WorkLayerName))
                throw new ReplayStateException("LayerIdentityUnconfirmed", "当前图层已离开已确认的工作图层，回放停止。");
        }
        try
        {
            for (int index = 0; index < schedule.Strokes.Count; index++)
            {
                token.ThrowIfCancellationRequested(); WorkLayer();
                var drawing = schedule.Strokes[index];
                var brush = drawing.OverrideBrush ?? drawing.State.Brush!;
                try { await states.RestoreBrushAsync(brush, token); }
                catch (InvalidOperationException ex) { throw new ReplayStateException("BrushUnrestorable", ex.Message, ex); }
                bool colorRestored = true;
                try { await colors.RestoreAsync(drawing.State.Color, token); }
                catch (ReplayStateException ex) when (!StrictColor && ex.Code == "ColorUnrestorable")
                { colorRestored = false; status("颜色未还原，按当前颜色重放：" + ex.Message); }
                try { await views.RestoreAsync(drawing.Stroke.View, token); }
                catch (InvalidOperationException ex) { throw new ReplayStateException("ViewUnrestorable", ex.Message, ex); }
                WorkLayer();
                if(schedule.VerifyRestoredStateAsync is { } verify)
                {
                    await verify(drawing,token);
                    token.ThrowIfCancellationRequested(); WorkLayer();
                }
                var viewport = env.CurrentView?.Viewport ?? throw new ReplayStateException("ViewUnrestorable", "当前画布视口不可用。");
                foreach (var sample in drawing.Stroke.Samples.Where(s => s.InContact))
                    if (!viewport.Contains(checked((int)Math.Round(sample.X)), checked((int)Math.Round(sample.Y))))
                        throw new ReplayStateException("ViewUnrestorable", "完整笔画有采样点位于当前画布视口之外。");
                status($"重放事件 {index + 1}/{schedule.Strokes.Count}，历史图层：{drawing.State.Layer}");
                var gap = options.GapBetween(drawing.Stroke.EndTicks,
                    index + 1 < schedule.Strokes.Count ? schedule.Strokes[index + 1].Stroke.StartTicks : schedule.FollowingStrokeStartTicks, schedule.Frequency);
                await PenReplayEngine.ReplayStrokeCoreAsync(drawing.Stroke, schedule.Frequency, speed, sample =>
                {
                    token.ThrowIfCancellationRequested(); WorkLayer();
                    if (env.Feed.Brush?.Matches(brush) != true)
                        throw new ReplayStateException("BrushUnrestorable", "回放采样期间笔刷发生变化。");
                    if (colorRestored && (StrictColor || drawing.State.Color.ValueKind == System.Text.Json.JsonValueKind.Object) &&
                        !PacketReplayRunner.ColorMatches(env.Feed.Color, drawing.State.Color))
                        throw new ReplayStateException("ColorUnrestorable", "回放采样期间颜色发生变化。");
                    var current = env.CurrentView;
                    if (current is null || !ViewRestorer.Matches(current, drawing.Stroke.View) || current.Viewport is not { } region)
                        throw new ReplayStateException("ViewUnrestorable", "回放采样期间画布视图发生变化。");
                    env.EnsureCanvasPoint(sample, region);
                    return Task.CompletedTask;
                }, token, options, env.CreateInjector, env.Idle, status, gap);
                completed.Add(drawing.EditableKey);
            }
            return new(completed.Count, completed);
        }
        finally { env.ReleaseInputs(); }
    }
}
