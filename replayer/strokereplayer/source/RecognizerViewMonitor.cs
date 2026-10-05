using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;

namespace StrokeReplay;

/// <summary>Confirmed canvas coordinates, expressed in physical screen pixels.</summary>
public sealed record CanvasViewSnapshot(double ScalePercent, double RotationDegrees, double OriginX, double OriginY,
    Rectangle? Viewport = null, Rectangle? ZoomInput = null, Rectangle? RotationInput = null,
    int? PixelWidth = null, int? PixelHeight = null)
{
    public static bool TryParse(JsonElement corePayload, out CanvasViewSnapshot? view) =>
        TryParse(corePayload, Property(corePayload, "rawResult"), out view);

    public static bool TryParse(JsonElement corePayload, JsonElement rawResult, out CanvasViewSnapshot? view, bool requireCausalAnchor = true)
    {
        view = null;
        if (Text(corePayload, "status") is not ("changed" or "unchanged")) return false;
        if (requireCausalAnchor && Property(Property(corePayload, "evidence"), "causalAmbiguous").ValueKind == JsonValueKind.True) return false;
        if (Text(corePayload, "module") is { } module && module != "canvasViewState") return false;
        view = ParseState(Property(corePayload, "state"), rawResult);
        return view is not null;
    }

    public static CanvasViewSnapshot? ParseState(JsonElement state, JsonElement rawResult = default)
    {
        var origin = Property(state, "canvasOriginScreenPx");
        if (!Number(state, "ocrScalePercent", out double scale) || scale <= 0
            || !Number(state, "ocrRotationDegrees", out double rotation)
            || !Number(origin, "x", out double x) || !Number(origin, "y", out double y)) return null;
        if (Property(rawResult, "success").ValueKind == JsonValueKind.False) return null;
        var transform = Property(state, "transform");
        var snapshot = Property(rawResult, "snapshot");
        var layout = Property(rawResult, "ocrLayout");
        if (layout.ValueKind != JsonValueKind.Object) layout = Property(snapshot, "ocrLayout");
        return new(scale, rotation, x, y,
            ParseRectangle(Property(rawResult, "canvasWindowRoiScreenPx")),
            ParseRectangle(Property(layout, "scaleDigitsScreen")),
            ParseRectangle(Property(layout, "rotationDigitsScreen")),
            PositiveInteger(transform, "canvasPixelWidth") ?? PositiveInteger(snapshot, "canvasPixelWidth"),
            PositiveInteger(transform, "canvasPixelHeight") ?? PositiveInteger(snapshot, "canvasPixelHeight"));
    }

    /// <summary>Loads document dimensions only; click positions must come from the current OCR result.</summary>
    public CanvasViewSnapshot WithInitializationConfiguration(JsonElement configuration)
    {
        var size = Property(configuration, "canvasPixelSize");
        int? width = PixelWidth, height = PixelHeight;
        if (size.ValueKind == JsonValueKind.Array && size.GetArrayLength() == 2)
        {
            if (Integer(size[0], out int w) && w > 0) width ??= w;
            if (Integer(size[1], out int h) && h > 0) height ??= h;
        }
        return this with { PixelWidth = width, PixelHeight = height };
    }

    internal static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    internal static string? Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
    private static bool Number(JsonElement value, string name, out double number)
    {
        var field = Property(value, name);
        number = 0;
        return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out number) && double.IsFinite(number);
    }
    private static int? PositiveInteger(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } field && field.TryGetInt32(out int number) && number > 0 ? number : null;
    private static bool Integer(JsonElement value, out int number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }

    internal static Rectangle? ParseRectangle(JsonElement value)
    {
        int left, top, right, bottom;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 4)
        {
            if (!Integer(value[0], out left) || !Integer(value[1], out top)
                || !Integer(value[2], out int width) || !Integer(value[3], out int height)
                || width <= 0 || height <= 0 || (long)left + width > int.MaxValue || (long)top + height > int.MaxValue) return null;
            right = left + width; bottom = top + height;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var l = Property(value, "left"); var t = Property(value, "top");
            var r = Property(value, "right"); var b = Property(value, "bottom");
            if (l.ValueKind != JsonValueKind.Number || t.ValueKind != JsonValueKind.Number
                || r.ValueKind != JsonValueKind.Number || b.ValueKind != JsonValueKind.Number
                || !l.TryGetInt32(out left) || !t.TryGetInt32(out top) || !r.TryGetInt32(out right) || !b.TryGetInt32(out bottom)) return null;
        }
        else return null;
        return right > left && bottom > top && (long)right - left <= int.MaxValue && (long)bottom - top <= int.MaxValue
            ? Rectangle.FromLTRB(left, top, right, bottom) : null;
    }
}

