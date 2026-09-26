using System.Drawing;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ocr;
using ScreenCanvasTransform.Services;
using ScreenCanvasTransform.State;
using Xunit;

namespace ScreenCanvasTransform.Tests;

public sealed class RecomputePipelineTests
{
    [Fact]
    public void TryFromSession_FailsWithoutOcrLayoutUsed()
    {
        var previous = CreateSessionResult(includeOcrLayout: false);

        bool ok = RecomputeAnchorSet.TryFromSession(
            previous, 4000, 3000, out var anchors, out string error);

        Assert.False(ok);
        Assert.Null(anchors);
        Assert.Contains("OcrLayout", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryFromSession_UsesThumbnailNotNavigatorPanelRoi()
    {
        IntRect panel = new(920, 100, 1100, 700);
        IntRect thumb = new(940, 120, 1080, 260);
        var previous = CreateSessionResult(includeOcrLayout: true, navigatorPanel: panel, thumbnail: thumb);

        Assert.True(RecomputeAnchorSet.TryFromSession(
            previous, 4000, 3000, out var anchors, out _));
        Assert.NotNull(anchors);

        Assert.Equal(thumb, anchors!.SystemNavigatorThumbnailRoiScreen);
        Assert.NotEqual(panel, anchors.SystemNavigatorThumbnailRoiScreen);
    }

    [Fact]
    public void FromArchive_MapsSystemFieldsOneToOne()
    {
        var archive = CreateArchive();
        var anchors = RecomputeAnchorSet.FromArchive(archive);

        Assert.Equal(archive.CanvasPixelWidth, anchors.CanvasPixelWidth);
        Assert.Equal(archive.CanvasPixelHeight, anchors.CanvasPixelHeight);
        Assert.Equal(archive.SystemWorkspaceRoiScreen.ToIntRect(), anchors.SystemWorkspaceRoiScreen);
        Assert.Equal(
            archive.SystemNavigatorThumbnailRoiScreen.ToIntRect(),
            anchors.SystemNavigatorThumbnailRoiScreen);
        Assert.Equal(archive.OcrLayout.ScaleSlotScreen.ToIntRect(), anchors.OcrLayout.ScaleSlotScreen);
        Assert.Equal(archive.OcrLayout.RotationSlotScreen.ToIntRect(), anchors.OcrLayout.RotationSlotScreen);
        Assert.Same(archive.Background, anchors.WorkspaceBackgroundModel);
    }

    [Fact]
    public void SessionAndArchive_SameData_ProduceEqualAnchorSets()
    {
        var archive = CreateArchive();
        var archiveAnchors = RecomputeAnchorSet.FromArchive(archive);

        var session = CreateSessionResult(
            includeOcrLayout: true,
            workspace: archive.SystemWorkspaceRoiScreen.ToIntRect(),
            navigatorPanel: new(900, 80, 1120, 720),
            thumbnail: archive.SystemNavigatorThumbnailRoiScreen.ToIntRect(),
            ocrLayout: OcrLayoutScreen.FromDto(archive.OcrLayout),
            background: archive.Background);

        Assert.True(RecomputeAnchorSet.TryFromSession(
            session,
            archive.CanvasPixelWidth,
            archive.CanvasPixelHeight,
            out var sessionAnchors,
            out _));
        Assert.NotNull(sessionAnchors);

        AssertEqualAnchors(archiveAnchors, sessionAnchors!);
    }

    [Fact]
    public void RecomputeAsync_Source_DoesNotCallDetectNavigatorThumbnailOrCii()
    {
        string path = FindSourceFile("TransformPipelineService.cs");
        string src = File.ReadAllText(path);

        int recomputeStart = src.IndexOf(
            "public async Task<PipelineResult> RecomputeAsync(",
            StringComparison.Ordinal);
        Assert.True(recomputeStart >= 0);

        int nextMethod = src.IndexOf(
            "public async Task<PipelineResult> RecomputeFromArchiveAsync(",
            recomputeStart + 1,
            StringComparison.Ordinal);
        Assert.True(nextMethod > recomputeStart);

        string recomputeBody = src[recomputeStart..nextMethod];
        Assert.DoesNotContain("DetectNavigatorThumbnail", recomputeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectNavigatorThumbnailCii", recomputeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySetRoi", recomputeBody, StringComparison.Ordinal);
        Assert.Contains("RecomputeCoreAsync", recomputeBody, StringComparison.Ordinal);
        Assert.Contains("TryFromSession", recomputeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RecomputeFromArchiveAsync_Source_UsesSameRecomputeCore()
    {
        string path = FindSourceFile("TransformPipelineService.cs");
        string src = File.ReadAllText(path);

        int start = src.IndexOf(
            "public async Task<PipelineResult> RecomputeFromArchiveAsync(",
            StringComparison.Ordinal);
        Assert.True(start >= 0);

        int core = src.IndexOf(
            "public async Task<PipelineResult> RecomputeCoreAsync(",
            start + 1,
            StringComparison.Ordinal);
        Assert.True(core > start);

        string body = src[start..core];
        Assert.Contains("RecomputeCoreAsync", body, StringComparison.Ordinal);
        Assert.Contains("FromArchive", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectNavigatorThumbnail", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RecomputeCoreAsync_Source_InjectsFixedOcrLayout_NoDetection()
    {
        string path = FindSourceFile("TransformPipelineService.cs");
        string src = File.ReadAllText(path);

        int start = src.IndexOf(
            "public async Task<PipelineResult> RecomputeCoreAsync(",
            StringComparison.Ordinal);
        Assert.True(start >= 0);

        int next = src.IndexOf(
            "private static IntRect DeriveNavigatorRoiForSolve(",
            start + 1,
            StringComparison.Ordinal);
        Assert.True(next > start);

        string body = src[start..next];
        Assert.Contains("fixedOcrLayout:", body, StringComparison.Ordinal);
        Assert.Contains("SystemNavigatorThumbnailRoiScreen", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectNavigatorThumbnail(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectWorkspace(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAnchorsOnCapture_RejectsOffscreenThumbnail_WithoutRewriting()
    {
        using var bmp = new Bitmap(200, 200);
        using var session = new CaptureSession(
            "cap",
            bmp,
            new IntRect(0, 0, 200, 200),
            96f,
            96f);

        var anchors = new RecomputeAnchorSet
        {
            CanvasPixelWidth = 1000,
            CanvasPixelHeight = 800,
            SystemWorkspaceRoiScreen = new IntRect(10, 10, 180, 180),
            WorkspaceBackgroundModel = CreateBackground(),
            SystemNavigatorThumbnailRoiScreen = new IntRect(5000, 5000, 5100, 5100),
            OcrLayout = new OcrLayoutScreen(new IntRect(20, 20, 60, 40), new IntRect(20, 50, 60, 70))
        };

        var pipeline = new TransformPipelineService();
        var ex = Assert.Throws<PipelineFailureException>(() =>
            pipeline.RecomputeCoreAsync(session, anchors).GetAwaiter().GetResult());

        Assert.Equal(TransformStage.ReacquiringEvidence, ex.Stage);
        Assert.Contains("SystemNavigatorThumbnailRoiScreen", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertEqualAnchors(RecomputeAnchorSet a, RecomputeAnchorSet b)
    {
        Assert.Equal(a.CanvasPixelWidth, b.CanvasPixelWidth);
        Assert.Equal(a.CanvasPixelHeight, b.CanvasPixelHeight);
        Assert.Equal(a.SystemWorkspaceRoiScreen, b.SystemWorkspaceRoiScreen);
        Assert.Equal(a.SystemNavigatorThumbnailRoiScreen, b.SystemNavigatorThumbnailRoiScreen);
        Assert.Equal(a.OcrLayout.ScaleSlotScreen, b.OcrLayout.ScaleSlotScreen);
        Assert.Equal(a.OcrLayout.RotationSlotScreen, b.OcrLayout.RotationSlotScreen);
        Assert.Equal(a.WorkspaceBackgroundModel.CenterLabL, b.WorkspaceBackgroundModel.CenterLabL);
        Assert.Equal(a.WorkspaceBackgroundModel.Confidence, b.WorkspaceBackgroundModel.Confidence);
    }

    private static PipelineResult CreateSessionResult(
        bool includeOcrLayout,
        IntRect? workspace = null,
        IntRect? navigatorPanel = null,
        IntRect? thumbnail = null,
        OcrLayoutScreen? ocrLayout = null,
        WorkspaceBackgroundModel? background = null)
    {
        IntRect ws = workspace ?? new(10, 10, 900, 700);
        IntRect panel = navigatorPanel ?? new(920, 100, 1100, 700);
        IntRect thumb = thumbnail ?? new(940, 120, 1080, 260);
        OcrLayoutScreen layout = ocrLayout
            ?? NavigatorOcrService.LayoutFromUserRegion(new IntRect(panel.Left, thumb.Bottom, panel.Left + panel.Width / 2, panel.Bottom));

        return new PipelineResult
        {
            Stage = TransformStage.TrackingStable,
            WorkspaceRoiScreen = ws,
            NavigatorRoiScreen = panel,
            NavigatorThumbnailRoiScreen = thumb,
            Background = background ?? CreateBackground(),
            OcrLayoutUsed = includeOcrLayout ? layout : null,
            Snapshot = new TransformSnapshotDto
            {
                Status = 0,
                CanvasPixelWidth = 4000,
                CanvasPixelHeight = 3000,
                Generation = 1
            }
        };
    }

    private static SaveArchive CreateArchive()
    {
        IntRect workspace = new(10, 10, 900, 700);
        IntRect panel = new(920, 100, 1100, 700);
        IntRect thumb = new(940, 120, 1080, 260);
        var layout = NavigatorOcrService.LayoutFromUserRegion(
            new IntRect(panel.Left, thumb.Bottom, panel.Left + panel.Width / 2, panel.Bottom));

        return new SaveArchive
        {
            ArchiveId = "test",
            DisplayName = "test",
            CreatedAtUtc = DateTime.UtcNow,
            CanvasPixelWidth = 4000,
            CanvasPixelHeight = 3000,
            SystemWorkspaceRoiScreen = ScreenPhysicalRectDto.FromIntRect(workspace),
            Background = CreateBackground(),
            SystemNavigatorThumbnailRoiScreen = ScreenPhysicalRectDto.FromIntRect(thumb),
            OcrLayout = new OcrLayoutDto
            {
                ScaleSlotScreen = ScreenPhysicalRectDto.FromIntRect(layout.ScaleSlotScreen),
                RotationSlotScreen = ScreenPhysicalRectDto.FromIntRect(layout.RotationSlotScreen)
            },
            Provenance = new SaveArchiveProvenance
            {
                InitCaptureId = "init",
                InitGeneration = 1,
                InitCompletedAtUtc = DateTime.UtcNow
            }
        };
    }

    private static WorkspaceBackgroundModel CreateBackground()
        => new()
        {
            CenterLabL = 50,
            CenterLabA = 0,
            CenterLabB = 0,
            StrongDeltaE = 10,
            WeakDeltaE = 5,
            Confidence = 0.9f,
            SourceCaptureId = "init"
        };

    private static string FindSourceFile(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "app", "Services", fileName);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir, "..", "..", "..", "..", "app", "Services", fileName);
            candidate = Path.GetFullPath(candidate);
            if (File.Exists(candidate))
                return candidate;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate {fileName} for source contract tests.");
    }
}
