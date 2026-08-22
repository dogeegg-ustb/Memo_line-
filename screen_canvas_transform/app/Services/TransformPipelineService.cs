using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Detection;
using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ocr;
using ScreenCanvasTransform.State;

namespace ScreenCanvasTransform.Services;

public sealed class PipelineFailureException : Exception
{
    public TransformStage Stage { get; }
    public int Status { get; }
    public string CaptureId { get; }
    public ulong Generation { get; }
    public string SourceRevision { get; }
    public string EvidenceSummary { get; }

    public PipelineFailureException(
        TransformStage stage,
        int status,
        string message,
        string captureId,
        ulong generation,
        string sourceRevision = "",
        string evidenceSummary = "")
        : base(message)
    {
        Stage = stage;
        Status = status;
        CaptureId = captureId;
        Generation = generation;
        SourceRevision = sourceRevision;
        EvidenceSummary = evidenceSummary;
    }
}

public sealed class PipelineResult
{
    public TransformSnapshotDto Snapshot { get; init; } = null!;
    public IntRect WorkspaceRoiScreen { get; init; }
    public IntRect NavigatorRoiScreen { get; init; }
    public IntRect NavigatorThumbnailRoiScreen { get; init; }
    public WorkspaceBackgroundModel Background { get; init; } = null!;
    public TransformStage Stage { get; init; }
}

/// <summary>
/// Orchestrates architecture §4/§5 pipeline. Does not invent geometry or matrices.
/// Workspace detection failure MUST stop before navigator stage.
/// </summary>
public sealed class TransformPipelineService
{
    private readonly SctNativeService _native = new();
    private readonly NavigatorOcrService _ocr = new();

    public float? InitialScalePercent { get; private set; }
    public float? PreviousScalePercent { get; private set; }
    public TransformSnapshotDto? LastSnapshot { get; private set; }
    public ulong Generation { get; private set; }

    /// <summary>
    /// Correct WorkspaceUserRoi → WorkspaceRoi + background model.
    /// On failure the host MUST end initialization (no navigator selection).
    /// </summary>
    public DetectOutcome DetectWorkspace(CaptureSession session)
    {
        if (session.WorkspaceUserRoiCapturePx is null)
        {
            return new DetectOutcome
            {
                Success = false,
                Status = 103,
                StatusName = "InvalidInput",
                Message = "缺少 WorkspaceUserRoi",
                SourceCaptureId = session.CaptureId
            };
        }

        return _native.DetectWorkspace(session);
    }

    /// <summary>
    /// C-II thumbnail inside NavigatorRoi using confirmed workspace background.
    /// </summary>
    public DetectOutcome DetectNavigatorThumbnail(CaptureSession session, DetectOutcome workspace)
    {
        if (!workspace.Success || workspace.Background is null)
        {
            return new DetectOutcome
            {
                Success = false,
                Status = 101,
                StatusName = "WorkspaceDetectionFailed",
                Message = "缺少有效工作区背景模型",
                SourceCaptureId = session.CaptureId
            };
        }

        if (session.NavigatorRoiCapturePx is null)
        {
            return new DetectOutcome
            {
                Success = false,
                Status = 103,
                StatusName = "NavigatorRoiInvalid",
                Message = "缺少 NavigatorRoi",
                SourceCaptureId = session.CaptureId
            };
        }

        return _native.DetectNavigatorThumbnailCii(
            session, session.NavigatorRoiCapturePx.Value, workspace.Background);
    }

    /// <summary>
    /// Continues after workspace + thumbnail are established.
    /// </summary>
    public async Task<PipelineResult> ContinueAfterThumbnailAsync(
        CaptureSession session,
        DetectOutcome workspace,
        DetectOutcome thumbnail,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!workspace.Success || workspace.Background is null)
        {
            throw Fail(
                TransformStage.DetectingWorkspace,
                workspace.Status == 0 ? 101 : workspace.Status,
                workspace.Message,
                session,
                Generation,
                workspace.SourceRevision,
                "WorkspaceDetectionFailed");
        }