public sealed record RecognizerUpdateTiming(long Sequence, bool IsSnapshot,
    double? CaptureToResultMilliseconds, double? DeliveryMilliseconds, bool Accepted, string? Reason);

/// <summary>Continuously consumes Recognizer's IPC without opening HID or running recognition.</summary>
public sealed class RecognizerViewMonitor : ICanvasViewFeed, IAsyncDisposable
{
    private sealed record Endpoint(string PipeName, string? ManifestPath, DateTime LastWriteUtc);
    private readonly string? _endpointOrPipe;
    private readonly string? _discoveryRoot;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly HashSet<string> _endedPipes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _failedUntil = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = Signal();
    private Task? _run;
    private CanvasViewSnapshot? _confirmedView;
    private RecognizerUpdateTiming? _lastUpdateTiming;
    private JsonElement? _driverConfiguration;
    private readonly Dictionary<string, JsonElement> _devices = new(StringComparer.Ordinal);
    private bool _connected;
    private bool _preferTabletMetadata = true;
    private bool _configurationReady;
    private string? _configurationFailure;
    private long? _clockOriginTicks;
    private long _minimumCaptureTicks;
    private long _revision, _connectionGeneration;
    private long _confirmedRevision = -1;
    private int _disposed;
    private string _status = "等待 Recognizer 实时接口";

    public RecognizerViewMonitor(string? endpointOrPipe = null, string? discoveryRoot = null)
    {
        _endpointOrPipe = string.IsNullOrWhiteSpace(endpointOrPipe) ? null : endpointOrPipe.Trim();
        _discoveryRoot = string.IsNullOrWhiteSpace(discoveryRoot) ? null : Path.GetFullPath(discoveryRoot);
    }

