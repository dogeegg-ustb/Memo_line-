using System.Text.Json;
using BehaviorRecognizer.Abstractions.Config;

namespace BehaviorRecognizer.Config;

public sealed class PenProfileProvider : IPenProfileProvider
{
    private readonly string _configDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public PenProfileProvider(string? configDirectory = null)
    {
        _configDirectory = configDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "config");
    }

    public bool HasDefaultConfigFile =>
        File.Exists(Path.Combine(_configDirectory, "default_pen_profile.json"));

    public PenProfile GetDefaultProfile()
    {
        var path = Path.Combine(_configDirectory, "default_pen_profile.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<PenProfileDto>(json, _jsonOptions);
                if (dto is not null)
                    return dto.ToProfile();
            }
            catch
            {
                // Fall through to hardcoded defaults — config must never block capture.
            }
        }

        return CreateHardcodedDefault();
    }

    public PenProfile? TryLoadUserProfile()
    {
        var path = Path.Combine(_configDirectory, "user_pen_profile.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<PenProfileDto>(json, _jsonOptions);
            return dto?.ToProfile();
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<PenProfile> GetDevicePresets()
    {
        var presetsDir = Path.Combine(_configDirectory, "device_presets");
        if (!Directory.Exists(presetsDir))
            return [];

        var list = new List<PenProfile>();
        foreach (var file in Directory.EnumerateFiles(presetsDir, "*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<PenProfileDto>(File.ReadAllText(file), _jsonOptions);
                if (dto is not null)
                    list.Add(dto.ToProfile());
            }
            catch
            {
                // Skip broken presets.
            }
        }

        return list;
    }

    public static PenProfile CreateHardcodedDefault() => new()
    {
        ProfileId = "builtin-default",
        DisplayName = "Default Pen Profile",
        PressureCurve = [0f, 0.15f, 0.45f, 0.75f, 1f],
        PressureSensitivity = 1f,
        TipThreshold = 0.01f,
        ButtonMappings = new Dictionary<string, string>
        {
            ["button1"] = "secondary",
            ["button2"] = "eraser"
        },
        TiltEnabled = true,
        HoverTracking = true,
        Notes = "Hardcoded fallback when config files are missing."
    };

    private sealed class PenProfileDto
    {
        public string? ProfileId { get; set; }
        public string? DisplayName { get; set; }
        public float[]? PressureCurve { get; set; }
        public float PressureSensitivity { get; set; } = 1f;
        public float TipThreshold { get; set; } = 0.01f;
        public Dictionary<string, string>? ButtonMappings { get; set; }
        public bool TiltEnabled { get; set; } = true;
        public bool HoverTracking { get; set; } = true;
        public string? MatchedDeviceName { get; set; }
        public string? Notes { get; set; }

        public PenProfile ToProfile() => new()
        {
            ProfileId = string.IsNullOrWhiteSpace(ProfileId) ? "unnamed" : ProfileId,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed" : DisplayName,
            PressureCurve = PressureCurve ?? [0f, 0.25f, 0.5f, 0.75f, 1f],
            PressureSensitivity = PressureSensitivity,
            TipThreshold = TipThreshold,
            ButtonMappings = ButtonMappings ?? new Dictionary<string, string>(),
            TiltEnabled = TiltEnabled,
            HoverTracking = HoverTracking,
            MatchedDeviceName = MatchedDeviceName,
            Notes = Notes
        };
    }
}

public sealed class DeviceProfileMatcher : IDeviceProfileMatcher
{
    public PenProfile Match(
        PenProfile defaults,
        IEnumerable<PenProfile> presets,
        string? deviceName,
        PenProfile? userOverride)
    {
        PenProfile selected = defaults;

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = presets.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.MatchedDeviceName) &&
                deviceName.Contains(p.MatchedDeviceName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                selected = match;
        }

        if (userOverride is null)
            return selected;

        // User profile overlays defaults / device preset.
        return new PenProfile
        {
            ProfileId = userOverride.ProfileId,
            DisplayName = userOverride.DisplayName,
            PressureCurve = userOverride.PressureCurve.Count > 0 ? userOverride.PressureCurve : selected.PressureCurve,
            PressureSensitivity = userOverride.PressureSensitivity,
            TipThreshold = userOverride.TipThreshold,
            ButtonMappings = userOverride.ButtonMappings.Count > 0 ? userOverride.ButtonMappings : selected.ButtonMappings,
            TiltEnabled = userOverride.TiltEnabled,
            HoverTracking = userOverride.HoverTracking,
            MatchedDeviceName = selected.MatchedDeviceName,
            Notes = userOverride.Notes ?? selected.Notes
        };
    }
}

public sealed class ConfigurationSnapshotProvider : IConfigurationSnapshotProvider
{
    public ConfigurationSnapshot CreateSnapshot(PenProfile profile, string source) => new()
    {
        SnapshotId = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        AppliedProfile = profile,
        Source = source
    };
}
