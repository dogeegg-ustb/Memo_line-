using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Abstractions.Storage;
using System.Linq;

namespace BehaviorRecognizer.Storage;

/// <summary>
/// Binary container writer: Header / Manifest / Streams / Chunks / Footer.
/// Unknown chunks remain skippable for forward compatibility.
/// </summary>
public sealed class BrlogSessionWriter : ISessionWriter
{
    public const int FormatVersion = 1;
    private static readonly byte[] Magic = "BRLOG\0\0\0"u8.ToArray();

    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly string _partPath;
    private readonly string _finalPath;
    private readonly List<ManifestEntry> _manifest = [];
    private long _eventCount;
    private bool _headerWritten;
    private bool _completed;

    public BrlogSessionWriter(string sessionsDirectory, string sessionId)
    {
        Directory.CreateDirectory(sessionsDirectory);

        var sessionDirectory = Path.Combine(
            sessionsDirectory,
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy"),
            DateTimeOffset.UtcNow.UtcDateTime.ToString("MM"),
            DateTimeOffset.UtcNow.UtcDateTime.ToString("dd"),
            sessionId);

        Directory.CreateDirectory(sessionDirectory);

        _finalPath = Path.Combine(sessionDirectory, $"session-{sessionId}.brlog");
        _partPath = _finalPath + ".part";
        _stream = new FileStream(_partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public string SessionFilePath => _finalPath;

    public async Task WriteHeaderAsync(SessionHeader header, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_headerWritten)
            return;

        // File header: magic + formatVersion + reserved
        _writer.Write(Magic);
        _writer.Write(FormatVersion);
        _writer.Write(0); // reserved

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header, JsonDefaults.Options);
        WriteBlock(BlockType.Header, headerJson, itemCount: 1);

        // Placeholder manifest — rewritten on Complete.
        var manifestOffset = _stream.Position;
        WriteBlock(BlockType.Manifest, Array.Empty<byte>(), itemCount: 0);
        _manifest.Add(new ManifestEntry("header", Offset: 16, Length: headerJson.Length));
        _ = manifestOffset;

        _headerWritten = true;
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task WriteEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.SerializeToUtf8Bytes(inputEvent, JsonDefaults.Options);
        WriteBlock(BlockType.StreamEvents, payload, itemCount: 1);
        _eventCount++;
        if (_eventCount % 64 == 0)
            await _stream.FlushAsync(cancellationToken);
    }

    public Task WriteChunkAsync(string chunkType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var typeBytes = Encoding.UTF8.GetBytes(chunkType);
        var buffer = new byte[4 + typeBytes.Length + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), typeBytes.Length);
        typeBytes.CopyTo(buffer.AsSpan(4));
        payload.Span.CopyTo(buffer.AsSpan(4 + typeBytes.Length));
        WriteBlock(BlockType.Chunk, buffer, itemCount: 1);
        return Task.CompletedTask;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
            return;

        var footer = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventsWritten = _eventCount,
            closedAt = DateTimeOffset.UtcNow,
            intact = true
        }, JsonDefaults.Options);
        WriteBlock(BlockType.Footer, footer, itemCount: 1);

        await _stream.FlushAsync(cancellationToken);
        _writer.Flush();
        _writer.Dispose();
        await _stream.DisposeAsync();

        if (File.Exists(_finalPath))
            File.Delete(_finalPath);
        File.Move(_partPath, _finalPath);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed)
            return;

        try
        {
            _writer.Flush();
            await _stream.FlushAsync();
        }
        catch
        {
            // ignored
        }

        try { _writer.Dispose(); } catch { /* ignore */ }
        try { await _stream.DisposeAsync(); } catch { /* ignore */ }
        // Leave .part for recovery.
    }

    private void WriteBlock(BlockType type, ReadOnlySpan<byte> payload, uint itemCount)
    {
        // Block header 24 bytes: type, formatVersion, itemCount, payloadBytes, crc32, reserved
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], itemCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], (uint)payload.Length);
        var crc = Crc32.HashToUInt32(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], 0);

        _stream.Write(header);
        _stream.Write(payload);
    }

    private enum BlockType : uint
    {
        Header = 1,
        Manifest = 2,
        StreamEvents = 3,
        Chunk = 4,
        Footer = 5
    }

    private sealed record ManifestEntry(string Name, long Offset, int Length);
}

public sealed class EventStore : IEventStore
{
    private readonly ISessionWriter _writer;
    private readonly object _sync = new();

