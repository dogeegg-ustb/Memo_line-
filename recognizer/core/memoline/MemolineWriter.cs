using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace BehaviorRecognizer.Storage.Memoline;

public sealed record EventStamp(ulong EventId, long Ticks);
public sealed record HardwareDeviceSource(string DeviceType, string CaptureApi,
    string? DeviceId, string Identification);
public sealed record MemolineEvent(ulong AppendId, string Path, string Kind,
    ulong EventId, long Ticks, long AppendedTicks, ulong? OperationId,
    ulong[]? RelatedEventIds, HardwareDeviceSource? DeviceSource, JsonElement Data);

public sealed class MemolineWriterOptions
{
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;
    public TimeSpan DurableFlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Confirmed core states only; failed/raw attempts go to a sibling diagnostics JSONL log.</summary>
    public bool SuccessfulStatesOnly { get; init; }
}

/// <summary>One owner for both producers. Frames are immutable and never rewritten.</summary>
public sealed class MemolineWriter : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _sync = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private sealed record WriteWork(byte[]? Payload, bool Durable = false, TaskCompletionSource? Completion = null,
        bool Diagnostic = false);
    private readonly Channel<WriteWork> _frames = Channel.CreateUnbounded<WriteWork>(new() { SingleReader = true });
    private readonly FileStream _stream;
    private readonly Task _pump;
    private readonly Task _flushTicker;
    private readonly CancellationTokenSource _flushCancellation = new();
    private readonly MemolineWriterOptions _options;
    private ulong _eventId, _appendId, _statePackageOrder;
    private readonly Dictionary<string, (ulong Anchor, long Ticks, string Reason, ulong Order)> _statePackages = [];
    private bool _closed;
    public string FilePath { get; }
    public string LiveFilePath => FilePath + ".part";
    public string DiagnosticFilePath => System.IO.Path.ChangeExtension(FilePath, ".diagnostics.jsonl");
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public long NowTicks => Stopwatch.GetTimestamp() - _origin;
    public long ClockOriginTicks => _origin;
    public ulong LastHardwareEventId { get { lock (_sync) return _eventId; } }
    // Runs under the writer lock on the capture worker; small logical slot reservations are permitted.
    public event Action<MemolineEvent>? HardwareAppended;
    // Subscribers must enqueue only: this callback runs on the producer's ordered append path.
    public event Action<MemolineEvent>? RecordAppended;
    // Diagnostic-only observations have AppendId=0 and no frame in the memoline file.
    public event Action<MemolineEvent>? DiagnosticRecorded;

    public MemolineWriter(string directory, object metadata, MemolineWriterOptions? options = null)
    {
        _options = options ?? new();
        if (_options.DurableFlushInterval <= TimeSpan.Zero || _options.DurableFlushInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(_options.CompressionLevel)) throw new ArgumentOutOfRangeException(nameof(options));
        Directory.CreateDirectory(directory);
        FilePath = System.IO.Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{SessionId}.memoline");
        _stream = new FileStream(LiveFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        MemolineFormat.WriteHeader(_stream);
        _stream.Flush(flushToDisk: true);
        Enqueue(new { kind = "header", version = MemolineFormat.CurrentVersion, sessionId = SessionId,
            createdAt = DateTimeOffset.UtcNow, clock = "Stopwatch", frequency = Stopwatch.Frequency,
            originTicks = _origin, timelineModel = "causalPackagesV1",
            statePersistence = _options.SuccessfulStatesOnly ? "successfulOnly" : "allResults",
            compression = _options.CompressionLevel == CompressionLevel.NoCompression ? "none" : "brotliPerFrame",
            durableFlushIntervalMs = _options.DurableFlushInterval.TotalMilliseconds, metadata });
        _pump = Task.Run(PumpAsync);
        _flushTicker = Task.Run(FlushTickerAsync);
    }

    public EventStamp AppendHardware(string kind, object data, HardwareDeviceSource deviceSource,
        ulong? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(deviceSource);
        lock (_sync)
        {
            var stamp = new EventStamp(++_eventId, NowTicks);
            Append("hardware", kind, stamp, operationId, null, deviceSource, data);
            return stamp;
        }
    }

    /// <summary>Occurrence time is in this session's ticks, never wall time. References survive delayed append.</summary>
    public void AppendState(string kind, long occurredTicks, ulong[] relatedEventIds, object data, string path = "state")
    {
        lock (_sync)
        {
            if (occurredTicks < 0 || occurredTicks > NowTicks || relatedEventIds.Any(id => id == 0 || id > _eventId))
                throw new ArgumentOutOfRangeException(nameof(occurredTicks));
            if (path is not ("state" or "immediate" or "delayed")) throw new ArgumentException("Invalid write path.", nameof(path));
            if (_options.SuccessfulStatesOnly && MemolineStatePersistence.IsDiagnostic(kind, data))
            {
                AppendDiagnostic(path, kind, occurredTicks, relatedEventIds, data);
                return;
            }
            Append(path, kind, new EventStamp(0, occurredTicks), null, relatedEventIds, null, data);
        }
    }

    /// <summary>Reserve a logical slot after an input. SuccessfulStatesOnly keeps it in memory until a confirmed result arrives.</summary>
    public string ReserveStatePackage(ulong anchorEventId, long occurredTicks, string reason)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (anchorEventId > _eventId) throw new ArgumentOutOfRangeException(nameof(anchorEventId));
            if (occurredTicks < 0 || occurredTicks > NowTicks) throw new ArgumentOutOfRangeException(nameof(occurredTicks));
            string id = Guid.NewGuid().ToString("N");
            _statePackages.Add(id, (anchorEventId, occurredTicks, reason, ++_statePackageOrder));
            if (!_options.SuccessfulStatesOnly)
                AppendReservation(id, anchorEventId, occurredTicks, reason, _statePackageOrder);
            return id;
        }
    }

    private void AppendReservation(string id, ulong anchorEventId, long occurredTicks, string reason, ulong order) =>
        AppendState("statePackageReserved", occurredTicks, anchorEventId == 0 ? [] : [anchorEventId],
            new { packageId = id, afterEventId = anchorEventId, occurredTicks, reason, reservationOrder = order, status = "pending" }, "immediate");

    public void AppendPackageResult(string packageId, object data)
    {
        lock (_sync)
        {
            if (!_statePackages.TryGetValue(packageId, out var slot)) throw new InvalidDataException("Unknown state package.");
            if (_options.SuccessfulStatesOnly)
            {
                var original = JsonSerializer.SerializeToElement(data, Json);
                var confirmed = MemolineStatePersistence.SuccessfulPackage(original);
                if (confirmed is null || original.GetProperty("updates").GetArrayLength() != confirmed["updates"]!.AsArray().Count)
                    AppendDiagnostic("delayed", "statePackageResult", slot.Ticks, slot.Anchor == 0 ? [] : [slot.Anchor],
                        new { packageId, afterEventId = slot.Anchor, occurredTicks = slot.Ticks, result = original });
                if (confirmed is null)
                {
                    _statePackages.Remove(packageId);
                    return;
                }
                data = confirmed;
                AppendReservation(packageId, slot.Anchor, slot.Ticks, slot.Reason, slot.Order);
            }
            AppendState("statePackageResult", slot.Ticks, slot.Anchor == 0 ? [] : [slot.Anchor],
                new { packageId, afterEventId = slot.Anchor, occurredTicks = slot.Ticks, result = data }, "delayed");
            _statePackages.Remove(packageId);
        }
    }

    private void AppendDiagnostic(string path, string kind, long ticks, ulong[] related, object data)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_pump.IsFaulted) _pump.GetAwaiter().GetResult();
        var record = new MemolineEvent(0, path, kind, 0, ticks, NowTicks, null, related, null,
            JsonSerializer.SerializeToElement(data, Json));
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (payload.Length > MemolineFormat.MaximumFrameBytes) throw new InvalidDataException("Frame exceeds 64 MiB.");
        if (!_frames.Writer.TryWrite(new(payload, Diagnostic: true))) throw new ObjectDisposedException(nameof(MemolineWriter));
        DiagnosticRecorded?.Invoke(record);
    }

    private void Append(string path, string kind, EventStamp stamp, ulong? operationId,
        ulong[]? related, HardwareDeviceSource? deviceSource, object data)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_pump.IsFaulted) _pump.GetAwaiter().GetResult();
        var record = new MemolineEvent(++_appendId, path, kind, stamp.EventId, stamp.Ticks,
            NowTicks, operationId ?? (path == "hardware" ? stamp.EventId : null), related,
            deviceSource,
            JsonSerializer.SerializeToElement(data, Json));
        Enqueue(record);
        RecordAppended?.Invoke(record);
        if (path == "hardware") HardwareAppended?.Invoke(record);
    }

    private void Enqueue(object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MemolineFormat.MaximumFrameBytes) throw new InvalidDataException("Frame exceeds 64 MiB.");
        if (!_frames.Writer.TryWrite(new(payload))) throw new ObjectDisposedException(nameof(MemolineWriter));
    }

    /// <summary>Waits for all preceding records to reach disk (or the OS cache with durable=false).</summary>
    public Task FlushAsync(bool durable = true, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_pump.IsFaulted) _pump.GetAwaiter().GetResult();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_frames.Writer.TryWrite(new(null, durable, completion)))
                throw new ObjectDisposedException(nameof(MemolineWriter));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task FlushTickerAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.DurableFlushInterval);
            while (await timer.WaitForNextTickAsync(_flushCancellation.Token))
                if (!_frames.Writer.TryWrite(new(null, Durable: true))) break;
        }
        catch (OperationCanceledException) when (_flushCancellation.IsCancellationRequested) { }
    }

    private async Task PumpAsync()
    {
        bool dirty = false;
        long lastDurableFlush = Stopwatch.GetTimestamp();
        WriteWork? current = null;
        FileStream? diagnostics = null;
        try
        {
            await foreach (var work in _frames.Reader.ReadAllAsync())
            {
                current = work;
                if (work.Payload is { } payload)
                {
                    if (work.Diagnostic)
                    {
                        diagnostics ??= new FileStream(DiagnosticFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                        diagnostics.Write(payload);
                        diagnostics.WriteByte((byte)'\n');
                    }
                    else MemolineFormat.WriteFrame(_stream, payload, _options.CompressionLevel);
                    dirty = true;
                }
                if (work.Durable || Stopwatch.GetElapsedTime(lastDurableFlush) >= _options.DurableFlushInterval)
                {
                    if (dirty)
                    {
                        _stream.Flush(flushToDisk: true);
                        diagnostics?.Flush(flushToDisk: true);
                    }
                    dirty = false;
                    lastDurableFlush = Stopwatch.GetTimestamp();
                }
                else if (work.Completion is not null || !_frames.Reader.TryPeek(out _))
                {
                    await _stream.FlushAsync();
                    if (diagnostics is not null) await diagnostics.FlushAsync();
                }
                work.Completion?.TrySetResult();
                current = null;
            }
            diagnostics?.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            current?.Completion?.TrySetException(ex);
            _frames.Writer.TryComplete(ex);
            while (_frames.Reader.TryRead(out var work)) work.Completion?.TrySetException(ex);
            throw;
        }
        finally { if (diagnostics is not null) await diagnostics.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closed) return;
            foreach (string id in _statePackages.Keys.ToArray())
                AppendPackageResult(id, new { status = "error", updates = Array.Empty<object>(), reason = "sessionEndedBeforeStateResolution" });
            _closed = true;
            Enqueue(new { kind = "footer", hardwareEvents = _eventId, appendedRecords = _appendId, ticks = NowTicks });
            _frames.Writer.TryComplete();
        }
        try
        {
            await _flushCancellation.CancelAsync();
            await _flushTicker;
            await _pump;
            _stream.Flush(true);
        }
        finally { _flushCancellation.Dispose(); await _stream.DisposeAsync(); }
        File.Move(LiveFilePath, FilePath);
    }
}
