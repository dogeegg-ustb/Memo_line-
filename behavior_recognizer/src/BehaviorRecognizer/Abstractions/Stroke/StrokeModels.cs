namespace BehaviorRecognizer.Abstractions.Stroke;

/// <summary>STRO 格式版本（固定为 1）。</summary>
public static class StrokeFormat
{
    /// <summary>二进制格式版本号。</summary>
    public const uint Version = 1;

    /// <summary>文件魔数 ASCII "STRO"。</summary>
    public static readonly byte[] Magic = [(byte)'S', (byte)'T', (byte)'R', (byte)'O'];

    /// <summary>抬笔后等待提交的超时（毫秒）。</summary>
    public const uint PenUpTimeoutMs = 500;

    /// <summary>单个分段允许的最大笔划数。</summary>
    public const uint MaxStrokesPerSegment = 100;

    /// <summary>托管层 tick 周期（毫秒）。</summary>
    public const int TickIntervalMs = 50;

    /// <summary>异步写入队列默认容量。</summary>
    public const int DefaultQueueCapacity = 32;

    /// <summary>队列容量显式为 0 时的回退容量。</summary>
    public const int FallbackQueueCapacity = 8;

    /// <summary>写入器/插件版本字符串。</summary>
    public const string PluginVersion = "0.1.0";
}

/// <summary>分段提交原因。</summary>
public enum FlushReason : byte
{
    /// <summary>尚未提交。</summary>
    None = 0,
    /// <summary>抬笔超时触发。</summary>
    PenUpTimeout = 1,
    /// <summary>笔划数达到阈值。</summary>
    StrokeCountThreshold = 2,
    /// <summary>会话停止触发。</summary>
    SessionStop = 3,
    /// <summary>显式手动提交。</summary>
    Manual = 4,
}

/// <summary>记录器状态机状态。</summary>
public enum RecorderState : byte
{
    /// <summary>会话存在，无活动笔划。</summary>
    Idle = 0,
    /// <summary>笔尖接触中。</summary>
    InStroke = 1,
    /// <summary>已抬笔，等待超时或阈值。</summary>
    AwaitFlush = 2,
    /// <summary>正在冻结并入队分段。</summary>
    Flushing = 3,
    /// <summary>会话未运行或已结束。</summary>
    Stopped = 4,
}

/// <summary>事件帧类型。</summary>
public enum StrokeEventType : byte
{
    /// <summary>会话开始。</summary>
    SessionStart = 1,
    /// <summary>笔划开始。</summary>
    StrokeStart = 2,
    /// <summary>笔划采样点。</summary>
    StrokePoint = 3,
    /// <summary>笔划结束。</summary>
    StrokeEnd = 4,
    /// <summary>分段提交。</summary>
    SessionFlush = 5,
    /// <summary>会话结束。</summary>
    SessionEnd = 6,
}

/// <summary>分段写入状态。</summary>
public enum WriteStatus : byte
{
    /// <summary>正常写入。</summary>
    Ok = 0,
    /// <summary>队列满时丢弃了最旧分段。</summary>
    DroppedOldest = 1,
    /// <summary>队列停止后丢弃新分段。</summary>
    DroppedNewest = 2,
    /// <summary>队列已满（保留枚举兼容）。</summary>
    QueueFull = 3,
    /// <summary>磁盘 I/O 失败。</summary>
    IoError = 4,
}

/// <summary>单个采样点（持久化字段顺序与二进制编码一致）。</summary>
public sealed class SamplePoint
{
    /// <summary>UTC Unix 毫秒时间戳。</summary>
    public ulong TimestampMs { get; set; }

    /// <summary>与本笔划内上一已接收点的非负时间差（毫秒）；每笔首点为 0。</summary>
    public ulong DeltaTimeMs { get; set; }

    /// <summary>PreTransform 原始 X。</summary>
    public double X { get; set; }

    /// <summary>PreTransform 原始 Y。</summary>
    public double Y { get; set; }

    /// <summary>原始压力值。</summary>
    public double Pressure { get; set; }

    /// <summary>是否处于接触状态。</summary>
    public bool InContact { get; set; }

    /// <summary>笔按钮位掩码（最多 32 位）。</summary>
    public uint Buttons { get; set; }

    /// <summary>原始 X 倾斜；无数据为 0。</summary>
    public double TiltX { get; set; }

