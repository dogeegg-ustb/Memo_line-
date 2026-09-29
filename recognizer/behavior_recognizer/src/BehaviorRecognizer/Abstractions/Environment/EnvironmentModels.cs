namespace BehaviorRecognizer.Abstractions.Environment;

public enum VMultiStatus
{
    Installed,
    NotInstalled,
    InstalledButInactive,
    PermissionDenied,
    Unknown
}

public enum WindowsInkStatus
{
    Available,
    Unavailable,
    NotApplicable,
    Unknown
}

public sealed class CapabilityGuide
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string? DocumentationUrl { get; init; }
    public string? InstallerUrl { get; init; }
    public bool BlocksBasicCapture { get; init; }
}

public sealed class EnvironmentSnapshot
{
    public required bool IsWindows { get; init; }
    public required bool HasElevatedPrivileges { get; init; }
    public required VMultiStatus VMulti { get; init; }
    public required WindowsInkStatus WindowsInk { get; init; }
    public required bool TabletDevicePresent { get; init; }
    public required bool DefaultConfigPresent { get; init; }
    public required IReadOnlyList<CapabilityGuide> Guides { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
}
