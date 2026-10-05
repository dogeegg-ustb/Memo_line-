using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed class InputControlTests
{
    [Fact]
    public async Task OneShotSaveDoesNotWaitForInputOrChangeInterceptionPolicy()
    {
        await using var session = new TestSession();
        var before = session.Guard.GetInputControlStatus();
        var saving = session.Guard.RequestClipSaveAsync(new(@"C:\art\drawing.clip", "external-test"));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(session.Backend.Replayed);
        session.Backend.SaveAcknowledged.TrySetResult();
        var result = await saving.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Success);
        Assert.True(result.SaveInputDispatched);
        Assert.Equal("external-test", result.RequestId);
        Assert.NotNull(result.SaveInputDispatchedTicks);
        Assert.False(result.SaveCompletionConfirmed);
        await session.WaitIdleAsync();
        var after = session.Guard.GetInputControlStatus();
        Assert.Equal(before.Controlled, after.Controlled);
        Assert.Equal(before.Enabled, after.Enabled);
        Assert.Equal(1, session.Backend.Dispatches);
        Assert.Empty(session.Backend.Replayed);
    }

    [Theory]
    [InlineData("CLIP STUDIO PAINT")]
    [InlineData("")]
    [InlineData("another document.clip - CLIP STUDIO PAINT")]
    public async Task OneShotDoesNotValidateDocumentTitles(string title)
    {
        await using var session = new TestSession();
        session.Backend.ForegroundWindowTitle = title;
        session.Backend.SaveAcknowledged.TrySetResult();
        var result = await session.Guard.RequestClipSaveAsync(new(@"C:\art\drawing.clip", "untitled-window"));
        Assert.True(result.Success);
        Assert.True(result.SaveInputDispatched);
        Assert.Equal(1, session.Backend.Dispatches);
    }

    [Fact]
    public async Task OneShotRejectsForeignForeground()
    {
        await using var session = new TestSession();
        session.Backend.ForegroundWindow = 2;
        var foreign = await session.Guard.RequestClipSaveAsync(new(@"C:\art\drawing.clip", "foreign"));
        Assert.False(foreign.Success);
        Assert.False(foreign.SaveInputDispatched);
        Assert.Equal(0, session.Backend.Dispatches);
    }

    [Fact]
    public async Task OneShotReturnsFailureOnDispatchErrorAndBusyWorker()
    {
        await using var session = new TestSession();
        var saving = session.Guard.RequestClipSaveAsync(new(@"C:\art\drawing.clip", "first"));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var busy = await session.Guard.RequestClipSaveAsync(new(@"C:\art\drawing.clip", "second"));
        Assert.False(busy.Success);
        Assert.False(busy.SaveInputDispatched);
        session.Backend.DispatchError = new IOException("dispatch failed");
        session.Backend.SaveAcknowledged.TrySetResult();
        var result = await saving;
        Assert.False(result.Success);
        Assert.False(result.SaveCompletionConfirmed);
        Assert.False(result.SaveInputDispatched);
    }
    private static RecorderInputControlRequest EnableWindows => new()
    {
        Enabled = true
    };

    [Fact]
    public async Task NativeControlCanBePreparedWithoutAnAhkProcessOrAnyInputInjection()
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-native-control-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = new MemolineWriter(directory, new { test = "prepare only, no native input" });
            await using var guard = new LayerSaveGuard(writer, _ => { });
            var status = await guard.ConfigureInputInterceptionAsync(new() { Enabled = true });
            Assert.True(status.Success);
            Assert.True(status.Enabled);
            Assert.True(status.SaveWorkerReady);
            Assert.False(status.Busy);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SaveChordRestoresHeldModifiersAndTagsEveryInjectedEdge()
    {
        await using var session = new TestSession();
        session.Backend.HeldKeys.UnionWith([0xA3, 0xA0, 0x5B]); // right Ctrl, left Shift, left Win
        var (inputs, count) = session.Guard.CreateSaveInputs();
        Assert.Equal(10, count);
        Assert.Equal(new ushort[] { 0xA3, 0xA0, 0x5B, 0xA2, 0x53, 0x53, 0xA2, 0xA3, 0xA0, 0x5B },
            inputs.Take(count).Select(i => i.Union.Keyboard.Vk));
        Assert.Equal(new uint[] { 3, 2, 3, 0, 0, 2, 2, 1, 0, 1 },
            inputs.Take(count).Select(i => i.Union.Keyboard.Flags));
        Assert.All(inputs.Take(count), i => Assert.Equal((nuint)0x4D4C5243, i.Union.Keyboard.Extra));
        Assert.Empty(session.Backend.Replayed); // Building a chord never sends hardware input.
    }

    [Fact]
    public async Task PenCompatibilityMessagesArePassedThroughByControlledPolicy()
    {
        await using var session = new TestSession();
        var status = await session.Guard.ConfigureInputInterceptionAsync(new() { Enabled = true });
        Assert.True(status.Success);
        Assert.True(status.Enabled);
        Assert.False(session.Guard.Mouse(0x201, 400, 300, 0, penCompatibility: true));
        Assert.True(session.Guard.Keyboard(65, 30, false, true));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.Guard.Mouse(0x200, 400, 300, 0, penCompatibility: true));
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        Assert.Single(session.Backend.Replayed);
        Assert.Equal(1u, session.Backend.Replayed.Single().Type);
    }

    [Fact]
    public async Task WorkerMustBeReadyBeforeInterceptionCanBeEnabled()
    {
        await using var session = new TestSession();
        session.Backend.WarmupError = new IOException("offline save worker");
        var status = await session.Guard.ConfigureInputInterceptionAsync(EnableWindows);
        Assert.False(status.Success);
        Assert.False(status.Enabled);
        Assert.False(status.SaveWorkerReady);
        Assert.False(session.Guard.Keyboard(65, 30, false, true));
    }

    [Fact]
    public async Task AllQueuedInputIsReleasedInOrderOnlyAfterSaveDispatch()
    {
        await using var session = new TestSession();
        Assert.True((await session.Guard.ConfigureInputInterceptionAsync(EnableWindows)).Success);
        Assert.True(session.Guard.Keyboard(0xA2, 29, false, true));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.Guard.Mouse(0x20A, 400, 300, 0xFF880000)); // -120, signed wheel delta
        Assert.True(session.Guard.Keyboard(83, 31, false, true));
        Assert.True(session.Guard.Keyboard(83, 31, false, false));
        Assert.True(session.Guard.Keyboard(0xA2, 29, false, false));
        Assert.Empty(session.Backend.Replayed);
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        var inputs = session.Backend.Replayed.ToArray();
        Assert.Equal(5, inputs.Length);
        Assert.Equal((ushort)0xA2, inputs[0].Union.Keyboard.Vk);
        Assert.Equal(0u, inputs[0].Union.Keyboard.Flags);
        Assert.Equal(0u, inputs[1].Type);
        Assert.Equal(0x800u, inputs[1].Union.Mouse.Flags & 0x1800u);
        Assert.Equal(unchecked((uint)-120), inputs[1].Union.Mouse.Data);
        Assert.Equal((ushort)83, inputs[2].Union.Keyboard.Vk);
        Assert.Equal(2u, inputs[3].Union.Keyboard.Flags);
        Assert.Equal((ushort)0xA2, inputs[4].Union.Keyboard.Vk);
        Assert.Equal(2u, inputs[4].Union.Keyboard.Flags);
        Assert.All(inputs, input => Assert.Equal((nuint)0x4D4C5243,
            input.Type == 0 ? input.Union.Mouse.Extra : input.Union.Keyboard.Extra));
        Assert.Equal(1, session.Backend.Dispatches);
    }

    [Fact]
    public async Task DisablingDuringSaveStillBuffersReleasesForQueuedKeyAndButton()
    {
        await using var session = new TestSession();
        await session.Guard.ConfigureInputInterceptionAsync(EnableWindows);
        Assert.True(session.Guard.Keyboard(65, 30, false, true));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.Guard.Keyboard(66, 48, false, true));
        Assert.True(session.Guard.Mouse(0x201, 400, 300, 0));
        await session.Guard.ConfigureInputInterceptionAsync(new() { Enabled = false });
        Assert.True(session.Guard.Keyboard(66, 48, false, false));
        Assert.True(session.Guard.Mouse(0x202, 400, 300, 0));
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        Assert.Equal(5, session.Backend.Replayed.Count);
        Assert.False(session.Guard.GetInputControlStatus().Enabled);
        Assert.False(session.Guard.Keyboard(67, 46, false, true));
    }

    [Fact]
    public async Task RestoringAutomaticProtectionDoesNotOvertakeBufferedModifierDown()
    {
        await using var session = new TestSession();
        await session.Guard.ConfigureInputInterceptionAsync(EnableWindows);
        Assert.True(session.Guard.Keyboard(0xA2, 29, false, true));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.Guard.RestoreAutomaticInputProtection().Controlled);
        Assert.True(session.Guard.Keyboard(0xA2, 29, false, false));
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        Assert.Equal(new uint[] { 0, 2 }, session.Backend.Replayed.Select(i => i.Union.Keyboard.Flags));
    }

    [Fact]
    public async Task SaveFailureReleasesInputAndStopsFurtherInterception()
    {
        await using var session = new TestSession();
        await session.Guard.ConfigureInputInterceptionAsync(EnableWindows);
        session.Backend.DispatchError = new IOException("save failed");
        session.Backend.SaveAcknowledged.TrySetResult();
        Assert.True(session.Guard.Keyboard(65, 30, false, true));
        await session.WaitIdleAsync();
        Assert.Single(session.Backend.Replayed);
        Assert.False(session.Guard.GetInputControlStatus().SaveWorkerReady);
        Assert.False(session.Guard.Keyboard(66, 48, false, true));
    }

    [Fact]
    public async Task ForegroundChangeCancelsReplayIntoAnotherApplication()
    {
        await using var session = new TestSession();
        await session.Guard.ConfigureInputInterceptionAsync(EnableWindows);
        Assert.True(session.Guard.Mouse(0x201, 400, 300, 0));
        await session.Backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.Backend.ForegroundWindow = 2;
        session.Backend.SaveAcknowledged.TrySetResult();
        await session.WaitIdleAsync();
        Assert.Empty(session.Backend.Replayed);
    }

    [Fact]
    public async Task StoppingDrainsSaveDiagnosticsWithDispatchAndReleaseTimings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-control-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            var backend = new FakeBackend();
            backend.SaveAcknowledged.TrySetResult();
            await using (var writer = new MemolineWriter(directory, new { test = "drain diagnostics" }))
            {
                path = writer.FilePath;
                await using var guard = new LayerSaveGuard(writer, _ => { }, null, backend);
                await guard.ConfigureInputInterceptionAsync(EnableWindows);
                Assert.True(guard.Keyboard(65, 30, false, true));
                await backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            var records = MemolineReader.Read(path).ToArray();
            var request = Assert.Single(records, r => r.GetProperty("kind").GetString() == "clipSaveRequest");
            var result = Assert.Single(records, r => r.GetProperty("kind").GetString() == "saveGuardResult");
            Assert.Equal(request.GetProperty("data").GetProperty("saveId").GetString(),
                result.GetProperty("data").GetProperty("saveId").GetString());
            var data = result.GetProperty("data");
            Assert.True(data.GetProperty("released").GetBoolean());
            Assert.True(data.GetProperty("releasedTicks").GetInt64() >= data.GetProperty("saveInputDispatchedTicks").GetInt64());
            Assert.True(data.GetProperty("saveDispatchToReleaseMs").GetDouble() >= 0);
            Assert.False(data.GetProperty("saveCompletionConfirmed").GetBoolean());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PipeHandlesBadRequestsAndControlsKeyboardMousePolicy()
    {
        await using var session = new TestSession();
        string pipeName = "memoline-input-test-" + Guid.NewGuid().ToString("N");
        bool recordingReady = false;
        await using var server = new RecorderInputControlServer(session.Guard, () => recordingReady, pipeName);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        async Task<JsonElement> Query(string json)
        {
            await writer.WriteLineAsync(json);
            using var reply = JsonDocument.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? "null");
            return reply.RootElement.Clone();
        }
        Assert.False((await Query("{")).GetProperty("success").GetBoolean());
        Assert.False((await Query("""{"command":"configureInputInterception"}""")).GetProperty("success").GetBoolean());
        var unsupported = await Query("""{"command":"configureInputInterception","enabled":true,"scope":"allHardware"}""");
        Assert.False(unsupported.GetProperty("success").GetBoolean());
        Assert.False((await Query("""{"command":"configureInputInterception","enabled":true}""")).GetProperty("success").GetBoolean());
        recordingReady = true;
        Assert.True((await Query("""{"command":"configureInputInterception","enabled":true}""")).GetProperty("enabled").GetBoolean());
        Assert.True((await Query("""{"command":"getInputControlStatus"}""")).GetProperty("success").GetBoolean());
        Assert.True((await Query("""{"command":"configureInputInterception","enabled":false}""")).GetProperty("controlled").GetBoolean());
        Assert.False((await Query("""{"command":"restoreAutomaticInputProtection"}""")).GetProperty("controlled").GetBoolean());
        foreach (string invalidTicks in new[] { "-1", "1.5", "\"952451354\"", "true", "9223372036854775808" })
        {
            var invalid = await Query("""{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"invalid","triggerTicks": """ + invalidTicks + "}");
            Assert.False(invalid.GetProperty("success").GetBoolean());
            Assert.Contains("triggerTicks", invalid.GetProperty("error").GetString());
        }
        var unknown = await Query("""{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"invalid","unsupported":true}""");
        Assert.False(unknown.GetProperty("success").GetBoolean());
        Assert.Equal(0, session.Backend.Dispatches);
        session.Backend.SaveAcknowledged.TrySetResult();
        var save = await Query("""{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"pipe-save"}""");
        Assert.True(save.GetProperty("success").GetBoolean());
        Assert.True(save.GetProperty("saveInputDispatched").GetBoolean());
        Assert.False(save.GetProperty("saveCompletionConfirmed").GetBoolean());
        Assert.Equal("pipe-save", save.GetProperty("requestId").GetString());
        await session.WaitIdleAsync();
        var nullTicks = await Query("""{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"null-trigger","triggerTicks":null}""");
        Assert.True(nullTicks.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nullTicks.GetProperty("triggerTicks").ValueKind);
        await session.WaitIdleAsync();
        long triggerTicks = session.NowTicks;
        var withTrigger = await Query(JsonSerializer.Serialize(new {
            command = "requestClipSave", expectedClipPath = @"C:\art\drawing.clip", requestId = "viewport-trigger", triggerTicks }));
        Assert.True(withTrigger.GetProperty("success").GetBoolean());
        Assert.True(withTrigger.GetProperty("saveInputDispatched").GetBoolean());
        Assert.Equal("viewport-trigger", withTrigger.GetProperty("requestId").GetString());
        Assert.Equal(triggerTicks, withTrigger.GetProperty("triggerTicks").GetInt64());
        await session.WaitIdleAsync();
        var precise = await Query("""{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"precise-trigger","triggerTicks":9007199254740993}""");
        Assert.False(precise.GetProperty("success").GetBoolean());
        Assert.Contains("triggerTicks", precise.GetProperty("error").GetString());
        await session.WaitIdleAsync();
        var afterInvalid = await Query("""{"command":"requestClipSave","expectedClipPath":"C:\\art\\drawing.clip","requestId":"after-invalid","triggerTicks":0}""");
        Assert.True(afterInvalid.GetProperty("success").GetBoolean());
        Assert.True(afterInvalid.GetProperty("saveInputDispatched").GetBoolean());
        Assert.Equal(4, session.Backend.Dispatches);
        Assert.Empty(session.Backend.Replayed);
    }

    [Fact]
    public async Task ExternalSaveDiagnosticsUseTriggerTicksAndKeepActualDispatchTiming()
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-trigger-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            var backend = new FakeBackend();
            backend.SaveAcknowledged.TrySetResult();
            RecorderClipSaveResult saved;
            await using (var writer = new MemolineWriter(directory, new { test = "trigger save diagnostics" }))
            {
                path = writer.FilePath;
                await using var guard = new LayerSaveGuard(writer, _ => { }, null, backend);
                saved = await guard.RequestClipSaveAsync(new(@"C:\art\drawing.clip", "trigger-save", 0));
                Assert.True(saved.Success);
                Assert.Equal(0L, saved.TriggerTicks);
                Assert.True(saved.SaveInputDispatchedTicks > 0);
            }
            var records = MemolineReader.Read(path).ToArray();
            foreach (string kind in new[] { "clipSaveRequest", "saveGuardResult" })
            {
                var record = Assert.Single(records, r => r.GetProperty("kind").GetString() == kind);
                Assert.Equal(0L, record.GetProperty("ticks").GetInt64());
                var data = record.GetProperty("data");
                Assert.Equal(0L, data.GetProperty("triggerTicks").GetInt64());
                Assert.True(data.GetProperty("requestReceivedTicks").GetInt64() > 0);
                if (kind == "saveGuardResult")
                    Assert.Equal(saved.SaveInputDispatchedTicks, data.GetProperty("saveInputDispatchedTicks").GetInt64());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class TestSession : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memoline-control-test-" + Guid.NewGuid().ToString("N"));
        private readonly MemolineWriter _writer;
        public long NowTicks => _writer.NowTicks;
        public FakeBackend Backend { get; } = new();
        public LayerSaveGuard Guard { get; }
        public TestSession()
        {
            _writer = new MemolineWriter(_directory, new { test = "offline input control" });
            Guard = new LayerSaveGuard(_writer, _ => { }, null, Backend);
        }
        public async Task WaitIdleAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (Guard.GetInputControlStatus().Busy) await Task.Delay(1, timeout.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Backend.SaveAcknowledged.TrySetResult();
            await Guard.DisposeAsync();
            await _writer.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FakeBackend : ILayerSaveGuardBackend
    {
        public bool SaveWorkerReady { get; private set; }
        public bool IsCspForeground => ForegroundWindow == 1;
        public nint ForegroundWindow { get; set; } = 1;
        public string ForegroundWindowTitle { get; set; } = "drawing.clip - CLIP STUDIO PAINT";
        public int Initializations, Dispatches;
        public Exception? WarmupError, DispatchError;
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<LayerSaveGuard.Input> Replayed { get; } = new();
        public HashSet<int> HeldKeys { get; } = [];
        public bool IsCspPoint(int x, int y) => IsCspForeground;
        public bool IsKeyHeld(int vk) => HeldKeys.Contains(vk);
        public int GetSystemMetric(int metric) => metric is 78 or 79 ? 2000 : 0;
        public Task InitializeSaveWorkerAsync(string executable)
        {
            Initializations++;
            if (WarmupError is not null) throw WarmupError;
            SaveWorkerReady = true;
            return Task.CompletedTask;
        }
        public async Task DispatchSaveAsync(nint hwnd)
        {
            Dispatches++;
            SaveStarted.TrySetResult();
            await SaveAcknowledged.Task;
            if (DispatchError is not null) throw DispatchError;
        }
        public uint ReplayInputs(LayerSaveGuard.Input[] inputs)
        {
            foreach (var input in inputs) Replayed.Enqueue(input);
            return (uint)inputs.Length;
        }
    }
}
