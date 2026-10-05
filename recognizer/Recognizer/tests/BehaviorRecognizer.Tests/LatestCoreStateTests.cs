using System.Diagnostics;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;
using Xunit.Abstractions;

namespace BehaviorRecognizer.Tests;

public class LatestCoreStateTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("brushState")]
    [InlineData("currentLayerState")]
    [InlineData("colorState")]
    [InlineData("canvasViewState")]
    [InlineData("clipState")]
    public async Task PipeAndSnapshotReturnThisObservationForEveryCore(string module)
    {
        await using var session = new Session();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = RecorderRealtimeClient.FollowCoreAsync(session.Server.PipeName, module, timeout.Token).GetAsyncEnumerator();
        Assert.True(await client.MoveNextAsync());
        Assert.Equal("hello", client.Current.Kind);
        Assert.Equal(session.Writer.ClockOriginTicks, client.Current.Data.GetProperty("originTicks").GetInt64());
        session.Core(module, 1);
        Assert.True(await client.MoveNextAsync());
        AssertVersion(client.Current, 1);
        session.Core(module, 2);
        Assert.True(await client.MoveNextAsync());
        AssertVersion(client.Current, 2);
        await using var late = RecorderRealtimeClient.FollowCoreAsync(session.Server.PipeName, module, timeout.Token).GetAsyncEnumerator();
        Assert.True(await late.MoveNextAsync());
        Assert.True(await late.MoveNextAsync());
        Assert.True(late.Current.IsSnapshot);
        AssertVersion(late.Current, 2);
        session.Core(module, 3);
        Assert.True(await client.MoveNextAsync());
        AssertVersion(client.Current, 3);
        session.Core(module, null);
        Assert.True(await client.MoveNextAsync());
        Assert.Equal("unknown", client.Current.Data.GetProperty("status").GetString());
        Assert.Equal(3, client.Current.Data.GetProperty("lastConfirmedState").GetProperty("version").GetInt32());
        await using var afterUnknown = RecorderRealtimeClient.FollowCoreAsync(session.Server.PipeName, module, timeout.Token).GetAsyncEnumerator();
        Assert.True(await afterUnknown.MoveNextAsync());
        Assert.True(await afterUnknown.MoveNextAsync());
        Assert.True(afterUnknown.Current.IsSnapshot);
        AssertVersion(afterUnknown.Current, 3);
        Assert.True(await afterUnknown.MoveNextAsync());
        Assert.True(afterUnknown.Current.IsSnapshot);
        Assert.Equal("unknown", afterUnknown.Current.Data.GetProperty("status").GetString());

    }

    [Fact]
    public async Task CoreSubscriberReceivesNewestStateThroughTabletTrafficBeforeDurableFlush()
    {
        await using var session = new Session();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = RecorderRealtimeClient.FollowCoreAsync(session.Server.PipeName, "canvasViewState", timeout.Token).GetAsyncEnumerator();
        Assert.True(await client.MoveNextAsync());
        session.Core("canvasViewState", 1);
        Assert.True(await client.MoveNextAsync());
        for (int i = 0; i < 10000; i++)
            session.Writer.AppendState("tabletStateChanged", session.Writer.NowTicks, [], new { action = "hover", sample = new { x = i } });
        var elapsed = Stopwatch.StartNew();
        session.Core("canvasViewState", 2);
        Assert.True(await client.MoveNextAsync());
        AssertVersion(client.Current, 2);
        output.WriteLine($"10,000 queued tablet events: latest canvas update delivered in {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task TabletMetadataSubscriptionSkipsPenTrafficAndKeepsLiveDeviceUpdates()
    {
        await using var session = new Session();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = RecorderRealtimeClient.SubscribeAsync(session.Server.PipeName,
            [RecorderRealtimeTopics.TabletMetadata, "core.canvasViewState"], cancellationToken: timeout.Token).GetAsyncEnumerator();
        Assert.True(await client.MoveNextAsync());
        session.Writer.AppendState("driverConfiguration", session.Writer.NowTicks, [], new { snapshotId = "driver1" });
        Assert.True(await client.MoveNextAsync());
        Assert.Equal("driverConfiguration", client.Current.Kind);
        for (int i = 0; i < 10000; i++)
            session.Writer.AppendState("tabletStateChanged", session.Writer.NowTicks, [], new { action = "hover" });
        session.Writer.AppendState("tabletDeviceChanged", session.Writer.NowTicks, [], new { deviceId = "pen2" });
        Assert.True(await client.MoveNextAsync());
        Assert.Equal("tabletDeviceChanged", client.Current.Kind);
        session.Core("canvasViewState", 2);
        Assert.True(await client.MoveNextAsync());
        AssertVersion(client.Current, 2);
    }

    private static void AssertVersion(RecorderRealtimeEvent message, int version)
    {
        Assert.Equal("stateUpdated", message.Kind);
        foreach (string field in new[] { "state", "rawResult", "lastConfirmedState" })
            Assert.Equal(version, message.Data.GetProperty(field).GetProperty("version").GetInt32());
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "latest-core-" + Guid.NewGuid().ToString("N"));
        internal MemolineWriter Writer { get; }
        private RecorderRealtimeHub Hub { get; }
        internal RecorderRealtimePipeServer Server { get; }
        internal Session()
        {
            Writer = new(_directory, new { }, new() { DurableFlushInterval = TimeSpan.FromMinutes(1) });
            Hub = new(Writer);
            Server = new(Hub);
        }
        internal void Core(string module, int? version) => Writer.AppendState("coreStateUpdated", Writer.NowTicks, [], new
        {
            module, status = version is null ? "unknown" : "changed", state = version is null ? null : new { version },
            rawResult = version is null ? null : new { version }, evidence = new { capturedTicks = Writer.NowTicks }
        });
        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            await Hub.DisposeAsync();
            await Server.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
