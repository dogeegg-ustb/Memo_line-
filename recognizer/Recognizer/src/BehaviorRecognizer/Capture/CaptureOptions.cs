namespace BehaviorRecognizer.Capture;

/// <summary>Preserves the existing OTD capture path; passive pen capture is optional.</summary>
public sealed record CaptureOptions(bool EnableOtdHid);
