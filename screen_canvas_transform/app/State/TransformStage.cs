namespace ScreenCanvasTransform.State;

/// <summary>Architecture §16 state machine (host orchestration only).</summary>
public enum TransformStage
{
    Idle = 0,
    CaptureFrozen = 1,
    SelectingWorkspaceRoi = 2,
    DetectingWorkspace = 3,
    SelectingNavigatorRoi = 4,
    DetectingNavigatorThumbnailCII = 5,
    ObservingWorkspaceCanvas = 6,
    ObservingNavigatorCanvas = 7,
    ReadingNavigatorNumbers = 8,
    CompletingViewportFrame = 9,
    SolvingTransform = 10,
    ShowingCanvasTopLeftMarker = 11,
    TrackingStable = 12,
    RecomputeRequested = 19,
    ViewChanging = 13,
    WaitingForCspStable = 14,
    ReacquiringEvidence = 15,
    TrackingUncertain = 16,
    TrackingLost = 17,
    TrackingDegraded = 18,
    SelectingSaveArchive = 20,
    PersistingSaveArchive = 21,
    LoadingSaveArchive = 22,
    ArchiveRecomputeRequested = 23
}
