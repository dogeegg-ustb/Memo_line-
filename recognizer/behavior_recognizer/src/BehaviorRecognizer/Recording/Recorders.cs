using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Abstractions.Recording;
using BehaviorRecognizer.Abstractions.Stroke;
using BehaviorRecognizer.Storage.Strokebin;
using StrokeModel = BehaviorRecognizer.Abstractions.Stroke.Stroke;

namespace BehaviorRecognizer.Recording;

/// <summary>记录器总线：向已启用记录器分发事件，失败隔离�?/summary>
public sealed class RecorderBus : IRecorderBus
{
    private readonly List<IRecorder> _recorders = []; // 已注册记录器
    private readonly object _sync = new(); // 列表�?

    /// <summary>当前记录器快照�?/summary>
    public IReadOnlyList<IRecorder> Recorders
    {
        get
        {
            lock (_sync)
                return _recorders.ToList(); // 返回拷贝
        }
    }

    /// <summary>注册记录器�?/summary>
    public void Register(IRecorder recorder)
    {
        lock (_sync)
            _recorders.Add(recorder); // 追加
    }

    /// <summary>启用/禁用指定记录器�?/summary>
    public bool SetEnabled(string recorderId, bool enabled)
    {
        lock (_sync)
        {
            var recorder = _recorders.FirstOrDefault(r => r.Id == recorderId); // 查找
            if (recorder is null)
                return false;
            recorder.IsEnabled = enabled; // 设置开�?
            return true;
        }
    }

    /// <summary>分发事件；单个记录器异常不得影响其他记录器�?/summary>
    public async ValueTask DispatchAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
    {
        IRecorder[] snapshot; // 快照
        lock (_sync)
            snapshot = _recorders.Where(r => r.IsEnabled).ToArray(); // 仅启用的

        foreach (var recorder in snapshot)
        {
            try
            {
                await recorder.OnEventAsync(inputEvent, cancellationToken); // 分发
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecorderBus] {recorder.Id} failed: {ex.Message}"); // 隔离错误
            }
        }
    }

    /// <summary>释放全部记录器�?/summary>
    public async ValueTask DisposeAsync()
    {
        IRecorder[] snapshot;
        lock (_sync)
            snapshot = _recorders.ToArray();

        foreach (var recorder in snapshot)
            await recorder.DisposeAsync(); // 依次释放
    }
}

/// <summary>
/// 笔输入记录器：按 OTD 笔迹强约束实现状态机、分段、有界队列与 .strokebin 两阶段写入�?
/// </summary>
public sealed class PenInputRecorder : IRecorder, IMetadataRecorder
{
    private readonly string _outputRoot; // 输出根目录（其下�?stroke/�?
    private readonly int _queueCapacity; // 队列容量
    private readonly uint _penUpTimeoutMs; // 抬笔超时
    private readonly uint _maxStrokesPerSegment; // 分段笔划上限
    private readonly object _sync = new(); // 状态机互斥
    private readonly Dictionary<string, object?> _metadata = new(); // 元数�?
    private CancellationTokenSource _tickCts = new(); // tick 取消（会话级�?
    private Task? _tickLoop; // 50ms tick 任务

    private BinaryStrokeWriter? _writer; // 二进制写入器
    private AsyncRecordQueue? _queue; // 异步写队�?
    private RecordingSession _session = new(); // 当前会话
    private StrokeSegment _currentSegment = new(); // 当前分段
    private StrokeModel? _currentStroke; // 当前笔划
    private RecorderState _state = RecorderState.Stopped; // 状态机
    private ulong _nextStrokeId = 1; // 下一笔划 ID
    private ulong _nextSegmentId = 1; // 下一分段 ID
    private ulong _lastPointTimestampMs; // 本笔划内上一采样时间（抬笔后清零）
    private ulong _penUpTimestampMs; // 抬笔时间
    private bool _penDown; // 接触标志
    private SamplePoint? _lastAcceptedPoint; // 本笔划内上一已接受点（用于载荷去重）
    private string _deviceName = "unknown"; // 设备名
    private string _deviceId = "unknown"; // 设备 ID