    /// <summary>原始 Y 倾斜；无数据为 0。</summary>
    public double TiltY { get; set; }

    /// <summary>会话内采集序号。</summary>
    public ulong SequenceId { get; set; }
}

/// <summary>一次落笔到抬笔的笔划。</summary>
public sealed class Stroke
{
    /// <summary>会话内从 1 递增的笔划 ID。</summary>
    public ulong StrokeId { get; set; }

    /// <summary>笔划开始时间（UTC Unix 毫秒）。</summary>
    public ulong StartTimestampMs { get; set; }

    /// <summary>笔划结束时间（UTC Unix 毫秒）。</summary>
    public ulong EndTimestampMs { get; set; }

    /// <summary>按接收顺序追加的采样点。</summary>
    public List<SamplePoint> Points { get; } = [];
}

/// <summary>不可变异步写入批次（分段）。</summary>
public sealed class StrokeSegment
{
    /// <summary>会话内从 1 递增的分段 ID。</summary>
    public ulong SegmentId { get; set; }

    /// <summary>提交原因。</summary>
    public FlushReason Reason { get; set; }

    /// <summary>分段开始时间。</summary>
    public ulong StartTimestampMs { get; set; }

    /// <summary>分段结束时间。</summary>
    public ulong EndTimestampMs { get; set; }

    /// <summary>本分段包含的笔划。</summary>
    public List<Stroke> Strokes { get; set; } = [];

    /// <summary>点总数（等于所有笔划 points 之和）。</summary>
    public ulong PointCount { get; set; }

    /// <summary>写入状态。</summary>
    public WriteStatus WriteStatus { get; set; }
}

/// <summary>设备信息。</summary>
public sealed class StrokeDeviceInfo
{
    /// <summary>设备显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备标识。</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>会话文件头（二进制文件头字段）。</summary>
public sealed class StrokeSessionHeader
{
    /// <summary>格式版本。</summary>
    public uint Version { get; set; } = StrokeFormat.Version;

    /// <summary>创建时间（UTC Unix 毫秒）。</summary>
    public ulong CreatedAtUnixMs { get; set; }

    /// <summary>写入器版本。</summary>
    public string PluginVersion { get; set; } = StrokeFormat.PluginVersion;

    /// <summary>设备信息。</summary>
    public StrokeDeviceInfo Device { get; set; } = new();

    /// <summary>编码标记：0 = little-endian。</summary>
    public byte Encoding { get; set; }
}

/// <summary>一次 start→stop 对应的录制会话。</summary>
public sealed class RecordingSession
{
    /// <summary>会话 ID，格式 session-&lt;createdAtUnixMs&gt;。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>会话头。</summary>
    public StrokeSessionHeader Header { get; set; } = new();

    /// <summary>最终输出文件路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>已提交分段列表（诊断用）。</summary>
    public List<StrokeSegment> Segments { get; } = [];
}

/// <summary>异步队列统计。</summary>
public sealed class AsyncQueueStats
{
    /// <summary>入队次数。</summary>
    public ulong Enqueued { get; set; }

    /// <summary>成功写出次数。</summary>
    public ulong Written { get; set; }

    /// <summary>因满队列丢弃最旧次数。</summary>
    public ulong DroppedOldest { get; set; }

    /// <summary>停止后丢弃最新次数。</summary>
    public ulong DroppedNewest { get; set; }

    /// <summary>写盘错误次数。</summary>
    public ulong WriteErrors { get; set; }
}

/// <summary>枚举转字符串（导出 JSON 用）。</summary>
public static class StrokeEnumNames
{
    /// <summary>FlushReason 名称。</summary>
    public static string ToName(FlushReason reason) => reason switch
    {
        FlushReason.PenUpTimeout => "PenUpTimeout",
        FlushReason.StrokeCountThreshold => "StrokeCountThreshold",
        FlushReason.SessionStop => "SessionStop",
        FlushReason.Manual => "Manual",
        _ => "None",
    };

    /// <summary>WriteStatus 名称。</summary>
    public static string ToName(WriteStatus status) => status switch
    {
        WriteStatus.DroppedOldest => "DroppedOldest",
        WriteStatus.DroppedNewest => "DroppedNewest",
        WriteStatus.QueueFull => "QueueFull",
        WriteStatus.IoError => "IoError",
        _ => "Ok",
    };
}
