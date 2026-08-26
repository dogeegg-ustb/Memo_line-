using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ocr;
using ScreenCanvasTransform.Services;
using ScreenCanvasTransform.State;
using Xunit;

namespace ScreenCanvasTransform.Tests;

public sealed class SaveArchiveServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SaveArchiveService _service;

    public SaveArchiveServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sct_archive_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new SaveArchiveService(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void InitSuccess_WritesSystemAnchors_NotUserRoiFields()
    {
        var bundle = CreateValidBundle();
        var result = _service.TryCreateFromInitSuccess(bundle);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Archive);

        string json = File.ReadAllText(Path.Combine(_tempDir, $"{result.Archive!.ArchiveId}.json"));
        Assert.Contains("systemWorkspaceRoiScreen", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("systemNavigatorThumbnailRoiScreen", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ocrLayout", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspaceBackgroundModel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userWorkspaceRoi", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userNavigatorPanelRoi", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TransformSnapshot", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InitFailure_DoesNotWriteFile()
    {
        var valid = CreateValidBundle();
        var bundle = new InitSuccessBundle
        {
            Result = new PipelineResult
            {
                Snapshot = new TransformSnapshotDto
                {
                    Status = 999,
                    FailureStatus = 999,
                    CanvasPixelWidth = 1920,
                    CanvasPixelHeight = 1080
                },
                WorkspaceRoiScreen = valid.Result.WorkspaceRoiScreen,
                NavigatorRoiScreen = valid.Result.NavigatorRoiScreen,
                NavigatorThumbnailRoiScreen = valid.Result.NavigatorThumbnailRoiScreen,
                Background = valid.Result.Background,
                Stage = TransformStage.SolvingTransform
            },
            InitCaptureId = valid.InitCaptureId,
            NavigatorPanelScreenAtInit = valid.NavigatorPanelScreenAtInit
        };

        var result = _service.TryCreateFromInitSuccess(bundle);
        Assert.False(result.Success);
        Assert.False(Directory.EnumerateFiles(_tempDir, "*.json").Any());
    }

    [Fact]
    public void InterruptedTempFile_IsNotListed()
    {
        string tempPath = Path.Combine(_tempDir, "deadbeef.tmp.json");
        File.WriteAllText(tempPath, "{}");

        var list = _service.ListArchives().ToList();
        Assert.DoesNotContain(list, x => x.ArchiveId == "deadbeef");
    }

    [Fact]
    public void InvalidArchive_IsMarkedUnavailable()
    {
        string path = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"archiveId\":\"bad\",\"canvasPixelWidth\":0}");

        var item = _service.ListArchives().Single(x => x.ArchiveId == "bad");
        Assert.False(item.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(item.ValidationError));
    }

    [Fact]
    public void LoadInvalidArchive_ReturnsError()
    {
        string path = Path.Combine(_tempDir, "bad2.json");
        File.WriteAllText(path,
            "{\"schemaVersion\":99,\"archiveId\":\"bad2\",\"displayName\":\"x\",\"createdAtUtc\":\"2026-01-01T00:00:00Z\",\"coordinateConventionVersion\":1,\"canvasPixelWidth\":100,\"canvasPixelHeight\":100}");

        var load = _service.TryLoad("bad2");
        Assert.False(load.Success);
    }

    [Fact]
    public void DeleteArchive_RemovesFile()
    {
        var created = _service.TryCreateFromInitSuccess(CreateValidBundle());
        Assert.True(created.Success);

        Assert.True(_service.TryDelete(created.Archive!.ArchiveId));
        Assert.False(File.Exists(Path.Combine(_tempDir, $"{created.Archive.ArchiveId}.json")));
    }

    [Fact]
    public void OcrLayout_MatchesNavigatorServiceRules()
    {
        IntRect nav = new(100, 100, 300, 500);
        IntRect thumb = new(120, 120, 280, 280);
        var layout = NavigatorOcrService.ComputeOcrLayout(nav, thumb);

        Assert.False(layout.PrimarySearchBandScreen.IsEmpty);
        Assert.True(layout.PrimarySearchBandScreen.Top >= thumb.Bottom - 4
                    || layout.PrimarySearchBandScreen.Height >= CaptureSession.MinRoiSizePx);
        Assert.True(layout.LeftHalfSearchBandScreen.Width <= layout.PrimarySearchBandScreen.Width);
    }

    private InitSuccessBundle CreateValidBundle()
    {
        IntRect workspace = new(10, 10, 900, 700);
        IntRect navigator = new(920, 100, 1100, 700);
        IntRect thumb = new(940, 120, 1080, 260);
        var layout = NavigatorOcrService.ComputeOcrLayout(navigator, thumb);

        return new InitSuccessBundle
        {
            InitCaptureId = Guid.NewGuid().ToString("N"),
            NavigatorPanelScreenAtInit = navigator,
            Result = new PipelineResult
            {
                Stage = TransformStage.TrackingStable,
                WorkspaceRoiScreen = workspace,
                NavigatorRoiScreen = navigator,
                NavigatorThumbnailRoiScreen = thumb,
                Background = new WorkspaceBackgroundModel
                {
                    CenterLabL = 50,
                    CenterLabA = 0,
                    CenterLabB = 0,
                    StrongDeltaE = 10,
                    WeakDeltaE = 5,
                    Confidence = 0.9f,
                    SourceCaptureId = "init"
                },
                Snapshot = new TransformSnapshotDto
                {
                    Status = NativeSct.StatusOk,
                    CanvasPixelWidth = 4000,
                    CanvasPixelHeight = 3000,
                    Generation = 1,
                    SourceRevision = "rev",
                    CoordinateConventionVersion = SaveArchiveConstants.CoordinateConventionVersion
                }
            }
        };
    }
}
