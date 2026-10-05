using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaviorRecognizer.Storage.Memoline;

/// <summary>Retains a file handle and frame boundary while a recording grows or is renamed.</summary>
public sealed class MemolineReadSession : IDisposable
{
    private readonly FileStream _stream;
    private readonly bool _allowIncomplete;
    public uint? FormatVersion { get; private set; }
    public long Offset => _stream.Position;
    public bool IsComplete { get; private set; }

    internal MemolineReadSession(string path)
    {
        path = ResolvePath(path);
        _allowIncomplete = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
    }

    internal static string ResolvePath(string path)
    {
        if (!File.Exists(path) && !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path + ".part")) path += ".part";
        if (!File.Exists(path) && path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path[..^5])) path = path[..^5];
        return path;
    }

    /// <summary>Reads only new complete, CRC-valid frames; an incomplete tail is retried next call.</summary>
    public IReadOnlyList<JsonElement> ReadAvailable(int maxRecords = 256)
    {
        if (maxRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        var frames = new List<JsonElement>();
        if (FormatVersion is null)
        {
            FormatVersion = MemolineFormat.ReadHeader(_stream, _allowIncomplete);
            if (FormatVersion is null) return frames;
        }
        long decodedBytes = 0;
        while (frames.Count < maxRecords && decodedBytes < 8 * 1024 * 1024 && _stream.Position < _stream.Length)
        {
            if (IsComplete) throw new InvalidDataException("Data after footer.");
            long boundary = _stream.Position;
            var frame = MemolineFormat.ReadFrame(_stream, FormatVersion.Value, _allowIncomplete, out int frameBytes);
            if (frame is null)
            {
                _stream.Position = boundary;
                break;
            }
            IsComplete = frame.Value.GetProperty("kind").GetString() == "footer";
            decodedBytes += frameBytes;
            frames.Add(frame.Value);
        }
        if (IsComplete && _stream.Position < _stream.Length) throw new InvalidDataException("Data after footer.");
        if (!_allowIncomplete && _stream.Position == _stream.Length && !IsComplete)
            throw new InvalidDataException("Missing session footer.");
        return frames;
    }

    public void Dispose() => _stream.Dispose();
}

public sealed record MemolineCompressionResult(long InputBytes, long OutputBytes, long Records);

public static class MemolineReader
{
    public static MemolineReadSession Open(string path) => new(path);

    /// <summary>Reads v1/v2; a .part yields the complete prefix without waiting for more input.</summary>
    public static IEnumerable<JsonElement> Read(string path)
    {
        using var session = Open(path);
        while (true)
        {
            var frames = session.ReadAvailable(maxRecords: 1);
            if (frames.Count == 0) yield break;
            foreach (var frame in frames) yield return frame;
        }
    }

    /// <summary>Follows from the first record until footer or cancellation; each frame is delivered once.</summary>
    public static async IAsyncEnumerable<JsonElement> FollowAsync(string path, TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        using var session = Open(path);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frames = session.ReadAvailable();
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
            }
            if (session.IsComplete) yield break;
            if (frames.Count == 0) await Task.Delay(interval, cancellationToken);
        }
    }

    public static async Task ExportAsync(string input, string output, CancellationToken token)
    {
        EnsureDifferentPaths(input, output);
        using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
        foreach (var record in Read(input))
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(record.GetRawText());
        }
    }

    /// <summary>Creates a compressed copy, preserving all records except the header format version.</summary>
    public static async Task<MemolineCompressionResult> CompactAsync(string input, string output,
        CancellationToken cancellationToken = default)
    {
        EnsureDifferentPaths(input, output);
        if (File.Exists(output)) throw new IOException("Output already exists; choose a new path.");
        long inputBytes = new FileInfo(MemolineReadSession.ResolvePath(input)).Length, records = 0;
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".part";
        bool complete = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                MemolineFormat.WriteHeader(stream);
                foreach (var record in Read(input))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] json = Encoding.UTF8.GetBytes(record.GetRawText());
                    if (record.GetProperty("kind").GetString() == "header")
                    {
                        var header = JsonNode.Parse(json)!.AsObject();
                        header["version"] = MemolineFormat.CurrentVersion;
                        json = JsonSerializer.SerializeToUtf8Bytes(header);
                    }
                    complete = record.GetProperty("kind").GetString() == "footer";
                    MemolineFormat.WriteFrame(stream, json, CompressionLevel.Fastest);
                    records++;
                }
                if (!complete && !output.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An unfinished recording must be saved with a .part suffix.");
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, output);
            return new(inputBytes, new FileInfo(output).Length, records);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static void EnsureDifferentPaths(string input, string output)
    {
        string destination = Path.GetFullPath(output);
        if (Path.GetFullPath(input).Equals(destination, StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(MemolineReadSession.ResolvePath(input)).Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must differ from input.");
    }
}
