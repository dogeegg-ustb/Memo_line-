using System.Drawing;

namespace StrokeReplay;

internal interface ICanvasViewFeed
{
    CanvasViewSnapshot? Current { get; }
    long Revision { get; }
    void EnsureConnected();
    void MarkInputStarted();
    Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate,
        TimeSpan timeout, CancellationToken token);
}

internal interface IViewInput
{
    void EnsureTarget();
    Task SetNumberAsync(Rectangle input, double value, CancellationToken token);
    Task PanAsync(Point from, Point to, CancellationToken token);
}

/// <summary>Uses the available confirmed target view immediately; otherwise restores with feedback.</summary>
internal sealed class ViewRestorer(ICanvasViewFeed feed, IViewInput input, Action<string> status)
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(20);
    internal static bool SameScale(CanvasViewSnapshot a, CanvasViewSnapshot b) => Math.Abs(a.ScalePercent - b.ScalePercent) <= .05;
    internal static bool SameRotation(CanvasViewSnapshot a, CanvasViewSnapshot b) =>
        Math.Abs(Math.IEEERemainder(a.RotationDegrees - b.RotationDegrees, 360)) <= .05;
    internal static bool SameOrigin(CanvasViewSnapshot a, CanvasViewSnapshot b) =>
        Math.Abs(a.OriginX - b.OriginX) <= 2 && Math.Abs(a.OriginY - b.OriginY) <= 2;
    internal static bool Matches(CanvasViewSnapshot a, CanvasViewSnapshot b) => SameScale(a, b) && SameRotation(a, b) && SameOrigin(a, b);

    internal async Task RestoreAsync(CanvasViewSnapshot target, CancellationToken token)
    {
        input.EnsureTarget();
        var current = feed.Current ?? await feed.WaitForViewAsync(-1, _ => true, ObservationTimeout, token);
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            feed.EnsureConnected();
            input.EnsureTarget();
            if (target.PixelWidth is { } width && current.PixelWidth is { } currentWidth && width != currentWidth ||
                target.PixelHeight is { } height && current.PixelHeight is { } currentHeight && height != currentHeight)
                throw new InvalidOperationException("当前画布尺寸与录制画布不同，无法恢复视图。");
            if (Matches(current, target)) break;
            if (attempt >= 128) throw new InvalidOperationException("画布视图恢复未收敛，已停止回放。");
            long revision = feed.Revision;
            feed.MarkInputStarted();
            if (!SameScale(current, target))
            {
                var roi = current.ZoomInput ?? throw new InvalidOperationException("Recognizer 未提供当前缩放数字的精确屏幕位置，无法修正缩放；请使用提供数字位置接口的版本并重新初始化。");
                status($"恢复缩放：{target.ScalePercent:0.###}%");
                await input.SetNumberAsync(roi, target.ScalePercent, token);
            }
            else if (!SameRotation(current, target))
            {
                var roi = current.RotationInput ?? throw new InvalidOperationException("Recognizer 未提供当前旋转数字的精确屏幕位置，无法修正旋转；请使用提供数字位置接口的版本并重新初始化。");
                status($"恢复旋转：{target.RotationDegrees:0.###}°");
                await input.SetNumberAsync(roi, target.RotationDegrees, token);
            }
            else
            {
                var viewport = current.Viewport ?? throw new InvalidOperationException("Recognizer 未提供当前画布视口区域。");
                if (viewport.Width < 40 || viewport.Height < 40) throw new InvalidOperationException("当前画布视口过小，无法平移。");
                var deltaX = (int)Math.Round(Math.Clamp(target.OriginX - current.OriginX, -viewport.Width * .4, viewport.Width * .4));
                var deltaY = (int)Math.Round(Math.Clamp(target.OriginY - current.OriginY, -viewport.Height * .4, viewport.Height * .4));
                var from = new Point(viewport.Left + viewport.Width / 2 - deltaX / 2,
                    viewport.Top + viewport.Height / 2 - deltaY / 2);
                var to = new Point(from.X + deltaX, from.Y + deltaY);
                status($"恢复画布位置：平移 {deltaX}, {deltaY} 像素");
                await input.PanAsync(from, to, token);
            }
            // Recognition can finish during input delivery. Do not discard a confirmed
            // full target match just to require another update with the same values.
            if (feed.Current is { } available && Matches(available, target))
            {
                current = available;
                continue;
            }
            // Intermediate unknown results are ignored by the feed. Replan from the
            // next confirmed observation, even if the previous action did not fully converge.
            current = await feed.WaitForViewAsync(revision, _ => true, ObservationTimeout, token);
        }
        feed.EnsureConnected();
        input.EnsureTarget();
        if (!Matches(current, target)) throw new InvalidOperationException("画布视图未恢复到目标状态。");
    }
}
