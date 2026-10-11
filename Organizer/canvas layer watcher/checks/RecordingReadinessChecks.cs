using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CanvasLayerWatcher;

internal static class RecordingReadinessChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        async Task Scenario(string responseSession, bool captureAvailable, bool cancel)
        {
            string pipeName = "watcher-readiness-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int ready = 0, polls = 0, boundaries = 0;
            var serving = Task.Run(async () =>
            {
                try
                {
                    while (!timeout.IsCancellationRequested)
                    {
                        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.WaitForConnectionAsync(timeout.Token);
                        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                        using var request = JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? "null");
                        string? command = request.RootElement.GetProperty("command").GetString();
                        if (command == "getRecordingStatus")
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new { success = true, sessionId = responseSession,
                                recordingReady = Volatile.Read(ref ready) == 1, inputCaptureAvailable = captureAvailable }));
                            if (Interlocked.Increment(ref polls) >= 2) polled.TrySetResult();
                        }
                        else
                        {
                            Interlocked.Increment(ref boundaries);
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new { success = Volatile.Read(ref ready) == 1,
                                sessionId = responseSession, error = "Editing requires the current initialized input capture session." }));
                        }
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            });
            try
            {
                var client = new RecorderControlClient(pipeName);
                async Task StartBaseline()
                {
                    await client.WaitForRecordingReadyAsync("current-session", waiting.Token);
                    await client.PrepareEditBoundaryAsync("baseline-before", waiting.Token);
                }
                var starting = StartBaseline();
                if (responseSession != "current-session" || !captureAvailable)
                {
                    try { await starting; throw new Exception("An unavailable or foreign recorder was accepted"); }
                    catch (InvalidDataException)
                    { check(boundaries == 0, "A foreign session or missing capture cannot start the baseline"); }
                    return;
                }
                await polled.Task.WaitAsync(timeout.Token);
                check(!starting.IsCompleted && Volatile.Read(ref boundaries) == 0,
                    "Published configuration does not start a baseline while recorder initialization is pending");
                if (cancel)
                {
                    waiting.Cancel();
                    try { await starting; throw new Exception("Cancelled initialization completed"); }
                    catch (OperationCanceledException)
                    { check(boundaries == 0, "Cancelling initialization does not dispatch a baseline boundary"); }
                }
                else
                {
                    Volatile.Write(ref ready, 1);
                    await starting.WaitAsync(timeout.Token);
                    check(boundaries == 1, "The same baseline starts once after recorder initialization finishes");
                }
            }
            finally
            {
                await timeout.CancelAsync();
                await serving;
            }
        }
        await Scenario("current-session", captureAvailable: true, cancel: false);
        await Scenario("current-session", captureAvailable: true, cancel: true);
        await Scenario("foreign-session", captureAvailable: true, cancel: false);
        await Scenario("current-session", captureAvailable: false, cancel: false);
    }
}
