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

    public float? InjectedScalePercent { get; set; }

    public int CanvasPixelWidth { get; private set; }
    public int CanvasPixelHeight { get; private set; }
    public ulong RecomputeGeneration { get; private set; }
    public float? InitialScalePercent { get; private set; }
    public float? PreviousScalePercent { get; private set; }

    public void BeginNewInitializationGeneration(int canvasPixelWidth, int canvasPixelHeight)
    {
        if (canvasPixelWidth <= 0 || canvasPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasPixelWidth));

        CanvasPixelWidth = canvasPixelWidth;
        CanvasPixelHeight = canvasPixelHeight;
        Generation = 0;
        RecomputeGeneration = 0;
        InitialScalePercent = null;
        PreviousScalePercent = null;
        LastSnapshot = null;
    }

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
        CancellationToken cancellationToken = default,
        OcrLayoutScreen? fixedOcrLayout = null,
        IntRect? navigatorRoiScreenOverride = null)
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

        if (session.NavigatorRoiCapturePx is null && navigatorRoiScreenOverride is null)
            throw Fail(TransformStage.SelectingNavigatorRoi, 103, "缺少 NavigatorRoi", session, Generation);

        Generation++;
        ulong gen = Generation;
        string captureId = session.CaptureId;

        var background = workspace.Background;
        var workspaceRoiCapture = workspace.RectCapturePx;
        var workspaceRoiScreen = workspace.RectScreenPhysicalPx;
        var navigatorRoiScreen = navigatorRoiScreenOverride
                                 ?? session.CaptureToScreen(session.NavigatorRoiCapturePx!.Value);
        var navigatorRoiCapture = session.ScreenToCapture(navigatorRoiScreen);
        var thumbnailCapture = thumbnail.RectCapturePx;
        var thumbnailScreen = thumbnail.RectScreenPhysicalPx;

        int canvasW = CanvasPixelWidth;
        int canvasH = CanvasPixelHeight;
        if (canvasW <= 0 || canvasH <= 0)
        {
            throw Fail(TransformStage.Idle, 121, "缺少画布像素尺寸", session, gen);
        }

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
        NavigatorNumericReadingDto numbers;
        if (fixedOcrLayout is OcrLayoutScreen layout)
        {
            numbers = await _ocr.ReadWithLayoutAsync(session, layout, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            numbers = await _ocr.ReadAsync(
                    session, navigatorRoiCapture, thumbnailCapture, cancellationToken)
                .ConfigureAwait(false);
        }
        if (numbers.ScaleConfidence < 0.2f || numbers.ScalePercent <= 0)
        {
            if (!(InjectedScalePercent is > 0))
            {
                throw Fail(
                    TransformStage.ReadingNavigatorNumbers,
                    107,
                    $"OcrScaleFailed raw='{numbers.ScaleRawText}'",
                    session,
                    gen,
                    evidenceSummary: numbers.ScaleRawText);
            }
        }

        NativeSct.SctViewportFrame viewport = default;
        var wsRelation = _native.BuildWorkspaceCanvasRelation(
            session,
            workspaceRoiScreen,
            wsCanvas,
            canvasW,
            canvasH);

        if (!wsCanvas.FourSidesComplete || wsCanvas.Ambiguous)
        {
            progress?.Report(TransformStage.CompletingViewportFrame);
            IntRect navCanvasCapture = navCanvas.BoundsCapture.IsEmpty
                ? session.ScreenToCapture(navCanvas.BoundsScreen)
                : navCanvas.BoundsCapture;

            viewport = _native.CompleteViewportFrame(
                session,
                thumbnailCapture,
                navCanvasCapture,
                wsRelation);

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
        float injectedScale = InjectedScalePercent ?? 0f;

        var solveReq = new NativeSct.SctSolveRequest
        {
            CaptureId = captureId,
            Generation = gen,
            RecomputeGeneration = RecomputeGeneration,
            CanvasPixelWidth = canvasW,
            CanvasPixelHeight = canvasH,
            WorkspaceRoiScreen = NativeSct.SctIntRect.From(workspaceRoiScreen),
            NavigatorRoiScreen = NativeSct.SctIntRect.From(navigatorRoiScreen),
            NavigatorThumbnailRoiScreen = NativeSct.SctIntRect.From(thumbnailScreen),
            WorkspaceCanvas = wsCanvas.ToNative(),
            NavigatorCanvas = navCanvas.ToNative(),
            WorkspaceCanvasRelation = wsRelation,
            Numbers = numbers.ToNative(),
            Viewport = viewport,
            PreviousScalePercent = previous,
            InitialScalePercent = initial,
            InjectedScalePercent = injectedScale,
            RequireOcrRotation = 0,
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

        InitialScalePercent ??= injectedScale > 0 ? injectedScale : numbers.ScalePercent;
        PreviousScalePercent = injectedScale > 0 ? injectedScale : numbers.ScalePercent;
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
    /// Explicit recompute: fresh capture, same screen ROIs and canvas pixel size, full evidence re-solve.
    /// </summary>
    public async Task<PipelineResult> RecomputeAsync(
        CaptureSession session,
        PipelineResult previous,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(TransformStage.RecomputeRequested);
        RecomputeGeneration++;
        Generation++;

        progress?.Report(TransformStage.ReacquiringEvidence);

        var workspace = new DetectOutcome
        {
            Success = true,
            RectCapturePx = session.ScreenToCapture(previous.WorkspaceRoiScreen),
            RectScreenPhysicalPx = previous.WorkspaceRoiScreen,
            Background = previous.Background,
            SourceCaptureId = session.CaptureId,
            Confidence = 1f
        };

        if (!session.TrySetRoi(RoiKind.Navigator, session.ScreenToCapture(previous.NavigatorRoiScreen), out string navErr))
            throw Fail(TransformStage.ReacquiringEvidence, 103, navErr, session, Generation);

        var thumbnail = await Task.Run(() => DetectNavigatorThumbnail(session, workspace), cancellationToken)
            .ConfigureAwait(false);
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

        return await ContinueAfterThumbnailAsync(session, workspace, thumbnail, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Archive recompute: fresh capture, archive system anchors, fixed OcrLayout, no user ROI.
    /// </summary>
    public async Task<PipelineResult> RecomputeFromArchiveAsync(
        CaptureSession session,
        SaveArchive archive,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(TransformStage.ArchiveRecomputeRequested);
        ValidateArchiveForSession(session, archive);

        CanvasPixelWidth = archive.CanvasPixelWidth;
        CanvasPixelHeight = archive.CanvasPixelHeight;

        RecomputeGeneration++;
        Generation++;

        progress?.Report(TransformStage.ReacquiringEvidence);

        var workspace = new DetectOutcome
        {
            Success = true,
            RectCapturePx = session.ScreenToCapture(archive.SystemWorkspaceRoiScreen.ToIntRect()),
            RectScreenPhysicalPx = archive.SystemWorkspaceRoiScreen.ToIntRect(),
            Background = archive.Background,
            SourceCaptureId = session.CaptureId,
            Confidence = archive.Background.Confidence
        };

        var thumbnailScreen = archive.SystemNavigatorThumbnailRoiScreen.ToIntRect();
        var thumbnail = new DetectOutcome
        {
            Success = true,
            RectCapturePx = session.ScreenToCapture(thumbnailScreen),
            RectScreenPhysicalPx = thumbnailScreen,
            SourceCaptureId = session.CaptureId,
            Confidence = 1f
        };

        var ocrLayout = OcrLayoutScreen.FromDto(archive.OcrLayout);
        IntRect navigatorRoiScreen = DeriveNavigatorRoiForSolve(thumbnailScreen, ocrLayout);

        return await ContinueAfterThumbnailAsync(
                session,
                workspace,
                thumbnail,
                progress,
                cancellationToken,
                fixedOcrLayout: ocrLayout,
                navigatorRoiScreenOverride: navigatorRoiScreen)
            .ConfigureAwait(false);
    }

    private static IntRect DeriveNavigatorRoiForSolve(IntRect thumbnailScreen, OcrLayoutScreen ocrLayout)
    {
        IntRect band = ocrLayout.PrimarySearchBandScreen;
        int left = Math.Min(thumbnailScreen.Left, band.Left);
        int top = Math.Min(thumbnailScreen.Top, band.Top);
        int right = Math.Max(thumbnailScreen.Right, band.Right);
        int bottom = Math.Max(thumbnailScreen.Bottom, band.Bottom);
        return new IntRect(left, top, right, bottom);
    }

    private static void ValidateArchiveForSession(CaptureSession session, SaveArchive archive)
    {
        if (archive.CanvasPixelWidth <= 0 || archive.CanvasPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(archive), "存档画布尺寸无效");

        ValidateMappedRect(session, archive.SystemWorkspaceRoiScreen.ToIntRect(), "SystemWorkspaceRoiScreen");
        ValidateMappedRect(session, archive.SystemNavigatorThumbnailRoiScreen.ToIntRect(), "SystemNavigatorThumbnailRoiScreen");
        ValidateMappedRect(session, archive.OcrLayout.PrimarySearchBandScreen.ToIntRect(), "OcrLayout.PrimarySearchBandScreen");
        ValidateMappedRect(session, archive.OcrLayout.LeftHalfSearchBandScreen.ToIntRect(), "OcrLayout.LeftHalfSearchBandScreen");
    }

    private static void ValidateMappedRect(CaptureSession session, IntRect screenRect, string name)
    {
        var mapped = session.ScreenToCapture(screenRect).ClampTo(session.CaptureBounds);
        if (mapped.IsEmpty || mapped.Width < CaptureSession.MinRoiSizePx || mapped.Height < CaptureSession.MinRoiSizePx)
        {
            throw new PipelineFailureException(
                TransformStage.ReacquiringEvidence,
                107,
                $"存档 {name} 映射到当前截图后无效",
                session.CaptureId,
                0);
        }
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
