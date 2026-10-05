using System.Diagnostics;
using System.Reflection;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class LifecycleChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    // A real STA message pump exercises async FormClosing and the production
    // stop task. The host and accepted work never launch the recorder or inject input.
    public static int Run(string temporaryDirectory)
    {
        _checks = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var context = new ApplicationContext();
            EventHandler? begin = null;
            begin = async (_, _) =>
            {
                Application.Idle -= begin;
                try
                {
                    await StopAndCloseAsync(temporaryDirectory);
                    await CancelStartupAsync(temporaryDirectory);
                }
                catch (Exception ex) { failure = ex; }
                finally { context.ExitThread(); }
            };
            Application.Idle += begin;
            Application.Run(context);
        }) { IsBackground = true, Name = "Memoline lifecycle checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Lifecycle UI checks did not finish.");
        if (failure is not null) throw new InvalidOperationException("Lifecycle UI check failed.", failure);
        Console.WriteLine($"Passed {_checks} lifecycle checks; no Recognizer or CSP input was started.");
        return _checks;
    }

    private static async Task StopAndCloseAsync(string root)
    {
        var host = new FakeHost(Path.Combine(root, "lifecycle-stop"), pending: true);
        using var window = Window(host);
        window.Show();
        var monitor = new RecognizerMonitor(requireConfirmedViewChange: true);
        Set(window, "_monitor", monitor);
        var stopSource = new CancellationTokenSource();
        Set(window, "_stopSource", stopSource);
        Set(window, "_monitorTask", Task.CompletedTask);
        var acceptedGate = Completion();
        int committed = 0;
        async Task AcceptedCaptureAsync()
        {
            await acceptedGate.Task;
            Check(!window.IsDisposed, "The UI closes before accepted capture work can finish.");
            committed++;
        }
        var captures = Get<HashSet<Task>>(window, "_captureTasks");
        captures.Add(AcceptedCaptureAsync());
        Task first = Stop(window), second = Stop(window);
        Check(ReferenceEquals(first, second), "Concurrent Stop calls must await the same task.");
        Check(monitor.CaptureRequestsPaused, "Stopping must pause new capture requests.");
        window.Close();
        await Task.Delay(20);
        Check(!window.IsDisposed && !first.IsCompleted && host.FinishCalls == 0,
            "Close must wait for accepted captures before stopping the recorder.");
        Check(!stopSource.IsCancellationRequested, "Accepted captures must retain their live cancellation token until they drain.");
        window.Close();
        await Task.Delay(20);
        Check(!window.IsDisposed, "Repeated Close must not bypass the pending stop task.");
        acceptedGate.TrySetResult();
        await host.FinishStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(committed == 1 && host.FinishCalls == 1 && !window.IsDisposed,
            "Recorder shutdown must follow accepted capture completion exactly once.");
        host.FinishRelease.TrySetResult();
        await host.SealStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!window.IsDisposed && !first.IsCompleted && host.SealCalls == 1,
            "Close must keep the UI alive while sealing is in progress.");
        window.Close();
        await Task.Delay(20);
        Check(!window.IsDisposed, "Repeated Close during sealing must still wait for publication.");
        host.SealRelease.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => window.IsDisposed);
        Check(host.FinishCalls == 1 && host.SealCalls == 1 && captures.Count == 0,
            "Shutdown must publish once and drain the accepted-task set.");
    }

    private static async Task CancelStartupAsync(string root)
    {
        string? repository = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "replayer", "status change", "csp_blank_template.clip")))
            { repository = directory.FullName; break; }
        string bridge = Path.Combine(AppContext.BaseDirectory, "clip-layer-bridge.exe");
        string? originalBridge = repository is null ? null : Path.Combine(repository, "Organizer", "canvas layer watcher", "publish", "win-x64", "clip-layer-bridge.exe");
        if (repository is null || (!File.Exists(bridge) && !File.Exists(originalBridge)))
        {
            Console.WriteLine("Startup cancellation case skipped: read-only CLIP bridge/fixture is unavailable.");
            return;
        }
        bool ownedBridge = !File.Exists(bridge);
        if (ownedBridge) File.Copy(originalBridge!, bridge, overwrite: false);
        try
        {
            string clip = Path.Combine(repository, "replayer", "status change", "csp_blank_template.clip");
            var host = new FakeHost(Path.Combine(root, "lifecycle-start"), pending: false);
            using var window = Window(host, ["--clip", clip]);
            window.Show();
            Get<Button>(window, "_start").PerformClick();
            await host.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task starting = Get<Task>(window, "_startingTask");
            window.Close();
            await host.FinishStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.StartToken.IsCancellationRequested && !starting.IsCompleted && !window.IsDisposed,
                "Close must cancel startup and wait for its recorder cleanup continuation.");
            window.Close();
            await Task.Delay(20);
            Check(!window.IsDisposed && host.SealCalls == 0,
                "Repeated Close cannot skip unfinished startup cleanup.");
            host.FinishRelease.TrySetResult();
            await host.SealStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(starting.IsCompleted && !window.IsDisposed,
                "Startup cleanup must complete before final sealing and window closure.");
            host.SealRelease.TrySetResult();
            await UntilAsync(() => window.IsDisposed);
            Check(host.SealCalls == 1, "Cancelled initialization must seal its owned session once.");
        }
        finally { if (ownedBridge) File.Delete(bridge); }
    }

    private static MainWindow Window(FakeHost host, string[]? args = null)
        => new(args ?? [], host) { ShowInTaskbar = false, Opacity = 0 };
    private static Task Stop(MainWindow window) => (Task)(typeof(MainWindow).GetMethod("StopAsync", Private)!
        .Invoke(window, null) ?? throw new InvalidOperationException("Missing stop task."));
    private static T Get<T>(MainWindow window, string name) => (T)(typeof(MainWindow).GetField(name, Private)!
        .GetValue(window) ?? throw new InvalidOperationException("Missing lifecycle field: " + name));
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Window closure did not complete.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeHost : IWatcherHost
    {
        private bool _pending;
        private readonly string _root;
        public FakeHost(string root, bool pending) { _root = root; _pending = pending; Directory.CreateDirectory(root); }
        public string Title => "Lifecycle check";
        public string DiffRoot => Path.Combine(_root, "diffs");
        public string SettingsPath => Path.Combine(_root, "settings.json");
        public string OutputDirectory => Path.Combine(_root, "recordings");
        public bool HasPendingSeal => _pending;
        public event Action<string>? Status { add { } remove { } }
        public TaskCompletionSource PrepareStarted { get; } = Completion();
        public TaskCompletionSource FinishStarted { get; } = Completion();
        public TaskCompletionSource FinishRelease { get; } = Completion();
        public TaskCompletionSource SealStarted { get; } = Completion();
        public TaskCompletionSource SealRelease { get; } = Completion();
        public CancellationToken StartToken { get; private set; }
        public int FinishCalls { get; private set; }
        public int SealCalls { get; private set; }
        public async Task<string> PrepareAsync(string clipPath, CancellationToken token)
        {
            _pending = true; StartToken = token; PrepareStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable startup path.");
        }
        public void Attach(RecognizerMonitor monitor) { }
        public void CaptureQueued(CaptureRequest request) { }
        public Task PacketCommittedAsync(CaptureRequest request, SnapshotUpdate update, CancellationToken token) => Task.CompletedTask;
        public void CaptureFailed(CaptureRequest request, string reason) { }
        public async Task FinishRecordingAsync(CancellationToken token)
        { FinishCalls++; FinishStarted.TrySetResult(); await FinishRelease.Task; }
        public async Task<string> SealAsync(CancellationToken token)
        {
            SealCalls++; SealStarted.TrySetResult(); await SealRelease.Task;
            _pending = false; return Path.Combine(OutputDirectory, "test.memoline");
        }
    }
}
