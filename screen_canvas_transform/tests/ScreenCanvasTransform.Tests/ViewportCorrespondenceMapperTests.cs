using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Services;
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
}
