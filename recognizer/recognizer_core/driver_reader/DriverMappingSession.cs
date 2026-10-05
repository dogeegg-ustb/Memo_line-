using System.Text.Json;

namespace DriverReader;

public sealed record TabletDeviceInfo(string DeviceId, string Name, double WidthMm, double HeightMm,
    double MaxX, double MaxY, double MaxPressure);

public sealed record DisplayInfo(int Index, string Name, bool Primary, RectArea Bounds);

public sealed class DriverInitializationOptions
{
    public string? ConfigPath { get; init; }
    public TabletDeviceInfo? Device { get; init; }
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public int? ScreenIndexOverride { get; init; }
    public IReadOnlyList<DriverProfile>? Profiles { get; init; }
    public bool ReloadSelectedConfiguration { get; init; }
    // Also permits deterministic initialization from caller-owned configuration locations.
    public IReadOnlyList<string>? CandidatePaths { get; init; }
}

/// <summary>Row-major affine matrix acting on a homogeneous column vector (x,y,1).</summary>
public sealed record AffineTransform2D(double M0, double M1, double M2, double M3, double M4, double M5)
{
    public double[] Matrix => [M0, M1, M2, M3, M4, M5, 0, 0, 1];
    public (double X, double Y) Apply(double x, double y) => (M0 * x + M1 * y + M2, M3 * x + M4 * y + M5);
    public AffineTransform2D Inverse()
    {
        double determinant = M0 * M4 - M1 * M3;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-18)
            throw new InvalidOperationException("坐标映射矩阵不可逆");
        return new(M4 / determinant, -M1 / determinant, (M1 * M5 - M4 * M2) / determinant,
            -M3 / determinant, M0 / determinant, (M3 * M2 - M0 * M5) / determinant);
    }
}

