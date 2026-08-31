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
    /// <summary>OCR slots that succeeded during init (preferred when persisting archive).</summary>
    public OcrLayoutScreen? OcrLayoutUsed { get; init; }
}

/// <summary>
/// Frozen system anchors for recompute. Coordinates are ScreenPhysicalPx.
/// Recompute MUST inject these as-is; MUST NOT rediscover via DetectWorkspace / C-II.
/// </summary>
public sealed class RecomputeAnchorSet
{
    public int CanvasPixelWidth { get; init; }
    public int CanvasPixelHeight { get; init; }
    public IntRect SystemWorkspaceRoiScreen { get; init; }
    public WorkspaceBackgroundModel WorkspaceBackgroundModel { get; init; } = null!;
    public IntRect SystemNavigatorThumbnailRoiScreen { get; init; }
    public OcrLayoutScreen OcrLayout { get; init; }

    public static RecomputeAnchorSet FromArchive(SaveArchive archive)
        => new()
        {
            CanvasPixelWidth = archive.CanvasPixelWidth,
            CanvasPixelHeight = archive.CanvasPixelHeight,
            SystemWorkspaceRoiScreen = archive.SystemWorkspaceRoiScreen.ToIntRect(),
            WorkspaceBackgroundModel = archive.Background,
            SystemNavigatorThumbnailRoiScreen = archive.SystemNavigatorThumbnailRoiScreen.ToIntRect(),
            OcrLayout = OcrLayoutScreen.FromDto(archive.OcrLayout)
        };

    /// <summary>
    /// Session recompute anchors from last verified pipeline result.
    /// Requires <see cref="PipelineResult.OcrLayoutUsed"/>; does not use panel-level NavigatorRoi.
    /// <paramref name="canvasPixelWidth"/>/<paramref name="canvasPixelHeight"/> come from pipeline memory.
    /// </summary>
    public static bool TryFromSession(
        PipelineResult previous,
        int canvasPixelWidth,
        int canvasPixelHeight,
        out RecomputeAnchorSet? anchors,
        out string error)
    {
        anchors = null;
        error = "";

        if (previous.OcrLayoutUsed is not OcrLayoutScreen layout)
        {
            error = "会话缺少已固化的 OcrLayout，请从存档开始或重新初始化";
            return false;
        }

        if (previous.Background is null)
        {
            error = "会话缺少 WorkspaceBackgroundModel";
            return false;
        }

        if (previous.NavigatorThumbnailRoiScreen.IsEmpty
            || previous.NavigatorThumbnailRoiScreen.Width < CaptureSession.MinRoiSizePx
            || previous.NavigatorThumbnailRoiScreen.Height < CaptureSession.MinRoiSizePx)
        {
            error = "会话缺少有效的系统缩略图矩形 NavigatorThumbnailRoiScreen";
            return false;
        }

        int canvasW = canvasPixelWidth > 0
            ? canvasPixelWidth
            : previous.Snapshot?.CanvasPixelWidth ?? 0;
        int canvasH = canvasPixelHeight > 0
            ? canvasPixelHeight
            : previous.Snapshot?.CanvasPixelHeight ?? 0;
        if (canvasW <= 0 || canvasH <= 0)
        {
            error = "会话缺少有效的画布像素尺寸";
            return false;
        }

        anchors = new RecomputeAnchorSet
        {
            CanvasPixelWidth = canvasW,
            CanvasPixelHeight = canvasH,
            SystemWorkspaceRoiScreen = previous.WorkspaceRoiScreen,
            WorkspaceBackgroundModel = previous.Background,
            SystemNavigatorThumbnailRoiScreen = previous.NavigatorThumbnailRoiScreen,
            OcrLayout = layout
        };
        return true;
    }
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
        OcrLayoutScreen? ocrLayoutUsed = fixedOcrLayout;
        if (fixedOcrLayout is OcrLayoutScreen layout)
        {
            numbers = await _ocr.ReadWithLayoutAsync(session, layout, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            throw Fail(
                TransformStage.ReadingNavigatorNumbers,
                107,
                "缺少固化 OcrLayout（初始化须用户框选数字区；重算须来自存档）",
                session,
                gen,
                evidenceSummary: "MissingFixedOcrLayout");
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
            Stage = TransformStage.TrackingStable,
            OcrLayoutUsed = ocrLayoutUsed
        };
    }

