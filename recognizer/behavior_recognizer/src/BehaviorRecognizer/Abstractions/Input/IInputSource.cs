namespace BehaviorRecognizer.Abstractions.Input;

public interface IInputSource : IAsyncDisposable
{
    string Name { get; }

    bool IsRunning { get; }

    IReadOnlyList<DetectedDeviceInfo> DetectedDevices { get; }

    event EventHandler<RawInputReport>? ReportReceived;

    event EventHandler<DetectedDeviceInfo>? DeviceChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<bool> DetectDevicesAsync(CancellationToken cancellationToken = default);
}

public sealed class DetectedDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public string? Vendor { get; init; }
    public int? VendorId { get; init; }
    public int? ProductId { get; init; }
    public float MaxPressure { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}

/// <summary>
/// Driver-agnostic report handed to the normalizer.
/// </summary>
public sealed class RawInputReport
{
    public required string DeviceId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Pressure { get; init; }
    public float? MaxPressure { get; init; }
    public float? TiltX { get; init; }
    public float? TiltY { get; init; }
    public bool[]? PenButtons { get; init; }
    public bool IsOutOfRange { get; init; }
    public bool IsNearProximity { get; init; }
    public byte[]? RawBytes { get; init; }
}
