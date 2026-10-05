using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal sealed class DemoController : IWatcherHost, IAsyncDisposable
{
    private readonly string _base = AppContext.BaseDirectory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _finishGate = new(1, 1);
    private Process? _recorder;
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask;
    private TaskCompletionSource _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AggregateSession? _aggregate;
    private string? _mechanicalPath, _sessionId, _lastSealed;
    private string _workRoot;
    private bool _completed;
    private DateTime _recorderStartedUtc;
    private string RecorderRoot => Path.Combine(_base, "Recognizer");
    public string Title => "Memoline · CSP 聚集事件记录";
    public string SettingsPath => Path.Combine(_base, "demo-settings.json");
    public string OutputDirectory => Path.Combine(_base, "recordings");
    public string DiffRoot => Path.Combine(_workRoot, "layer-diffs");
    public bool HasPendingSeal => _recorder is not null || _mechanicalPath is not null;
    public event Action<string>? Status;

    public DemoController() => _workRoot = Path.Combine(_base, ".work", "standby");

    public async Task<string> PrepareAsync(string clipPath, CancellationToken token)
    {
        if (HasPendingSeal) throw new InvalidOperationException("上一份录制尚未封盘；请先点击停止并封盘。");
        string executable = Path.Combine(RecorderRoot, "BehaviorRecognizer.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少集成的 Recognizer，请运行 Build.ps1。", executable);
        _workRoot = Path.Combine(_base, ".work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot); Directory.CreateDirectory(OutputDirectory);
        _completed = false; _lastSealed = null;
        _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only the demo's private copy is configured. The original Recognizer
        // recorder implementation and native records are kept intact.
        string settingsPath = Path.Combine(RecorderRoot, "integration", "settings.json");
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, token))?.AsObject()
            ?? throw new InvalidDataException("Recognizer 设置文件无效。");
        settings["clipPath"] = Path.GetFullPath(clipPath);
        await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(new() { WriteIndented = true }), token);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = RecorderRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        _recorderStartedUtc = DateTime.UtcNow;
        _recorder = Process.Start(start) ?? throw new IOException("Recognizer 启动失败。");
        _stdout = PumpAsync(_recorder.StandardOutput);
        _stderr = PumpAsync(_recorder.StandardError);
        Status?.Invoke("Recognizer 已启动；请完成初始化并切回 CSP。");
        string directory = Path.Combine(RecorderRoot, "procedure", "stroke");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_recorder.HasExited) throw new IOException($"Recognizer 提前退出（{_recorder.ExitCode}），详见工作目录的 recognizer.log。");
            if (Directory.Exists(directory))
                foreach (string manifest in Directory.EnumerateFiles(directory, "*.memoline.live.json").OrderByDescending(File.GetLastWriteTimeUtc))
                    try
                    {
                        if (File.GetLastWriteTimeUtc(manifest) < _recorderStartedUtc.AddSeconds(-1)) continue;
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token));
                        var data = document.RootElement;
                        if (J.Tick(data, "processId", 0) != _recorder.Id) continue;
                        string recording = J.Text(data, "filePath") ?? throw new InvalidDataException("Recognizer 描述文件缺少文件路径。");
                        RequireUnder(recording, directory);
                        _mechanicalPath = Path.GetFullPath(recording);
                        _sessionId = J.Text(data, "sessionId") ?? throw new InvalidDataException("Recognizer 描述文件缺少 sessionId。");
                        return manifest;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
            await Task.Delay(100, token);
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (_sync)
                try { File.AppendAllText(Path.Combine(_workRoot, "recognizer.log"), line + Environment.NewLine); }
                catch (IOException) { }
            if (line.Contains("初始化", StringComparison.Ordinal) || line.Contains("启动失败", StringComparison.Ordinal)) Status?.Invoke(line);
        }
    }

    public void Attach(RecognizerMonitor monitor)
    {
        monitor.MessageReceived += message =>
        {
            if (message.Channel == "system" && message.Kind == "hello")
            {
                if (message.SessionId != _sessionId) throw new InvalidDataException("集成监听连接到了其他 Recognizer 会话。");
                lock (_sync)
                {
                    if (_aggregate is null)
                        _aggregate = new(Path.Combine(_workRoot, "aggregate-events.jsonl.part"),
                            _mechanicalPath ?? throw new InvalidOperationException("原生记录路径尚未建立。"), message);
                }
            }
            if (message.Channel == "system" && message.Kind == "sessionEnded") _sessionEnded.TrySetResult();
        };
        monitor.ViewportObserved += observation =>
        {
            lock (_sync) _aggregate?.ObserveViewport(observation);
            if (observation.Changed == 1) Status?.Invoke("画布视口已变化，记录聚集事件边界…");
        };
    }

    public void CaptureQueued(CaptureRequest request) { lock (_sync) _aggregate?.CaptureQueued(request); }
    public Task PacketCommittedAsync(CaptureRequest request, SnapshotUpdate update, CancellationToken token)
        => Task.Run(() => { lock (_sync) _aggregate?.CaptureCommitted(request, update); }, token);
    public void CaptureFailed(CaptureRequest request, string reason) { lock (_sync) _aggregate?.CaptureFailed(request, reason); }

    public async Task FinishRecordingAsync(CancellationToken token)
    {
        await _finishGate.WaitAsync(token).ConfigureAwait(false);
        try { await FinishCoreAsync(token).ConfigureAwait(false); }
        finally { _finishGate.Release(); }
    }

    private async Task FinishCoreAsync(CancellationToken token)
    {
        if (_recorder is not { } process) return;
        Status?.Invoke("等待 Recognizer 停止输入并写完原生记录…");
        // Initialization may be cancelled just after the native writer starts,
        // before PrepareAsync observes its manifest. Recover that owned path.
        DiscoverOwnedRecording(process.Id);
        if (!process.HasExited)
        {
            await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(4), token).ConfigureAwait(false);
        }
        await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        DiscoverOwnedRecording(process.Id);
        process.Dispose(); _recorder = null;
        // sessionEnded is sent after native footer and all recorder producers drain.
        if (_aggregate is not null)
            try { await _sessionEnded.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch (TimeoutException) { Status?.Invoke("实时接口已关闭，将以原生文件 footer 校验封盘。"); }
        if (_mechanicalPath is not null && _aggregate is null)
        {
            using var native = MemolineReader.Open(_mechanicalPath);
            var header = native.ReadAvailable().FirstOrDefault(f => J.Text(f, "kind") == "header");
            if (header.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生记录缺少 header。");
            var data = JsonSerializer.SerializeToElement(new
            {
                filePath = _mechanicalPath, liveFilePath = _mechanicalPath + ".part",
                frequency = J.Tick(header, "frequency", 0), originTicks = J.Tick(header, "originTicks", 0)
            });
            var hello = new RecorderRealtimeEvent(1, _sessionId!, 0, "system", "hello", false,
                0, 0, 0, 0, 0, null, null, null, data);
            _aggregate = new(Path.Combine(_workRoot, "aggregate-events.jsonl.part"), _mechanicalPath, hello);
        }
    }

    private void DiscoverOwnedRecording(int processId)
    {
        if (_mechanicalPath is not null) return;
        string stroke = Path.Combine(RecorderRoot, "procedure", "stroke");
        if (!Directory.Exists(stroke)) return;
        foreach (string manifest in Directory.EnumerateFiles(stroke, "*.memoline.live.json"))
            try
            {
                if (File.GetLastWriteTimeUtc(manifest) < _recorderStartedUtc.AddSeconds(-1)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                var data = document.RootElement;
                if (J.Tick(data, "processId", 0) != processId) continue;
                string path = J.Text(data, "filePath") ?? throw new InvalidDataException("原生文件路径缺失。");
                RequireUnder(path, stroke);
                _mechanicalPath = Path.GetFullPath(path);
                _sessionId = J.Text(data, "sessionId") ?? throw new InvalidDataException("原生会话 ID 缺失。");
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    public Task<string> SealAsync(CancellationToken token) => Task.Run(() =>
    {
        lock (_sync)
        {
            if (_lastSealed is not null) return _lastSealed;
            if (_recorder is not null) throw new InvalidOperationException("Recognizer 尚未正常停止，不能封盘。");
            var aggregate = _aggregate ?? throw new InvalidOperationException("没有可封盘的会话。");
            string native = _mechanicalPath ?? throw new InvalidOperationException("原生记录路径缺失。");
            if (!_completed) { aggregate.Complete(); aggregate.Dispose(); _completed = true; }
            string output = Path.Combine(OutputDirectory, Path.GetFileName(native));
            RequireUnder(native, Path.Combine(RecorderRoot, "procedure", "stroke"));
            RequireUnder(aggregate.FilePath, _workRoot);
            BundleArchive.Seal(native, aggregate.FilePath, output, token);
            // Publishing is the commit. Cleanup failures must not make a valid
            // sealed archive look unfinished or prevent the next recording.
            _lastSealed = output; _mechanicalPath = null; _sessionId = null; _aggregate = null;
            // Seal verifies byte hashes and references before publishing. Remove
            // only this owned session's two primary intermediate data files.
            foreach (string temporary in new[] { native, aggregate.FilePath })
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Status?.Invoke("已成功封盘，中间文件清理未完成：" + temporary + "；" + ex.Message); }
            return output;
        }
    }, token);

    internal static void RequireUnder(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件路径超出本次集成会话的目录。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_recorder is not null)
            try { await FinishRecordingAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Status?.Invoke("原生记录仍保留：" + ex.Message); }
        _aggregate?.Dispose();
    }
}
