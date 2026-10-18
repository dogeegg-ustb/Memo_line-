using System.Collections.Concurrent;
using System.Text.Json;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed partial class InputControlTests
{
    [Fact]
    public async Task GuardedLayerClickIsSavedAgainAfterItsRelease()
    {
        await using var session = await AutomaticSessionAsync();
        Assert.True(session.Guard.Mouse(0x201, 150, 150, 0));
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        Assert.True(session.Guard.Mouse(0x202, 150, 150, 0));
        await session.WaitIdleAsync();
        Assert.Equal(2, session.Backend.Dispatches);
        Assert.Equal(new uint[] { 2, 4 }, session.Backend.Replayed.Select(i => i.Union.Mouse.Flags & 6u));
        var saves = session.Notifications.Where(n => n.Type == "guardReleased").ToArray();
        Assert.Equal(2, saves.Length);
        Assert.NotEqual(saves[0].SaveId, saves[1].SaveId);
        // Nothing else is held back or saved: an unguarded click outside the layer panel passes straight through.
        Assert.False(session.Guard.Mouse(0x201, 50, 50, 0));
        Assert.False(session.Guard.Mouse(0x202, 50, 50, 0));
        await session.WaitIdleAsync();
        Assert.Equal(2, session.Backend.Dispatches);
    }

    [Fact]
    public async Task ReleaseDuringTheFirstSaveIsReplayedBeforeTheSecondSave()
    {
        await using var session = await AutomaticSessionAsync();
        Assert.True(session.Guard.Mouse(0x201, 150, 150, 0));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.Guard.Mouse(0x202, 150, 150, 0));
        Assert.Empty(session.Backend.Replayed);
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        Assert.Equal(2, session.Backend.Dispatches);
        Assert.Equal(2, session.Backend.Replayed.Count);
    }

    [Fact]
    public async Task GuardedLayerKeyIsSavedAgainAfterItsRelease()
    {
        await using var session = await AutomaticSessionAsync(bindings: [new { vk = 78, mods = 0 }]);
        session.Backend.SaveAcknowledged.TrySetResult();
        Assert.True(session.Guard.Keyboard(78, 49, false, true));
        await session.WaitIdleAsync();
        Assert.True(session.Guard.Keyboard(78, 49, false, false));
        await session.WaitIdleAsync();
        Assert.Equal(2, session.Backend.Dispatches);
        Assert.Equal(new uint[] { 0, 2 }, session.Backend.Replayed.Select(i => i.Union.Keyboard.Flags));
    }

    [Fact]
    public async Task ReleaseThatOpenedAPopupIsNotFollowedByASave()
    {
        await using var session = await AutomaticSessionAsync();
        session.Backend.SaveAcknowledged.TrySetResult();
        Assert.True(session.Guard.Mouse(0x201, 150, 150, 0));
        await session.WaitIdleAsync();
        session.Backend.Windows = [1, 7];
        Assert.True(session.Guard.Mouse(0x202, 150, 150, 0));
        await session.WaitIdleAsync();
        Assert.Equal(1, session.Backend.Dispatches);
        Assert.Equal(2, session.Backend.Replayed.Count);
    }

    [Fact]
    public async Task ReleaseIntoAMenuLoopIsNotFollowedByASave()
    {
        await using var session = await AutomaticSessionAsync();
        session.Backend.SaveAcknowledged.TrySetResult();
        Assert.True(session.Guard.Mouse(0x204, 150, 150, 0));
        await session.WaitIdleAsync();
        // A right click never asks for a second save.
        session.Guard.Mouse(0x205, 150, 150, 0);
        await session.WaitIdleAsync();
        Assert.Equal(1, session.Backend.Dispatches);
        Assert.True(session.Guard.Mouse(0x201, 150, 150, 0));
        await session.WaitIdleAsync();
        session.Backend.ModalUi = true;
        Assert.True(session.Guard.Mouse(0x202, 150, 150, 0));
        await session.WaitIdleAsync();
        Assert.Equal(2, session.Backend.Dispatches);
    }

    [Fact]
    public async Task AfterOperationSaveIsRecordedWithItsDispatchAsTriggerTime()
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-after-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            var backend = new FakeBackend();
            backend.SaveAcknowledged.TrySetResult();
            long releaseTicks;
            await using (var writer = new MemolineWriter(directory, new { test = "after operation save" }))
            {
                path = writer.FilePath;
                await using var guard = new LayerSaveGuard(writer, _ => { }, backend);
                await EnableAutomaticAsync(guard, []);
                Assert.True(guard.Mouse(0x201, 150, 150, 0));
                await WaitIdleAsync(guard);
                releaseTicks = writer.NowTicks;
                Assert.True(guard.Mouse(0x202, 150, 150, 0));
                await WaitIdleAsync(guard);
                Assert.True(guard.Mouse(0x201, 150, 150, 0));
                await WaitIdleAsync(guard);
                backend.Windows = [1, 9];
                Assert.True(guard.Mouse(0x202, 150, 150, 0));
                await WaitIdleAsync(guard);
            }
            var records = MemolineReader.Read(path).ToArray();
            var requests = records.Where(r => r.GetProperty("kind").GetString() == "clipSaveRequest").ToArray();
            Assert.Equal(new[] { "beforeLayerOperation", "afterLayerOperation", "beforeLayerOperation" },
                requests.Select(r => r.GetProperty("data").GetProperty("reason").GetString()));
            var after = requests[1];
            Assert.True(after.GetProperty("ticks").GetInt64() > releaseTicks);
            var result = Assert.Single(records, r => r.GetProperty("kind").GetString() == "saveGuardResult"
                && r.GetProperty("data").GetProperty("saveId").GetString() == after.GetProperty("data").GetProperty("saveId").GetString());
            Assert.True(result.GetProperty("data").GetProperty("saveInputDispatchedTicks").GetInt64() >= after.GetProperty("ticks").GetInt64());
            var skipped = Assert.Single(records, r => r.GetProperty("kind").GetString() == "clipSaveSkipped");
            Assert.Equal("cspPopupOpened", skipped.GetProperty("data").GetProperty("skipped").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task EnableAutomaticAsync(LayerSaveGuard guard, object[] bindings)
    {
        guard.Configure(JsonSerializer.SerializeToElement(new { enabled = true, region = new[] { 100, 100, 200, 200 },
            bindings, saveSettleMs = 0 }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!guard.GetInputControlStatus().SaveWorkerReady) await Task.Delay(1, timeout.Token);
    }

    private static async Task WaitIdleAsync(LayerSaveGuard guard)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (guard.GetInputControlStatus().Busy) await Task.Delay(1, timeout.Token);
    }

    private static async Task<NotifyingSession> AutomaticSessionAsync(object[]? bindings = null)
    {
        var session = new NotifyingSession();
        await EnableAutomaticAsync(session.Guard, bindings ?? []);
        return session;
    }

    private sealed record Notification(string Type, string SaveId);

    private sealed class NotifyingSession : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memoline-guard-test-" + Guid.NewGuid().ToString("N"));
        private readonly MemolineWriter _writer;
        public FakeBackend Backend { get; } = new();
        public LayerSaveGuard Guard { get; }
        public ConcurrentQueue<Notification> Notifications { get; } = new();
        public NotifyingSession()
        {
            _writer = new MemolineWriter(_directory, new { test = "layer operation saves" });
            Guard = new LayerSaveGuard(_writer, message =>
            {
                var json = JsonSerializer.SerializeToElement(message);
                Notifications.Enqueue(new(json.GetProperty("type").GetString()!, json.GetProperty("saveId").GetString()!));
            }, Backend);
        }
        public Task WaitIdleAsync() => InputControlTests.WaitIdleAsync(Guard);
        public async ValueTask DisposeAsync()
        {
            Backend.SaveAcknowledged.TrySetResult();
            await Guard.DisposeAsync();
            await _writer.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }
}
