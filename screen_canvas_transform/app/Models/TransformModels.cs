using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Models;

public sealed class WorkspaceBackgroundModel
{
    public float CenterLabL { get; init; }
    public float CenterLabA { get; init; }
    public float CenterLabB { get; init; }
    public float StrongDeltaE { get; init; }
    public float WeakDeltaE { get; init; }
    public float Confidence { get; init; }
    public string SourceCaptureId { get; init; } = "";
    public string SourceRevision { get; init; } = "";

    public NativeSct.SctBackgroundModel ToNative() => new()
    {
        CenterLabL = CenterLabL,
        CenterLabA = CenterLabA,
        CenterLabB = CenterLabB,
        StrongDeltaE = StrongDeltaE,
        WeakDeltaE = WeakDeltaE,
        Confidence = Confidence
    };

    public static WorkspaceBackgroundModel FromNative(NativeSct.SctBackgroundModel m, string captureId, string revision)
        => new()
        {
            CenterLabL = m.CenterLabL,
            CenterLabA = m.CenterLabA,
            CenterLabB = m.CenterLabB,
            StrongDeltaE = m.StrongDeltaE,
            WeakDeltaE = m.WeakDeltaE,
            Confidence = m.Confidence,
            SourceCaptureId = captureId,
            SourceRevision = revision
        };
}

public sealed class CanvasObservationDto
{
    public int Status { get; init; }
    public IntRect BoundsCapture { get; init; }
    public IntRect BoundsScreen { get; init; }
    public float AspectRatio { get; init; }
    public float Confidence { get; init; }
    public int VisibleEdgesMask { get; init; }
    public float[] BoundarySupport { get; init; } = new float[4];
    public bool FourSidesComplete { get; init; }
    public bool Ambiguous { get; init; }
    public string AmbiguityReason { get; init; } = "";

    public NativeSct.SctCanvasObservation ToNative() => new()
    {
        Status = Status,
        BoundsCapture = NativeSct.SctIntRect.From(BoundsCapture),
        BoundsScreen = NativeSct.SctIntRect.From(BoundsScreen),
        AspectRatio = AspectRatio,
        Confidence = Confidence,
        VisibleEdgesMask = VisibleEdgesMask,
        BoundarySupport0 = BoundarySupport.Length > 0 ? BoundarySupport[0] : 0,
        BoundarySupport1 = BoundarySupport.Length > 1 ? BoundarySupport[1] : 0,
        BoundarySupport2 = BoundarySupport.Length > 2 ? BoundarySupport[2] : 0,
        BoundarySupport3 = BoundarySupport.Length > 3 ? BoundarySupport[3] : 0,
        FourSidesComplete = FourSidesComplete ? 1 : 0,
        Ambiguous = Ambiguous ? 1 : 0,
        AmbiguityReason = AmbiguityReason ?? ""
    };

    public static CanvasObservationDto FromNative(NativeSct.SctCanvasObservation o) => new()
    {
        Status = o.Status,
        BoundsCapture = o.BoundsCapture.ToIntRect(),
        BoundsScreen = o.BoundsScreen.ToIntRect(),
        AspectRatio = o.AspectRatio,
        Confidence = o.Confidence,
        VisibleEdgesMask = o.VisibleEdgesMask,
        BoundarySupport = new[] { o.BoundarySupport0, o.BoundarySupport1, o.BoundarySupport2, o.BoundarySupport3 },
        FourSidesComplete = o.FourSidesComplete != 0,
        Ambiguous = o.Ambiguous != 0,
        AmbiguityReason = o.AmbiguityReason ?? ""
    };
}

public sealed class NavigatorNumericReadingDto
{
    public float ScalePercent { get; init; }
    public float RotationDegrees { get; init; }
    public float ScaleConfidence { get; init; }
    public float RotationConfidence { get; init; }
    public string ScaleRawText { get; init; } = "";
    public string RotationRawText { get; init; } = "";
    public string SourceCaptureId { get; init; } = "";
    public DateTime CapturedAt { get; init; }

    public NativeSct.SctNumericReading ToNative() => new()
    {
        ScalePercent = ScalePercent,
        RotationDegrees = RotationDegrees,
        ScaleConfidence = ScaleConfidence,
        RotationConfidence = RotationConfidence,
        ScaleRaw = ScaleRawText ?? "",
        RotationRaw = RotationRawText ?? "",
        CaptureId = SourceCaptureId ?? ""
    };
}

public sealed class TransformSnapshotDto
{
    public int Status { get; init; }
    public string SnapshotId { get; init; } = "";
    public ulong Generation { get; init; }
    public string CaptureId { get; init; } = "";
    public IntRect WorkspaceRoi { get; init; }
    public IntRect NavigatorRoi { get; init; }
    public IntRect NavigatorThumbnailRoi { get; init; }
    public CanvasObservationDto WorkspaceCanvas { get; init; } = new();
    public CanvasObservationDto NavigatorCanvas { get; init; } = new();
    public NavigatorNumericReadingDto Numbers { get; init; } = new();
    public float ScaleReference { get; init; }
    public float RelativeScale { get; init; }
    public float CumulativeRelativeScale { get; init; }
    public float RotationDegrees { get; init; }
    public NativeSct.SctMarkerGeometry Marker { get; init; }
    public float Confidence { get; init; }
    public bool UsedDirectWorkspacePath { get; init; }
    public string SourceRevision { get; init; } = "";
    public int CoordinateConventionVersion { get; init; }
    public string FailureMessage { get; init; } = "";
    public int FailureStatus { get; init; }
    public NativeSct.SctTransformSnapshot Raw { get; init; }

    public static TransformSnapshotDto FromNative(NativeSct.SctTransformSnapshot s) => new()
    {
        Status = s.Status,
        SnapshotId = s.SnapshotId ?? "",
        Generation = s.Generation,
        CaptureId = s.CaptureId ?? "",
        WorkspaceRoi = s.WorkspaceRoi.ToIntRect(),
        NavigatorRoi = s.NavigatorRoi.ToIntRect(),
        NavigatorThumbnailRoi = s.NavigatorThumbnailRoi.ToIntRect(),
        WorkspaceCanvas = CanvasObservationDto.FromNative(s.WorkspaceCanvas),
        NavigatorCanvas = CanvasObservationDto.FromNative(s.NavigatorCanvas),
        Numbers = new NavigatorNumericReadingDto
        {
            ScalePercent = s.Numbers.ScalePercent,
            RotationDegrees = s.Numbers.RotationDegrees,
            ScaleConfidence = s.Numbers.ScaleConfidence,
            RotationConfidence = s.Numbers.RotationConfidence,
            ScaleRawText = s.Numbers.ScaleRaw ?? "",
            RotationRawText = s.Numbers.RotationRaw ?? "",
            SourceCaptureId = s.Numbers.CaptureId ?? "",
            CapturedAt = DateTime.UtcNow
        },
        ScaleReference = s.ScaleReference,
        RelativeScale = s.RelativeScale,
        CumulativeRelativeScale = s.CumulativeRelativeScale,
        RotationDegrees = s.RotationDegrees,
        Marker = s.Marker,
        Confidence = s.Confidence,
        UsedDirectWorkspacePath = s.UsedDirectWorkspacePath != 0,
        SourceRevision = s.SourceRevision ?? "",
        CoordinateConventionVersion = s.CoordinateConventionVersion,
        FailureMessage = s.Failure.Message ?? "",
        FailureStatus = s.Failure.Status,
        Raw = s
    };
}
