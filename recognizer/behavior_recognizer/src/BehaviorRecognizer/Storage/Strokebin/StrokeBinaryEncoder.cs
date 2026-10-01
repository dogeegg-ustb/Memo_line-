using System.Buffers.Binary;
using System.Text;
using BehaviorRecognizer.Abstractions.Stroke;

namespace BehaviorRecognizer.Storage.Strokebin;

/// <summary>STRO v1 小端二进制编码器�?/summary>
public static class StrokeBinaryEncoder
{
    /// <summary>编码文件头�?/summary>
    public static byte[] EncodeHeader(StrokeSessionHeader header)
    {
        using var ms = new MemoryStream(64); // 预估缓冲
        WriteMagic(ms); // 写入魔数 STRO
        WriteU32(ms, header.Version); // 版本
        WriteU64(ms, header.CreatedAtUnixMs); // 创建时间
        WriteString(ms, header.PluginVersion); // 插件版本
        WriteString(ms, header.Device.Name); // 设备�?
        WriteString(ms, header.Device.Id); // 设备 ID
        WriteU8(ms, header.Encoding); // 编码标记
        return ms.ToArray(); // 返回字节
    }

    /// <summary>编码通用事件帧：type + payloadLength + payload�?/summary>
    public static byte[] EncodeEvent(StrokeEventType type, ReadOnlySpan<byte> payload)
    {
        var result = new byte[1 + 4 + payload.Length]; // 帧总长�?
        result[0] = (byte)type; // 事件类型
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1, 4), (uint)payload.Length); // 负载长度
        payload.CopyTo(result.AsSpan(5)); // 拷贝负载
        return result; // 返回完整�?
    }

    /// <summary>编码 SamplePoint（字段顺序固定）�?/summary>
    public static byte[] EncodeSamplePoint(SamplePoint point)
    {
        using var ms = new MemoryStream(72); // 固定字段�?69 字节
        WriteU64(ms, point.TimestampMs); // 时间�?
        WriteU64(ms, point.DeltaTimeMs); // 时间�?
        WriteF64(ms, point.X); // X
        WriteF64(ms, point.Y); // Y
        WriteF64(ms, point.Pressure); // 压力
        WriteU8(ms, point.InContact ? (byte)1 : (byte)0); // 接触标志
        WriteU32(ms, point.Buttons); // 按钮掩码
        WriteF64(ms, point.TiltX); // 倾斜 X
        WriteF64(ms, point.TiltY); // 倾斜 Y
        WriteU64(ms, point.SequenceId); // 序号
        return ms.ToArray(); // 返回
    }

    /// <summary>编码 SessionStart 负载�?/summary>
    public static byte[] EncodeSessionStart(RecordingSession session)
    {
        using var ms = new MemoryStream(128); // 会话开始负载缓�?
        WriteU64(ms, session.Header.CreatedAtUnixMs); // 创建时间
        WriteString(ms, session.SessionId); // 会话 ID
        WriteString(ms, session.Header.Device.Name); // 设备�?
        WriteString(ms, session.Header.Device.Id); // 设备 ID
        WriteString(ms, session.Header.PluginVersion); // 插件版本
        WriteU32(ms, StrokeFormat.PenUpTimeoutMs); // 抬笔超时
        WriteU32(ms, StrokeFormat.MaxStrokesPerSegment); // 最大笔划数
        return ms.ToArray(); // 返回
    }

    /// <summary>编码 StrokeStart 负载（含首点边界快照）�?/summary>
    public static byte[] EncodeStrokeStart(Stroke stroke)
    {
        using var ms = new MemoryStream(128); // 笔划开始缓�?
        WriteU64(ms, stroke.StrokeId); // 笔划 ID
        WriteU64(ms, stroke.StartTimestampMs); // 开始时�?
        if (stroke.Points.Count > 0) // 非空笔划才写首点快照
        {
            var first = EncodeSamplePoint(stroke.Points[0]); // 编码首点
            ms.Write(first); // 追加
        }
        return ms.ToArray(); // 返回
    }

    /// <summary>编码 StrokePoint 负载�?/summary>
    public static byte[] EncodeStrokePoint(ulong strokeId, SamplePoint point)
    {
        using var ms = new MemoryStream(96); // 点事件缓�?
        WriteU64(ms, strokeId); // 笔划 ID
        var pointBytes = EncodeSamplePoint(point); // 编码�?
        ms.Write(pointBytes); // 追加�?
        return ms.ToArray(); // 返回
    }

    /// <summary>编码 StrokeEnd 负载（含末点边界快照）�?/summary>
    public static byte[] EncodeStrokeEnd(Stroke stroke)
    {
        using var ms = new MemoryStream(128); // 笔划结束缓冲
        WriteU64(ms, stroke.StrokeId); // 笔划 ID
        WriteU64(ms, stroke.EndTimestampMs); // 结束时间
        WriteU32(ms, (uint)stroke.Points.Count); // 点数
        var duration = stroke.EndTimestampMs >= stroke.StartTimestampMs // 计算持续
            ? stroke.EndTimestampMs - stroke.StartTimestampMs
            : 0UL;
        WriteU64(ms, duration); // 写入持续
        if (stroke.Points.Count > 0) // 非空才写末点快照
        {
            var last = EncodeSamplePoint(stroke.Points[^1]); // 编码末点
            ms.Write(last); // 追加
        }
        return ms.ToArray(); // 返回
    }

    /// <summary>编码 SessionFlush 负载�?/summary>
    public static byte[] EncodeSessionFlush(StrokeSegment segment)
    {
        using var ms = new MemoryStream(40); // flush 负载缓冲
        WriteU64(ms, segment.EndTimestampMs); // 结束时间
        WriteU8(ms, (byte)segment.Reason); // 原因
        WriteU64(ms, segment.SegmentId); // 分段 ID
        WriteU32(ms, (uint)segment.Strokes.Count); // 笔划�?
        WriteU64(ms, segment.PointCount); // 点数
        WriteU8(ms, (byte)segment.WriteStatus); // 写入状�?
        return ms.ToArray(); // 返回
    }

    /// <summary>编码 SessionEnd 负载�?/summary>
    public static byte[] EncodeSessionEnd(ulong endTimestampMs, FlushReason reason, bool completed)
    {
        using var ms = new MemoryStream(16); // 会话结束缓冲
        WriteU64(ms, endTimestampMs); // 结束时间
        WriteU8(ms, (byte)reason); // 原因
        WriteU8(ms, completed ? (byte)1 : (byte)0); // 是否完整完成
        return ms.ToArray(); // 返回
    }

    /// <summary>写入魔数�?/summary>
    private static void WriteMagic(Stream stream) => stream.Write(StrokeFormat.Magic);

    /// <summary>�?uint8�?/summary>
    private static void WriteU8(Stream stream, byte value) => stream.WriteByte(value);

    /// <summary>�?uint32 LE�?/summary>
    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4]; // 临时缓冲
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value); // 小端编码
        stream.Write(buf); // 写出
    }

    /// <summary>�?uint64 LE�?/summary>
    private static void WriteU64(Stream stream, ulong value)
    {
        Span<byte> buf = stackalloc byte[8]; // 临时缓冲
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value); // 小端编码
        stream.Write(buf); // 写出
    }

    /// <summary>�?float64 LE（IEEE754 位模式）�?/summary>
    private static void WriteF64(Stream stream, double value)
    {
        Span<byte> buf = stackalloc byte[8]; // 临时缓冲
        BinaryPrimitives.WriteDoubleLittleEndian(buf, value); // 小端双精�?
        stream.Write(buf); // 写出
    }

    /// <summary>�?UTF-8 长度前缀字符串（uint16 长度）�?/summary>
    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty); // UTF-8 编码
        var len = (ushort)Math.Min(bytes.Length, 0xFFFF); // 截断�?16 �?
        Span<byte> lenBuf = stackalloc byte[2]; // 长度缓冲
        BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, len); // 写长�?
        stream.Write(lenBuf); // 写出长度
        stream.Write(bytes, 0, len); // 写出内容
    }
}
