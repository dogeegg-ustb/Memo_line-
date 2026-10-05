using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Config;
using DriverReader;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class DriverMappingTests
{
    private static readonly string ConfigPath = Path.GetFullPath("test-driver.json");
    private static readonly TabletDeviceInfo Device = new("dev", "Tablet", 200, 100, 40000, 20000, 1000);
    private static readonly DisplayInfo Display = new(0, "primary", true, new(0, 0, 2000, 1000));

    private static DriverProfile Profile(CoordinateMapping? mapping = null, double? gamma = null,
        double threshold = 0, CubicBezierCurve? curve = null) => new()
    {
        Vendor = "test", DeviceName = "Tablet", ConfigPath = ConfigPath, CurveSummary = "test",
        RecommendedPressureMax = 1000, CoordinateMapping = mapping, GammaCurve = gamma,
        ThresholdRatio = threshold, BezierCurve = curve
    };

    private static CoordinateMapping Mapping(string unit = "Counts", int rotation = 0) => new()
    {
        PhysicalUnit = unit, PhysicalArea = unit == "mm" ? new(0, 0, 200, 100) : new(0, 0, 40000, 20000),
        ScreenArea = new(-2000, 100, 0, 1100), RotationDegrees = rotation
    };

    private static DriverMappingSession Session(DriverProfile profile,
        IReadOnlyList<DisplayInfo>? displays = null, int? screenIndex = null) => DriverMappingSession.Initialize(new()
    {
        ConfigPath = ConfigPath, Profiles = [profile], CandidatePaths = [], Device = Device,
        Displays = displays ?? [Display], ScreenIndexOverride = screenIndex
    });

    [Fact]
    public void Discovery_DoesNotApplyEvenOneProfileWithoutUserSelection()
    {
        var session = DriverMappingSession.Initialize(new() { Profiles = [Profile(Mapping())], CandidatePaths = [] });
        Assert.Equal("selectionRequired", session.Snapshot.Status);
        Assert.Null(session.Snapshot.SelectedProfile);
        Assert.Null(session.Evaluate("dev", 1, 1, 100, 1000).NormalizedPressure);
        Assert.Single(session.Snapshot.DiscoveredProfiles);
    }

    [Fact]
    public void ExplicitSelection_UsesRequestedProfileRatherThanFirstCandidate()
    {
        var other = Profile(gamma: 2);
        var chosen = new DriverProfile { Vendor = "chosen", DeviceName = "Tablet", ConfigPath = ConfigPath + ".dt", CurveSummary = "linear" };
        var session = DriverMappingSession.Initialize(new() { ConfigPath = chosen.ConfigPath, Profiles = [other, chosen], CandidatePaths = [] });
        Assert.Equal("chosen", session.Snapshot.SelectedProfile!.Value.GetProperty("Vendor").GetString());
    }

    [Fact]
    public void Pressure_UsesActualHardwareMaximumAndDriverThreshold()
    {
        var session = Session(Profile(gamma: 2, threshold: .1));
        Assert.Equal(0, session.Evaluate("dev", 0, 0, 50, 1000).MappedPressure);
        var mapped = session.Evaluate("dev", 0, 0, 550, 1000);
        Assert.Equal(.25, mapped.NormalizedPressure!.Value, 8);
        Assert.Equal(250, mapped.MappedPressure!.Value, 8);
    }

    [Fact]
    public void Bezier_HandlesNormalizedControlPointDomainAndRange()
    {
        var curve = new CubicBezierCurve((.1, .2), (.3, .4), (.5, .6), (.7, .8));
        var session = Session(Profile(curve: curve));
        Assert.Equal(.5, session.Evaluate("dev", 0, 0, 500, 1000).NormalizedPressure!.Value, 5);
        Assert.Equal(0, session.Evaluate("dev", 0, 0, 0, 1000).MappedPressure);
    }

    [Theory]
    [InlineData(0, -1500, 350)]
    [InlineData(90, -500, 350)]
    [InlineData(180, -500, 850)]
    [InlineData(270, -1500, 850)]
    public void Counts_MapRotationAndNegativeDesktopOrigin(int rotation, double expectedX, double expectedY)
    {
        var session = Session(Profile(Mapping(rotation: rotation)));
        var result = session.Evaluate("dev", 10000, 5000, 500, 1000);
        Assert.Equal(expectedX, result.ScreenX!.Value, 5);
        Assert.Equal(expectedY, result.ScreenY!.Value, 5);
        var inverse = session.Snapshot.ScreenToPhysical!.Apply(result.ScreenX.Value, result.ScreenY.Value);
        Assert.Equal(10000, inverse.X, 5);
        Assert.Equal(5000, inverse.Y, 5);
    }

    [Fact]
    public void MillimeterProfile_ConvertsRawDigitizerCountsBeforeMapping()
    {
        var session = Session(Profile(Mapping("mm")));
        var result = session.Evaluate("dev", 10000, 5000, 500, 1000);
        Assert.Equal(50, result.PhysicalX);
        Assert.Equal(25, result.PhysicalY);
        Assert.Equal(-1500, result.ScreenX!.Value, 5);
        Assert.Equal(350, result.ScreenY!.Value, 5);
    }

    [Fact]
    public void ActiveAreaClampsOutsidePointsWithoutChangingRawPhysicalPosition()
    {
        var result = Session(Profile(Mapping())).Evaluate("dev", 80000, -50, 500, 1000);
        Assert.Equal(80000, result.PhysicalX);
        Assert.Equal(0, result.ScreenX!.Value, 5);
        Assert.Equal(100, result.ScreenY!.Value, 5);
    }

    [Fact]
    public void MultipleMonitors_RequireSelectionForVendorRelativeScreenBounds()
    {
        var mapping = Mapping(); mapping.ScreenArea = null; mapping.ScreenMapRatio = new(0, 0, 1, 1);
        DisplayInfo[] displays = [Display, new(1, "left", false, new(-1600, -200, 0, 700))];
        var unresolved = Session(Profile(mapping), displays);
        Assert.Equal("monitorSelectionRequired", unresolved.Snapshot.CoordinateMappingStatus);
        var selected = Session(Profile(mapping), displays, screenIndex: 1);
        var point = selected.Evaluate("dev", 20000, 10000, 500, 1000);
        Assert.Equal(-800, point.ScreenX!.Value, 5);
        Assert.Equal(250, point.ScreenY!.Value, 5);
    }

    [Fact]
    public void RelativeMode_KeepsPressureButDoesNotInventAbsolutePosition()
    {
        var mapping = Mapping(); mapping.MappingMode = "Relative";
        var result = Session(Profile(mapping)).Evaluate("dev", 10000, 5000, 500, 1000);
        Assert.Equal("relativeMode", result.CoordinateStatus);
        Assert.Null(result.ScreenX);
        Assert.Equal(.5, result.NormalizedPressure);
    }

    [Fact]
    public void MappingForOneDevice_IsNotAppliedToAnotherDevice()
    {
        var result = Session(Profile(Mapping())).Evaluate("other", 10000, 5000, 500, 1000);
        Assert.Equal("differentDevice", result.CoordinateStatus);
        Assert.Null(result.ScreenX);
        Assert.Null(result.NormalizedPressure);
    }

    [Fact]
    public void OtdRelativeOutputMode_IsNotMistakenForItsSavedAbsoluteArea()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {"Profiles":[{"Tablet":"Tablet","OutputMode":{"Path":"OpenTabletDriver.Output.RelativeMode"},
              "AbsoluteModeSettings":{"Tablet":{"Width":200,"Height":100},
              "Display":{"Width":2000,"Height":1000}}}]}
            """);
        try
        {
            var profile = DriverProfileParser.ParseOpenTabletDriver(path);
            Assert.Equal("Relative", profile.CoordinateMapping!.MappingMode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Normalizer_PreservesRawPressureAndDoesNotApplyDefaultCurveTwice()
    {
        var session = Session(Profile(Mapping(), gamma: 2));
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault(), () => session);
        var evt = normalizer.Normalize(new() { DeviceId = "dev", Timestamp = DateTimeOffset.UtcNow,
            X = 10000, Y = 5000, Pressure = 500, MaxPressure = 1000 }, "session", 1).Single();
        Assert.Equal(InputEventType.PenDown, evt.Type);
        Assert.Equal(500, evt.Pressure);
        Assert.Equal(.25f, evt.NormalizedPressure);
        Assert.Equal(-1500, evt.DriverMapping!.ScreenX!.Value, 5);
        Assert.Equal(session.Snapshot.SnapshotId, evt.CloneWithType(InputEventType.PenMove).DriverMapping!.DriverSnapshotId);
    }

    [Fact]
    public void Normalizer_MappedPressureDoesNotTurnHoverNoiseIntoContact()
    {
        var session = Session(Profile(Mapping(), gamma: .1));
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault(), () => session);
        var events = normalizer.Normalize(new() { DeviceId = "dev", Timestamp = DateTimeOffset.UtcNow,
            X = 10000, Y = 5000, Pressure = 1, MaxPressure = 1000, IsNearProximity = true }, "session", 1).ToArray();
        Assert.DoesNotContain(events, e => e.Type == InputEventType.PenDown);
        var hover = Assert.Single(events);
        Assert.Equal(ContactState.Hover, hover.ContactState);
        Assert.True(hover.NormalizedPressure > .1f);
    }

    [Fact]
    public void Normalizer_ZeroPressureWithoutProximityEndsMappedContact()
    {
        var session = Session(Profile(Mapping()));
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault(), () => session);
        RawInputReport Report(float pressure) => new() { DeviceId = "dev", Timestamp = DateTimeOffset.UtcNow,
            X = 10000, Y = 5000, Pressure = pressure, MaxPressure = 1000 };
        Assert.Contains(normalizer.Normalize(Report(100), "session", 1), e => e.Type == InputEventType.PenDown);
        var release = normalizer.Normalize(Report(0), "session", 2).ToArray();
        Assert.Contains(release, e => e.Type == InputEventType.PenUp);
        Assert.Contains(normalizer.Normalize(Report(100), "session", 3), e => e.Type == InputEventType.PenDown);
    }

    [Fact]
    public void Normalizer_PressureCurveCannotSuppressPhysicalClick()
    {
        var session = Session(Profile(Mapping(), gamma: 1000));
        var normalizer = new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault(), () => session);
        var evt = Assert.Single(normalizer.Normalize(new() { DeviceId = "dev", Timestamp = DateTimeOffset.UtcNow,
            X = 10000, Y = 5000, Pressure = 100, MaxPressure = 1000 }, "session", 1));
        Assert.Equal(InputEventType.PenDown, evt.Type);
        Assert.Equal(0f, evt.NormalizedPressure);
    }

    [Fact]
    public void ExplicitlyDisablingMapping_PreservesUnmappedReports()
    {
        var session = DriverMappingSession.Disabled(new() { Device = Device });
        Assert.Equal("disabled", session.Snapshot.Status);
        var mapped = session.Evaluate("dev", 10000, 5000, 500, 1000);
        Assert.Equal("disabled", mapped.PressureStatus);
        Assert.Null(mapped.ScreenX);
    }

    [Fact]
    public void InvalidConfiguration_DoesNotPreventDiscoveringOtherProfiles()
    {
        string invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(invalidPath, "{\"unrelated\":true}");
        try
        {
            var session = DriverMappingSession.Initialize(new() { ConfigPath = ConfigPath,
                Profiles = [Profile(Mapping())], CandidatePaths = [invalidPath] });
            // Explicit selection must not read unrelated candidates; validate discovery separately.
            var discovery = DriverMappingSession.Initialize(new() { Profiles = [Profile(Mapping())], CandidatePaths = [invalidPath] });
            Assert.Equal("selected", session.Snapshot.Status);
            Assert.Single(discovery.Snapshot.DiscoveredProfiles);
            Assert.Single(discovery.Snapshot.Warnings);
        }
        finally { File.Delete(invalidPath); }
    }

    [Fact]
    public async Task DriverSnapshotAndMappedPoint_SurviveMemolineRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), "driver-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var session = Session(Profile(Mapping(), gamma: 2));
            string path;
            await using (var writer = new MemolineWriter(directory, new { }))
            {
                path = writer.FilePath;
                writer.AppendState("driverConfiguration", writer.NowTicks, [], session.Snapshot, "immediate");
                writer.AppendHardware("penSample", session.Evaluate("dev", 10000, 5000, 500, 1000),
                    new("pen", "test", "dev", "deviceId"));
            }
            var frames = MemolineReader.Read(path).ToArray();
            var configuration = frames.Single(f => f.GetProperty("kind").GetString() == "driverConfiguration").GetProperty("data");
            var point = frames.Single(f => f.GetProperty("kind").GetString() == "penSample").GetProperty("data");
            Assert.Equal(configuration.GetProperty("snapshotId").GetString(), point.GetProperty("driverSnapshotId").GetString());
            Assert.Equal(9, configuration.GetProperty("physicalToScreen").GetProperty("matrix").GetArrayLength());
            Assert.Equal(-1500, point.GetProperty("screenX").GetDouble(), 5);
            Assert.Equal(.25, point.GetProperty("normalizedPressure").GetDouble(), 5);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
