using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Config;
using BehaviorRecognizer.Storage.Memoline;
using DriverReader;
using System.Text.Json;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class PenActivationTests
{
    private sealed class TargetProbe : IInputTargetProbe
    {
        public CspWindowProbe.ScreenPoint Point = new() { X = 200, Y = 100 };
        public bool TryGetCspCursor(out CspWindowProbe.ScreenPoint point)
        { point = Point; return IsCspPoint(point.X, point.Y); }
        public bool IsCspPoint(int x, int y) => x >= 100 && x < 600 && y >= 0 && y < 500;
        public bool IsCspForeground() => true;
    }

    private static DriverMappingSession Session(double gamma = 1)
    {
        string path = Path.GetFullPath("pen-activation-driver.json");
        return DriverMappingSession.Initialize(new()
        {
            ConfigPath = path, CandidatePaths = [], Profiles = [new DriverProfile
            {
                Vendor = "test", DeviceName = "Tablet", ConfigPath = path, CurveSummary = "test",
                GammaCurve = gamma, CoordinateMapping = new()
                {
                    PhysicalArea = new(0, 0, 40000, 20000),
                    ScreenArea = new(-2000, 0, 0, 1000)
                }
            }]
        });
    }

    private static async Task<JsonElement[]> CaptureAsync(Action<UnifiedInputCapture, TargetProbe> record)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pen-activation-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            await using (var writer = new MemolineWriter(directory, new { }))
            {
                path = writer.FilePath;
                // Use one probe for both report processing and periodic cursor observation.
                var probe = new TargetProbe();
                await using var capture = new UnifiedInputCapture(writer, targetProbe: probe);
                record(capture, probe);
            }
            return MemolineReader.Read(path).Where(e => e.TryGetProperty("path", out var branch)
                && branch.GetString() == "hardware").ToArray();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void Report(UnifiedInputCapture capture, InputEventNormalizer normalizer,
        float pressure, float x = 10000, ulong sequence = 1)
    {
        foreach (var evt in normalizer.Normalize(new()
        {
            DeviceId = "dev", Timestamp = DateTimeOffset.UtcNow,
            X = x, Y = 5000, Pressure = pressure, MaxPressure = 1000
        }, "session", sequence)) capture.OnPen(evt);
    }

    [Fact]
    public async Task MappedPointOutsideCsp_DoesNotSuppressClickDragOrRelease()
    {
        var session = Session();
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault(), () => session);
        var frames = await CaptureAsync((capture, probe) =>
        {
            probe.Point = new() { X = 200, Y = 100 };
            Report(capture, normalizer, 100);
            capture.RefreshCursor();
            probe.Point = new() { X = 300, Y = 120 };
            Report(capture, normalizer, 200, x: 12000, sequence: 2);
            Report(capture, normalizer, 0, x: 12000, sequence: 3);
            Report(capture, normalizer, 100, x: 12000, sequence: 4);
            Report(capture, normalizer, 0, x: 12000, sequence: 5);
        });
        Assert.Equal(new[] { "penBegin", "penSample", "penEnd", "penBegin", "penEnd" },
            frames.Select(f => f.GetProperty("kind").GetString()));
        var start = frames[0].GetProperty("data");
        Assert.Equal(200, start.GetProperty("x").GetInt32());
        Assert.Equal(100, start.GetProperty("y").GetInt32());
        Assert.Equal(-1500, start.GetProperty("screenX").GetDouble(), 5);
        Assert.Equal("windowsCursor", start.GetProperty("interactionCoordinateSource").GetString());
        Assert.Equal("driverMapping", start.GetProperty("screenCoordinateSource").GetString());
        Assert.Equal(frames[0].GetProperty("operationId").GetUInt64(), frames[2].GetProperty("operationId").GetUInt64());
        Assert.NotEqual(frames[0].GetProperty("operationId").GetUInt64(), frames[3].GetProperty("operationId").GetUInt64());
    }

    [Fact]
    public async Task HoverNoiseOutsideCsp_DoesNotBlockNextPhysicalClick()
    {
        var session = Session(gamma: .1);
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault(), () => session);
        var frames = await CaptureAsync((capture, probe) =>
        {
            probe.Point = new() { X = -10, Y = -10 };
            Report(capture, normalizer, 1);
            probe.Point = new() { X = 200, Y = 100 };
            Report(capture, normalizer, 100, sequence: 2);
            Report(capture, normalizer, 0, sequence: 3);
        });
        Assert.Equal(new[] { "penBegin", "penEnd" }, frames.Select(f => f.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task ActualCursorOutsideCsp_DoesNotStartAnOperation()
    {
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault());
        var frames = await CaptureAsync((capture, probe) =>
        {
            probe.Point = new() { X = -10, Y = -10 };
            Report(capture, normalizer, 100);
            Report(capture, normalizer, 0, sequence: 2);
        });
        Assert.Empty(frames);
    }
}