public sealed class DriverConfigurationSnapshot
{
    public string SnapshotId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; init; } = "unavailable";
    public string SelectionReason { get; init; } = "none";
    public TabletDeviceInfo? Device { get; init; }
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public JsonElement? SelectedProfile { get; init; }
    public IReadOnlyList<JsonElement> DiscoveredProfiles { get; init; } = [];
    public string PressureMappingStatus { get; init; } = "unavailable";
    public string CoordinateMappingStatus { get; init; } = "unavailable";
    public RectArea? TargetScreenArea { get; init; }
    public AffineTransform2D? PhysicalToScreen { get; init; }
    public AffineTransform2D? ScreenToPhysical { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record DriverPenMappingResult(string DriverSnapshotId, string PressureStatus,
    string CoordinateStatus, double? MappedPressure, double? NormalizedPressure,
    double? PhysicalX, double? PhysicalY, double? ScreenX, double? ScreenY);

/// <summary>Reads configuration once. Per-report evaluation performs no discovery, I/O or driver calls.</summary>
public sealed class DriverMappingSession
{
    private readonly DriverProfile? _profile;
    private readonly AffineTransform2D? _physicalToScreen;
    public DriverConfigurationSnapshot Snapshot { get; }

    private DriverMappingSession(DriverProfile? profile, DriverConfigurationSnapshot snapshot)
    {
        _profile = profile;
        Snapshot = snapshot;
        _physicalToScreen = snapshot.PhysicalToScreen;
    }

    public static DriverMappingSession Initialize(DriverInitializationOptions options)
    {
        var warnings = new List<string>();
        var profiles = options.Profiles?.ToList() ?? new List<DriverProfile>();
        var paths = !string.IsNullOrWhiteSpace(options.ConfigPath) ? new[] { options.ConfigPath! }
            : options.CandidatePaths ?? DriverProfileParser.AutoDetectCandidatePaths(includeBackups: false);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool SamePath(DriverProfile profile) => Path.GetFullPath(profile.ConfigPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (options.ReloadSelectedConfiguration && options.ConfigPath is not null) profiles.RemoveAll(SamePath);
            else if (profiles.Any(SamePath)) continue;
            try { profiles.Add(DriverProfileParser.Parse(path, options.Device?.Name)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { warnings.Add($"配置读取失败 {path}: {ex.Message}"); }
        }

        var (selected, reason) = SelectProfile(profiles, options);
        if (selected is null && options.ConfigPath is not null)
            warnings.Add("未能读取所选驱动配置，请选择其他配置文件。");

        string pressureStatus = selected is null ? "unavailable" : ValidatePressure(selected);
        if (selected is not null && pressureStatus != "ready")
            warnings.Add($"驱动压力映射不可用：{pressureStatus}");
        var (transform, screen, coordinateStatus) = CreateTransform(selected?.CoordinateMapping, options);
        if (selected is not null && coordinateStatus != "ready")
            warnings.Add($"驱动坐标映射不可用：{coordinateStatus}；保留 Windows 光标屏幕坐标。");
        var snapshot = new DriverConfigurationSnapshot
        {
            Status = selected is null ? (options.ConfigPath is null ? "selectionRequired" : "unavailable") : "selected",
            SelectionReason = reason, Device = options.Device, Displays = options.Displays,
            SelectedProfile = selected is null ? null : Export(selected),
            DiscoveredProfiles = profiles.Select(Export).ToArray(),
            PressureMappingStatus = pressureStatus, CoordinateMappingStatus = coordinateStatus,
            TargetScreenArea = screen, PhysicalToScreen = transform,
            ScreenToPhysical = transform?.Inverse(), Warnings = warnings
        };
        return new(selected, snapshot);
    }

    private static JsonElement Export(DriverProfile profile)
    {
        using var document = JsonDocument.Parse(profile.ToJson(indented: false));
        return document.RootElement.Clone();
    }

    private static (DriverProfile? Profile, string Reason) SelectProfile(
        List<DriverProfile> profiles, DriverInitializationOptions options)
    {
        return options.ConfigPath is null ? (null, "userSelectionRequired")
            : (profiles.FirstOrDefault(p => Path.GetFullPath(p.ConfigPath).Equals(
                Path.GetFullPath(options.ConfigPath), StringComparison.OrdinalIgnoreCase)), "userSelectedConfig");
    }

    public static DriverMappingSession Disabled(DriverInitializationOptions options) => new(null, new()
    {
        Status = "disabled", SelectionReason = "userDisabledMapping", Device = options.Device,
        Displays = options.Displays, PressureMappingStatus = "disabled", CoordinateMappingStatus = "disabled"
    });

    private static string ValidatePressure(DriverProfile profile)
    {
        // Unknown OTD plugins cannot be reconstructed from a filter name alone.
        if (profile.CurveSummary.StartsWith("OTD 激活滤镜:", StringComparison.Ordinal)) return "unsupportedPressureFilter";
        if (!double.IsFinite(profile.ThresholdRatio) || profile.ThresholdRatio < 0 || profile.ThresholdRatio >= 1)
            return "invalidThreshold";
        if (profile.GammaCurve is { } gamma && (!double.IsFinite(gamma) || gamma <= 0)) return "invalidGamma";
        if (profile.BezierCurve is { } b)
        {
            var points = new[] { b.P0, b.P1, b.P2, b.P3 };
            if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))
                || b.P0.X >= b.P3.X || b.P0.Y >= b.P3.Y
                || b.P1.X < b.P0.X || b.P2.X < b.P1.X || b.P3.X < b.P2.X)
                return "invalidBezier";
        }
        return "ready";
    }

    private static bool ValidArea(RectArea area) => double.IsFinite(area.Left) && double.IsFinite(area.Top)
        && double.IsFinite(area.Right) && double.IsFinite(area.Bottom) && area.Width > 0 && area.Height > 0;

    private static (AffineTransform2D? Transform, RectArea? Screen, string Status) CreateTransform(
        CoordinateMapping? mapping, DriverInitializationOptions options)
    {
        if (mapping is null) return (null, null, "missingMapping");
        if (!mapping.MappingMode.Equals("Absolute", StringComparison.OrdinalIgnoreCase)) return (null, null, "relativeMode");
        if (!ValidArea(mapping.PhysicalArea)) return (null, null, "invalidPhysicalArea");
        int rotation = ((mapping.RotationDegrees % 360) + 360) % 360;
        if (rotation % 90 != 0) return (null, null, "unsupportedRotation");
        RectArea? screen = mapping.ScreenArea;
        if (screen is null && mapping.ScreenMapRatio is { } ratio)
        {
            DisplayInfo? display = options.ScreenIndexOverride is { } index
                ? options.Displays.FirstOrDefault(d => d.Index == index)
                : options.Displays.Count == 1 ? options.Displays[0] : null;
            // Vendor ScreenNum conventions differ. Require an explicit index on multiple monitors.
            if (display is null) return (null, null, options.Displays.Count > 1 ? "monitorSelectionRequired" : "displayUnavailable");
            if (!ValidArea(ratio)) return (null, null, "invalidScreenRatio");
            screen = new(display.Bounds.Left + ratio.Left * display.Bounds.Width,
                display.Bounds.Top + ratio.Top * display.Bounds.Height,
                display.Bounds.Left + ratio.Right * display.Bounds.Width,
                display.Bounds.Top + ratio.Bottom * display.Bounds.Height);
        }
        if (screen is null || !ValidArea(screen)) return (null, null, "missingScreenArea");
        var p = mapping.PhysicalArea;
        // Rotation follows the normalized active-area convention of the extracted reader.
        (double X, double Y) Apply(double x, double y)
        {
            double u = (x - p.Left) / p.Width, v = (y - p.Top) / p.Height;
            (u, v) = rotation switch { 90 => (1 - v, u), 180 => (1 - u, 1 - v), 270 => (v, 1 - u), _ => (u, v) };
            return (screen.Left + u * screen.Width, screen.Top + v * screen.Height);
        }
        var origin = Apply(0, 0); var xAxis = Apply(1, 0); var yAxis = Apply(0, 1);
        return (new(xAxis.X - origin.X, yAxis.X - origin.X, origin.X,
            xAxis.Y - origin.Y, yAxis.Y - origin.Y, origin.Y), screen, "ready");
    }

    public DriverPenMappingResult Evaluate(string deviceId, double? rawX, double? rawY,
        double? rawPressure, double? pressureMaximum)
    {
        string pressureStatus = Snapshot.PressureMappingStatus, coordinateStatus = Snapshot.CoordinateMappingStatus;
        double? mappedPressure = null, normalizedPressure = null, physicalX = null, physicalY = null, screenX = null, screenY = null;
        bool matchesDevice = Snapshot.Device is null || Snapshot.Device.DeviceId == deviceId;
        if (!matchesDevice) pressureStatus = coordinateStatus = "differentDevice";
        if (matchesDevice && pressureStatus == "ready" && rawPressure is { } pressure && double.IsFinite(pressure))
        {
            double maximum = pressureMaximum ?? _profile!.RecommendedPressureMax ?? 0;
            if (double.IsFinite(maximum) && maximum > 0)
            {
                mappedPressure = _profile!.TransformPressure(pressure, maximum);
                normalizedPressure = mappedPressure / maximum;
            }
            else pressureStatus = "missingPressureMaximum";
        }
        else if (pressureStatus == "ready") pressureStatus = "missingPressure";
        if (matchesDevice && coordinateStatus == "ready" && rawX is { } x && rawY is { } y
            && double.IsFinite(x) && double.IsFinite(y))
        {
            var mapping = _profile!.CoordinateMapping!;
            if (mapping.PhysicalUnit.Equals("Counts", StringComparison.OrdinalIgnoreCase))
            { physicalX = x; physicalY = y; }
            else if (mapping.PhysicalUnit.Equals("mm", StringComparison.OrdinalIgnoreCase)
                && Snapshot.Device is { MaxX: > 0, MaxY: > 0, WidthMm: > 0, HeightMm: > 0 } device)
            { physicalX = x * device.WidthMm / device.MaxX; physicalY = y * device.HeightMm / device.MaxY; }
            else coordinateStatus = "missingPhysicalUnitConversion";
            if (physicalX is { } px && physicalY is { } py)
            {
                // Saturation at the active area is distinct from the stored affine matrix.
                var area = mapping.PhysicalArea;
                var screenPoint = _physicalToScreen!.Apply(Math.Clamp(px, area.Left, area.Right), Math.Clamp(py, area.Top, area.Bottom));
                screenX = screenPoint.X; screenY = screenPoint.Y;
            }
        }
        else if (coordinateStatus == "ready") coordinateStatus = "missingPosition";
        return new(Snapshot.SnapshotId, pressureStatus, coordinateStatus, mappedPressure,
            normalizedPressure, physicalX, physicalY, screenX, screenY);
    }
}
