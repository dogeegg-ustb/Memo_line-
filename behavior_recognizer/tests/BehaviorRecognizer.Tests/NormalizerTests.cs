using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Config;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class NormalizerTests
{
    [Fact]
    public void PenDown_Then_Move_Then_Up()
    {
        var profile = PenProfileProvider.CreateHardcodedDefault();
        var normalizer = new InputEventNormalizer(profile);

        var down = normalizer.Normalize(Report(pressure: 1000, max: 8191), "s", 1).ToList();
        Assert.Contains(down, e => e.Type == InputEventType.PenDown);

        var move = normalizer.Normalize(Report(pressure: 2000, max: 8191), "s", 2).ToList();
        Assert.Contains(move, e => e.Type == InputEventType.PenMove);

        var up = normalizer.Normalize(Report(pressure: 0, max: 8191, near: true), "s", 3).ToList();
        Assert.Contains(up, e => e.Type == InputEventType.PenUp);
    }

    [Fact]
    public void OutOfRange_EmitsHover()
    {
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault());
        var events = normalizer.Normalize(new RawInputReport
        {
            DeviceId = "dev",
            Timestamp = DateTimeOffset.UtcNow,
            IsOutOfRange = true,
            MaxPressure = 8191
        }, "s", 1).ToList();

        Assert.Contains(events, e => e.Type == InputEventType.PenHover);
    }

    private static RawInputReport Report(float pressure, float max, bool near = false) => new()
    {
        DeviceId = "dev",
        Timestamp = DateTimeOffset.UtcNow,
        X = 10,
        Y = 20,
        Pressure = pressure,
        MaxPressure = max,
        IsNearProximity = near
    };
}
