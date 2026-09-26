using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Services;
using ScreenCanvasTransform.Interop;
using Xunit;

namespace ScreenCanvasTransform.Tests;

public sealed class ViewportCorrespondenceMapperTests
{
    [Theory]
    [InlineData(WorkspaceEdgeBits.Top, 180, WorkspaceEdgeBits.Bottom)]
    [InlineData(WorkspaceEdgeBits.Bottom, 180, WorkspaceEdgeBits.Top)]
    [InlineData(WorkspaceEdgeBits.Left, 90, WorkspaceEdgeBits.Bottom)]
    public void InverseRotation_MapsScreenSideToCanonical(int screenSide, int degrees, int expected)
    {
        int q = ViewportCorrespondenceMapper.NearestQuarter(degrees);
        Assert.Equal(expected, ViewportCorrespondenceMapper.CanonicalRoleFromScreenSide(screenSide, q));
    }

    [Fact]
    public void MapScreenEdge_PrefersAssignedWorkspaceEdgeOverGeometrySpin()
    {
        // 系统已认定该边为「右」；即使几何角会推出别的角色，也不得覆盖。
        var labeled = ViewportCorrespondenceMapper.MapScreenEdge(
            screenX0: 100, screenY0: 10, screenX1: 100, screenY1: 80,
            isComplete: true,
            assignedWorkspaceEdge: WorkspaceEdgeBits.Right,
            geometryRotationDegrees: 90f,
            geometryRotationConfidence: 1f,
            canvasCenterScreenX: 50,
            canvasCenterScreenY: 50);
        Assert.Equal(WorkspaceEdgeBits.Right, labeled.WorkspaceEdge);
    }

    [Fact]
    public void MapCompletedViewportEdges_EmitsTheWholeSemanticFrameInScreenCoordinates()
    {
        var frame = new NativeSct.SctViewportFrame
        {
            Width = 80,
            Height = 60,
            Corner0 = new NativeSct.SctVec2 { X = 10, Y = 20 },
            Corner1 = new NativeSct.SctVec2 { X = 90, Y = 20 },
            Corner2 = new NativeSct.SctVec2 { X = 90, Y = 80 },
            Corner3 = new NativeSct.SctVec2 { X = 10, Y = 80 }
        };

        var edges = ViewportCorrespondenceMapper.MapCompletedViewportEdges(frame, 100, -50);

        Assert.Collection(edges,
            top =>
            {
                Assert.Equal(WorkspaceEdgeBits.Top, top.WorkspaceEdge);
                Assert.Equal(110, top.X0);
                Assert.Equal(-30, top.Y0);
                Assert.Equal(190, top.X1);
                Assert.Equal(-30, top.Y1);
            },
            right => Assert.Equal(WorkspaceEdgeBits.Right, right.WorkspaceEdge),
            bottom => Assert.Equal(WorkspaceEdgeBits.Bottom, bottom.WorkspaceEdge),
            left => Assert.Equal(WorkspaceEdgeBits.Left, left.WorkspaceEdge));
    }
}
