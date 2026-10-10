using System.Text.Json;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Realtime;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed class ColorInputStateTests
{
    [Fact]
    public void ColorInputIsAnExplicitRequestModuleAndAnIndependentRealtimeTopic()
    {
        Assert.Contains("colorInputState", RecorderStateRequests.Modules);
        Assert.Contains("core.colorInputState", RecorderRealtimeTopics.Expand(["cores"]));
        Assert.Equal(["core.colorInputState"], RecorderRealtimeTopics.Expand(["core.colorInputState"]));
        Assert.Contains("core.colorState", RecorderRealtimeTopics.All);
    }

    [Fact]
    public async Task ColorInputResponseUsesOnlyItsFreshRequestPackageAndKeepsUnknownDiagnostics()
    {
        var requests = new RecorderStateRequests();
        var response = requests.Add("fresh", new("rgb", "session", ["colorInputState"]), 100);
        var data = JsonSerializer.SerializeToElement(new
        {
            module = "colorInputState", status = "unknown", state = (object?)null,
            observedState = new { status = "panelNotVisible", activeTab = "unknown", activeColorSlot = "unknown" },
            evidence = new { triggerTicks = 100 }
        });
        requests.Part("old", "colorInputState", data);
        Assert.False(response.IsCompleted);
        requests.Part("fresh", "colorState", data);
        Assert.False(response.IsCompleted);
        requests.Part("fresh", "colorInputState", data);
        var result = await response;
        Assert.Equal("rgb", result.RequestId);
        Assert.Equal(100, result.RequestedTicks);
        Assert.Equal("unknown", result.Results["colorInputState"].GetProperty("status").GetString());
        Assert.Equal("panelNotVisible", result.Results["colorInputState"].GetProperty("observedState").GetProperty("status").GetString());
    }
}
