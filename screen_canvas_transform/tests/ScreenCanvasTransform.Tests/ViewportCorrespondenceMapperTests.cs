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
}
