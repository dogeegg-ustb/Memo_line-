using System.Drawing;
using System.Text.Json;
using PacketReplay;

internal static partial class PacketChecks
{
    private sealed class PickFeed : IPacketStateFeed
    {
        internal int[] Values = [0, 0, 0]; internal string Kind = "rgb"; internal bool Unreadable;
        internal readonly List<string[]> Requests = [];
        public BrushSnapshot? Brush => null; public string? Layer => null; public JsonElement Layers => default; public JsonElement LayersEvidence => default;
        public SubtoolSnapshot? Subtools => null; public long Revision => Requests.Count;
        public ShortcutConfiguration? Shortcuts { get; set; } = new([new("menu_1", "获取屏幕颜色", ColorRestorer.PickerCommand, "Ctrl + Shift + I"),
            new("menu_2", "切换到透明色", ColorRestorer.TransparentCommand, "C")]);
        public JsonElement Color { get; private set; } = Json(new { kind = "rgb", rgb = new[] { 0, 0, 0 } });
        public void EnsureConnected() { }
        public Task RequestAsync(string[] modules, CancellationToken token, string? saveId = null)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(modules);
            if (modules.Contains("colorState"))
            {
                if (Unreadable) throw new InvalidOperationException("本次主动请求未确认颜色：colorWheelNotVisible");
                Color = Kind == "transparent" ? Json(new { kind = "transparent" }) : Json(new { kind = Kind, rgb = Values });
            }
            return Task.CompletedTask;
        }
    }
    private sealed class PickInput(PickFeed feed) : IPacketInput
    {
        internal readonly List<(int[] Rgb, string Shortcut, TimeSpan Dwell)> Picks = [];
        internal readonly List<string> Pressed = [];
        internal int Missed; // picks CSP takes no colour from
        public void EnsureTarget() { }
        public Task PickColorAsync(IReadOnlyList<int> rgb, ShortcutBinding picker, TimeSpan dwell, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Picks.Add((rgb.ToArray(), picker.Shortcut, dwell));
            // Read back from the colour wheel's swatch, which the recorder reports as kind "color".
            if (Picks.Count > Missed) { feed.Values = rgb.ToArray(); feed.Kind = "color"; }
            return Task.CompletedTask;
        }
        public Task ShortcutAsync(ShortcutBinding binding, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Pressed.Add(binding.Shortcut);
            if (binding.Command == ColorRestorer.TransparentCommand) feed.Kind = "transparent";
            return Task.CompletedTask;
        }
        public Task NumberAsync(Rectangle area, double value, CancellationToken token) => throw new Exception("unexpected number");
        public Task ClickAsync(Rectangle r, CancellationToken t) => throw new Exception("unexpected click");
        public Task ScrollAsync(Rectangle r, int d, CancellationToken t) => throw new Exception("unexpected scroll");
    }
    internal static async Task ColorPickAsync()
    {
        var target = Json(new { kind = "rgb", rgb = new[] { 12, 34, 56 } });
        var feed = new PickFeed(); var input = new PickInput(feed);
        await new ColorRestorer(feed, input).RestoreAsync(target, default);
        Check(input.Picks.Count == 1 && input.Picks[0].Rgb.SequenceEqual(new[] { 12, 34, 56 }) && input.Picks[0].Shortcut == "Ctrl + Shift + I"
            && input.Picks[0].Dwell == ColorRestorer.Dwells[0], "The swatch shows the target colour and the configured screen picker takes it.");
        Check(feed.Requests.Count == 2 && feed.Requests.All(r => r.SequenceEqual(new[] { "colorState" })),
            "One fresh reading before the pick and one confirming it.");
        Check(PacketReplayRunner.ColorMatches(feed.Color, target), "A colour wheel reading (kind color) confirms an RGB target of the same value.");
        feed.Requests.Clear(); await new ColorRestorer(feed, input).RestoreAsync(target, default);
        Check(input.Picks.Count == 1 && feed.Requests.Count == 0, "An already confirmed colour needs neither a pick nor a reading.");

        feed = new PickFeed(); input = new PickInput(feed) { Missed = 1 };
        await new ColorRestorer(feed, input).RestoreAsync(target, default);
        Check(input.Picks.Select(p => p.Dwell).SequenceEqual(ColorRestorer.Dwells), "A pick that reads back wrong is repeated once with a longer stay.");

        feed = new PickFeed(); input = new PickInput(feed) { Missed = 9 };
        try { await new ColorRestorer(feed, input).RestoreAsync(target, default); throw new Exception("Expected a colour failure"); }
        catch (ReplayStateException ex)
        { Check(ex.Code == "ColorUnrestorable" && ex.Message.Contains("#0C2238") && ex.Message.Contains("#000000"), "The failure names the target and the colour read back."); }
        Check(input.Picks.Count == ColorRestorer.Dwells.Length, "Picking stops after the longer retry.");

        feed = new PickFeed { Shortcuts = new([new("menu_2", "切换到透明色", ColorRestorer.TransparentCommand, "C")]) }; input = new(feed);
        try { await new ColorRestorer(feed, input).RestoreAsync(target, default); throw new Exception("Expected a missing shortcut"); }
        catch (ReplayStateException ex) { Check(ex.Message == ColorRestorer.PickerMessage, "Without the picker shortcut the user is told which command to bind."); }
        Check(input.Picks.Count == 0 && ColorRestorer.MissingPicker(feed.Shortcuts) == ColorRestorer.PickerMessage
            && ColorRestorer.MissingPicker(new PickFeed().Shortcuts) is null, "Nothing is picked without a configured shortcut.");

        feed = new PickFeed { Unreadable = true }; input = new(feed);
        try { await new ColorRestorer(feed, input).RestoreAsync(target, default); throw new Exception("Expected an unconfirmed pick"); }
        catch (ReplayStateException ex) { Check(ex.Code == "ColorUnrestorable" && ex.Message.Contains("未确认颜色"), "A pick no colour reading confirms stops the replay."); }
        Check(input.Picks.Count == 1, "An unreadable colour panel before the pick does not prevent it; the one after decides.");

        feed = new PickFeed(); input = new(feed);
        await new ColorRestorer(feed, input).RestoreAsync(Json(new { kind = "transparent" }), default);
        Check(input.Pressed.SequenceEqual(new[] { "C" }) && input.Picks.Count == 0, "A transparent target uses CSP's transparent colour shortcut.");

        feed = new PickFeed(); input = new(feed);
        await RejectAsync<ReplayStateException>(() => new ColorRestorer(feed, input).RestoreAsync(Json(new { kind = "rgb", rgb = new[] { 1, 2 } }), default));
        await RejectAsync<ReplayStateException>(() => new ColorRestorer(feed, input).RestoreAsync(default, default));
        Check(input.Picks.Count == 0, "Malformed or unconfirmed targets are never picked.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await RejectAsync<OperationCanceledException>(() => new ColorRestorer(feed, input).RestoreAsync(target, cancel.Token));
        Check(input.Picks.Count == 0, "A cancelled colour restore never injects input.");

        Check(PacketReplayRunner.ColorMatches(Json(new { kind = "color", rgb = new[] { 1, 2, 3 } }), Json(new { kind = "rgb", rgb = new[] { 1, 2, 3 } }))
            && !PacketReplayRunner.ColorMatches(Json(new { kind = "color", rgb = new[] { 1, 2, 4 } }), Json(new { kind = "rgb", rgb = new[] { 1, 2, 3 } }))
            && !PacketReplayRunner.ColorMatches(Json(new { kind = "transparent" }), Json(new { kind = "rgb", rgb = new[] { 1, 2, 3 } })),
            "Wheel and slider readings compare by value; transparency is its own kind.");
    }
}
