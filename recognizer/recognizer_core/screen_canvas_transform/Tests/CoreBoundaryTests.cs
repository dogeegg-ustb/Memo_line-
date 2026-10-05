using System.Drawing;
using System.Drawing.Imaging;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Core;
using ScreenCanvasTransform.Detection;
using ScreenCanvasTransform.Interop;
using ScreenCanvasTransform.Models;
using Xunit;

namespace ScreenCanvasTransform.Tests;

public sealed class CoreBoundaryTests
{
    [Fact]
    public void NativeRuntime_MatchesManagedAbi() =>
        Assert.Equal(NativeSct.ApiVersionExpected, NativeSct.sct_api_version());

    [Fact]
    public void ExtractedAssembly_HasNoDesktopUiDependency()
    {
        var names = typeof(ScreenCanvasTransformCore).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name).ToArray();
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("PresentationCore", names);
        Assert.DoesNotContain("System.Windows.Forms", names);
    }

    [Fact]
    public void DirectSolve_PreservesNegativeScreenOriginThroughNativeAbi()
    {
        var paper = new IntRect(-1450, 150, -450, 650);
        var request = new NativeSct.SctSolveRequest
        {
            CaptureId = "core-abi", CanvasPixelWidth = 2000, CanvasPixelHeight = 1000,
            WorkspaceRoiScreen = NativeSct.SctIntRect.From(new(-1600, 100, -300, 800)),
            WorkspaceCanvas = new()
            {
                BoundsScreen = NativeSct.SctIntRect.From(paper), FourSidesComplete = 1,
                VisibleEdgesMask = 15, Confidence = 1,
                BoundarySupport0 = 1, BoundarySupport1 = 1,
                BoundarySupport2 = 1, BoundarySupport3 = 1
            },
            Numbers = new() { ScalePercent = 50, ScaleConfidence = 1, RotationConfidence = 1 },
            MarkerEpsilonCanvas = 0.01
        };
        var result = new SctNativeService().SolveTransform(request);
        Assert.Equal(0, result.Status);
        Assert.True(result.UsedDirectWorkspacePath);
        Assert.Equal(-1450, result.Raw.CanvasToScreen.M2, 6);
        Assert.Equal(150, result.Raw.CanvasToScreen.M5, 6);
        Assert.Equal(1000, result.Raw.CanvasToScreen.M0, 6);
        Assert.Equal(500, result.Raw.CanvasToScreen.M4, 6);
    }

    [Fact]
    public async Task Recompute_RequiresSuccessfulInitialization()
    {
        using var core = new ScreenCanvasTransformCore();
        using var frame = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        await Assert.ThrowsAsync<InvalidOperationException>(() => core.RecomputeAsync(frame));
    }

    [Fact]
    public void CallerFrameCoordinates_RoundTripAtNegativeOrigin()
    {
        using var session = new CaptureSession("negative", new Bitmap(100, 80), -1920, -100, 144, 144);
        var roi = new IntRect(10, 20, 60, 70);
        Assert.True(session.TrySetRoi(RoiKind.WorkspaceUser, roi, out _));
        Assert.Equal(new IntRect(-1910, -80, -1860, -30), session.CaptureToScreen(roi));
        Assert.Equal(roi, session.ScreenToCapture(session.CaptureToScreen(roi)));
    }
}
