using ScreenCanvasTransform.Models;
using Xunit;

namespace ScreenCanvasTransform.Tests;

public sealed class WorkspaceEdgeBitsTests
{
    [Theory]
    [InlineData(WorkspaceEdgeBits.Left, "左")]
    [InlineData(WorkspaceEdgeBits.Top, "上")]
    [InlineData(WorkspaceEdgeBits.Right, "右")]
    [InlineData(WorkspaceEdgeBits.Bottom, "下")]
    public void ToLabel_KnownBits_ReturnsChinese(int bit, string expected)
    {
        Assert.Equal(expected, WorkspaceEdgeBits.ToLabel(bit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(15)]
    public void ToLabel_UnknownBits_ReturnsNull(int bit)
    {
        Assert.Null(WorkspaceEdgeBits.ToLabel(bit));
    }
}
