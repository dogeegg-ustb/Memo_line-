using System.Drawing;
using System.Drawing.Imaging;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Detection;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ocr;
using ScreenCanvasTransform.Services;
using ScreenCanvasTransform.State;

namespace ScreenCanvasTransform.Core;

public sealed class ScreenCanvasTransformCoreConfig
{
    /// <summary>Optional PP-OCRv5 model directory used only after Windows OCR misses a value.</summary>
    public string? RapidOcrModelDirectory { get; init; }
}

public sealed class ScreenCanvasTransformInput
{
    public required Bitmap Image { get; init; }
    public required IntRect WorkspaceRoi { get; init; }
    public required IntRect NavigatorRoi { get; init; }
    public required IntRect OcrNumbersRoi { get; init; }
    public required int CanvasPixelWidth { get; init; }
    public required int CanvasPixelHeight { get; init; }
    public int OriginX { get; init; }
    public int OriginY { get; init; }
    public float DpiX { get; init; } = 96;
    public float DpiY { get; init; } = 96;
    public float? InjectedScalePercent { get; init; }
}

/// <summary>One same-frame crop and its top-left position in ScreenPhysicalPx.</summary>
public sealed class ScreenCanvasRegionCapture
{
    public required Bitmap Image { get; init; }
    public required int ScreenX { get; init; }
    public required int ScreenY { get; init; }
}

public sealed class ScreenCanvasTransformInitializationInput
{
    public required ScreenCanvasRegionCapture Workspace { get; init; }
    public required ScreenCanvasRegionCapture Navigator { get; init; }
    public required ScreenCanvasRegionCapture OcrNumbers { get; init; }
    /// <summary>Canvas document resolution in pixels, not the screenshot size.</summary>
    public required int CanvasPixelWidth { get; init; }
    public required int CanvasPixelHeight { get; init; }
    public float DpiX { get; init; } = 96;
    public float DpiY { get; init; } = 96;
    public float? InjectedScalePercent { get; init; }
}

/// <summary>Three current-frame crops for one frozen-anchor recomputation.</summary>
public sealed class ScreenCanvasTransformFrameInput
{
    public required ScreenCanvasRegionCapture Workspace { get; init; }
    public required ScreenCanvasRegionCapture Navigator { get; init; }
    public required ScreenCanvasRegionCapture OcrNumbers { get; init; }
    public float DpiX { get; init; } = 96;
    public float DpiY { get; init; } = 96;
    public float? InjectedScalePercent { get; init; }
}

/// <summary>Point in ScreenPhysicalPx; the canvas origin is its top-left (0, 0).</summary>
public readonly record struct ScreenPoint(double X, double Y);

public sealed class ScreenCanvasTransformCoreResult
{
    public bool Success { get; init; }
    /// <summary>Corrected canvas-window/workspace ROI in ScreenPhysicalPx.</summary>
    public IntRect? CanvasWindowRoiScreenPx { get; init; }
    /// <summary>Detected Navigator thumbnail ROI in ScreenPhysicalPx.</summary>
    public IntRect? NavigatorThumbnailRoiScreenPx { get; init; }
    /// <summary>Top-left of the supplied capture in ScreenPhysicalPx.</summary>
    public ScreenPoint? ScreenCoordinateOriginScreenPx { get; init; }
    /// <summary>Calculated top-left canvas origin in physical screen pixels.</summary>
    public ScreenPoint? CanvasOriginScreenPx { get; init; }
    /// <summary>Scale read by OCR, in percent; null when OCR did not read it reliably.</summary>
    public float? OcrScalePercent { get; init; }
    /// <summary>Rotation read by OCR, in degrees; null when OCR did not read it reliably.</summary>
    public float? OcrRotationDegrees { get; init; }
    /// <summary>Detailed solver state retained for archive and diagnostics.</summary>
    public TransformSnapshotDto? Snapshot { get; init; }
    public TransformStage? FailedStage { get; init; }
    public int Status { get; init; }
    public string Message { get; init; } = "";
    /// <summary>Underlying algorithm result, exposed for the preserved archive API.</summary>
    public PipelineResult? PipelineState { get; internal init; }
}

