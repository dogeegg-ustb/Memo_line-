namespace ScreenCanvasTransform.State;

/// <summary>Recognition stages reported by the transform core.</summary>
public enum TransformStage
{
    Idle = 0,
    DetectingWorkspace = 3,
    SelectingNavigatorRoi = 4,
    DetectingNavigatorThumbnailCII = 5,
    ObservingWorkspaceCanvas = 6,
    ObservingNavigatorCanvas = 7,
    ReadingNavigatorNumbers = 8,
    CompletingViewportFrame = 9,
    SolvingTransform = 10,
    TrackingStable = 12,
    ReacquiringEvidence = 15,
}
