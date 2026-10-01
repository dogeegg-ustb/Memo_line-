using BehaviorRecognizer.Abstractions.Stroke;

namespace BehaviorRecognizer.Storage.Strokebin;

/// <summary>两阶段提交的 .strokebin 写入器（�?.part，成功后再改名）�?/summary>
public sealed class BinaryStrokeWriter : IDisposable
{
    private readonly object _sync = new(); // 写盘互斥
    private FileStream? _stream; // 当前 .part �?
    private string _finalPath = string.Empty; // 最终路�?
    private string _partPath = string.Empty; // 临时路径
    private bool _headerWritten; // 是否已写文件�?
    private bool _sessionEnded; // 是否已写 SessionEnd
    private bool _disposed; // 是否已释�?

    /// <summary>最�?.strokebin 路径�?/summary>
    public string FinalPath
    {
        get { lock (_sync) return _finalPath; }
    }

    /// <summary>是否仍打开�?/summary>
    public bool IsOpen
    {
        get { lock (_sync) return _stream is not null; }
    }

    /// <summary>打开会话：创�?.part 并写入文件头 + SessionStart�?/summary>
    public bool OpenSession(string finalPath, RecordingSession session)
    {
        lock (_sync)
        {
            if (_stream is not null) // 禁止重复打开
                return false;

            _finalPath = finalPath; // 记录最终路�?
            _partPath = finalPath + ".part"; // 临时后缀
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!); // 确保目录

            _stream = new FileStream( // 截断创建 .part
                _partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);

            if (!WriteBytes(StrokeBinaryEncoder.EncodeHeader(session.Header))) // 写文件头
                return false;

            var startPayload = StrokeBinaryEncoder.EncodeSessionStart(session); // SessionStart 负载
            if (!WriteBytes(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.SessionStart, startPayload)))
                return false;

            _headerWritten = true; // 标记头已�?
            _sessionEnded = false; // 会话未结�?
            return true; // 成功
        }
    }

    /// <summary>将一个冻结分段写入事件流�?/summary>
    public bool WriteSegment(StrokeSegment segment)
    {
        lock (_sync)
        {
            if (_stream is null || !_headerWritten || _sessionEnded) // 未就绪则失败
                return false;

            foreach (var stroke in segment.Strokes) // 按笔划顺序写
            {
                var startPayload = StrokeBinaryEncoder.EncodeStrokeStart(stroke); // StrokeStart
                if (!WriteBytes(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.StrokeStart, startPayload)))
                    return false;

                foreach (var point in stroke.Points) // 全部 StrokePoint
                {
                    var pointPayload = StrokeBinaryEncoder.EncodeStrokePoint(stroke.StrokeId, point);
                    if (!WriteBytes(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.StrokePoint, pointPayload)))
                        return false;
                }

                var endPayload = StrokeBinaryEncoder.EncodeStrokeEnd(stroke); // StrokeEnd
                if (!WriteBytes(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.StrokeEnd, endPayload)))
                    return false;
            }

            var flushPayload = StrokeBinaryEncoder.EncodeSessionFlush(segment); // SessionFlush
            return WriteBytes(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.SessionFlush, flushPayload));
        }
    }

    /// <summary>写入 SessionEnd、flush、关闭，并原子改名为 .strokebin�?/summary>
    public bool CloseSession(FlushReason reason, bool completed)
    {
        lock (_sync)
        {
            if (_stream is null) // 未打开
                return false;

            try
            {
                if (!_sessionEnded) // 幂等：只写一�?SessionEnd
                {
                    var endPayload = StrokeBinaryEncoder.EncodeSessionEnd( // 会话结束负载
                        StrokePathUtil.NowUnixMs(),
                        reason,
                        completed);
                    if (!WriteBytes(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.SessionEnd, endPayload)))
                    {
                        _stream.Dispose(); // 失败也关闭流
                        _stream = null;
                        return false;
                    }
                    _sessionEnded = true; // 标记已结�?
                }

                _stream.Flush(true); // 刷盘
                _stream.Dispose(); // 关闭
                _stream = null;
                return FinalizePart(); // .part �?.strokebin
            }
            catch
            {
                try { _stream?.Dispose(); } catch { /* 忽略 */ }
                _stream = null;
                return false;
            }
        }
    }

    /// <summary>释放未完成会话时留下 .part（不改名）�?/summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_sync)
        {
            try { _stream?.Flush(true); } catch { /* 忽略 */ }
            try { _stream?.Dispose(); } catch { /* 忽略 */ }
            _stream = null;
        }
    }

    /// <summary>写字节到当前流�?/summary>
    private bool WriteBytes(byte[] bytes)
    {
        if (_stream is null)
            return false;
        if (bytes.Length == 0)
            return true;
        try
        {
            _stream.Write(bytes, 0, bytes.Length); // 同步写出
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>仅在完整关闭后将 .part 重命名为最终文件�?/summary>
    private bool FinalizePart()
    {
        try
        {
            if (File.Exists(_finalPath)) // 已存在则删除
                File.Delete(_finalPath);
            File.Move(_partPath, _finalPath); // 原子改名
            return true;
        }
        catch
        {
            return false;
        }
    }
}