    /// <summary>The current session state table, replaced only by confirmed observations.</summary>
    public CanvasViewSnapshot? Current { get { lock (_sync) return Ready ? _confirmedView : null; } }
    public RecognizerUpdateTiming? LastUpdateTiming { get { lock (_sync) return _lastUpdateTiming; } }
    public long Revision { get { lock (_sync) return _revision; } }
    public string Status { get { lock (_sync) return _status; } }
    public JsonElement? DriverConfiguration { get { lock (_sync) return _driverConfiguration; } }
    public IReadOnlyDictionary<string, JsonElement> TabletDevices { get { lock (_sync) return new Dictionary<string, JsonElement>(_devices); } }
    public event Action<string>? StatusChanged;

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _run ??= Task.Run(() => FollowAsync(cancellationToken));
        }
    }

    public void EnsureConnected()
    {
        lock (_sync)
            if (!Ready)
                throw new InvalidOperationException("Recognizer 尚未提供当前画布的已确认视图。" + _status);
    }

    public void MarkInputStarted()
    {
        lock (_sync)
        {
            if (!Ready || _clockOriginTicks is not { } origin)
                throw new InvalidOperationException("Recognizer 连接或录制时钟不可用，无法确认输入后的视图。");
            _minimumCaptureTicks = Stopwatch.GetTimestamp() - origin;
            Pulse();
        }
    }

    /// <summary>A negative revision waits for startup. Other waits require the same uninterrupted connection.</summary>
    public async Task<CanvasViewSnapshot> WaitForViewAsync(long afterRevision, Func<CanvasViewSnapshot, bool> predicate,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(timeout);
        long generation;
        lock (_sync) generation = _connectionGeneration;
        try
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_connected && _configurationFailure is { } failure)
                        throw new InvalidOperationException("无法读取 Recognizer 当前会话的校准信息：" + failure);
                    if (afterRevision >= 0 && (!_connected || generation != _connectionGeneration))
                        throw new IOException("Recognizer 连接已中断，无法确认视图恢复。" + _status);
                    if (_confirmedRevision > afterRevision && Ready)
                    {
                        if (_confirmedView is { } view && predicate(view)) return view;
                    }
                    wait = _changed.Task;
                }
                await wait.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        { throw new TimeoutException("等待 Recognizer 确认画布视图超时。" + Status); }
    }

    private async Task FollowAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Endpoint? endpoint = null;
                try
                {
                    endpoint = DiscoverEndpoint();
                    if (endpoint is null) SetStatus(_endpointOrPipe is { } specified
                        ? "指定 Recognizer 接口尚未可用或会话已结束：" + specified
                        : "等待正在录制的 Recognizer；可指定 .live.json 或管道名");
                    else await ConsumeAsync(endpoint, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (InvalidDataException ex) when (_preferTabletMetadata && ex.Message.Contains("Unknown or empty realtime channel selection", StringComparison.Ordinal))
                {
                    _preferTabletMetadata = false;
                    Invalidate("旧版 Recognizer 不支持设备元数据过滤，将使用兼容订阅", disconnect: true);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
                {
                    if (_endpointOrPipe is null && endpoint is not null)
                        _failedUntil[endpoint.PipeName] = DateTime.UtcNow.AddSeconds(20);
                    Invalidate("Recognizer 接口断开，将重连：" + ex.Message, disconnect: true);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Invalidate("Recognizer 监听已停止", disconnect: true); }
    }

    private async Task ConsumeAsync(Endpoint endpoint, CancellationToken token)
    {
        Invalidate("正在连接 Recognizer：" + endpoint.PipeName, disconnect: true);
        using var configurationStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? configurationTask = null;
        JsonElement configuration = default;
        try
        {
            await foreach (var message in RecorderRealtimeClient.SubscribeAsync(endpoint.PipeName,
                ["core.canvasViewState", _preferTabletMetadata ? RecorderRealtimeTopics.TabletMetadata : "tablet"], includeSnapshot: true, cancellationToken: token).ConfigureAwait(false))
            {
                if (message.Channel == "system")
                {
                    if (message.Kind == "hello")
                    {
                        _failedUntil.Remove(endpoint.PipeName);
                        lock (_sync)
                        {
                            _connected = true; _confirmedView = null; _lastUpdateTiming = null; _confirmedRevision = -1;
                            _configurationReady = false; _configurationFailure = null;
                            _clockOriginTicks = null; _minimumCaptureTicks = 0; _driverConfiguration = null; _devices.Clear();
                            _connectionGeneration++; Pulse();
                        }
                        var helloOrigin = CanvasViewSnapshot.Property(message.Data, "originTicks");
                        var helloFrequency = CanvasViewSnapshot.Property(message.Data, "frequency");
                        bool hasClock = helloOrigin.ValueKind == JsonValueKind.Number && helloOrigin.TryGetInt64(out _)
                            && helloFrequency.ValueKind == JsonValueKind.Number && helloFrequency.TryGetInt64(out long hz)
                            && hz == Stopwatch.Frequency;
                        if (hasClock)
                            lock (_sync) { _clockOriginTicks = helloOrigin.GetInt64(); _configurationReady = true; Pulse(); }
                        SetStatus("已连接 Recognizer；等待确认的画布视图");
                        string? recording = CanvasViewSnapshot.Text(message.Data, "liveFilePath")
                            ?? CanvasViewSnapshot.Text(message.Data, "filePath");
                        if (recording is not null)
                            configurationTask = Task.Run(() => ReadConfigurationAsync(recording, (origin, frequency) =>
                            {
                                if (frequency != Stopwatch.Frequency)
                                { ConfigurationFailed("录制时钟频率与当前机器不一致"); return; }
                                lock (_sync) { _clockOriginTicks = origin; _configurationReady = true; Pulse(); }
                            }, value =>
                            {
                                CanvasViewSnapshot? view;
                                lock (_sync)
                                {
                                    configuration = value; _configurationReady = true;
                                    if (_confirmedView is { } recognized) _confirmedView = recognized.WithInitializationConfiguration(value);
                                    view = _confirmedView;
                                    Pulse();
                                }
                                if (view is not null) SetStatus($"Recognizer 视图：{view.ScalePercent:0.###}% / {view.RotationDegrees:0.###}°");
                            }, failure => { if (!hasClock) ConfigurationFailed(failure); }, configurationStop.Token));
                        else if (!hasClock) ConfigurationFailed("实时接口 hello 消息未提供当前录制文件或录制时钟");
                    }
                    else if (message.Kind == "sessionEnded")
                    {
                        _endedPipes.Add(endpoint.PipeName);
                        Invalidate("Recognizer 录制会话已结束；等待下一个会话", disconnect: true);
                    }
                    continue;
                }
                if (message.Channel == "tablet")
                {
                    lock (_sync)
                    {
                        if (message.Kind == "driverConfiguration") _driverConfiguration = message.Data.Clone();
                        if (message.Kind == "tabletDeviceChanged" && CanvasViewSnapshot.Text(message.Data, "deviceId") is { } deviceId)
                            _devices[deviceId] = message.Data.Clone();
                    }
                    continue;
                }
                if (message.Channel != "core.canvasViewState" || message.Kind != "stateUpdated") continue;
                bool valid = CanvasViewSnapshot.TryParse(message.Data, CanvasViewSnapshot.Property(message.Data, "rawResult"),
                    out var confirmed, requireCausalAnchor: false);
                lock (_sync)
                {
                    var evidence = CanvasViewSnapshot.Property(message.Data, "evidence");
                    var captured = CanvasViewSnapshot.Property(evidence, "capturedTicks");
                    var completed = CanvasViewSnapshot.Property(evidence, "completedTicks");
                    bool oldCapture = _minimumCaptureTicks > 0 && (captured.ValueKind != JsonValueKind.Number
                        || !captured.TryGetInt64(out long captureTicks) || captureTicks < _minimumCaptureTicks);
                    double? captureToResult = captured.ValueKind == JsonValueKind.Number && captured.TryGetInt64(out long start)
                        && completed.ValueKind == JsonValueKind.Number && completed.TryGetInt64(out long finish)
                        ? Math.Max(0, finish - start) * 1000d / Stopwatch.Frequency : null;
                    double? delivery = _clockOriginTicks is { } clockOrigin
                        ? Math.Max(0, Stopwatch.GetTimestamp() - clockOrigin - message.PublishedTicks) * 1000d / Stopwatch.Frequency : null;
                    _lastUpdateTiming = new(message.Sequence, message.IsSnapshot, captureToResult, delivery,
                        valid && !oldCapture, oldCapture ? "输入前的旧截图" : valid ? null : CanvasViewSnapshot.Text(message.Data, "status"));
                    if (oldCapture) continue;
                    _revision++;
                    if (valid)
                    {
                        _confirmedView = confirmed!.WithInitializationConfiguration(configuration);
                        _confirmedRevision = _revision;
                    }
                    // Keep the session's last confirmed state through unknown intermediate
                    // observations. They neither overwrite the table nor confirm an input.
                    Pulse();
                }
                SetStatus(valid ? (Current is not null
                    ? $"Recognizer 视图：{confirmed!.ScalePercent:0.###}% / {confirmed.RotationDegrees:0.###}°"
                    : "Recognizer 视图已识别；等待当前会话的校准和时钟")
                    : "Recognizer 中间态未确认，保留状态表：" + (CanvasViewSnapshot.Text(message.Data, "status") ?? "unknown"));
            }
        }
        finally
        {
            await configurationStop.CancelAsync().ConfigureAwait(false);
            if (configurationTask is not null) await configurationTask.ConfigureAwait(false);
        }
    }

    private static async Task ReadConfigurationAsync(string recording, Action<long, long> clockReady,
        Action<JsonElement> ready, Action<string> failed, CancellationToken token)
    {
        try
        {
            using var reader = MemolineReader.Open(recording);
            bool clockRead = false;
            while (!token.IsCancellationRequested)
            {
                foreach (var frame in reader.ReadAvailable())
                {
                    if (CanvasViewSnapshot.Text(frame, "kind") == "header")
                    {
                        var originValue = CanvasViewSnapshot.Property(frame, "originTicks");
                        var frequencyValue = CanvasViewSnapshot.Property(frame, "frequency");
                        if (originValue.ValueKind == JsonValueKind.Number && originValue.TryGetInt64(out long origin)
                            && frequencyValue.ValueKind == JsonValueKind.Number && frequencyValue.TryGetInt64(out long frequency))
                        { clockReady(origin, frequency); clockRead = true; }
                        else { failed("录制文件头缺少 Stopwatch 时钟原点或频率"); return; }
                    }
                    if (CanvasViewSnapshot.Text(frame, "kind") == "initializationConfiguration")
                    {
                        if (!clockRead) failed("初始化校准前未读取到录制时钟");
                        else ready(CanvasViewSnapshot.Property(frame, "data").Clone());
                        return;
                    }
                }
                if (reader.IsComplete)
                {
                    if (!clockRead) failed("录制文件缺少有效的时钟记录");
                    return;
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { failed(ex.Message); }
    }

    private Endpoint? DiscoverEndpoint()
    {
        if (_endpointOrPipe is { } explicitEndpoint)
        {
            if (!explicitEndpoint.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !explicitEndpoint.Contains(Path.DirectorySeparatorChar) && !explicitEndpoint.Contains(Path.AltDirectorySeparatorChar))
                return _endedPipes.Contains(explicitEndpoint) ? null : new(explicitEndpoint, null, DateTime.MinValue);
            return ReadManifest(explicitEndpoint, requireRunning: false);
        }
        return DiscoveryDirectories().Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists).SelectMany(SafeManifests)
            .Select(path => ReadManifest(path, requireRunning: true)).Where(value => value is not null)
            .OrderByDescending(value => value!.LastWriteUtc).FirstOrDefault();
    }

    private Endpoint? ReadManifest(string path, bool requireRunning)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var version = CanvasViewSnapshot.Property(root, "schemaVersion");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1) return null;
            string? pipe = CanvasViewSnapshot.Text(root, "pipeName");
            if (string.IsNullOrWhiteSpace(pipe) || _endedPipes.Contains(pipe)) return null;
            if (requireRunning && _failedUntil.TryGetValue(pipe, out var retryAfter) && retryAfter > DateTime.UtcNow) return null;
            if (requireRunning)
            {
                var pidValue = CanvasViewSnapshot.Property(root, "processId");
                if (pidValue.ValueKind != JsonValueKind.Number || !pidValue.TryGetInt32(out int pid)) return null;
                using var process = Process.GetProcessById(pid);
                if (process.HasExited || process.StartTime.ToUniversalTime() > File.GetLastWriteTimeUtc(path).AddSeconds(1)) return null;
            }
            return new(pipe, Path.GetFullPath(path), File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private IEnumerable<string> DiscoveryDirectories()
    {
        if (_discoveryRoot is { } root)
        {
            yield return root; yield return Path.Combine(root, "stroke");
            yield return Path.Combine(root, "procedure", "stroke");
            yield return Path.Combine(root, "publish", "win-x64", "procedure", "stroke");
            yield return Path.Combine(root, "Recognizer", "publish", "win-x64", "procedure", "stroke");
        }
        foreach (var starting in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (DirectoryInfo? parent = new(starting); parent is not null; parent = parent.Parent)
            {
                yield return Path.Combine(parent.FullName, "procedure", "stroke");
                yield return Path.Combine(parent.FullName, "recognizer", "Recognizer", "publish", "win-x64", "procedure", "stroke");
                yield return Path.Combine(parent.FullName, "Recognizer", "publish", "win-x64", "procedure", "stroke");
            }
        foreach (var process in Process.GetProcessesByName("BehaviorRecognizer"))
        {
            string? executable = null;
            using (process)
                try { executable = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (executable is not null)
                yield return Path.Combine(Path.GetDirectoryName(executable)!, "procedure", "stroke");
        }
    }

    private static IEnumerable<string> SafeManifests(string directory)
    {
        try { return Directory.GetFiles(directory, "*.memoline.live.json", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private void Invalidate(string status, bool disconnect)
    {
        lock (_sync)
        {
            _confirmedView = null; _lastUpdateTiming = null; _confirmedRevision = -1; _revision++;
            if (disconnect)
            {
                _connected = false; _clockOriginTicks = null; _configurationReady = false; _configurationFailure = null;
                _connectionGeneration++; _driverConfiguration = null; _devices.Clear();
            }
            Pulse();
        }
        SetStatus(status);
    }

    private void SetStatus(string status)
    {
        lock (_sync) { if (_status == status) return; _status = status; }
        StatusChanged?.Invoke(status);
    }

    private bool Ready => _connected && _configurationReady && _clockOriginTicks is not null && _configurationFailure is null;
    private void ConfigurationFailed(string failure)
    {
        lock (_sync) { _configurationFailure = failure; _confirmedView = null; _confirmedRevision = -1; Pulse(); }
        SetStatus("Recognizer 当前会话校准不可用：" + failure);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var old = _changed; _changed = Signal(); old.TrySetResult(); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? running;
        lock (_sync) running = _run;
        if (running is not null) await running.ConfigureAwait(false);
        _stop.Dispose();
    }
}
