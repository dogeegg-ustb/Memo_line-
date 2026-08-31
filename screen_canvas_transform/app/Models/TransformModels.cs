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

public sealed class CompleteEdgeDto
{
    public double P0CaptureX { get; init; }
    public double P0CaptureY { get; init; }
    public double P1CaptureX { get; init; }
    public double P1CaptureY { get; init; }
    public int WorkspaceEdge { get; init; }

    public static CompleteEdgeDto FromNative(NativeSct.SctCompleteEdge e) => new()
    {
        P0CaptureX = e.P0Capture.X,
        P0CaptureY = e.P0Capture.Y,
        P1CaptureX = e.P1Capture.X,
        P1CaptureY = e.P1Capture.Y,
        WorkspaceEdge = e.WorkspaceEdge
    };
}

public sealed class TransformSnapshotDto
{
    public int Status { get; init; }
    public string SnapshotId { get; init; } = "";
    public ulong Generation { get; init; }
    public ulong RecomputeGeneration { get; init; }
    public string CaptureId { get; init; } = "";
    public int CanvasPixelWidth { get; init; }
    public int CanvasPixelHeight { get; init; }
    public IntRect WorkspaceRoi { get; init; }
    public IntRect NavigatorRoi { get; init; }
    public IntRect NavigatorThumbnailRoi { get; init; }
    public CanvasObservationDto WorkspaceCanvas { get; init; } = new();
    public CanvasObservationDto NavigatorCanvas { get; init; } = new();
    public NavigatorNumericReadingDto Numbers { get; init; } = new();
    public float ScaleReference { get; init; }
    public float RelativeScale { get; init; }
    public float CumulativeRelativeScale { get; init; }
    public float RotationDegreesGeometry { get; init; }
    public float RotationDegreesOcrOrInjected { get; init; }
    public float RotationDegrees { get; init; }
    public float ScalePercentOcrOrInjected { get; init; }
    public float ScaleGeometryEstimate { get; init; }
    public float ScaleConsistencyError { get; init; }
    public NativeSct.SctMarkerGeometry Marker { get; init; }
    public float Confidence { get; init; }
    public bool UsedDirectWorkspacePath { get; init; }
    /// <summary>ViewportCompletionPattern encoding: 0=0.0/direct, 1=0.1, 2=0.2, 10=1.0, 20=2.0, 21=2.1, 30=3.0, 40=4.0.</summary>
    public int ViewportCompletionStrategy { get; init; }
    /// <summary>Confirmed complete red-frame edges from viewport completion (0–4).</summary>
    public int ConfirmedCompleteEdgeCount { get; init; }
    public CompleteEdgeDto[] CompleteEdges { get; init; } = Array.Empty<CompleteEdgeDto>();
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
        RecomputeGeneration = s.RecomputeGeneration,
        CaptureId = s.CaptureId ?? "",
        CanvasPixelWidth = s.CanvasPixelWidth,
        CanvasPixelHeight = s.CanvasPixelHeight,
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
        RotationDegreesGeometry = s.RotationDegreesGeometry,
        RotationDegreesOcrOrInjected = s.RotationDegreesOcrOrInjected,
        RotationDegrees = s.RotationDegrees,
        ScalePercentOcrOrInjected = s.ScalePercentOcrOrInjected,
        ScaleGeometryEstimate = s.ScaleGeometryEstimate,
        ScaleConsistencyError = s.ScaleConsistencyError,
        Marker = s.Marker,
        Confidence = s.Confidence,
        UsedDirectWorkspacePath = s.UsedDirectWorkspacePath != 0,
        ViewportCompletionStrategy = s.Viewport.CompletionStrategy,
        ConfirmedCompleteEdgeCount = s.Viewport.ConfirmedCompleteEdgeCount,
        CompleteEdges = ExtractCompleteEdges(s.Viewport),
        SourceRevision = s.SourceRevision ?? "",
        CoordinateConventionVersion = s.CoordinateConventionVersion,
        FailureMessage = s.Failure.Message ?? "",
        FailureStatus = s.Failure.Status,
        Raw = s
    };

    private static CompleteEdgeDto[] ExtractCompleteEdges(NativeSct.SctViewportFrame v)
    {
        int n = Math.Clamp(v.ConfirmedCompleteEdgeCount, 0, 4);
        if (n == 0)
            return Array.Empty<CompleteEdgeDto>();

        var edges = new NativeSct.SctCompleteEdge[]
        {
            v.CompleteEdge0, v.CompleteEdge1, v.CompleteEdge2, v.CompleteEdge3
        };
        var list = new CompleteEdgeDto[n];
        for (int i = 0; i < n; i++)
            list[i] = CompleteEdgeDto.FromNative(edges[i]);
        return list;
    }
}
