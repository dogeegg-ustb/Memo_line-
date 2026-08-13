using System.Buffers.Binary;
using System.Text;
using BehaviorRecognizer.Abstractions.Stroke;

namespace BehaviorRecognizer.Storage.Strokebin;

/// <summary>STRO v1 二进制读取器（边界快照不重复计入点数组）�?/summary>
public static class StrokeBinaryReader
{
    /// <summary>读取完整或可恢复�?.strokebin / .part 文件�?/summary>
    public static RecordingSession Read(string path)
    {
        var bytes = File.ReadAllBytes(path); // 读入全部字节
        var session = new RecordingSession { FilePath = path }; // 会话对象
        var offset = 0; // 当前偏移

        if (bytes.Length < 4 || // 魔数检�?
            bytes[0] != (byte)'S' || bytes[1] != (byte)'T' ||
            bytes[2] != (byte)'R' || bytes[3] != (byte)'O')
            throw new InvalidDataException("invalid STRO magic");

        offset = 4; // 跳过魔数
        session.Header.Version = ReadU32(bytes, ref offset); // 版本
        session.Header.CreatedAtUnixMs = ReadU64(bytes, ref offset); // 创建时间
        session.Header.PluginVersion = ReadString(bytes, ref offset); // 插件版本
        session.Header.Device.Name = ReadString(bytes, ref offset); // 设备�?
        session.Header.Device.Id = ReadString(bytes, ref offset); // 设备 ID
        session.Header.Encoding = ReadU8(bytes, ref offset); // 编码

        StrokeSegment? activeSegment = null; // 当前分段
        Stroke? activeStroke = null; // 当前笔划

        while (offset < bytes.Length)
        {
            if (offset + 5 > bytes.Length) // 帧头不完整则停止
                break;

            var typeRaw = ReadU8(bytes, ref offset); // 事件类型
            var payloadLen = ReadU32(bytes, ref offset); // 负载长度
            if (offset + payloadLen > bytes.Length) // 负载被截�?
                break;

            var payloadStart = offset; // 负载起点
            var payloadEnd = offset + (int)payloadLen; // 负载终点
            offset = payloadEnd; // 先跳过，保证未知事件可跳�?
            var ep = payloadStart; // 负载内游�?

            switch ((StrokeEventType)typeRaw)
            {
                case StrokeEventType.SessionStart:
                {
                    _ = ReadU64(bytes, ref ep); // createdAt
                    session.SessionId = ReadString(bytes, ref ep); // sessionId
                    var deviceName = ReadString(bytes, ref ep); // 设备�?
                    var deviceId = ReadString(bytes, ref ep); // 设备 ID
                    var pluginVersion = ReadString(bytes, ref ep); // 插件版本
                    _ = ReadU32(bytes, ref ep); // penUpTimeout
                    _ = ReadU32(bytes, ref ep); // maxStrokes
                    if (!string.IsNullOrEmpty(deviceName))
                        session.Header.Device.Name = deviceName;
                    if (!string.IsNullOrEmpty(deviceId))
                        session.Header.Device.Id = deviceId;
                    if (!string.IsNullOrEmpty(pluginVersion))
                        session.Header.PluginVersion = pluginVersion;
                    break;
                }
                case StrokeEventType.StrokeStart:
                {
                    activeSegment ??= BeginSegment(session); // 确保有分�?
                    var stroke = new Stroke
                    {
                        StrokeId = ReadU64(bytes, ref ep), // 笔划 ID
                        StartTimestampMs = ReadU64(bytes, ref ep), // 开始时�?
                    };
                    if (ep < payloadEnd) // 可选首点边界快照：读取但不加入 points
                        _ = TryReadSamplePoint(bytes, ref ep, payloadEnd);
                    activeSegment.Strokes.Add(stroke); // 追加笔划
                    activeStroke = stroke; // 设为当前
                    break;
                }
                case StrokeEventType.StrokePoint:
                {
                    if (activeSegment is null)
                        break;
                    var strokeId = ReadU64(bytes, ref ep); // 笔划 ID
                    if (!TryReadSamplePoint(bytes, ref ep, payloadEnd, out var point))
                        break;
                    activeStroke = FindStroke(activeSegment, strokeId) ?? activeStroke; // 定位笔划
                    if (activeStroke is not null)
                    {
                        activeStroke.Points.Add(point); // �?StrokePoint 计入
                        activeStroke.EndTimestampMs = point.TimestampMs;
                        activeSegment.PointCount++;
                    }
                    break;
                }
                case StrokeEventType.StrokeEnd:
                {
                    var strokeId = ReadU64(bytes, ref ep); // 笔划 ID
                    var endTs = ReadU64(bytes, ref ep); // 结束时间
                    _ = ReadU32(bytes, ref ep); // pointCount
                    _ = ReadU64(bytes, ref ep); // duration
                    // 末点边界快照不加�?points
                    if (activeStroke is not null && activeStroke.StrokeId == strokeId)
                        activeStroke.EndTimestampMs = endTs;
                    break;
                }
                case StrokeEventType.SessionFlush:
                {
                    activeSegment ??= BeginSegment(session);
                    activeSegment.EndTimestampMs = ReadU64(bytes, ref ep);
                    activeSegment.Reason = (FlushReason)ReadU8(bytes, ref ep);
                    activeSegment.SegmentId = ReadU64(bytes, ref ep);
                    _ = ReadU32(bytes, ref ep); // strokeCount
                    activeSegment.PointCount = ReadU64(bytes, ref ep);
                    activeSegment.WriteStatus = (WriteStatus)ReadU8(bytes, ref ep);
                    if (activeSegment.StartTimestampMs == 0 && activeSegment.Strokes.Count > 0)
                        activeSegment.StartTimestampMs = activeSegment.Strokes[0].StartTimestampMs;
                    activeSegment = null; // 分段结束
                    activeStroke = null;
                    break;
                }
                case StrokeEventType.SessionEnd:
                    // 已由分段重建会话，忽略剩余字�?
                    break;
                default:
                    // 未知事件已按 payloadLength 跳过
                    break;
            }
        }

        return session;
    }