        if (!thumbnail.Success)
        {
            throw Fail(
                TransformStage.DetectingNavigatorThumbnailCII,
                thumbnail.Status == 0 ? 104 : thumbnail.Status,
                thumbnail.Message,
                session,
                Generation,
                thumbnail.SourceRevision,
                "NavigatorThumbnailCiiFailed");
        }

        if (session.NavigatorRoiCapturePx is null)
            throw Fail(TransformStage.SelectingNavigatorRoi, 103, "缺少 NavigatorRoi", session, Generation);

        Generation++;
        ulong gen = Generation;
        string captureId = session.CaptureId;

        var background = workspace.Background;
        var workspaceRoiCapture = workspace.RectCapturePx;
        var workspaceRoiScreen = workspace.RectScreenPhysicalPx;
        var navigatorRoiCapture = session.NavigatorRoiCapturePx.Value;
        var navigatorRoiScreen = session.CaptureToScreen(navigatorRoiCapture);
        var thumbnailCapture = thumbnail.RectCapturePx;
        var thumbnailScreen = thumbnail.RectScreenPhysicalPx;

        progress?.Report(TransformStage.ObservingWorkspaceCanvas);
        var wsCanvas = _native.ObserveCanvas(session, workspaceRoiCapture, background);
        if (wsCanvas.Ambiguous && !wsCanvas.FourSidesComplete)
        {
            if (wsCanvas.BoundsCapture.IsEmpty && wsCanvas.BoundsScreen.IsEmpty)
            {
                throw Fail(
                    TransformStage.ObservingWorkspaceCanvas,
                    106,
                    wsCanvas.AmbiguityReason,
                    session,
                    gen,
                    evidenceSummary: "WorkspaceCanvasAmbiguous");
            }
        }

        progress?.Report(TransformStage.ObservingNavigatorCanvas);
        var navCanvas = _native.ObserveCanvas(session, thumbnailCapture, background);
        if (navCanvas.Ambiguous || (navCanvas.BoundsCapture.IsEmpty && navCanvas.BoundsScreen.IsEmpty))
        {
            throw Fail(
                TransformStage.ObservingNavigatorCanvas,
                105,
                string.IsNullOrWhiteSpace(navCanvas.AmbiguityReason)
                    ? "NavigatorCanvasAmbiguous"
                    : navCanvas.AmbiguityReason,
                session,
                gen,
                evidenceSummary: "NavigatorCanvasAmbiguous");
        }

        progress?.Report(TransformStage.ReadingNavigatorNumbers);
        var numbers = await _ocr.ReadAsync(
                session, navigatorRoiCapture, thumbnailCapture, cancellationToken)
            .ConfigureAwait(false);
        if (numbers.ScaleConfidence < 0.2f || numbers.ScalePercent <= 0)
        {
            throw Fail(
                TransformStage.ReadingNavigatorNumbers,
                107,
                $"OcrScaleFailed raw='{numbers.ScaleRawText}'",
                session,
                gen,
                evidenceSummary: numbers.ScaleRawText);
        }
        if (numbers.RotationConfidence < 0.2f)
        {
            throw Fail(
                TransformStage.ReadingNavigatorNumbers,
                108,
                $"OcrRotationFailed raw='{numbers.RotationRawText}'",
                session,
                gen,
                evidenceSummary: numbers.RotationRawText);
        }

