using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Capture;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed partial class InputControlTests
{
    [Fact]
    public async Task StateRequestsCollectOnlyTheRequestedPackageAndReturnFullPayloads()
    {
        var requests = new RecorderStateRequests();
        var request = new RecorderStateRequest("request", "session", ["brushState", "subtoolState"]);
        var result = requests.Add("fresh-package", request, 100);
        using var json = JsonDocument.Parse("""{"module":"brushState","status":"changed","state":{"name":"G筆"},"valueRegions":[{"screenBbox":[1,2,3,4]}]}""");
        requests.Part("old-package", "brushState", json.RootElement);
        requests.Part("fresh-package", "colorState", json.RootElement);
        Assert.False(result.IsCompleted);
        requests.Part("fresh-package", "brushState", json.RootElement);
        requests.Part("fresh-package", "brushState", json.RootElement);
        Assert.False(result.IsCompleted);
        requests.Part("fresh-package", "subtoolState", JsonSerializer.SerializeToElement(new { module = "subtoolState", status = "unknown", ocrEntries = Array.Empty<object>() }));
        var response = await result;
        Assert.Equal("request", response.RequestId);
        Assert.Equal("session", response.SessionId);
        Assert.Equal(100, response.RequestedTicks);
        Assert.Equal(2, response.Results.Count);
        Assert.Equal(4, response.Results["brushState"].GetProperty("valueRegions")[0].GetProperty("screenBbox")[3].GetInt32());
        Assert.Equal("unknown", response.Results["subtoolState"].GetProperty("status").GetString());
        requests.Remove("fresh-package");
    }

    [Fact]
    public async Task StateRequestsStopWhenCaptureHelperEnds()
    {
        var requests = new RecorderStateRequests();
        var task = requests.Add("package", new("request", "session", ["brushState"]), 0);
        requests.FailAll("helper stopped");
        await Assert.ThrowsAsync<IOException>(() => task);
    }

    [Fact]
    public async Task StateControlPipeValidatesSessionAndModulesAndDirectlyReturnsFreshResults()
    {
        await using var session = new TestSession();
        string name = "state-request-check-" + Guid.NewGuid().ToString("N");
        int calls = 0;
        await using var server = new RecorderInputControlServer(session.Guard, () => true, name, session.Writer,
            (request, token) =>
            {
                calls++;
                return Task.FromResult(new RecorderStateResponse(true, request.RequestId, request.SessionId, session.NowTicks,
                    request.Modules.ToDictionary(m => m, m => JsonSerializer.SerializeToElement(new { module = m, status = "unchanged",
                        state = "current", evidence = new { capturedTicks = session.NowTicks } }))));
            });
        async Task<JsonElement> Query(object request)
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(stop.Token);
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), stop.Token);
            using var json = JsonDocument.Parse((await reader.ReadLineAsync(stop.Token))!);
            return json.RootElement.Clone();
        }
        var valid = await Query(new { command = "requestStates", sessionId = session.Writer.SessionId,
            requestId = "request", modules = new[] { "brushState", "currentLayerState" } });
        Assert.True(valid.GetProperty("success").GetBoolean());
        Assert.Equal("request", valid.GetProperty("requestId").GetString());
        Assert.Equal(2, valid.GetProperty("results").EnumerateObject().Count());
        foreach (var request in new object[] {
            new { command = "requestStates", sessionId = "other", requestId = "r", modules = new[] { "brushState" } },
            new { command = "requestStates", sessionId = session.Writer.SessionId, requestId = "r", modules = new[] { "invalid" } },
            new { command = "requestStates", sessionId = session.Writer.SessionId, requestId = "r", modules = new[] { "brushState", "brushState" } },
            new { command = "requestStates", sessionId = session.Writer.SessionId, requestId = "r", modules = new[] { "clipState" }, saveId = "../outside" } })
            Assert.False((await Query(request)).GetProperty("success").GetBoolean());
        Assert.Equal(1, calls);
        Assert.Equal(0, session.Backend.Dispatches);
        Assert.Empty(session.Backend.Replayed);
    }
}
