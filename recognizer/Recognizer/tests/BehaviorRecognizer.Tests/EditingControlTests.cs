using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed partial class InputControlTests
{
    private sealed class EditProbe : IInputTargetProbe
    {
        public bool TryGetCspCursor(out CspWindowProbe.ScreenPoint point)
        { point = new() { X = 200, Y = 100 }; return true; }
        public bool IsCspPoint(int x, int y) => true;
        public bool IsCspForeground() => true;
    }

    private static InputEvent EditPen(InputEventType type) => new()
    {
        Type = type, ContactState = type == InputEventType.PenUp ? ContactState.Hover : ContactState.Contact,
        Timestamp = DateTimeOffset.UtcNow, SessionId = "test", DeviceId = "tablet", Sequence = 1, Pressure = .5f
    };

    private sealed class EditControl : IRecorderInputControl
    {
        public bool Controlled, Enabled, FailRestore;
        public RecorderInputControlStatus GetInputControlStatus() => new(true, Controlled, Enabled, true, false, null);
        public Task<RecorderInputControlStatus> ConfigureInputInterceptionAsync(RecorderInputControlRequest request, CancellationToken cancellationToken = default)
        { Controlled = true; Enabled = request.Enabled; return Task.FromResult(GetInputControlStatus()); }
        public RecorderInputControlStatus RestoreAutomaticInputProtection()
        {
            if (FailRestore) { FailRestore = false; return GetInputControlStatus() with { Success = false, Error = "restore failed" }; }
            Controlled = false; Enabled = false; return GetInputControlStatus();
        }
        public Task<RecorderClipSaveResult> RequestClipSaveAsync(RecorderClipSaveRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No save is expected in an edit control test.");
    }

    [Fact]
    public async Task RecordingStatusWaitsForInitializationWithoutChangingCaptureOrInputProtection()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        int ready = 0;
        string pipeName = "memoline-readiness-" + Guid.NewGuid().ToString("N");
        await using var server = new RecorderInputControlServer(session.Guard, () => Volatile.Read(ref ready) == 1,
            pipeName, session.Writer, capture: capture);
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
        session.Writer.AppendState("initializationConfiguration", session.Writer.NowTicks, [], new { test = true }, "immediate");
        var before = session.Guard.GetInputControlStatus();
        var pending = await Query("""{"command":"getRecordingStatus"}""");
        Assert.True(pending.GetProperty("success").GetBoolean());
        Assert.Equal(session.Writer.SessionId, pending.GetProperty("sessionId").GetString());
        Assert.True(pending.GetProperty("inputCaptureAvailable").GetBoolean());
        Assert.False(pending.GetProperty("recordingReady").GetBoolean());
        Assert.False((await Query("""{"command":"prepareEditBoundary","requestId":"too-early"}""")).GetProperty("success").GetBoolean());
        Assert.DoesNotContain(frames, f => f.Kind == "editBoundaryRequested");
        Volatile.Write(ref ready, 1);
        Assert.True((await Query("""{"command":"getRecordingStatus"}""")).GetProperty("recordingReady").GetBoolean());
        Assert.Equal(before, session.Guard.GetInputControlStatus());
        Assert.Single(frames);
        Assert.True((await Query("""{"command":"prepareEditBoundary","requestId":"after-ready"}""")).GetProperty("success").GetBoolean());
        Assert.Single(frames, f => f.Kind == "editBoundaryRequested");
        Assert.Empty(session.Backend.Replayed);
        Assert.Equal(0, session.Backend.Dispatches);
    }

    [Fact]
    public async Task FailedTransactionRestoreKeepsIntervalOpenUntilSuccessfulRetry()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        var control = new EditControl();
        await capture.BeginEditTransactionAsync("retry", control);
        control.FailRestore = true;
        await Assert.ThrowsAsync<IOException>(() => capture.EndEditTransactionAsync("retry", "failed", control));
        Assert.True(capture.EditTransactionActive);
        Assert.DoesNotContain(frames, f => f.Kind == "editTransactionEnded");
        capture.PostKey(65, 30, false, true);
        var end = await capture.EndEditTransactionAsync("retry", "failed", control);
        Assert.False(control.Enabled);
        Assert.False(control.Controlled);
        Assert.False(capture.EditTransactionActive);
        Assert.True(frames.Single(f => f.Kind == "physicalInputDuringTransaction").AppendId < end.AppendId);
        Assert.Single(frames, f => f.Kind == "editTransactionEnded");
        Assert.Equal(end, await capture.EndEditTransactionAsync("retry", "failed", control));
    }

    [Fact]
    public async Task CaptureDisposeDrainsQueuedBeginAndRestoresInterception()
    {
        await using var session = new TestSession();
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        var control = new EditControl();
        var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var begin = capture.BeginEditTransactionAsync("dispose", control);
        await capture.DisposeAsync();
        await begin;
        Assert.False(control.Controlled);
        Assert.False(control.Enabled);
        Assert.Single(frames, f => f.Kind == "editTransactionBegan");
        Assert.Equal("failed", frames.Single(f => f.Kind == "editTransactionEnded").Data.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TransactionOwnsGuardPolicyEvenThroughDirectControlApi()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        await capture.BeginEditTransactionAsync("owned", session.Guard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Guard.ConfigureInputInterceptionAsync(new() { Enabled = false }));
        Assert.Throws<InvalidOperationException>(() => session.Guard.RestoreAutomaticInputProtection());
        Assert.True(session.Guard.GetInputControlStatus().Enabled);
        await capture.EndEditTransactionAsync("owned", "success", session.Guard);
        Assert.False(session.Guard.GetInputControlStatus().Controlled);
    }

    [Fact]
    public async Task PrepareEditBoundaryWaitsForCompleteContactIsIdempotentAndKeepsRecordingOpen()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        capture.PostPen(EditPen(InputEventType.PenDown));
        var first = capture.PrepareEditBoundaryAsync("b-1");
        await Task.Delay(40);
        Assert.False(first.IsCompleted);
        Assert.Contains(frames, f => f.Kind == "penBegin");
        capture.PostPen(EditPen(InputEventType.PenUp));
        var boundary = await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("editBoundaryRequested", boundary.Kind);
        Assert.Equal("b-1", boundary.Data.GetProperty("requestId").GetString());
        Assert.Equal(boundary, await capture.PrepareEditBoundaryAsync("b-1"));
        var second = await capture.PrepareEditBoundaryAsync("b-2");
        Assert.True(second.AppendId > boundary.AppendId);
        Assert.True(frames.Single(f => f.Kind == "penEnd").AppendId < boundary.AppendId);
        Assert.DoesNotContain(frames, f => f.Kind is "penInterrupted" or "recordingEndRequested" or "footer");
        capture.PostPen(EditPen(InputEventType.PenDown));
        capture.PostPen(EditPen(InputEventType.PenUp));
        await capture.PrepareEditBoundaryAsync("b-3");
        Assert.Equal(2, frames.Count(f => f.Kind == "penBegin"));
        Assert.Equal(3, frames.Count(f => f.Kind == "editBoundaryRequested"));
    }

    [Fact]
    public async Task CancelledBoundaryDoesNotAppendAndCanBeRetried()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        capture.PostPen(EditPen(InputEventType.PenDown));
        using var cancellation = new CancellationTokenSource();
        var pending = capture.PrepareEditBoundaryAsync("cancel", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        capture.PostPen(EditPen(InputEventType.PenUp));
        await capture.PrepareEditBoundaryAsync("after-cancel");
        Assert.DoesNotContain(frames, f => f.Kind == "editBoundaryRequested" && f.Data.GetProperty("requestId").GetString() == "cancel");
        Assert.Equal("cancel", (await capture.PrepareEditBoundaryAsync("cancel")).Data.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task EditPipeValidatesIdentitiesAndRestoresPreviousInterception()
    {
        await using var session = new TestSession();
        await session.Guard.ConfigureInputInterceptionAsync(new() { Enabled = false });
        var before = session.Guard.GetInputControlStatus();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        string pipeName = "memoline-edit-control-" + Guid.NewGuid().ToString("N");
        await using var server = new RecorderInputControlServer(session.Guard, () => true, pipeName, session.Writer, capture: capture);
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
        Assert.False((await Query("""{"command":"prepareEditBoundary","requestId":""}""")).GetProperty("success").GetBoolean());
        var first = await Query("""{"command":"prepareEditBoundary","requestId":"pipe-1"}""");
        Assert.True(first.GetProperty("success").GetBoolean());
        Assert.Equal(session.Writer.SessionId, first.GetProperty("sessionId").GetString());
        Assert.Equal(first.GetRawText(), (await Query("""{"command":"prepareEditBoundary","requestId":"pipe-1"}""")).GetRawText());
        Assert.True((await Query("""{"command":"beginEditTransaction","transactionId":"tx-1"}""")).GetProperty("appendId").GetUInt64() > 0);
        Assert.True(session.Guard.GetInputControlStatus().Enabled);
        Assert.False((await Query("""{"command":"beginEditTransaction","transactionId":"tx-2"}""")).GetProperty("success").GetBoolean());
        Assert.False((await Query("""{"command":"endEditTransaction","transactionId":"wrong","outcome":"failed"}""")).GetProperty("success").GetBoolean());
        Assert.False((await Query("""{"command":"configureInputInterception","enabled":false}""")).GetProperty("success").GetBoolean());
        Assert.True((await Query("""{"command":"endEditTransaction","transactionId":"tx-1","outcome":"success"}""")).GetProperty("success").GetBoolean());
        var after = session.Guard.GetInputControlStatus();
        Assert.Equal(before.Controlled, after.Controlled);
        Assert.Equal(before.Enabled, after.Enabled);
        Assert.Single(frames, f => f.Kind == "editTransactionBegan");
        Assert.Single(frames, f => f.Kind == "editTransactionEnded");
        Assert.Empty(session.Backend.Replayed);
        Assert.Equal(0, session.Backend.Dispatches);
    }

    [Fact]
    public async Task PhysicalAlertsDoNotCreateDrawingRecordsDuringReplay()
    {
        await using var session = new TestSession();
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        session.Writer.HardwareAppended += frame => session.Writer.ReserveStatePackage(frame.EventId, frame.Ticks, "synchronous-followup");
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        await capture.BeginEditTransactionAsync("exact-reference", session.Guard);
        capture.PostPen(EditPen(InputEventType.PenDown));
        capture.PostPen(EditPen(InputEventType.PenUp));
        capture.PostKey(65, 30, false, true);
        capture.PostKey(65, 30, false, false);
        await capture.EndEditTransactionAsync("exact-reference", "success", session.Guard);
        var alerts = frames.Where(f => f.Kind == "physicalInputDuringTransaction").ToArray();
        Assert.Equal(4, alerts.Length);
        Assert.All(alerts, alert=>
        {
            Assert.Equal(0UL,alert.Data.GetProperty("appendId").GetUInt64());
            Assert.False(alert.Data.GetProperty("inputRecorded").GetBoolean());
        });
        Assert.DoesNotContain(frames,f=>f.Kind is "penBegin" or "penEnd" or "keyInput" or "keyboardStateChanged" or "statePackageReserved");
    }

    [Fact]
    public async Task PhysicalAlertForUnrecordedMouseMoveDoesNotReusePreviousInputReference()
    {
        await using var session = new TestSession();
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        await capture.BeginEditTransactionAsync("move-reference", session.Guard);
        capture.PostMouse(0x0201, 200, 100, 0);
        capture.PostMouse(0x0202, 200, 100, 0);
        capture.PostMouse(0x0200, 210, 100, 0);
        await capture.EndEditTransactionAsync("move-reference", "success", session.Guard);
        var alerts = frames.Where(f => f.Kind == "physicalInputDuringTransaction").ToArray();
        Assert.Equal(3, alerts.Length);
        Assert.All(alerts,a=>Assert.Equal(0UL,a.Data.GetProperty("appendId").GetUInt64()));
        Assert.Equal(0UL, alerts[2].Data.GetProperty("appendId").GetUInt64());
        Assert.False(alerts[2].Data.GetProperty("inputRecorded").GetBoolean());
        Assert.True(alerts[2].Data.GetProperty("latestAppendId").GetUInt64() > 0);
        Assert.Equal("move-reference", alerts[2].Data.GetProperty("transactionId").GetString());
        Assert.DoesNotContain(frames, f => f.Kind == "mouseDrag");
    }

    [Fact]
    public async Task TransactionReportsPhysicalPenMouseAndKeyboardButNotInjectedOrTaggedInput()
    {
        await using var session = new TestSession();
        await using var hub = new RecorderRealtimeHub(session.Writer);
        await using var subscription = hub.Subscribe(["all"], includeSnapshot: false);
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        await capture.BeginEditTransactionAsync("tx", session.Guard);
        capture.PostPen(EditPen(InputEventType.PenDown));
        capture.PostPen(EditPen(InputEventType.PenUp));
        var router = new WindowsMouseInputRouter(capture, passivePen: true);
        router.Observe(0x201, 200, 100, 0, 0, 0);
        router.Observe(0x202, 200, 100, 0, 0, 0);
        router.Observe(0x201, 200, 100, 0, 1, 0xFF515700);
        router.Observe(0x202, 200, 100, 0, 1, 0xFF515700);
        capture.PostKey(65, 30, false, true);
        capture.PostKey(65, 30, false, false);
        capture.PostKey(66, 48, false, true, injected: true);
        capture.PostKey(66, 48, false, false, injected: true);
        capture.PostKey(67, 46, false, true, injected: true, extraInfo: WindowsInputHooks.RecorderInputTag);
        router.Observe(0x201, 200, 100, 0, 1, WindowsInputHooks.RecorderInputTag);
        await capture.EndEditTransactionAsync("tx", "success", session.Guard);
        var alerts = frames.Where(f => f.Kind == "physicalInputDuringTransaction").ToArray();
        Assert.Equal(new[] { "pen", "pen", "mouse", "mouse", "keyboard", "keyboard" },
            alerts.Select(f => f.Data.GetProperty("deviceType").GetString()));
        Assert.All(alerts, f => Assert.Equal("tx", f.Data.GetProperty("transactionId").GetString()));
        Assert.All(alerts, f => Assert.True(f.Data.GetProperty("appendId").GetUInt64() < f.AppendId));
        Assert.DoesNotContain(frames, f => f.Kind == "keyInput" && f.Data.GetProperty("vk").GetInt32() == 67);
        var passive = frames.Where(f => f.Kind is "penBegin" or "penEnd" && f.DeviceSource?.CaptureApi == "Windows.LowLevelMouseHook").ToArray();
        Assert.Empty(passive);
        Assert.All(passive, f => Assert.True(f.Data.GetProperty("injected").GetBoolean()));
        Assert.All(frames.Where(f => f.Kind is "mouseDown" or "mouseUp"), f => Assert.False(f.Data.GetProperty("injected").GetBoolean()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int published = 0;
        await foreach (var message in subscription.ReadAllAsync(timeout.Token))
        {
            if (message.Kind == "physicalInputDuringTransaction") { Assert.Equal("system", message.Channel); published++; }
            if (message.Kind == "editTransactionEnded") break;
        }
        Assert.Equal(alerts.Length, published);
    }

    [Theory]
    [InlineData("eventEditing")]
    [InlineData("preciseUndo")]
    public async Task SemanticEditIsRecordedOnceAndPostReplayUndoIsNormal(string kind)
    {
        await using var session=new TestSession();
        await using var capture=new UnifiedInputCapture(session.Writer,targetProbe:new EditProbe());
        var frames=new ConcurrentQueue<MemolineEvent>();session.Writer.RecordAppended+=frames.Enqueue;
        using var changes=JsonDocument.Parse("[{\"beginAppendId\":1,\"delete\":true}]");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>capture.RecordEditingActionAsync("semantic",kind,changes.RootElement,default));
        await capture.BeginEditTransactionAsync("semantic",session.Guard);
        capture.PostMouse(0x201,200,100,0,injected:true);
        capture.PostMouse(0x202,200,100,0,injected:true);
        capture.PostKey(65,30,false,true,injected:true);
        capture.PostKey(65,30,false,false,injected:true);
        await capture.EndEditTransactionAsync("semantic","executed",session.Guard);
        using var replayed=JsonDocument.Parse("[{\"sessionId\":\"s\",\"beginAppendId\":1}]");
        using var malformed=JsonDocument.Parse("[{\"sessionId\":\"s\",\"beginAppendId\":0}]");
        await Assert.ThrowsAsync<ArgumentException>(()=>capture.RecordEditingActionAsync("semantic",kind,changes.RootElement,default,default,malformed.RootElement));
        var first=await capture.RecordEditingActionAsync("semantic",kind,changes.RootElement,default,default,replayed.RootElement);
        var duplicate=await capture.RecordEditingActionAsync("semantic",kind,changes.RootElement,default,default,replayed.RootElement);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>capture.RecordEditingActionAsync("semantic",kind,changes.RootElement,default));
        Assert.Equal(first.AppendId,duplicate.AppendId);
        Assert.Single(frames,f=>f.Kind=="editingActionCompleted");
        Assert.Equal(kind,first.Data.GetProperty("editKind").GetString());
        // The contacts Memoline drew again are kept with the record: they make its Memoline-operation packet.
        Assert.Equal(1UL,first.Data.GetProperty("replayed")[0].GetProperty("beginAppendId").GetUInt64());
        Assert.DoesNotContain(frames,f=>f.Path=="hardware");
        capture.PostKey(0x11,29,false,true);
        capture.PostKey(90,44,false,true);
        capture.PostKey(90,44,false,false);
        capture.PostKey(0x11,29,false,false);
        await capture.PrepareEditBoundaryAsync("after-semantic");
        Assert.Contains(frames,f=>f.Kind=="keyInput" && f.Data.GetProperty("vk").GetInt32()==90 && f.Data.GetProperty("modifiers").EnumerateArray().Any(m=>m.GetString()=="Ctrl"));
    }
}