    public EventStore(ISessionWriter writer)
    {
        _writer = writer;
    }

    public Task AppendAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return _writer.WriteEventAsync(inputEvent, cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // Writer flushes periodically; completion finalizes file.
        await Task.CompletedTask;
    }
}

public sealed class JsonEventExporter : IEventExporter
{
    /// <summary>导出 .strokebin（优先）或旧版 .brlog 为 JSON/JSONL。</summary>
    public async Task ExportJsonAsync(string sessionFilePath, string outputPath, CancellationToken cancellationToken = default)
    {
        // STRO 笔迹文件：导出结构化 JSON
        if (sessionFilePath.EndsWith(".strokebin", StringComparison.OrdinalIgnoreCase) ||
            sessionFilePath.EndsWith(".strokebin.part", StringComparison.OrdinalIgnoreCase) ||
            IsStroFile(sessionFilePath))
        {
            await Strokebin.StrokeJsonExporter.ExportFileAsync(sessionFilePath, outputPath, cancellationToken);
            return;
        }

        await using var input = File.OpenRead(sessionFilePath);

        if (input.Length < 16)
            throw new InvalidDataException("File too small to be a BRLOG container.");

        input.Position = 16;
        var headerBuf = new byte[24];
        SessionHeader? sessionHeader = null;
        JsonElement? footer = null;
        var records = new List<object>();

        while (input.Position + 24 <= input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(headerBuf.AsMemory(0, 24), cancellationToken);
            if (read < 24)
                break;

            var type = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(0, 4));
            var payloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(12, 4));
            var payload = new byte[payloadBytes];
            var payloadRead = await input.ReadAsync(payload.AsMemory(0, (int)payloadBytes), cancellationToken);
            if (payloadRead < payloadBytes)
                break;

            if (type == 1)
            {
                sessionHeader = JsonSerializer.Deserialize<SessionHeader>(payload, JsonDefaults.Options);
            }
            else if (type == 3)
            {
                var evt = JsonSerializer.Deserialize<InputEvent>(payload, JsonDefaults.Options);
                if (evt is null)
                    continue;

                if (evt.Type == InputEventType.Custom && evt.Extensions is not null && evt.Extensions.TryGetValue("strokes", out var strokesValue) && strokesValue is not null)
                {
                    records.AddRange(ExpandStrokeBatch(evt, strokesValue));
                }
                else
                {
                    records.Add(evt);
                }
            }
            else if (type == 5)
            {
                footer = JsonSerializer.Deserialize<JsonElement>(payload, JsonDefaults.Options);
            }
        }

        await using var output = File.Create(outputPath);
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (sessionHeader is not null)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                header = new
                {
                    version = sessionHeader.FormatVersion,
                    createdAt = sessionHeader.StartedAt,
                    sessionId = sessionHeader.SessionId,
                    deviceCount = sessionHeader.Devices.Count
                }
            }, JsonDefaults.Options));
        }

        foreach (var record in records)
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonDefaults.Options));

        if (footer is not null)
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { footer }, JsonDefaults.Options));

        await writer.FlushAsync();
        await output.FlushAsync(cancellationToken);
    }

    private static bool IsStroFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) < 4)
                return false;
            return magic[0] == (byte)'S' && magic[1] == (byte)'T' &&
                   magic[2] == (byte)'R' && magic[3] == (byte)'O';
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<object> ExpandStrokeBatch(InputEvent batchEvent, object strokesValue)
    {
        if (strokesValue is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var stroke in element.EnumerateArray())
                yield return stroke;
            yield break;
        }

        yield return batchEvent;
    }
}

public sealed class RecoveryReader : IRecoveryReader
{
    /// <summary>
    /// 扫描未完整提交的 .strokebin.part；按强约束禁止默认改名为完整会话，仅统计数量。
    /// 仍兼容扫描旧版 .brlog.part（仅计数，不再改名冒充完整）。
    /// </summary>
    public Task<int> RecoverPartFilesAsync(string sessionsDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sessionsDirectory))
            return Task.FromResult(0);

        var leftover = 0;
        foreach (var part in Directory.EnumerateFiles(sessionsDirectory, "*.part", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.EndsWith(".strokebin.part", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".brlog.part", StringComparison.OrdinalIgnoreCase))
            {
                leftover++; // 仅计数，禁止把 .part 当成完整文件改名
                Console.Error.WriteLine($"[Recovery] 保留未完整文件: {part}");
            }
        }

        return Task.FromResult(leftover);
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
