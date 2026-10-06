using StrokeReplay;

namespace PacketReplay;

/// <summary>StrokeReplay view restoration with explicit Memoline request/response feedback.</summary>
internal sealed class PacketViewFeed(LivePacketFeed feed) : ICanvasViewFeed
{
    public CanvasViewSnapshot? Current => feed.RequestedView;
    public long Revision => feed.Revision;
    public void EnsureConnected() => feed.EnsureConnected();
    public void MarkInputStarted() => feed.View.MarkInputStarted();
    public async Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate,
        TimeSpan timeout, CancellationToken token)
    {
        await feed.RequestAsync(["canvasViewState"], token);
        var view = Current ?? throw new InvalidOperationException("本次请求没有确认画布视图。");
        if (!predicate(view)) throw new InvalidOperationException("本次请求的画布视图未满足要求。");
        return view;
    }
}
