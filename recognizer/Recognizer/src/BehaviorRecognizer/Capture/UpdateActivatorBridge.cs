using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Capture;

/// <summary>Owns the configuration/overlay helper. All session writes stay in the recorder.</summary>
public sealed class UpdateActivatorBridge : IAsyncDisposable
{
    private static readonly object ConsoleStateLock = new();
    private static readonly JsonSerializerOptions ConsoleJson = new() { WriteIndented = true };
    private readonly MemolineWriter _writer;
    private readonly Process _process;
    private readonly ProcessLifetimeJob _processJob;
    private readonly Channel<object> _outgoing = Channel.CreateUnbounded<object>(new() { SingleReader = true });
    private readonly Task _send, _receive, _errors;
    private long _lastCursorTicks;
    private volatile bool _stopping;
    public LayerSaveGuard Guard { get; }
    private volatile bool _recordingReady;
    public bool RecordingReady => _recordingReady;
    private readonly string _initialPackage;
    private readonly DriverInitializationService? _driverInitialization;

    private UpdateActivatorBridge(MemolineWriter writer, Process process, ProcessLifetimeJob processJob,
        DriverInitializationService? driverInitialization)
    {
        _writer = writer;
        _process = process;
        _processJob = processJob;
        _driverInitialization = driverInitialization;
        Guard = new LayerSaveGuard(writer, msg => _outgoing.Writer.TryWrite(msg),_processJob.Add);
        _initialPackage = writer.ReserveStatePackage(0, 0, "initialState");
        _outgoing.Writer.TryWrite(new { type = "timelineSession", initialPackageId = _initialPackage });
        if (driverInitialization is not null)
            _outgoing.Writer.TryWrite(new { type = "driverConfigurations", data = driverInitialization.Catalog });
        _writer.HardwareAppended += OnHardware;
        _send = SendAsync();
        _receive = ReceiveAsync();
        _errors = ErrorsAsync();
    }

