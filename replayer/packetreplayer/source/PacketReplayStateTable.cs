using StrokeReplay;

namespace PacketReplay;

/// <summary>The recorded state table and the confirmed live table advance only on changes.</summary>
internal sealed class PacketReplayStateTable(IPacketStateFeed feed, IPacketInput input,
    ICanvasViewFeed viewFeed, IViewInput viewInput, Action<string> status, HistoricalState initial)
{
    private readonly PacketStateRestorer _states = new(feed, input, status);
    private readonly ViewRestorer _views = new(viewFeed, viewInput, status);
    internal HistoricalState Target { get; private set; } = initial;

    internal async Task RestoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); feed.EnsureConnected();
        input.EnsureTarget();
        if (Target.Layer is { } layer && J.Name(feed.Layer ?? "") != J.Name(layer)) await _states.RestoreLayerAsync(layer, token);
        if (Target.Brush is { } brush && feed.Brush?.Matches(brush) != true) await _states.RestoreBrushAsync(brush, token);
        await RestoreColorAsync(token);
        if (Target.View is { } view) await _views.RestoreAsync(view, token);
    }

    internal async Task ApplyAsync(PacketStep step, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); feed.EnsureConnected();
        Target = Target.Apply(step);
        switch (step.Kind)
        {
            case "brush": await _states.RestoreBrushAsync(Target.Brush!, token); break;
            case "layer": await _states.RestoreLayerAsync(Target.Layer!, token); break;
            case "color": await RestoreColorAsync(token); break;
            case "view": await _views.RestoreAsync(Target.View!, token); break;
        }
    }

    internal async Task PrepareStrokeAsync(PacketStroke drawing, CancellationToken token)
    {
        // Reconcile the resolved file table using cached live values. An unchanged
        // stroke makes no state request, even across separate "next stroke" runs.
        Target = drawing.State with { View = drawing.Stroke.View };
        await RestoreAsync(token);
        AssertMatches();
    }

    private async Task RestoreColorAsync(CancellationToken token)
    {
        if (PacketReplayRunner.ColorMatches(feed.Color, Target.Color)) return;
        await feed.RequestAsync(["colorState"], token);
        if (!PacketReplayRunner.ColorMatches(feed.Color, Target.Color))
            throw new InvalidOperationException("当前颜色与文件状态表不同，请在 CSP 中准备对应颜色再重试。");
    }

    internal void AssertMatches()
    {
        feed.EnsureConnected();
        if (Target.Brush is { } brush && feed.Brush?.Matches(brush) != true
            || Target.Layer is { } layer && J.Name(feed.Layer ?? "") != J.Name(layer)
            || !PacketReplayRunner.ColorMatches(feed.Color, Target.Color))
            throw new InvalidOperationException("当前笔刷、图层或颜色与文件状态表不一致，已停止。");
        if (Target.View is { } view && (viewFeed.Current is not { } current || !ViewRestorer.Matches(current, view)))
            throw new InvalidOperationException("当前画布视图与文件状态表不一致，已停止。");
    }
}
