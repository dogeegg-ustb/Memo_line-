using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed partial class InputControlTests
{
    [Fact]
    public async Task RecordingEndDrainsLastContactsWithoutAnyViewportOrEditTrigger()
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-final-contact-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            await using (var writer = new MemolineWriter(directory, new { test = "last contacts followed directly by stop" }))
            {
                path = writer.FilePath;
                await using var capture = new UnifiedInputCapture(writer, targetProbe: new EditProbe());
                for (int i = 0; i < 3; i++)
                {
                    capture.PostPen(EditPen(InputEventType.PenDown));
                    capture.PostPen(EditPen(InputEventType.PenUp));
                }
                var marker = await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await writer.FlushAsync();
                using var prefix = MemolineReader.Open(writer.LiveFilePath);
                var beforeStop = prefix.ReadAvailable();
                var ends = beforeStop.Where(frame => frame.GetProperty("kind").GetString() == "penEnd").ToArray();
                Assert.Equal(3, ends.Length);
                Assert.All(ends, end => Assert.True(end.GetProperty("appendId").GetUInt64() < marker.AppendId));
            }
            var sealedFrames = MemolineReader.Read(path).ToArray();
            Assert.Equal(3, sealedFrames.Count(frame => frame.GetProperty("kind").GetString() == "penBegin"));
            Assert.Equal(3, sealedFrames.Count(frame => frame.GetProperty("kind").GetString() == "penEnd"));
            Assert.Single(sealedFrames, frame => frame.GetProperty("kind").GetString() == "recordingEndRequested");
            Assert.Equal("footer", sealedFrames[^1].GetProperty("kind").GetString());
            Assert.DoesNotContain(sealedFrames, frame => frame.GetProperty("kind").GetString() is
                "editBoundaryRequested" or "penInterrupted" or "core.canvasViewState");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task EndPipeWaitsForPenUpAndReturnsOnlyAfterNativePrefixIsFlushed()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Writer.RecordAppended += frame =>
        {
            frames.Enqueue(frame);
            if (frame.Kind == "penBegin") begun.TrySetResult();
        };
        string pipeName = "memoline-final-barrier-" + Guid.NewGuid().ToString("N");
        await using var server = new RecorderInputControlServer(session.Guard, () => true, pipeName, session.Writer, capture: capture);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
        using var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        async Task<JsonElement> Query()
        {
            await output.WriteLineAsync("""{"command":"prepareRecordingEnd"}""");
            using var result = JsonDocument.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? "null");
            return result.RootElement.Clone();
        }
        var inputProtection = session.Guard.GetInputControlStatus();
        capture.PostPen(EditPen(InputEventType.PenDown));
        await begun.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = Query();
        await Task.Delay(40);
        Assert.False(pending.IsCompleted);
        Assert.DoesNotContain(frames, frame => frame.Kind == "recordingEndRequested");
        capture.PostPen(EditPen(InputEventType.PenUp));
        var response = await pending;
        Assert.True(response.GetProperty("success").GetBoolean());
        ulong markerAppend = response.GetProperty("appendId").GetUInt64();
        using (var native = MemolineReader.Open(session.Writer.LiveFilePath))
        {
            var persisted = native.ReadAvailable();
            var end = Assert.Single(persisted, frame => frame.GetProperty("kind").GetString() == "penEnd");
            var marker = Assert.Single(persisted, frame => frame.GetProperty("kind").GetString() == "recordingEndRequested");
            Assert.Equal(markerAppend, marker.GetProperty("appendId").GetUInt64());
            Assert.True(end.GetProperty("appendId").GetUInt64() < markerAppend);
            Assert.True(end.GetProperty("ticks").GetInt64() <= response.GetProperty("triggerTicks").GetInt64());
        }
        Assert.Equal(response.GetRawText(), (await Query()).GetRawText());
        Assert.Single(frames, frame => frame.Kind == "recordingEndRequested");
        Assert.Equal(inputProtection, session.Guard.GetInputControlStatus());
        Assert.Equal(0, session.Backend.Dispatches);
    }

    [Fact]
    public async Task CancelledEndWaiterDoesNotCreateAnEndMarkerAndCanBeRetried()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        capture.PostPen(EditPen(InputEventType.PenDown));
        using var cancellation = new CancellationTokenSource();
        var pending = capture.PrepareRecordingEndAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.DoesNotContain(frames, frame => frame.Kind == "recordingEndRequested");
        capture.PostPen(EditPen(InputEventType.PenUp));
        var completed = await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(frames.Single(frame => frame.Kind == "penEnd").AppendId < completed.AppendId);
        Assert.Single(frames, frame => frame.Kind == "recordingEndRequested");
    }

    [Fact]
    public async Task EndBarrierAlsoWaitsForAnActiveMouseContact()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        capture.PostMouse(0x0201, 200, 100, 0);
        var pending = capture.PrepareRecordingEndAsync();
        await Task.Delay(40);
        Assert.False(pending.IsCompleted);
        capture.PostMouse(0x0202, 200, 100, 0);
        var marker = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(frames.Single(frame => frame.Kind == "mouseUp").AppendId < marker.AppendId);
    }

    [Fact]
    public async Task PhysicalInputAfterEndMarkerIsPersistedForTheFinalCaptureFailureGate()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        capture.PostPen(EditPen(InputEventType.PenDown));
        capture.PostPen(EditPen(InputEventType.PenUp));
        var marker = await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        capture.PostPen(EditPen(InputEventType.PenDown));
        capture.PostPen(EditPen(InputEventType.PenUp));
        // A repeat passes through the same queue and preserves the immutable marker.
        Assert.Equal(marker, await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await session.Writer.FlushAsync();
        using var native = MemolineReader.Open(session.Writer.LiveFilePath);
        var persisted = native.ReadAvailable();
        Assert.Equal(2, persisted.Count(frame => frame.GetProperty("kind").GetString() == "penBegin"));
        Assert.Equal(2, persisted.Count(frame => frame.GetProperty("kind").GetString() == "penEnd"));
        Assert.Contains(persisted, frame => frame.GetProperty("kind").GetString() == "penBegin" &&
            frame.GetProperty("appendId").GetUInt64() > marker.AppendId);
        Assert.Contains(persisted, frame => frame.GetProperty("kind").GetString() == "penEnd" &&
            frame.GetProperty("appendId").GetUInt64() > marker.AppendId);
        Assert.Single(frames, frame => frame.Kind == "recordingEndRequested");
        Assert.DoesNotContain(frames, frame => frame.Kind is "editTransactionBegan" or "physicalInputDuringTransaction");
    }

    [Fact]
    public async Task RecorderTaggedSaveKeysAndMouseAfterEndMarkerDoNotEnterNativePrefix()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var marker = await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        foreach (var edge in new[] { (Vk: 0x11, Scan: 29u, Down: true), (Vk: 83, Scan: 31u, Down: true),
            (Vk: 83, Scan: 31u, Down: false), (Vk: 0x11, Scan: 29u, Down: false) })
            capture.PostKey(edge.Vk, edge.Scan, false, edge.Down, injected: true,
                extraInfo: WindowsInputHooks.RecorderInputTag);
        var router = new WindowsMouseInputRouter(capture, passivePen: true);
        foreach (int message in new[] { 0x0201, 0x0200, 0x0202 })
        {
            Assert.False(router.Observe(message, 200, 100, 0, 1, WindowsInputHooks.RecorderInputTag));
            // The queue also filters the tag if a caller bypasses the hook router.
            capture.PostMouse(message, 200, 100, 0, injected: true,
                extraInfo: WindowsInputHooks.RecorderInputTag);
        }
        Assert.Equal(marker, await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await session.Writer.FlushAsync();
        using var native = MemolineReader.Open(session.Writer.LiveFilePath);
        var persisted = native.ReadAvailable();
        Assert.Single(persisted, frame => frame.GetProperty("kind").GetString() == "recordingEndRequested");
        Assert.DoesNotContain(persisted, frame => frame.TryGetProperty("path", out var path) && path.GetString() == "hardware");
        Assert.DoesNotContain(persisted, frame => frame.GetProperty("kind").GetString() is
            "keyboardStateChanged" or "keyInput" or "mouseDown" or "mouseDrag" or "mouseUp" or "penBegin" or "penSample" or "penEnd");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalInjectedDriverKeysAndContactsAfterEndMarkerRemainInNative(bool passivePen)
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var marker = await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        capture.PostKey(0x11, 29, false, true, injected: true, extraInfo: 0);
        capture.PostKey(90, 44, false, true, injected: true, extraInfo: 0);
        capture.PostKey(90, 44, false, false, injected: true, extraInfo: 0);
        capture.PostKey(0x11, 29, false, false, injected: true, extraInfo: 0);
        if (passivePen)
        {
            capture.PostMouse(0x0201, 200, 100, 0, passivePen: true, injected: true, extraInfo: 0);
            capture.PostMouse(0x0200, 220, 100, 0, passivePen: true, injected: true, extraInfo: 0);
            capture.PostMouse(0x0202, 220, 100, 0, passivePen: true, injected: true, extraInfo: 0);
        }
        else
        {
            var router = new WindowsMouseInputRouter(capture, passivePen: false);
            router.Observe(0x0201, 200, 100, 0, 1, 0);
            router.Observe(0x0200, 220, 100, 0, 1, 0);
            router.Observe(0x0202, 220, 100, 0, 1, 0);
        }
        Assert.Equal(marker, await capture.PrepareRecordingEndAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await session.Writer.FlushAsync();
        using var native = MemolineReader.Open(session.Writer.LiveFilePath);
        var persisted = native.ReadAvailable();
        var hardware = persisted.Where(frame => frame.TryGetProperty("path", out var path) && path.GetString() == "hardware").ToArray();
        var undo = Assert.Single(hardware, frame => frame.GetProperty("kind").GetString() == "keyInput");
        Assert.Equal(90, undo.GetProperty("data").GetProperty("vk").GetInt32());
        Assert.Equal("0x0", undo.GetProperty("data").GetProperty("extraInfo").GetString());
        Assert.Equal("Ctrl", undo.GetProperty("data").GetProperty("modifiers")[0].GetString());
        string[] contactKinds = passivePen ? ["penBegin", "penSample", "penEnd"] : ["mouseDown", "mouseDrag", "mouseUp"];
        var contacts = hardware.Where(frame => contactKinds.Contains(frame.GetProperty("kind").GetString())).ToArray();
        Assert.Equal(contactKinds, contacts.Select(frame => frame.GetProperty("kind").GetString()));
        ulong beginId = contacts[0].GetProperty("eventId").GetUInt64();
        Assert.All(contacts.Skip(1), frame => Assert.Equal(beginId, frame.GetProperty("operationId").GetUInt64()));
        Assert.All(hardware, frame =>
        {
            Assert.True(frame.GetProperty("appendId").GetUInt64() > marker.AppendId);
            Assert.True(frame.GetProperty("data").GetProperty("injected").GetBoolean());
        });
        var undoTransition = Assert.Single(persisted, frame => frame.GetProperty("kind").GetString() == "keyboardStateChanged"
            && frame.GetProperty("data").GetProperty("vk").GetInt32() == 90
            && frame.GetProperty("data").GetProperty("action").GetString() == "keyDown");
        Assert.True(undoTransition.GetProperty("appendId").GetUInt64() > marker.AppendId);
        Assert.True(undoTransition.GetProperty("data").GetProperty("injected").GetBoolean());
        Assert.Single(persisted, frame => frame.GetProperty("kind").GetString() == "recordingEndRequested");
    }

    [Fact]
    public async Task EndMarkerIsRejectedDuringAnActiveEditTransaction()
    {
        await using var session = new TestSession();
        await using var capture = new UnifiedInputCapture(session.Writer, targetProbe: new EditProbe());
        var frames = new ConcurrentQueue<MemolineEvent>();
        session.Writer.RecordAppended += frames.Enqueue;
        await capture.BeginEditTransactionAsync("end-conflict", session.Guard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.PrepareRecordingEndAsync());
        Assert.DoesNotContain(frames, frame => frame.Kind == "recordingEndRequested");
        await capture.EndEditTransactionAsync("end-conflict", "failed", session.Guard);
        Assert.True((await capture.PrepareRecordingEndAsync()).AppendId > 0);
    }
}
