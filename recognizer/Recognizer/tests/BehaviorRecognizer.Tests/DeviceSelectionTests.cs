using BehaviorRecognizer.Capture;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed class DeviceSelectionTests
{
    [Fact]
    public void AutomaticModeKeepsAllDevices()
    {
        var options = new CaptureOptions(true);
        Assert.True(options.AcceptsDevice("tablet-a"));
        Assert.True(options.AcceptsDevice("tablet-b"));
    }
    [Fact]
    public void SelectedModeRejectsReportsFromOtherDevices()
    {
        var options = new CaptureOptions(true, "tablet-a");
        Assert.True(options.AcceptsDevice("tablet-a"));
        Assert.False(options.AcceptsDevice("tablet-b"));
    }
}
