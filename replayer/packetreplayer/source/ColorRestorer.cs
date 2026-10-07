using System.Text.Json;

namespace PacketReplay;

/// <summary>Until slider OCR and color freshness are verified, only confirmed equal colors are usable.</summary>
internal sealed class ColorRestorer(IPacketStateFeed feed)
{
    internal async Task RestoreAsync(JsonElement target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); feed.EnsureConnected();
        if (target.ValueKind != JsonValueKind.Object)
            throw new ReplayStateException("ColorUnrestorable", "历史颜色未确认，不能重放。");
        if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
        try { await feed.RequestAsync(["colorState"], token); }
        catch (InvalidOperationException ex) { throw new ReplayStateException("ColorUnrestorable", ex.Message, ex); }
        if (!PacketReplayRunner.ColorMatches(feed.Color, target))
            throw new ReplayStateException("ColorUnrestorable", "当前颜色与历史不同；色彩滑块写入和颜色过时状态尚未核实。");
    }
}