    public static UpdateActivatorBridge? Start(MemolineWriter writer, DriverInitializationService? driverInitialization = null)
    {
        ProcessLifetimeJob? processJob=null;
        Process? process=null;
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "integration", "runtime.py");
            if (!File.Exists(script)) throw new FileNotFoundException("Missing integration/runtime.py", script);
            string? configured = System.Environment.GetEnvironmentVariable("MEMOLINE_PYTHON");
            var start = new ProcessStartInfo(configured ?? "py")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            if (configured is null) start.ArgumentList.Add("-3");
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(script);
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["MEMOLINE_CLOCK_ORIGIN"] = writer.ClockOriginTicks.ToString();
            start.Environment["MEMOLINE_SPOOL"] = Path.Combine(Path.GetDirectoryName(writer.FilePath)!, writer.SessionId + ".spool");
            processJob=new ProcessLifetimeJob();
            process = Process.Start(start) ?? throw new IOException("Unable to start workspace helper.");
            processJob.Add(process);
            return new UpdateActivatorBridge(writer, process,processJob, driverInitialization);
        }
        catch (Exception ex)
        {
            processJob?.Dispose();
            if (process is { HasExited:false }) process.Kill(entireProcessTree:true);
            process?.Dispose();
            Console.Error.WriteLine($"[UpdateActivator] {ex.Message}");
            writer.AppendState("updateActivatorError", writer.NowTicks, [], new { message = ex.Message });
            return null;
        }
    }

    private void OnHardware(MemolineEvent evt)
    {
        if (!_stopping && evt.Kind is "keyInput" or "mouseWheel" or "mouseDown" or "mouseUp" or "penBegin" or "penEnd")
        {
            string packageId = _writer.ReserveStatePackage(evt.EventId, evt.Ticks, "hardwareActivation");
            _outgoing.Writer.TryWrite(new { type = "input", ticks = evt.Ticks, statePackageId = packageId, @event = evt });
        }
    }

    public void ObserveCursor(int x, int y, bool inCsp, bool foreground, int[] heldKeys, ulong[] operations)
    {
        long ticks = _writer.NowTicks;
        if (_stopping || ticks - _lastCursorTicks < Stopwatch.Frequency / 30) return;
        _lastCursorTicks = ticks;
        _outgoing.Writer.TryWrite(new { type = "cursor", ticks, x, y, inCsp, foreground,
            heldKeys, pointerDown = operations.Length > 0, relatedEventIds = operations });
    }

    private async Task SendAsync()
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync())
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg, MemolineWriter.Json));
            await _process.StandardInput.FlushAsync();
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(ex.Message);
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var msg = document.RootElement;
                string type = msg.GetProperty("type").GetString()!;
                if (type == "selectDriverConfiguration")
                {
                    try
                    {
                        string? driverConfigPath = msg.GetProperty("path").GetString();
                        int? screenIndex = msg.TryGetProperty("screenIndex", out var index) && index.ValueKind == JsonValueKind.Number
                            ? index.GetInt32() : null;
                        var snapshot = (_driverInitialization ?? throw new InvalidOperationException("驱动初始化服务不可用"))
                            .Select(driverConfigPath, screenIndex);
                        _writer.AppendState("driverConfiguration", _writer.NowTicks, [], snapshot, "immediate");
                        _outgoing.Writer.TryWrite(new { type = "driverConfigurationResult", requestId = msg.GetProperty("requestId").GetString(), data = snapshot });
                    }
                    catch (Exception ex)
                    {
                        _outgoing.Writer.TryWrite(new { type = "driverConfigurationResult", requestId = msg.GetProperty("requestId").GetString(),
                            data = new { status = "unavailable", warnings = new[] { ex.Message } } });
                    }
                    continue;
                }
                if (type == "timelineResult")
                {
                    DisplayTimelineResult(msg.GetProperty("data"), msg.GetProperty("packageId").GetString()!);
                    _writer.AppendPackageResult(msg.GetProperty("packageId").GetString()!, msg.GetProperty("data"));
                    continue;
                }
                if (type == "timelineReserve")
                {
                    string requestId = msg.GetProperty("requestId").GetString()!;
                    string id = _writer.ReserveStatePackage(_writer.LastHardwareEventId,
                        _writer.NowTicks, "observationBoundary");
                    _outgoing.Writer.TryWrite(new { type = "timelineReserved", requestId, packageId = id });
                    continue;
                }
                if (type == "initialStateReady")
                {
                    _recordingReady = true;
                    bool degraded=msg.TryGetProperty("degraded",out var degradedValue) && degradedValue.GetBoolean();
                    Console.WriteLine(degraded
                        ? $"[Initialization] 初始化已结束，部分状态尚不可用：{msg.GetProperty("unavailableModules")}. 开始录制输入；相关操作会重试状态更新。"
                        : "[Initialization] 初始状态已登记，开始录制硬件事件。");
                    continue;
                }
                if (type == "captureWatermark")
                {
                    _outgoing.Writer.TryWrite(new { type = "captureWatermark", requestId = msg.GetProperty("requestId").GetString(),
                        lastEventId = _writer.LastHardwareEventId });
                    continue;
                }
                if (msg.GetProperty("type").GetString() == "guardConfig")
                {
                    Guard.Configure(msg.GetProperty("data"));
                    continue;
                }
                if (msg.GetProperty("type").GetString() != "state") continue;
                string kind = msg.GetProperty("kind").GetString()!;
                if (kind is not ("shortcutConfiguration" or "workspaceStatus" or "workspaceSuspended" or
                    "panelUpdateRequested" or "panelActivationEnded" or "shortcutResolved" or "updateActivatorError" or
                    "screenshotBlob" or "captureUnavailable" or "stateResult" or "coreStateUpdated" or "coreEvidenceCaptured" or "analysisError" or
                    "initializationConfiguration" or "initializationStatus" or "clipParseResult" or "clipParseError"))
                    throw new InvalidDataException($"Unknown update message: {kind}");
                var refs = msg.GetProperty("relatedEventIds").EnumerateArray().Select(v => v.GetUInt64()).ToArray();
                _writer.AppendState(kind, msg.GetProperty("ticks").GetInt64(), refs, msg.GetProperty("data"),
                    msg.TryGetProperty("path", out var path) ? path.GetString()! : "immediate");
                if (kind == "workspaceStatus")
                    Console.WriteLine($"[Workspace] {msg.GetProperty("data").GetProperty("status").GetString()}");
                else if (kind == "updateActivatorError")
                    Console.Error.WriteLine($"[UpdateActivator] {msg.GetProperty("data").GetRawText()}");
                else if (kind is "captureUnavailable" or "analysisError")
                    Console.Error.WriteLine($"[状态更新失败] {msg.GetProperty("data").GetRawText()}");
                else if (kind == "panelUpdateRequested" && msg.GetProperty("data").GetProperty("phase").GetString() == "invalidate")
                    Console.WriteLine($"[状态更新触发] {msg.GetProperty("data").GetProperty("panels")}，来源={msg.GetProperty("data").GetProperty("reason")}");
            }
            if (!_stopping) Fail("Workspace helper exited; overlay and update requests are unavailable.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private static void DisplayTimelineResult(JsonElement data, string packageId)
    {
        if (data.TryGetProperty("reason", out var reason) && reason.GetString() == "noStateActivation")
            return;

        bool initial = data.TryGetProperty("initial", out var initialValue) && initialValue.GetBoolean();
        string status = data.TryGetProperty("status", out var statusValue)
            ? DescribeStatus(statusValue.GetString()) : "未知";
        lock (ConsoleStateLock)
        {
            Console.WriteLine();
            Console.WriteLine(initial
                ? $"[初始状态解析完成] {status}  package={packageId}"
                : $"[状态更新解析完成] {status}  package={packageId}");

            if (!data.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine(JsonSerializer.Serialize(data, ConsoleJson));
                return;
            }

            foreach (var update in updates.EnumerateArray())
            {
                string module = update.TryGetProperty("module", out var moduleValue)
                    ? DescribeModule(moduleValue.GetString()) : "状态模块";
                string updateStatus = update.TryGetProperty("status", out var updateStatusValue)
                    ? DescribeStatus(updateStatusValue.GetString()) : "未知";
                Console.WriteLine($"  [{module}] {updateStatus}");

                if (update.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    Console.WriteLine($"  错误: {error.GetString()}");
                if (update.TryGetProperty("evidence", out var evidence))
                {
                    foreach (string field in new[] { "triggerTicks", "capturedTicks", "observedAfterEventId", "saveId" })
                    {
                        if (evidence.TryGetProperty(field, out var value))
                            Console.WriteLine($"  {field}: {value}");
                    }
                }
                if (update.TryGetProperty("state", out var state) && state.ValueKind is not JsonValueKind.Null)
                    Console.WriteLine(JsonSerializer.Serialize(state, ConsoleJson));
                else if (update.TryGetProperty("observedState", out var observedState))
                {
                    Console.WriteLine("  部分识别结果（未知项仍保留，未作为完整确认状态）：");
                    Console.WriteLine(JsonSerializer.Serialize(observedState, ConsoleJson));
                }
            }
        }
    }

    private static string DescribeModule(string? module) => module switch
    {
        "brushState" => "笔刷状态",
        "currentLayerState" => "当前图层状态",
        "colorState" => "色彩状态",
        "canvasViewState" => "画布视图状态",
        "clipState" => "Clip 文档结构",
        _ => module ?? "状态模块"
    };

    private static string DescribeStatus(string? status) => status switch
    {
        "changed" => "已识别 / 状态变化",
        "unchanged" => "已识别 / 与前次相同",
        "unknown" => "未知 / 无法确认",
        "ambiguous" => "识别结果有歧义",
        "error" => "解析失败",
        _ => status ?? "未知"
    };

    private async Task ErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                Console.Error.WriteLine($"[Workspace helper] {line}");
        }
        catch (IOException ex) { Fail(ex.Message); }
    }

    private void Fail(string message)
    {
        if (_stopping) return;
        _stopping = true;
        _outgoing.Writer.TryComplete();
        _writer.AppendState("updateActivatorError", _writer.NowTicks, [], new { message });
        string gap = _writer.ReserveStatePackage(_writer.LastHardwareEventId, _writer.NowTicks, "stateIntegrationUnavailable");
        _writer.AppendPackageResult(gap, new { status = "error", updates = Array.Empty<object>(), reason = message });
        Console.Error.WriteLine($"[UpdateActivator] {message}");
    }

    public async ValueTask DisposeAsync()
    {
        _writer.HardwareAppended -= OnHardware;
        await Guard.DisposeAsync();
        _stopping = true;
        _outgoing.Writer.TryComplete();
        var shutdown = Task.WhenAll(_send, _receive, _errors, _process.WaitForExitAsync());
        try
        {
            // Draining disk-spooled analysis is deliberate; the writer remains open until EOF.
            await shutdown;
        }
        catch (Exception ex)
        {
            _writer.AppendState("updateActivatorError", _writer.NowTicks, [], new { message = ex.Message, phase = "shutdown" });
            Console.Error.WriteLine($"[UpdateActivator shutdown] {ex.Message}");
        }
        finally { _process.Dispose(); _processJob.Dispose(); }
    }
}
