using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using System.Threading.Channels;

namespace BehaviorRecognizer.Storage.Memoline;

public sealed record EventStamp(ulong EventId, long Ticks);
public sealed record HardwareDeviceSource(string DeviceType, string CaptureApi,
    string? DeviceId, string Identification);
public sealed record MemolineEvent(ulong AppendId, string Path, string Kind,
    ulong EventId, long Ticks, long AppendedTicks, ulong? OperationId,
    ulong[]? RelatedEventIds, HardwareDeviceSource? DeviceSource, JsonElement Data);

/// <summary>One owner for both producers. Frames are immutable and never rewritten.</summary>
public sealed class MemolineWriter : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _sync = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    private readonly FileStream _stream;
    private readonly Task _pump;
    private ulong _eventId, _appendId;
    private readonly Dictionary<string, (ulong Anchor, long Ticks)> _statePackages = [];
    private bool _closed;
    public string FilePath { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public long NowTicks => Stopwatch.GetTimestamp() - _origin;
    public long ClockOriginTicks => _origin;
    public ulong LastHardwareEventId { get { lock (_sync) return _eventId; } }
    // Runs under the writer lock on the capture worker; small logical slot reservations are permitted.
    public event Action<MemolineEvent>? HardwareAppended;

    public MemolineWriter(string directory, object metadata)
    {
        Directory.CreateDirectory(directory);
        FilePath = System.IO.Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{SessionId}.memoline");
        _stream = new FileStream(FilePath + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _stream.Write("MEMOLINE"u8);
        using (var writer = new BinaryWriter(_stream, System.Text.Encoding.UTF8, true)) writer.Write(1u);
        Enqueue(new { kind = "header", version = 1, sessionId = SessionId,
            createdAt = DateTimeOffset.UtcNow, clock = "Stopwatch", frequency = Stopwatch.Frequency,
            originTicks = _origin, timelineModel = "causalPackagesV1", metadata });
        _pump = Task.Run(PumpAsync);
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
            Append(path, kind, new EventStamp(0, occurredTicks), null, relatedEventIds, null, data);
        }
    }

    /// <summary>Reserve a logical slot after an input, independently of physical append order.</summary>
    public string ReserveStatePackage(ulong anchorEventId, long occurredTicks, string reason)
    {
        lock (_sync)
        {
            if (anchorEventId > _eventId) throw new ArgumentOutOfRangeException(nameof(anchorEventId));
            string id = Guid.NewGuid().ToString("N");
            _statePackages.Add(id, (anchorEventId, occurredTicks));
            AppendState("statePackageReserved", occurredTicks, anchorEventId == 0 ? [] : [anchorEventId],
                new { packageId = id, afterEventId = anchorEventId, occurredTicks, reason, status = "pending" }, "immediate");
            return id;
        }
    }

    public void AppendPackageResult(string packageId, object data)
    {
        lock (_sync)
        {
            if (!_statePackages.TryGetValue(packageId, out var slot)) throw new InvalidDataException("Unknown state package.");
            AppendState("statePackageResult", slot.Ticks, slot.Anchor == 0 ? [] : [slot.Anchor],
                new { packageId, afterEventId = slot.Anchor, occurredTicks = slot.Ticks, result = data }, "delayed");
            _statePackages.Remove(packageId);
        }
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
        if (path == "hardware") HardwareAppended?.Invoke(record);
    }

    private void Enqueue(object value) => _frames.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private async Task PumpAsync()
    {
        using var writer = new BinaryWriter(_stream, System.Text.Encoding.UTF8, true);
        await foreach (var payload in _frames.Reader.ReadAllAsync())
        {
            writer.Write(payload.Length);
            writer.Write(Crc32.HashToUInt32(payload));
            writer.Write(payload);
            // Commit each available batch; idle sessions do not leave the last action buffered.
            if (!_frames.Reader.TryPeek(out _)) await _stream.FlushAsync();
        }
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
            await _pump;
            _stream.Flush(true);
        }
        finally { await _stream.DisposeAsync(); }
        File.Move(FilePath + ".part", FilePath);
    }
}