    /// <summary>
    /// Session recompute: map verified system anchors → same <see cref="RecomputeCoreAsync"/> as archive.
    /// MUST NOT run C-II / DetectWorkspace / derive OCR layout.
    /// </summary>
    public async Task<PipelineResult> RecomputeAsync(
        CaptureSession session,
        PipelineResult previous,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(TransformStage.RecomputeRequested);

        if (!RecomputeAnchorSet.TryFromSession(
                previous, CanvasPixelWidth, CanvasPixelHeight, out var anchors, out string error)
            || anchors is null)
        {
            throw Fail(
                TransformStage.ReacquiringEvidence,
                107,
                error,
                session,
                Generation,
                evidenceSummary: "MissingOcrLayoutOrAnchors");
        }

        return await RecomputeCoreAsync(session, anchors, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Archive recompute: archive system anchors → same <see cref="RecomputeCoreAsync"/> as session.
    /// </summary>
    public async Task<PipelineResult> RecomputeFromArchiveAsync(
        CaptureSession session,
        SaveArchive archive,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(TransformStage.ArchiveRecomputeRequested);
        var anchors = RecomputeAnchorSet.FromArchive(archive);
        return await RecomputeCoreAsync(session, anchors, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared recompute core: inject frozen anchors, refresh evidence, re-solve. No detection.
    /// </summary>
    public async Task<PipelineResult> RecomputeCoreAsync(
        CaptureSession session,
        RecomputeAnchorSet anchors,
        IProgress<TransformStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAnchorsOnCapture(session, anchors);

        CanvasPixelWidth = anchors.CanvasPixelWidth;
        CanvasPixelHeight = anchors.CanvasPixelHeight;

        RecomputeGeneration++;
        Generation++;

        progress?.Report(TransformStage.ReacquiringEvidence);

        var workspaceScreen = anchors.SystemWorkspaceRoiScreen;
        var thumbnailScreen = anchors.SystemNavigatorThumbnailRoiScreen;
        var ocrLayout = anchors.OcrLayout;

        var workspace = new DetectOutcome
        {
            Success = true,
            RectCapturePx = session.ScreenToCapture(workspaceScreen),
            RectScreenPhysicalPx = workspaceScreen,
            Background = anchors.WorkspaceBackgroundModel,
            SourceCaptureId = session.CaptureId,
            Confidence = anchors.WorkspaceBackgroundModel.Confidence
        };

        var thumbnail = new DetectOutcome
        {
            Success = true,
            RectCapturePx = session.ScreenToCapture(thumbnailScreen),
            RectScreenPhysicalPx = thumbnailScreen,
            SourceCaptureId = session.CaptureId,
            Confidence = 1f
        };

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
        IntRect scale = ocrLayout.ScaleSlotScreen;
        IntRect rotation = ocrLayout.RotationSlotScreen;
        int left = Math.Min(Math.Min(thumbnailScreen.Left, scale.Left), rotation.Left);
        int top = Math.Min(Math.Min(thumbnailScreen.Top, scale.Top), rotation.Top);
        int right = Math.Max(Math.Max(thumbnailScreen.Right, scale.Right), rotation.Right);
        int bottom = Math.Max(Math.Max(thumbnailScreen.Bottom, scale.Bottom), rotation.Bottom);
        return new IntRect(left, top, right, bottom);
    }

    private static void ValidateAnchorsOnCapture(CaptureSession session, RecomputeAnchorSet anchors)
    {
        if (anchors.CanvasPixelWidth <= 0 || anchors.CanvasPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchors), "锚点画布尺寸无效");

        ValidateMappedRect(session, anchors.SystemWorkspaceRoiScreen, "SystemWorkspaceRoiScreen");
        ValidateMappedRect(session, anchors.SystemNavigatorThumbnailRoiScreen, "SystemNavigatorThumbnailRoiScreen");
        ValidateMappedRect(session, anchors.OcrLayout.ScaleSlotScreen, "OcrLayout.ScaleSlotScreen", minSizePx: 8);
        ValidateMappedRect(session, anchors.OcrLayout.RotationSlotScreen, "OcrLayout.RotationSlotScreen", minSizePx: 8);
    }

    private static void ValidateMappedRect(CaptureSession session, IntRect screenRect, string name, int minSizePx = CaptureSession.MinRoiSizePx)
    {
        var mapped = session.ScreenToCapture(screenRect).ClampTo(session.CaptureBounds);
        if (mapped.IsEmpty || mapped.Width < minSizePx || mapped.Height < minSizePx)
        {
            throw new PipelineFailureException(
                TransformStage.ReacquiringEvidence,
                107,
                $"{name} 映射到当前截图后无效（请检查 CSP 窗口位置/缩放/布局是否相对存档变化）",
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
