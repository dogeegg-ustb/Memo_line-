using BehaviorRecognizer.Abstractions.Stroke;

namespace BehaviorRecognizer.Storage.Strokebin;

/// <summary>有界异步分段队列：满时丢最旧，停止时排空（对齐 OtdStrokePlugin）�?/summary>
public sealed class AsyncRecordQueue : IAsyncDisposable
{
    private readonly int _capacity; // 固定容量
    private readonly Func<StrokeSegment, bool> _writeFn; // 后台写盘回调
    private readonly object _sync = new(); // 队列互斥
    private readonly Queue<StrokeSegment> _queue = new(); // 待写队列
    private readonly AsyncQueueStats _stats = new(); // 统计
    private readonly CancellationTokenSource _cts = new(); // 取消�?
    private readonly Task _worker; // 后台线程
    private bool _running = true; // 是否接受新数�?
    private bool _disposed; // 是否已释�?

    /// <summary>创建队列；capacity �?0 时回退�?8�?/summary>
    public AsyncRecordQueue(int capacity, Func<StrokeSegment, bool> writeFn)
    {
        _capacity = capacity == 0 ? StrokeFormat.FallbackQueueCapacity : capacity; // 回退容量
        _writeFn = writeFn; // 保存写回�?
        _worker = Task.Run(WorkerLoop); // 启动后台写盘
    }

    /// <summary>入队冻结分段；满则丢最旧保留最新；禁止阻塞采集线程�?/summary>
    public WriteStatus Enqueue(StrokeSegment segment)
    {
        lock (_sync)
        {
            if (!_running) // 已停止：拒绝新数�?
            {
                _stats.DroppedNewest++; // 记丢最�?
                return WriteStatus.DroppedNewest;
            }

            if (_queue.Count >= _capacity) // 队列�?
            {
                _queue.Dequeue(); // 丢最�?
                _stats.DroppedOldest++; // 计数
                segment.WriteStatus = WriteStatus.DroppedOldest; // 标记新分�?
                _queue.Enqueue(segment); // 保留新分�?
                _stats.Enqueued++; // 入队计数
                Monitor.Pulse(_sync); // 唤醒写线�?
                return WriteStatus.DroppedOldest;
            }

            _queue.Enqueue(segment); // 正常入队
            _stats.Enqueued++; // 入队计数
            Monitor.Pulse(_sync); // 唤醒写线�?
            return WriteStatus.Ok;
        }
    }

    /// <summary>停止接收并排空已接受分段�?/summary>
    public void StopAndDrain()
    {
        lock (_sync)
        {
            if (!_running) // 幂等
                return;
            _running = false; // 拒绝新数�?
            Monitor.PulseAll(_sync); // 唤醒写线程退出等�?
        }

        try { _worker.Wait(); } // 等待排空完成
        catch { /* 忽略 */ }
    }

    /// <summary>异步停止排空�?/summary>
    public Task StopAndDrainAsync()
    {
        return Task.Run(StopAndDrain); // 避免在持锁上下文中死�?
    }

    /// <summary>当前统计快照�?/summary>
    public AsyncQueueStats Stats
    {
        get
        {
            lock (_sync)
            {
                return new AsyncQueueStats // 拷贝
                {
                    Enqueued = _stats.Enqueued,
                    Written = _stats.Written,
                    DroppedOldest = _stats.DroppedOldest,
                    DroppedNewest = _stats.DroppedNewest,
                    WriteErrors = _stats.WriteErrors,
                };
            }
        }
    }

    /// <summary>当前队列长度�?/summary>
    public int Size
    {
        get { lock (_sync) return _queue.Count; }
    }

    /// <summary>释放资源�?/summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAndDrainAsync().ConfigureAwait(false); // 排空
        _cts.Dispose(); // 释放取消�?
    }

    /// <summary>后台循环：顺序写盘�?/summary>
    private void WorkerLoop()
    {
        while (true)
        {
            StrokeSegment? segment = null; // 待写分段
            lock (_sync)
            {
                while (_queue.Count == 0 && _running) // 空且运行中则等待
                    Monitor.Wait(_sync);

                if (_queue.Count == 0 && !_running) // 停止且已空：退�?
                    return;

                segment = _queue.Dequeue(); // 取出最�?
            }

            var ok = false; // 写结�?
            try { ok = _writeFn(segment); } // 调用写盘（锁外）
            catch { ok = false; }

            lock (_sync)
            {
                if (ok)
                    _stats.Written++; // 成功
                else
                    _stats.WriteErrors++; // 失败
            }
        }
    }
}
