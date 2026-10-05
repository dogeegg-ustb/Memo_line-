namespace StrokeReplay;

/// <summary>Replay-ready tablet operations with their causally preceding canvas state.</summary>
internal sealed record MemolineReplayDocument(
    string FilePath,
    string SessionId,
    long Frequency,
    IReadOnlyList<MemolineReplayStroke> Strokes,
    int SkippedNavigationOperations = 0,
    int SkippedOutsideCanvasOperations = 0,
    int SkippedInvalidViewOperations = 0);

internal sealed record MemolineReplayStroke(
    ulong OperationId,
    long StartTicks,
    long EndTicks,
    CanvasViewSnapshot View,
    IReadOnlyList<MemolineReplaySample> Samples);

/// <summary>Absolute recorded Windows screen coordinates and already mapped, normalized pressure.</summary>
internal sealed record MemolineReplaySample(
    long Ticks,
    double X,
    double Y,
    double Pressure,
    double TiltX,
    double TiltY,
    bool InContact);