    /// <summary>构造笔输入记录器�?/summary>
    public PenInputRecorder(
        string outputRoot,
        int queueCapacity = StrokeFormat.DefaultQueueCapacity,
        uint penUpTimeoutMs = StrokeFormat.PenUpTimeoutMs,
        uint maxStrokesPerSegment = StrokeFormat.MaxStrokesPerSegment)
    {
        _outputRoot = outputRoot; // 保存根目�?
        _queueCapacity = queueCapacity; // 容量
        _penUpTimeoutMs = penUpTimeoutMs; // 超时
        _maxStrokesPerSegment = maxStrokesPerSegment; // 阈�?
    }

    /// <summary>记录�?ID�?/summary>
    public string Id => "pen-input";

    /// <summary>显示名�?/summary>
    public string DisplayName => "笔输入记录器";

    /// <summary>是否启用�?/summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>当前输出文件路径�?/summary>
    public string CurrentFilePath
    {
        get { lock (_sync) return _session.FilePath; }
    }

    /// <summary>当前会话 ID�?/summary>
    public string CurrentSessionId
    {
        get { lock (_sync) return _session.SessionId; }
    }

    /// <summary>当前状态�?/summary>
    public RecorderState State
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>启动录制会话（创�?.part 并启�?tick）�?/summary>
    public void StartSession(string? deviceName = null, string? deviceId = null)
    {
        lock (_sync)
        {
            if (_state is not RecorderState.Stopped and not RecorderState.Idle)
                return; // 已在运行则忽�?

            if (!string.IsNullOrWhiteSpace(deviceName))
                _deviceName = deviceName;
            if (!string.IsNullOrWhiteSpace(deviceId))
                _deviceId = deviceId;

            var createdAt = StrokePathUtil.NowUnixMs(); // UTC 毫秒
            _session = new RecordingSession
            {
                SessionId = $"session-{createdAt}", // 强制 session-<ms>
                Header = new StrokeSessionHeader
                {
                    Version = StrokeFormat.Version,
                    CreatedAtUnixMs = createdAt,
                    PluginVersion = StrokeFormat.PluginVersion,
                    Device = new StrokeDeviceInfo { Name = _deviceName, Id = _deviceId },
                    Encoding = 0,
                },
            };

            var strokeDir = StrokePathUtil.MakeStrokeDir(_outputRoot); // <root>/stroke
            var path = StrokePathUtil.NextStrokeBinPath(strokeDir, createdAt); // 文件�?
            _session.FilePath = path;

            _writer = new BinaryStrokeWriter(); // 新建写入�?
            if (!_writer.OpenSession(path, _session)) // 打开 .part
            {
                _writer.Dispose();
                _writer = null;
                _state = RecorderState.Stopped;
                return;
            }

            var writer = _writer; // 捕获
            _queue = new AsyncRecordQueue(_queueCapacity, segment => writer.WriteSegment(segment)); // 有界队列

            _currentSegment = new StrokeSegment(); // 清空当前分段
            _currentStroke = null;
            _nextStrokeId = 1;
            _nextSegmentId = 1;
            _lastPointTimestampMs = 0;
            _lastAcceptedPoint = null;
            _penUpTimestampMs = 0;
            _penDown = false;
            _state = RecorderState.Idle; // 进入 Idle

            // 每个会话使用新的 tick 取消源（避免复用已取消的 CTS�?
            try { _tickCts.Dispose(); } catch { /* 忽略 */ }
            _tickCts = new CancellationTokenSource();
        }

        _tickLoop = Task.Run(() => TickLoopAsync(_tickCts.Token)); // 启动 50ms tick
    }

