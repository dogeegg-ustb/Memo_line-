using System.Drawing;
using System.Text.Json;

namespace PacketReplay;

internal sealed record RgbInputSnapshot(string CaptureId, string Slot, int[] Values, Rectangle[] Inputs)
{
    internal static RgbInputSnapshot? Parse(JsonElement data)
    {
        if (!J.Confirmed(data)) return null;
        var state = J.Get(data, "state"); var evidence = J.Get(data, "evidence");
        string? capture = J.Text(state, "captureFrameId"); string? slot = J.Text(state, "activeColorSlot");
        var roi = Box(J.Get(state, "panelRoi"));
        if (J.Text(state, "status") != "ok" || J.Text(state, "activeTab") != "rgb" ||
            slot is not ("main" or "sub") || string.IsNullOrWhiteSpace(capture) || capture != J.Text(evidence, "captureId") ||
            roi is null || Box(J.Get(evidence, "panelRoi")) != roi ||
            J.I64(evidence, "capturedTicks", -1) < 0 || J.I64(evidence, "captureEndTicks", -1) < J.I64(evidence, "capturedTicks", -1)) return null;
        var regions = J.Array(J.Get(state, "valueRegions")).ToArray();
        if (regions.Length != 3) return null;
        var values = new int[3]; var inputs = new Rectangle[3];
        for (int i = 0; i < 3; i++)
        {
            var candidates = regions.Where(r => J.Text(r, "channel") == "rgb"[i].ToString()).ToArray();
            if (candidates.Length != 1) return null;
            var region = candidates[0]; var value = J.Get(region, "value"); var box = Box(J.Get(region, "screenBbox"));
            if (J.Text(region, "status") != "ok" || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out values[i]) || values[i] is < 0 or > 255 ||
                box is null || !roi.Value.Contains(box.Value)) return null;
            inputs[i] = box.Value;
        }
        if (inputs.Distinct().Count() != 3 || inputs[0].IntersectsWith(inputs[1]) || inputs[1].IntersectsWith(inputs[2]) || inputs[0].IntersectsWith(inputs[2])) return null;
        return new(capture, slot!, values, inputs);
    }
    private static Rectangle? Box(JsonElement raw)
    {
        var parts = J.Array(raw).ToArray();
        if (parts.Length != 4 || parts.Any(p => p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out _))) return null;
        int[] v = parts.Select(p => p.GetInt32()).ToArray();
        return v[2] > 0 && v[3] > 0 ? new(v[0], v[1], v[2], v[3]) : null;
    }
}

/// <summary>Writes fresh RGB OCR targets and confirms each committed channel before drawing.</summary>
internal sealed class ColorRestorer(IPacketStateFeed feed, IPacketInput? input = null)
{
    internal const string PanelMessage = "请暂停作画，将原颜色面板切换到色彩滑块的 RGB 页，并选中主色，再重试；记艺线会自动输入目标 RGB。";
    internal async Task RestoreAsync(JsonElement target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); feed.EnsureConnected();
        if (target.ValueKind != JsonValueKind.Object)
            throw Failure("历史颜色未确认，不能重放。");
        if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
        try
        {
            try { await feed.RequestAsync(["colorState"], token); }
            catch (InvalidOperationException) { /* wheel is hidden by the RGB page */ }
            if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
            var rgb = J.Array(J.Get(target, "rgb")).ToArray();
            if (J.Text(target, "kind") != "rgb" || rgb.Length != 3 || rgb.Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n is < 0 or > 255))
                throw Failure("历史颜色不能表示为三个 0–255 的 RGB 数值。");
            await feed.RequestAsync(["colorInputState"], token);
            var snapshot = RgbInputSnapshot.Parse(feed.ColorInput) ?? throw Failure(PanelMessage);
            if (snapshot.Slot != "main" || input is null) throw Failure(PanelMessage);
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested(); feed.EnsureConnected(); input.EnsureTarget();
                int wanted = rgb[i].GetInt32();
                if (snapshot.Values[i] == wanted) continue;
                string previousCapture = snapshot.CaptureId;
                int[] expected = snapshot.Values.ToArray(); expected[i] = wanted;
                await input.NumberAsync(snapshot.Inputs[i], wanted, token);
                await feed.RequestAsync(["colorInputState"], token);
                snapshot = RgbInputSnapshot.Parse(feed.ColorInput) ?? throw Failure("RGB 写入后未取得可确认的新截图。" + PanelMessage);
                if (snapshot.CaptureId == previousCapture || snapshot.Slot != "main" || !snapshot.Values.SequenceEqual(expected))
                    throw Failure("RGB 写入后的新截图未确认通道数值，已停止回放。");
            }
            await feed.RequestAsync(["colorState"], token);
            if (!PacketReplayRunner.ColorMatches(feed.Color, target)) throw Failure("RGB 数值已输入，但当前颜色读回不符，已停止回放。");
        }
        catch (ReplayStateException) { throw; }
        catch (InvalidOperationException ex) { throw Failure(ex.Message, ex); }
    }
    private static ReplayStateException Failure(string message, Exception? inner = null) => new("ColorUnrestorable", message, inner);
}