/// <summary>Runs the existing workspace → navigator → OCR → transform pipeline once per supplied frame.</summary>
public sealed class ScreenCanvasTransformCore : IDisposable
{
    private readonly TransformPipelineService _pipeline;
    private ScreenCanvasTransformCoreResult? _lastResult;

    public ScreenCanvasTransformCore(ScreenCanvasTransformCoreConfig? config = null)
    {
        _pipeline = new TransformPipelineService(config?.RapidOcrModelDirectory);
    }

    /// <summary>Initialize from three crops of one frozen screen frame, using the original OCR calibration path.</summary>
    public async Task<ScreenCanvasTransformCoreResult> InitializeAsync(
        ScreenCanvasTransformInitializationInput input,
        CancellationToken cancellationToken = default)
    {
        using var image = ComposeInitializationFrame(input, out var composed);
        return await ProcessInitializationAsync(composed, calibrateOcr: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ScreenCanvasTransformCoreResult> ProcessAsync(
        ScreenCanvasTransformInput input,
        CancellationToken cancellationToken = default)
        => ProcessInitializationAsync(input, calibrateOcr: false, cancellationToken);

    /// <summary>Initialize on a caller's same-frame ROI context without synthesizing black pixels between crops.</summary>
    public Task<ScreenCanvasTransformCoreResult> InitializeFrameAsync(
        ScreenCanvasTransformInput input,
        CancellationToken cancellationToken = default)
        => ProcessInitializationAsync(input, calibrateOcr: true, cancellationToken);

    private async Task<ScreenCanvasTransformCoreResult> ProcessInitializationAsync(
        ScreenCanvasTransformInput input,
        bool calibrateOcr,
        CancellationToken cancellationToken)
    {
        Validate(input);
        _lastResult = null;
        using var frame = ToArgb32(input.Image);
        using var session = new CaptureSession(
            Guid.NewGuid().ToString("N"), frame, input.OriginX, input.OriginY, input.DpiX, input.DpiY);
        if (!session.TrySetRoi(RoiKind.WorkspaceUser, input.WorkspaceRoi, out string error)
            || !session.TrySetRoi(RoiKind.Navigator, input.NavigatorRoi, out error)
            || !session.TrySetRoi(RoiKind.OcrNumbers, input.OcrNumbersRoi, out error))
            throw new ArgumentException(error, nameof(input));

        _pipeline.BeginNewInitializationGeneration(input.CanvasPixelWidth, input.CanvasPixelHeight);
        _pipeline.InjectedScalePercent = input.InjectedScalePercent;
        var workspace = _pipeline.DetectWorkspace(session);
        if (!workspace.Success)
            return Failed(TransformStage.DetectingWorkspace, workspace.Status, workspace.Message);

        var thumbnail = _pipeline.DetectNavigatorThumbnail(session, workspace);
        if (!thumbnail.Success)
            return Failed(TransformStage.DetectingNavigatorThumbnailCII, thumbnail.Status, thumbnail.Message);

        var numbersRegion = session.CaptureToScreen(input.OcrNumbersRoi);
        try
        {
            OcrLayoutScreen ocrLayout;
            if (calibrateOcr)
            {
                var probe = await _pipeline.TryReadUserRegionAsync(session, numbersRegion, cancellationToken)
                    .ConfigureAwait(false);
                if (!probe.Ok)
                    return Failed(TransformStage.ReadingNavigatorNumbers, 107,
                        $"OCR did not read both scale and rotation (scale='{probe.Numbers.ScaleRawText}', rotation='{probe.Numbers.RotationRawText}').");
                ocrLayout = probe.Layout;
            }
            else
            {
                ocrLayout = NavigatorOcrService.LayoutFromUserRegion(numbersRegion);
            }
            var result = await _pipeline.ContinueAfterThumbnailAsync(
                session, workspace, thumbnail,
                cancellationToken: cancellationToken,
                fixedOcrLayout: ocrLayout).ConfigureAwait(false);
            return Succeeded(result, input.OriginX, input.OriginY);
        }
        catch (PipelineFailureException ex)
        {
            return Failed(ex.Stage, ex.Status, ex.Message);
        }
    }

    /// <summary>Re-solve a later supplied frame using anchors from the previous successful result.</summary>
    public async Task<ScreenCanvasTransformCoreResult> RecomputeAsync(
        ScreenCanvasTransformInput input,
        ScreenCanvasTransformCoreResult previous,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        if (!ReferenceEquals(previous, _lastResult) || previous.PipelineState is null)
            throw new ArgumentException("previous must be the last successful result from this core instance.", nameof(previous));

        using var frame = ToArgb32(input.Image);
        using var session = new CaptureSession(
            Guid.NewGuid().ToString("N"), frame, input.OriginX, input.OriginY, input.DpiX, input.DpiY);
        _pipeline.InjectedScalePercent = input.InjectedScalePercent;
        try
        {
            var result = await _pipeline.RecomputeAsync(
                session, previous.PipelineState, cancellationToken).ConfigureAwait(false);
            return Succeeded(result, input.OriginX, input.OriginY);
        }
        catch (PipelineFailureException ex)
        {
            return Failed(ex.Stage, ex.Status, ex.Message);
        }
    }

    /// <summary>Recompute from the last successful initialization/session anchors, without re-detecting ROIs.</summary>
    public Task<ScreenCanvasTransformCoreResult> RecomputeAsync(
        Bitmap image,
        int originX = 0,
        int originY = 0,
        float dpiX = 96,
        float dpiY = 96,
        float? injectedScalePercent = null,
        CancellationToken cancellationToken = default)
        => RecomputeFrameAsync(image, originX, originY, dpiX, dpiY,
            injectedScalePercent, cancellationToken);

    private async Task<ScreenCanvasTransformCoreResult> RecomputeFrameAsync(
        Bitmap image, int originX, int originY, float dpiX, float dpiY,
        float? injectedScalePercent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateFrameOptions(dpiX, dpiY, injectedScalePercent);
        if (_lastResult is not { Success: true, PipelineState: not null })
            throw new InvalidOperationException("Initialize successfully before recomputing.");
        using var frame = ToArgb32(image);
        using var session = new CaptureSession(
            Guid.NewGuid().ToString("N"), frame, originX, originY, dpiX, dpiY);
        _pipeline.InjectedScalePercent = injectedScalePercent;
        try
        {
            var result = await _pipeline.RecomputeAsync(session, _lastResult.PipelineState, cancellationToken)
                .ConfigureAwait(false);
            return Succeeded(result, originX, originY);
        }
        catch (PipelineFailureException ex)
        {
            return Failed(ex.Stage, ex.Status, ex.Message);
        }
    }

    /// <summary>Recompute from three current crops using the frozen anchors from initialization.</summary>
    public async Task<ScreenCanvasTransformCoreResult> RecomputeAsync(
        ScreenCanvasTransformFrameInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_lastResult is not { Success: true, PipelineState: { } previous, Snapshot: { } snapshot })
            throw new InvalidOperationException("Initialize successfully before recomputing.");
        if (previous.OcrLayoutUsed is not OcrLayoutScreen layout)
            throw new InvalidOperationException("The previous result has no calibrated OCR layout.");
        ValidateRecomputeCoverage(input, previous.WorkspaceRoiScreen,
            previous.NavigatorThumbnailRoiScreen, layout);
        using var image = ComposeFrame(input, snapshot.CanvasPixelWidth, snapshot.CanvasPixelHeight, out var composed);
        return await RecomputeFrameAsync(image, composed.OriginX, composed.OriginY,
            input.DpiX, input.DpiY, input.InjectedScalePercent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-solve a supplied frame from the frozen anchors in a validated save archive.</summary>
    public Task<ScreenCanvasTransformCoreResult> RecomputeFromArchiveAsync(
        Bitmap image,
        SaveArchive archive,
        int originX = 0,
        int originY = 0,
        float dpiX = 96,
        float dpiY = 96,
        float? injectedScalePercent = null,
        CancellationToken cancellationToken = default)
        => RecomputeArchiveFrameAsync(image, archive, originX, originY, dpiX, dpiY,
            injectedScalePercent, cancellationToken);

    private async Task<ScreenCanvasTransformCoreResult> RecomputeArchiveFrameAsync(
        Bitmap image, SaveArchive archive, int originX, int originY, float dpiX, float dpiY,
        float? injectedScalePercent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(archive);
        string? archiveError = SaveArchiveService.ValidateArchive(archive);
        if (archiveError is not null)
            throw new ArgumentException($"Invalid save archive: {archiveError}", nameof(archive));
        ValidateFrameOptions(dpiX, dpiY, injectedScalePercent);

        using var frame = ToArgb32(image);
        using var session = new CaptureSession(
            Guid.NewGuid().ToString("N"), frame, originX, originY, dpiX, dpiY);
        _pipeline.InjectedScalePercent = injectedScalePercent;
        try
        {
            var result = await _pipeline.RecomputeFromArchiveAsync(session, archive, cancellationToken)
                .ConfigureAwait(false);
            return Succeeded(result, originX, originY);
        }
        catch (PipelineFailureException ex)
        {
            return Failed(ex.Stage, ex.Status, ex.Message);
        }
    }

    /// <summary>Recompute from a saved archive using three current crops, without re-detecting anchors.</summary>
    public async Task<ScreenCanvasTransformCoreResult> RecomputeFromArchiveAsync(
        ScreenCanvasTransformFrameInput input,
        SaveArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(archive);
        string? archiveError = SaveArchiveService.ValidateArchive(archive);
        if (archiveError is not null)
            throw new ArgumentException($"Invalid save archive: {archiveError}", nameof(archive));
        ValidateRecomputeCoverage(input, archive.SystemWorkspaceRoiScreen.ToIntRect(),
            archive.SystemNavigatorThumbnailRoiScreen.ToIntRect(), OcrLayoutScreen.FromDto(archive.OcrLayout));
        using var image = ComposeFrame(input, archive.CanvasPixelWidth, archive.CanvasPixelHeight, out var composed);
        return await RecomputeArchiveFrameAsync(image, archive, composed.OriginX, composed.OriginY,
            input.DpiX, input.DpiY, input.InjectedScalePercent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Save initialization data and the original same-frame visual fingerprint.</summary>
    public SaveArchiveOperationResult TryCreateArchive(
        ScreenCanvasTransformInput input,
        ScreenCanvasTransformCoreResult initialized,
        SaveArchiveService archiveService,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(archiveService);
        ArgumentNullException.ThrowIfNull(initialized);
        Validate(input);
        if (!ReferenceEquals(initialized, _lastResult)
            || !initialized.Success
            || initialized.PipelineState is null
            || initialized.Snapshot is null)
            throw new ArgumentException("initialized must be the last successful ProcessAsync result from this core instance.", nameof(initialized));

        using var frame = ToArgb32(input.Image);
        using var session = new CaptureSession(
            initialized.Snapshot.CaptureId, frame, input.OriginX, input.OriginY, input.DpiX, input.DpiY);
        var bundle = new InitSuccessBundle
        {
            Result = initialized.PipelineState,
            InitCaptureId = initialized.Snapshot.CaptureId,
            CaptureSession = session,
            NavigatorPanelScreenAtInit = session.CaptureToScreen(input.NavigatorRoi)
        };
        return archiveService.TryCreateFromInitSuccess(bundle, displayName);
    }

    /// <summary>Preserve archive creation for the three-crop initialization entry point.</summary>
    public SaveArchiveOperationResult TryCreateArchive(
        ScreenCanvasTransformInitializationInput input,
        ScreenCanvasTransformCoreResult initialized,
        SaveArchiveService archiveService,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(initialized);
        if (initialized.Success && initialized.PipelineState is { } state
            && (!ContainsExteriorPixels(input.Workspace, state.WorkspaceRoiScreen)
                || !ContainsExteriorPixels(input.Navigator, state.NavigatorThumbnailRoiScreen)))
            return new SaveArchiveOperationResult
            {
                Error = "Initialization crops must include the exterior pixels around both corrected ROIs for the archive fingerprint."
            };
        using var image = ComposeInitializationFrame(input, out var composed);
        return TryCreateArchive(composed, initialized, archiveService, displayName);
    }

    public void Dispose() => _pipeline.Dispose();

    private ScreenCanvasTransformCoreResult Succeeded(PipelineResult result, int captureOriginX, int captureOriginY)
    {
        var snapshot = result.Snapshot;
        var origin = snapshot.Marker.AnchorScreen;
        return _lastResult = new ScreenCanvasTransformCoreResult
        {
            Success = true,
            CanvasWindowRoiScreenPx = result.WorkspaceRoiScreen,
            NavigatorThumbnailRoiScreenPx = result.NavigatorThumbnailRoiScreen,
            ScreenCoordinateOriginScreenPx = new ScreenPoint(captureOriginX, captureOriginY),
            CanvasOriginScreenPx = double.IsFinite(origin.X) && double.IsFinite(origin.Y)
                ? new ScreenPoint(origin.X, origin.Y) : null,
            OcrScalePercent = snapshot.Numbers.ScaleConfidence >= 0.2f
                && float.IsFinite(snapshot.Numbers.ScalePercent) && snapshot.Numbers.ScalePercent > 0
                    ? snapshot.Numbers.ScalePercent : null,
            OcrRotationDegrees = snapshot.Numbers.RotationConfidence >= 0.2f
                && float.IsFinite(snapshot.Numbers.RotationDegrees)
                    ? snapshot.Numbers.RotationDegrees : null,
            Snapshot = snapshot,
            PipelineState = result,
        };
    }

    private static ScreenCanvasTransformCoreResult Failed(TransformStage stage, int status, string message)
        => new() { Success = false, FailedStage = stage, Status = status, Message = message };

    private static void Validate(ScreenCanvasTransformInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Image);
        if (input.CanvasPixelWidth <= 0 || input.CanvasPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Canvas pixel dimensions must be positive.");
        ValidateFrameOptions(input.DpiX, input.DpiY, input.InjectedScalePercent);
        ValidateRoi(input.WorkspaceRoi, input.Image, CaptureSession.MinRoiSizePx, nameof(input.WorkspaceRoi));
        ValidateRoi(input.NavigatorRoi, input.Image, CaptureSession.MinRoiSizePx, nameof(input.NavigatorRoi));
        ValidateRoi(input.OcrNumbersRoi, input.Image, CaptureSession.MinRoiSizePx, nameof(input.OcrNumbersRoi));
    }

    private static void ValidateFrameOptions(float dpiX, float dpiY, float? injectedScalePercent)
    {
        if (!float.IsFinite(dpiX) || !float.IsFinite(dpiY) || dpiX <= 0 || dpiY <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiX), "DPI values must be finite and positive.");
        if (injectedScalePercent is float scale && (!float.IsFinite(scale) || scale <= 0))
            throw new ArgumentOutOfRangeException(nameof(injectedScalePercent), "InjectedScalePercent must be finite and positive.");
    }

    private static void ValidateRoi(IntRect roi, Bitmap image, int minSize, string name)
    {
        if (roi.IsEmpty || roi.Width < minSize || roi.Height < minSize
            || roi.Left < 0 || roi.Top < 0 || roi.Right > image.Width || roi.Bottom > image.Height)
            throw new ArgumentException($"{name} must be at least {minSize}×{minSize} pixels and inside Image.", name);
    }

    private static Bitmap ToArgb32(Bitmap source)
    {
        // Clone a pixel rectangle directly. Graphics.DrawImageUnscaled can apply
        // physical DPI sizing when source and destination resolutions differ.
        return source.Clone(new Rectangle(0, 0, source.Width, source.Height),
            PixelFormat.Format32bppArgb);
    }

    private static Bitmap ComposeInitializationFrame(
        ScreenCanvasTransformInitializationInput input,
        out ScreenCanvasTransformInput composed)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CanvasPixelWidth <= 0 || input.CanvasPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Canvas pixel dimensions must be positive.");
        ValidateFrameOptions(input.DpiX, input.DpiY, input.InjectedScalePercent);
        var crops = new[] { input.Workspace, input.Navigator, input.OcrNumbers };
        foreach (var crop in crops)
        {
            ArgumentNullException.ThrowIfNull(crop);
            ArgumentNullException.ThrowIfNull(crop.Image);
            if (crop.Image.Width < CaptureSession.MinRoiSizePx
                || crop.Image.Height < CaptureSession.MinRoiSizePx)
                throw new ArgumentException("Each crop must be at least 32×32 pixels.", nameof(input));
        }
        long left = crops.Min(c => (long)c.ScreenX);
        long top = crops.Min(c => (long)c.ScreenY);
        long right = crops.Max(c => (long)c.ScreenX + c.Image.Width);
        long bottom = crops.Max(c => (long)c.ScreenY + c.Image.Height);
        int width = checked((int)(right - left));
        int height = checked((int)(bottom - top));
        int originX = checked((int)left);
        int originY = checked((int)top);
        var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            foreach (var crop in crops)
                graphics.DrawImage(crop.Image,
                    new Rectangle(checked(crop.ScreenX - originX), checked(crop.ScreenY - originY),
                        crop.Image.Width, crop.Image.Height),
                    new Rectangle(0, 0, crop.Image.Width, crop.Image.Height), GraphicsUnit.Pixel);
        }
        IntRect Roi(ScreenCanvasRegionCapture crop) => IntRect.FromXYWH(
            checked(crop.ScreenX - originX), checked(crop.ScreenY - originY),
            crop.Image.Width, crop.Image.Height);
        composed = new ScreenCanvasTransformInput
        {
            Image = image,
            WorkspaceRoi = Roi(input.Workspace),
            NavigatorRoi = Roi(input.Navigator),
            OcrNumbersRoi = Roi(input.OcrNumbers),
            CanvasPixelWidth = input.CanvasPixelWidth,
            CanvasPixelHeight = input.CanvasPixelHeight,
            OriginX = originX,
            OriginY = originY,
            DpiX = input.DpiX,
            DpiY = input.DpiY,
            InjectedScalePercent = input.InjectedScalePercent
        };
        return image;
    }

    private static bool ContainsExteriorPixels(ScreenCanvasRegionCapture crop, IntRect roi)
    {
        int margin = ArchiveVisualFingerprintService.NormalBandDepth;
        long left = crop.ScreenX;
        long top = crop.ScreenY;
        long right = left + crop.Image.Width;
        long bottom = top + crop.Image.Height;
        return roi.Left - margin >= left && roi.Top - margin >= top
            && (long)roi.Right + margin <= right && (long)roi.Bottom + margin <= bottom;
    }

    private static Bitmap ComposeFrame(
        ScreenCanvasTransformFrameInput input,
        int canvasPixelWidth,
        int canvasPixelHeight,
        out ScreenCanvasTransformInput composed)
        => ComposeInitializationFrame(new ScreenCanvasTransformInitializationInput
        {
            Workspace = input.Workspace,
            Navigator = input.Navigator,
            OcrNumbers = input.OcrNumbers,
            CanvasPixelWidth = canvasPixelWidth,
            CanvasPixelHeight = canvasPixelHeight,
            DpiX = input.DpiX,
            DpiY = input.DpiY,
            InjectedScalePercent = input.InjectedScalePercent
        }, out composed);

    private static void ValidateRecomputeCoverage(
        ScreenCanvasTransformFrameInput input,
        IntRect workspace,
        IntRect thumbnail,
        OcrLayoutScreen ocrLayout)
    {
        if (!ContainsRegion(input.Workspace, workspace)
            || !ContainsRegion(input.Navigator, thumbnail)
            || !ContainsRegion(input.OcrNumbers, ocrLayout.ScaleSlotScreen)
            || !ContainsRegion(input.OcrNumbers, ocrLayout.RotationSlotScreen))
            throw new ArgumentException("Current crops must fully contain the frozen workspace, thumbnail, and OCR slots at their screen positions.", nameof(input));
    }

    private static bool ContainsRegion(ScreenCanvasRegionCapture crop, IntRect region)
    {
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentNullException.ThrowIfNull(crop.Image);
        long left = crop.ScreenX;
        long top = crop.ScreenY;
        return region.Left >= left && region.Top >= top
            && region.Right <= left + crop.Image.Width
            && region.Bottom <= top + crop.Image.Height;
    }
}
