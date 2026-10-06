namespace BehaviorRecognizer.Capture;

/// <summary>Preserves the existing OTD capture path; passive pen capture is optional.</summary>
public sealed record CaptureOptions(bool EnableOtdHid, string? TabletDeviceId = null)
{
    public bool AcceptsDevice(string deviceId) => TabletDeviceId is null
        || string.Equals(TabletDeviceId, deviceId, StringComparison.Ordinal);
}
