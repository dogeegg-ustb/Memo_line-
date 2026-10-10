using StrokeReplay;

namespace PacketReplay;

/// <summary>StrokeReplay view restoration with explicit Memoline request/response feedback.
/// During replay every view observation is an active request; pushed updates are not used.</summary>
internal sealed class PacketViewFeed(LivePacketFeed feed) : ICanvasViewFeed
{
    // Pause after a view input so CSP has finished redrawing before the requested screenshot.
    internal static TimeSpan SettleDelay { get; set; } = TimeSpan.FromMilliseconds(150);
    private bool _inputPending;

    public bool RequestsActively => true;
    public CanvasViewSnapshot? Current => feed.RequestedView;
    public long Revision => feed.Revision;
    public void EnsureConnected() => feed.EnsureConnected();
    public void MarkInputStarted() { _inputPending = true; feed.View.MarkInputStarted(); }

    public async Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate,
        TimeSpan timeout, CancellationToken token)
    {
        if (_inputPending && SettleDelay > TimeSpan.Zero) await Task.Delay(SettleDelay, token);
        _inputPending = false;
        await feed.RequestAsync(["canvasViewState"], token);
        var view = Current ?? throw new InvalidOperationException("本次请求没有确认画布视图。");
        if (!predicate(view)) throw new InvalidOperationException("本次请求的画布视图未满足要求。");
        return view;
    }
}