    /// <summary>停止会话：结束活动笔划、提交剩余分段、排空队列、写 SessionEnd�?/summary>
    public void StopSession()
    {
        _tickCts.Cancel(); // �?tick
        try { _tickLoop?.GetAwaiter().GetResult(); } catch { /* 忽略 */ }

        lock (_sync)
        {
            if (_state == RecorderState.Stopped)
                return; // 幂等

            if (_penDown && _currentStroke is not null) // 仍在落笔：先结束笔划
            {
                var ts = _lastPointTimestampMs == 0 ? StrokePathUtil.NowUnixMs() : _lastPointTimestampMs;
                FinalizeCurrentStroke(ts);
                _penDown = false;
                _lastPointTimestampMs = 0;
                _lastAcceptedPoint = null;
            }

            if (_currentSegment.Strokes.Count > 0) // 非空分段以 SessionStop 提交
                FlushLocked(FlushReason.SessionStop, StrokePathUtil.NowUnixMs());

            _queue?.StopAndDrain(); // 排空已接受分段
            _queue = null;

            _writer?.CloseSession(FlushReason.SessionStop, completed: true); // SessionEnd + 改名
            _writer?.Dispose();
            _writer = null;

            _state = RecorderState.Stopped;
            _penDown = false;
            _lastPointTimestampMs = 0;
            _lastAcceptedPoint = null;
        }
    }

    /// <summary>记录元数据（设备名等）�?/summary>
    public ValueTask RecordMetadataAsync(string key, object? value, CancellationToken cancellationToken = default)
    {
        lock (_metadata)
            _metadata[key] = value; // 保存

        if (key is "deviceName" && value is string name)
            _deviceName = name;
        if (key is "deviceId" && value is string id)
            _deviceId = id;

        return ValueTask.CompletedTask;
    }

    /// <summary>消费总线事件：仅在启用且会话运行时记录接触点�?/summary>
    public ValueTask OnEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return ValueTask.CompletedTask;

