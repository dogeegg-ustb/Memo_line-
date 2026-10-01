namespace BehaviorRecognizer.Abstractions.Config;

public sealed class PenProfile
{
    public required string ProfileId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<float> PressureCurve { get; init; } = [0f, 0.25f, 0.5f, 0.75f, 1f];
    public float PressureSensitivity { get; init; } = 1f;
    public float TipThreshold { get; init; } = 0.01f;
    public IReadOnlyDictionary<string, string> ButtonMappings { get; init; }
        = new Dictionary<string, string>();
    public bool TiltEnabled { get; init; } = true;
    public bool HoverTracking { get; init; } = true;
    public string? MatchedDeviceName { get; init; }
    public string? Notes { get; init; }
}

public sealed class ConfigurationSnapshot
{
    public required string SnapshotId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required PenProfile AppliedProfile { get; init; }
    public string Source { get; init; } = "builtin-default";
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public interface IPenProfileProvider
{
    bool HasDefaultConfigFile { get; }

    PenProfile GetDefaultProfile();

    PenProfile? TryLoadUserProfile();

    IReadOnlyList<PenProfile> GetDevicePresets();
}

public interface IDeviceProfileMatcher
{
    PenProfile Match(PenProfile defaults, IEnumerable<PenProfile> presets, string? deviceName, PenProfile? userOverride);
}

public interface IConfigurationSnapshotProvider
{
    ConfigurationSnapshot CreateSnapshot(PenProfile profile, string source);
}
