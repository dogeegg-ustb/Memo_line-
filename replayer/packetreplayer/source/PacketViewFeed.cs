using StrokeReplay;

namespace PacketReplay;

/// <summary>StrokeReplay view restoration with explicit Memoline request/response feedback.</summary>
internal sealed class PacketViewFeed(LivePacketFeed feed) : ICanvasViewFeed
{
    // How long to wait for the recognizer's own canvas-view refresh after a view input before
    // asking for one. A pushed view is only accepted when it was captured after the input.
    internal static TimeSpan PassiveWait { get; set; } = TimeSpan.FromSeconds(1.5);
    private long _passiveRevision = -1;

    public CanvasViewSnapshot? Current => feed.RequestedView;
    public long Revision => feed.Revision;
    public void EnsureConnected() => feed.EnsureConnected();
    public void MarkInputStarted()
    {
        _passiveRevision = feed.View.Revision;
        feed.View.MarkInputStarted();
    }

    public async Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate,
        TimeSpan timeout, CancellationToken token)
    {
        long passive = _passiveRevision; _passiveRevision = -1;
        if (passive >= 0 && PassiveWait > TimeSpan.Zero)
        {
            try { return await feed.View.WaitForViewAsync(passive, predicate, PassiveWait, token); }
            catch (TimeoutException) { /* no refresh in time: request one below */ }
            catch (IOException) { /* stream reconnecting: the active request is authoritative */ }
        }
        await feed.RequestAsync(["canvasViewState"], token);
        var view = Current ?? throw new InvalidOperationException("本次请求没有确认画布视图。");
        if (!predicate(view)) throw new InvalidOperationException("本次请求的画布视图未满足要求。");
        return view;
    }
}