        lock (_sync)
        {
            if (_state == RecorderState.Stopped)
                return ValueTask.CompletedTask;

            // tipDown := Pressure > 0
            var tipDown = (inputEvent.Pressure ?? 0f) > 0f;

            // OutOfRange 且此前接触：等价 pen_up
            if (inputEvent.Type == InputEventType.PenUp ||
                inputEvent.ContactState == ContactState.OutOfRange ||
                inputEvent.Type == InputEventType.PenHover)
            {
                if (_penDown)
                    HandlePenUp();
                return ValueTask.CompletedTask; // 悬空点不持久�?
            }

            if (tipDown && !_penDown) // 落笔边沿：先 pen_down 再提交点
            {
                HandlePenDown();
                RecordPointFromEvent(inputEvent, inContact: true);
                return ValueTask.CompletedTask;
            }

            if (!tipDown && _penDown) // 抬笔边沿：pen_up，不提交非接触点
            {
                HandlePenUp();
                return ValueTask.CompletedTask;
            }

            if (tipDown && _penDown) // 接触中：提交�?
            {
                RecordPointFromEvent(inputEvent, inContact: true);
            }
            // tipDown==false 且未接触：禁止持久化悬空�?
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>释放：停止会话�?/summary>
    public ValueTask DisposeAsync()
    {
        try { StopSession(); } catch { /* 忽略 */ }
        _tickCts.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>50ms 周期评估抬笔超时�?/summary>
    private async Task TickLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(StrokeFormat.TickIntervalMs));
            while (await timer.WaitForNextTickAsync(token))
            {
                lock (_sync)
                {
                    if (_state != RecorderState.AwaitFlush)
                        continue;
                    var nowMs = StrokePathUtil.NowUnixMs(); // tick(0) 语义：用当前 UTC
                    var reason = DecideFlushReason(nowMs);
                    if (reason != FlushReason.None)
                        FlushLocked(reason, nowMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    /// <summary>处理落笔（幂等）�?/summary>
    private void HandlePenDown()
    {
        if (_penDown)
            return; // 重复 pen_down 幂等

        var now = StrokePathUtil.NowUnixMs();
        _penDown = true;
        _penUpTimestampMs = 0;
        BeginSegmentIfNeeded(now);
        EnsureCurrentStroke(now);
        _state = RecorderState.InStroke;
    }

    /// <summary>处理抬笔（幂等）。</summary>
    private void HandlePenUp()
    {
        if (!_penDown)
            return; // 重复 pen_up 幂等

        var now = _lastPointTimestampMs == 0 ? StrokePathUtil.NowUnixMs() : _lastPointTimestampMs;
        FinalizeCurrentStroke(now);
        _penDown = false;
        _penUpTimestampMs = now;
        _lastPointTimestampMs = 0; // 下一笔首点 deltaTime=0
        _lastAcceptedPoint = null; // 跨笔划不去重
        _state = RecorderState.AwaitFlush;

        if ((ulong)_currentSegment.Strokes.Count >= _maxStrokesPerSegment) // 达阈值立即提交
            FlushLocked(FlushReason.StrokeCountThreshold, now);
    }

    /// <summary>从 InputEvent 构造 SamplePoint 并追加（缺失字段用零值；相同载荷去重）。</summary>
    private void RecordPointFromEvent(InputEvent evt, bool inContact)
    {
        var point = new SamplePoint
        {
            TimestampMs = (ulong)Math.Max(0, evt.Timestamp.ToUnixTimeMilliseconds()), // UTC 毫秒
            X = evt.Position?.X ?? 0.0,
            Y = evt.Position?.Y ?? 0.0,
            Pressure = evt.Pressure ?? 0.0,
            InContact = inContact,
            Buttons = EncodeButtons(evt.PenButtons), // 位掩码
            TiltX = evt.Tilt?.X ?? 0.0,
            TiltY = evt.Tilt?.Y ?? 0.0,
            SequenceId = evt.Sequence,
        };

        if (point.TimestampMs == 0)
            point.TimestampMs = StrokePathUtil.NowUnixMs();

        // 同一数位板输入载荷与上一已接受点完全相同则跳过（忽略时间戳/序号）
        if (_lastAcceptedPoint is not null && IsDuplicatePayload(point, _lastAcceptedPoint))
            return;

        if (!_penDown && point.InContact) // 防御：点内接触自启笔划
        {
            _penDown = true;
            _penUpTimestampMs = 0;
            BeginSegmentIfNeeded(point.TimestampMs);
            EnsureCurrentStroke(point.TimestampMs);
            _state = RecorderState.InStroke;
        }

        if (_currentStroke is null)
            return; // 无活动笔划则忽略

        // deltaTime 仅作用于本笔划内相邻点；每笔首点恒为 0
        if (_currentStroke.Points.Count == 0 || _lastPointTimestampMs == 0)
            point.DeltaTimeMs = 0;
        else if (point.TimestampMs >= _lastPointTimestampMs)
            point.DeltaTimeMs = point.TimestampMs - _lastPointTimestampMs;
        else
            point.DeltaTimeMs = 0; // 时间倒退兜底

        _lastPointTimestampMs = point.TimestampMs;
        _lastAcceptedPoint = point;

        _currentStroke.Points.Add(point); // 按序追加，禁止重采样
        if (_currentStroke.StartTimestampMs == 0)
            _currentStroke.StartTimestampMs = point.TimestampMs;
        _currentStroke.EndTimestampMs = point.TimestampMs;
    }

    /// <summary>比较数位板采样载荷是否相同（不含 timestamp / sequence / deltaTime）。</summary>
    private static bool IsDuplicatePayload(SamplePoint candidate, SamplePoint previous) =>
        candidate.X == previous.X
        && candidate.Y == previous.Y
        && candidate.Pressure == previous.Pressure
        && candidate.InContact == previous.InContact
        && candidate.Buttons == previous.Buttons
        && candidate.TiltX == previous.TiltX
        && candidate.TiltY == previous.TiltY;

    /// <summary>PenButtons[i]==true �?buttons |= 1&lt;&lt;i（i&lt;32）�?/summary>
    private static uint EncodeButtons(bool[]? buttons)
    {
        if (buttons is null)
            return 0;
        uint mask = 0;
        var n = Math.Min(buttons.Length, 32);
        for (var i = 0; i < n; i++)
        {
            if (buttons[i])
                mask |= 1u << i;
        }
        return mask;
    }

    /// <summary>确保存在当前笔划�?/summary>
    private void EnsureCurrentStroke(ulong timestampMs)
    {
        if (_currentStroke is not null)
            return;
        _currentStroke = new StrokeModel
        {
            StrokeId = _nextStrokeId++,
            StartTimestampMs = timestampMs,
            EndTimestampMs = timestampMs,
        };
    }

    /// <summary>按需开始新分段；AwaitFlush 内再落笔复用同一分段�?/summary>
    private void BeginSegmentIfNeeded(ulong timestampMs)
    {
        if (_currentSegment.SegmentId != 0 || _currentSegment.Strokes.Count > 0)
        {
            if (_currentSegment.StartTimestampMs == 0)
                _currentSegment.StartTimestampMs = timestampMs;
            return; // 复用当前分段
        }

        _currentSegment.SegmentId = _nextSegmentId++;
        _currentSegment.StartTimestampMs = timestampMs;
        _currentSegment.Reason = FlushReason.None;
        _currentSegment.PointCount = 0;
        _currentSegment.WriteStatus = WriteStatus.Ok;
        _currentSegment.Strokes = [];
    }

    /// <summary>结束当前笔划；空笔划不持久化�?/summary>
    private void FinalizeCurrentStroke(ulong timestampMs)
    {
        if (_currentStroke is null)
            return;

        if (_currentStroke.Points.Count == 0) // 空笔划禁止产�?
        {
            _currentStroke = null;
            return;
        }

        _currentStroke.EndTimestampMs = timestampMs;
        _currentSegment.PointCount += (ulong)_currentStroke.Points.Count;
        if (_currentSegment.SegmentId == 0)
        {
            _currentSegment.SegmentId = _nextSegmentId++;
            _currentSegment.StartTimestampMs = _currentStroke.StartTimestampMs;
        }
        _currentSegment.Strokes.Add(_currentStroke); // 追加到分�?
        _currentStroke = null;
    }

    /// <summary>评估是否应提交�?/summary>
    private FlushReason DecideFlushReason(ulong nowMs)
    {
        if ((ulong)_currentSegment.Strokes.Count >= _maxStrokesPerSegment)
            return FlushReason.StrokeCountThreshold;
        if (_penUpTimestampMs != 0 && nowMs >= _penUpTimestampMs + _penUpTimeoutMs)
            return FlushReason.PenUpTimeout;
        return FlushReason.None;
    }

    /// <summary>冻结当前分段并入队；空分段不写入�?/summary>
    private void FlushLocked(FlushReason reason, ulong nowMs)
    {
        if (_currentSegment.Strokes.Count == 0)
        {
            _state = RecorderState.Idle;
            return;
        }

        _state = RecorderState.Flushing;
        _currentSegment.Reason = reason;
        _currentSegment.EndTimestampMs = nowMs;

        // 冻结拷贝
        var frozen = new StrokeSegment
        {
            SegmentId = _currentSegment.SegmentId,
            Reason = _currentSegment.Reason,
            StartTimestampMs = _currentSegment.StartTimestampMs,
            EndTimestampMs = _currentSegment.EndTimestampMs,
            Strokes = _currentSegment.Strokes,
            PointCount = _currentSegment.PointCount,
            WriteStatus = _currentSegment.WriteStatus,
        };
        _currentSegment = new StrokeSegment(); // 清空当前分段

        var status = WriteStatus.Ok;
        if (_queue is not null)
            status = _queue.Enqueue(frozen); // 入队后台�?
        else if (_writer is not null)
            status = _writer.WriteSegment(frozen) ? WriteStatus.Ok : WriteStatus.IoError;

        frozen.WriteStatus = status;
        _session.Segments.Add(frozen); // 诊断保留

        _penUpTimestampMs = 0;
        _state = RecorderState.Idle;
    }
}

/// <summary>键盘上下文记录器占位�?/summary>
public sealed class KeyboardContextRecorder : IContextRecorder
{
    public string Id => "keyboard-context";
    public string DisplayName => "键盘上下文记录器";
    public string ContextDomain => "keyboard";
    public bool IsEnabled { get; set; }

    public ValueTask OnEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>笔刷属性记录器占位�?/summary>
public sealed class BrushContextRecorder : IContextRecorder
{
    public string Id => "brush-context";
    public string DisplayName => "笔刷属性记录器";
    public string ContextDomain => "brush";
    public bool IsEnabled { get; set; }

    public ValueTask OnEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>图层状态记录器占位�?/summary>
public sealed class LayerContextRecorder : IContextRecorder
{
    public string Id => "layer-context";
    public string DisplayName => "图层状态记录器";
    public string ContextDomain => "layer";
    public bool IsEnabled { get; set; }

    public ValueTask OnEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
