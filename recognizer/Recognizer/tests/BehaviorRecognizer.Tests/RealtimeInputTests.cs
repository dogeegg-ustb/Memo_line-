using System.Numerics;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class RealtimeInputTests
{
    private sealed class Probe : IInputTargetProbe
    {
        public bool Foreground = true;
        public CspWindowProbe.ScreenPoint Point = new() { X = 200, Y = 100 };
        public bool TryGetCspCursor(out CspWindowProbe.ScreenPoint p) { p = Point; return IsCspPoint(p.X, p.Y); }
        public bool IsCspPoint(int x, int y) => x >= 100 && x < 600 && y >= 0 && y < 500;
        public bool IsCspForeground() => Foreground;
    }

    private static async Task<JsonElement[]> CaptureAsync(Action<UnifiedInputCapture, Probe> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-input-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            await using (var writer = new MemolineWriter(directory, new { }))
            {
                path = writer.FilePath;
                var probe = new Probe();
                await using var capture = new UnifiedInputCapture(writer, targetProbe: probe);
                action(capture, probe);
            }
            return MemolineReader.Read(path).Where(e => e.TryGetProperty("appendId", out _)).ToArray();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task KeyboardPublishesModifiersRepeatsReleasesAndFocusReset()
    {
        var frames = await CaptureAsync((c, p) =>
        {
            c.OnKey(0xA2, 29, false, true);
            c.OnKey(65, 30, false, true, injected: true, extraInfo: 42);
            c.OnKey(65, 30, false, true);
            c.OnKey(65, 30, false, false);
            p.Foreground = false;
            c.RefreshCursor();
        });
        var changes = frames.Where(e => e.GetProperty("kind").GetString() == "keyboardStateChanged").Select(e => e.GetProperty("data")).ToArray();
        Assert.Equal(new[] { "keyDown", "keyDown", "keyDown", "keyUp", "reset" }, changes.Select(d => d.GetProperty("action").GetString()));
        Assert.True(changes[0].GetProperty("isModifier").GetBoolean());
        Assert.Equal(new[] { 65, 0xA2 }, changes[1].GetProperty("heldKeys").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("Ctrl", changes[1].GetProperty("modifiers")[0].GetString());
        Assert.True(changes[1].GetProperty("injected").GetBoolean());
        Assert.Equal("0x2A", changes[1].GetProperty("extraInfo").GetString());
        Assert.True(changes[2].GetProperty("repeat").GetBoolean());
        Assert.False(changes[3].GetProperty("pressed").GetBoolean());
        Assert.Empty(changes[4].GetProperty("heldKeys").EnumerateArray());
        // New transition frames do not duplicate the legacy hardware activation events.
        Assert.Equal(2, frames.Count(e => e.GetProperty("kind").GetString() == "keyInput"));
    }

    [Fact]
    public async Task MousePublishesButtonSetWheelAndInterruption()
    {
        var frames = await CaptureAsync((c, p) =>
        {
            c.OnMouse(0x0201, 200, 100, 0);
            c.OnMouse(0x0204, 200, 100, 0);
            c.OnMouse(0x0200, 220, 100, 0);
            c.OnMouse(0x020A, 220, 100, 120 << 16);
            c.OnMouse(0x0202, 220, 100, 0);
            p.Point = new() { X = 0, Y = 0 };
            c.RefreshCursor();
        });
        var drag = frames.First(e => e.GetProperty("kind").GetString() == "mouseDrag").GetProperty("data");
        Assert.Equal(new[] { "left", "right" }, drag.GetProperty("heldButtons").EnumerateArray().Select(e => e.GetString()));
        var wheel = frames.Single(e => e.GetProperty("kind").GetString() == "mouseWheel").GetProperty("data");
        Assert.Equal(120, wheel.GetProperty("delta").GetInt32());
        var up = frames.Single(e => e.GetProperty("kind").GetString() == "mouseUp").GetProperty("data");
        Assert.Equal("right", up.GetProperty("heldButtons")[0].GetString());
        Assert.Empty(frames.Single(e => e.GetProperty("kind").GetString() == "mouseInterrupted").GetProperty("data").GetProperty("heldButtons").EnumerateArray());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public async Task MouseHookRoutesPhysicalAndInjectedGesturesToActivationEvents(uint flags)
    {
        var frames = await CaptureAsync((capture, _) =>
        {
            var router = new WindowsMouseInputRouter(capture, passivePen: false);
            capture.PostKey(32, 57, false, true, injected: true);
            router.Observe(0x0201, 200, 100, 0, flags, 42);
            router.Observe(0x0200, 220, 100, 0, flags, 42);
            router.Observe(0x0202, 220, 100, 0, flags, 42);
            router.Observe(0x020A, 220, 100, 120u << 16, flags, 42);
            router.Observe(0x020E, 220, 100, 120u << 16, flags, 42);
            capture.PostKey(32, 57, false, false, injected: true);
        });
        var hardware = frames.Where(e => e.GetProperty("path").GetString() == "hardware").ToArray();
        var down = Assert.Single(hardware, e => e.GetProperty("kind").GetString() == "mouseDown");
        var drag = Assert.Single(hardware, e => e.GetProperty("kind").GetString() == "mouseDrag");
        var up = Assert.Single(hardware, e => e.GetProperty("kind").GetString() == "mouseUp");
        Assert.Equal(down.GetProperty("eventId").GetUInt64(), drag.GetProperty("operationId").GetUInt64());
        Assert.Equal(down.GetProperty("eventId").GetUInt64(), up.GetProperty("operationId").GetUInt64());
        Assert.Contains(32, down.GetProperty("data").GetProperty("heldKeys").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(2, hardware.Count(e => e.GetProperty("kind").GetString() == "mouseWheel"));
        Assert.Contains(hardware, e => e.GetProperty("kind").GetString() == "keyInput" &&
            e.GetProperty("data").GetProperty("injected").GetBoolean());
    }

    [Fact]
    public async Task RecorderReplayAndTouchDoNotCreateDuplicateMouseActivation()
    {
        var frames = await CaptureAsync((capture, _) =>
        {
            var router = new WindowsMouseInputRouter(capture, passivePen: false);
            foreach (nuint tag in new[] { WindowsInputHooks.RecorderInputTag, (nuint)0xFF515780, (nuint)0xFF515700 })
            {
                router.Observe(0x0201, 200, 100, 0, 1, tag);
                router.Observe(0x0200, 220, 100, 0, 1, tag);
                router.Observe(0x0202, 220, 100, 0, 1, tag);
            }
        });
        Assert.DoesNotContain(frames, e => e.GetProperty("path").GetString() == "hardware");
    }

    [Fact]
    public async Task TabletPublishesHoverButtonsAndOutOfRangeWithoutFakeStrokes()
    {
        var frames = await CaptureAsync((c, p) =>
        {
            InputEvent Report(InputEventType type, ContactState state, bool button = false) => new()
            {
                Type = type, ContactState = state, Timestamp = DateTimeOffset.UtcNow, SessionId = "test", DeviceId = "tablet1",
                Sequence = 1, Position = new Vector2(1000, 2000), Pressure = 0, Tilt = new Vector2(2, 3), PenButtons = [button]
            };
            c.OnPen(Report(InputEventType.PenHover, ContactState.Hover));
            c.OnPen(Report(InputEventType.PenHover, ContactState.Hover));
            c.OnPen(Report(InputEventType.PenButtonChanged, ContactState.Hover, true));
            c.OnPen(Report(InputEventType.PenHover, ContactState.OutOfRange));
        });
        var states = frames.Where(e => e.GetProperty("kind").GetString() == "tabletStateChanged").Select(e => e.GetProperty("data")).ToArray();
        Assert.Equal(new[] { "hover", "buttons", "outOfRange" }, states.Select(e => e.GetProperty("action").GetString()));
        var sample = states[1].GetProperty("sample");
        Assert.Equal(1000, sample.GetProperty("tabletX").GetSingle());
        Assert.Equal(200, sample.GetProperty("x").GetInt32());
        Assert.Equal("tablet1", sample.GetProperty("deviceId").GetString());
        Assert.True(sample.GetProperty("penButtons")[0].GetBoolean());
        Assert.DoesNotContain(frames, e => e.GetProperty("path").GetString() == "hardware");
    }
}