    /// <summary>文件是否包含完整 SessionEnd(completed=1)�?/summary>
    public static bool HasCompletedSessionEnd(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path); // 读文�?
            var offset = 0;
            if (bytes.Length < 4 || bytes[0] != (byte)'S')
                return false;
            // 粗略扫描事件帧寻�?SessionEnd
            // 跳过文件头：魔数后变长字符串，改为逐帧扫描整文�?
            offset = 4;
            if (offset + 4 + 8 > bytes.Length)
                return false;
            _ = ReadU32(bytes, ref offset); // version
            _ = ReadU64(bytes, ref offset); // createdAt
            _ = ReadString(bytes, ref offset); // pluginVersion
            _ = ReadString(bytes, ref offset); // deviceName
            _ = ReadString(bytes, ref offset); // deviceId
            if (offset >= bytes.Length)
                return false;
            _ = ReadU8(bytes, ref offset); // encoding

            while (offset + 5 <= bytes.Length)
            {
                var type = (StrokeEventType)ReadU8(bytes, ref offset);
                var len = ReadU32(bytes, ref offset);
                if (offset + len > bytes.Length)
                    return false;
                if (type == StrokeEventType.SessionEnd && len >= 10)
                {
                    var ep = offset;
                    _ = ReadU64(bytes, ref ep); // endTs
                    _ = ReadU8(bytes, ref ep); // reason
                    var completed = ReadU8(bytes, ref ep); // completed
                    return completed == 1;
                }
                offset += (int)len;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static StrokeSegment BeginSegment(RecordingSession session)
    {
        var segment = new StrokeSegment { SegmentId = (ulong)(session.Segments.Count + 1) };
        session.Segments.Add(segment);
        return segment;
    }

    private static Stroke? FindStroke(StrokeSegment segment, ulong strokeId)
    {
        foreach (var s in segment.Strokes)
        {
            if (s.StrokeId == strokeId)
                return s;
        }
        return null;
    }

    private static bool TryReadSamplePoint(byte[] bytes, ref int offset, int end)
        => TryReadSamplePoint(bytes, ref offset, end, out _);

    private static bool TryReadSamplePoint(byte[] bytes, ref int offset, int end, out SamplePoint point)
    {
        point = new SamplePoint();
        var start = offset;
        try
        {
            if (end - offset < 69) // 最�?SamplePoint 大小�?69
                return false;
            point.TimestampMs = ReadU64(bytes, ref offset);
            point.DeltaTimeMs = ReadU64(bytes, ref offset);
            point.X = ReadF64(bytes, ref offset);
            point.Y = ReadF64(bytes, ref offset);
            point.Pressure = ReadF64(bytes, ref offset);
            point.InContact = ReadU8(bytes, ref offset) != 0;
            point.Buttons = ReadU32(bytes, ref offset);
            point.TiltX = ReadF64(bytes, ref offset);
            point.TiltY = ReadF64(bytes, ref offset);
            point.SequenceId = ReadU64(bytes, ref offset);
            return offset <= end;
        }
        catch
        {
            offset = start;
            return false;
        }
    }

    private static byte ReadU8(byte[] bytes, ref int offset)
    {
        if (offset >= bytes.Length)
            throw new EndOfStreamException();
        return bytes[offset++];
    }

    private static ushort ReadU16(byte[] bytes, ref int offset)
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        offset += 2;
        return v;
    }

    private static uint ReadU32(byte[] bytes, ref int offset)
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        return v;
    }

    private static ulong ReadU64(byte[] bytes, ref int offset)
    {
        var v = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        offset += 8;
        return v;
    }

    private static double ReadF64(byte[] bytes, ref int offset)
    {
        var v = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset, 8));
        offset += 8;
        return v;
    }

    private static string ReadString(byte[] bytes, ref int offset)
    {
        var len = ReadU16(bytes, ref offset);
        if (offset + len > bytes.Length)
            throw new EndOfStreamException();
        var s = Encoding.UTF8.GetString(bytes, offset, len);
        offset += len;
        return s;
    }
}
