using System.Text.Json;

namespace PacketReplay;

/// <summary>
/// Sets CSP's current colour with its own screen colour picker: Memoline shows the colour on the virtual screen, parks
/// the cursor on it and presses the configured "obtain screen colour" shortcut. Nothing depends on which colour palette
/// is open; a fresh colorState reading confirms the result before anything is drawn.
/// </summary>
internal sealed class ColorRestorer(IPacketStateFeed feed, IPacketInput? input = null)
{
    internal const string PickerCommand = "applicationscreeneyecolorpicker";
    internal const string TransparentCommand = "applicationchangecolortransparent";
    internal const string PickerMessage = "请在 CSP 的快捷键设置里给「获取屏幕颜色」（applicationscreeneyecolorpicker）配置快捷键并保存；" +
        "记艺线用它吸取虚拟屏上的色块来切换颜色。";
    // How long the cursor stays on the swatch after CSP handled the shortcut. A pick that reads back wrong is repeated
    // once with a longer stay, in case CSP takes the pixel later than it handles the key.
    internal static readonly TimeSpan[] Dwells = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)];

    internal async Task RestoreAsync(JsonElement target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); feed.EnsureConnected();
        if (target.ValueKind != JsonValueKind.Object)
            throw Failure("历史颜色未确认，不能重放。");
        if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
        try
        {
            try { await feed.RequestAsync(["colorState"], token); }
            catch (InvalidOperationException) { /* no confirmed reading now; the one after the pick decides */ }
            if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
            if (input is null) throw Failure("没有可用的输入方式来切换颜色。");
            var shortcuts = feed.Shortcuts ?? throw Failure("尚未取得 CSP 快捷键配置。");
            if (J.Text(target, "kind") == "transparent")
            {
                input.EnsureTarget();
                await input.ShortcutAsync(Binding(shortcuts, TransparentCommand, "CSP 没有给「切换到透明色」配置快捷键。"), token);
                await feed.RequestAsync(["colorState"], token);
                if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
                throw Failure($"已按「切换到透明色」，但读回的当前颜色是 {Describe(feed.Color)}。");
            }
            var rgb = Rgb(target) ?? throw Failure("历史颜色不能表示为三个 0–255 的 RGB 数值。");
            var picker = Binding(shortcuts, PickerCommand, PickerMessage);
            foreach (var dwell in Dwells)
            {
                token.ThrowIfCancellationRequested(); feed.EnsureConnected(); input.EnsureTarget();
                await input.PickColorAsync(rgb, picker, dwell, token);
                await feed.RequestAsync(["colorState"], token);
                if (PacketReplayRunner.ColorMatches(feed.Color, target)) return;
            }
            throw Failure($"已用「获取屏幕颜色」吸取色块 {Hex(rgb)}，但读回的当前颜色是 {Describe(feed.Color)}。");
        }
        catch (ReplayStateException) { throw; }
        catch (InvalidOperationException ex) { throw Failure(ex.Message, ex); }
    }

    /// <summary>Why <see cref="PickerCommand"/> cannot be sent with this configuration, or null when it can.</summary>
    internal static string? MissingPicker(ShortcutConfiguration? shortcuts)
    {
        if (shortcuts is null) return "尚未取得 CSP 快捷键配置。";
        try { shortcuts.ForCommand(PickerCommand); return null; }
        catch (InvalidOperationException) { return PickerMessage; }
    }

    /// <summary>The colorState shape of an RGB colour, as the recorder reports the colour slider's RGB page.</summary>
    internal static JsonElement State(IReadOnlyList<int> rgb) => JsonSerializer.SerializeToElement(new { kind = "rgb", rgb });

    internal static int[]? Rgb(JsonElement color)
    {
        if (J.Text(color, "kind") is not ("rgb" or "color")) return null;
        var values = J.Array(J.Get(color, "rgb")).Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : -1).ToArray();
        return values.Length == 3 && values.All(v => v is >= 0 and <= 255) ? values : null;
    }

    private static ShortcutBinding Binding(ShortcutConfiguration shortcuts, string command, string missing)
    {
        try { return shortcuts.ForCommand(command); }
        catch (InvalidOperationException) { throw Failure(missing); }
    }

    private static string Hex(IReadOnlyList<int> rgb) => $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";

    private static string Describe(JsonElement color) => J.Text(color, "kind") switch
    {
        "transparent" => "透明色",
        "rgb" or "color" when Rgb(color) is { } rgb => Hex(rgb),
        _ => "无法读取（请在 CSP 里打开色轮或色彩滑块面板）",
    };

    private static ReplayStateException Failure(string message, Exception? inner = null) => new("ColorUnrestorable", message, inner);
}