        NativeSct.SctViewportFrame viewport = default;
        if (!wsCanvas.FourSidesComplete || wsCanvas.Ambiguous)
        {
            progress?.Report(TransformStage.CompletingViewportFrame);
            float aspect = workspaceRoiScreen.Height > 0
                ? (float)workspaceRoiScreen.Width / workspaceRoiScreen.Height
                : 1f;
            IntRect navCanvasCapture = navCanvas.BoundsCapture.IsEmpty
                ? new IntRect(
                    navCanvas.BoundsScreen.Left - session.OriginX,
                    navCanvas.BoundsScreen.Top - session.OriginY,
                    navCanvas.BoundsScreen.Right - session.OriginX,
                    navCanvas.BoundsScreen.Bottom - session.OriginY)
                : navCanvas.BoundsCapture;

            viewport = _native.CompleteViewportFrame(
                session,
                thumbnailCapture,
                navCanvasCapture,
                aspect);

            if (viewport.Status != NativeSct.StatusOk)
            {
                throw Fail(
                    TransformStage.CompletingViewportFrame,
                    viewport.Status == 0 ? 110 : viewport.Status,
                    string.IsNullOrWhiteSpace(viewport.Message) ? "viewport failed" : viewport.Message,
                    session,
                    gen,
                    evidenceSummary: viewport.Message ?? "");
            }
        }

        progress?.Report(TransformStage.SolvingTransform);
        float initial = InitialScalePercent ?? numbers.ScalePercent;
        float previous = PreviousScalePercent ?? numbers.ScalePercent;

        var solveReq = new NativeSct.SctSolveRequest
        {
            CaptureId = captureId,
            Generation = gen,
            WorkspaceRoiScreen = NativeSct.SctIntRect.From(workspaceRoiScreen),
            NavigatorRoiScreen = NativeSct.SctIntRect.From(navigatorRoiScreen),
            NavigatorThumbnailRoiScreen = NativeSct.SctIntRect.From(thumbnailScreen),
            WorkspaceCanvas = wsCanvas.ToNative(),
            NavigatorCanvas = navCanvas.ToNative(),
            Numbers = numbers.ToNative(),
            Viewport = viewport,
            PreviousScalePercent = previous,
            InitialScalePercent = initial,
            MarkerEpsilonCanvas = 0.04
        };

        var snapshot = _native.SolveTransform(solveReq);
        if (snapshot.Status != NativeSct.StatusOk && snapshot.FailureStatus != 117)
        {
            if (snapshot.Status != NativeSct.StatusOk)
            {
                throw Fail(
                    TransformStage.SolvingTransform,
                    snapshot.Status == 0 ? snapshot.FailureStatus : snapshot.Status,
                    string.IsNullOrWhiteSpace(snapshot.FailureMessage) ? "solve failed" : snapshot.FailureMessage,
                    session,
                    gen,
                    snapshot.SourceRevision,
                    snapshot.FailureMessage);
            }
        }

        InitialScalePercent ??= numbers.ScalePercent;
        PreviousScalePercent = numbers.ScalePercent;
        LastSnapshot = snapshot;

        progress?.Report(TransformStage.ShowingCanvasTopLeftMarker);
        return new PipelineResult
        {
            Snapshot = snapshot,
            WorkspaceRoiScreen = workspaceRoiScreen,
            NavigatorRoiScreen = navigatorRoiScreen,
            NavigatorThumbnailRoiScreen = thumbnailScreen,
            Background = background,
            Stage = TransformStage.TrackingStable
        };
    }

    /// <summary>
    /// Continues after a successful workspace detect and a user-adopted NavigatorRoi.
    /// </summary>
    public async Task<PipelineResult> ContinueAfterWorkspaceAsync(
        CaptureSession session,
        DetectOutcome workspace,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(TransformStage.DetectingNavigatorThumbnailCII);
        var thumb = DetectNavigatorThumbnail(session, workspace);
        if (!thumb.Success)
        {
            throw Fail(
                TransformStage.DetectingNavigatorThumbnailCII,
                thumb.Status == 0 ? 104 : thumb.Status,
                thumb.Message,
                session,
                Generation,
                thumb.SourceRevision,
                "NavigatorThumbnailCiiFailed");
        }

        return await ContinueAfterThumbnailAsync(session, workspace, thumb, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static PipelineFailureException Fail(
        TransformStage stage,
        int status,
        string message,
        CaptureSession session,
        ulong generation,
        string sourceRevision = "",
        string evidenceSummary = "")
        => new(stage, status, message, session.CaptureId, generation, sourceRevision, evidenceSummary);
}
