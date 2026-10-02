using System.Buffers.Binary;
using System.Text.Json;
using BehaviorRecognizer.Storage.Strokebin;
using DirtyMatrix.Core;

namespace DirtyMatrix.IO;

public sealed record InputPoint(double X, double Y, bool InContact);
public sealed record InputStroke(string Id, ulong StartTimestampMs, IReadOnlyList<InputPoint> Points);
public sealed record StrokeFile(InputSpace? DeclaredSpace, IReadOnlyList<InputStroke> Strokes);

public static class StrokeFiles
{
    public static StrokeFile Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 256L * 1024 * 1024) throw new InvalidDataException("单个输入文件超过 256 MB，请拆分后导入。");
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return ReadJson(path);
        // The linked recognizer reader recovers complete frames from a partially written file.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("STRO"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 1)
                throw new InvalidDataException("仅支持 BehaviorRecognizer 的 STRO v1 笔迹文件。");
        }
        // File.ReadAllBytes in the original parser cannot share an open writer's
        // write access. Copy a shared, length-bounded .part snapshot before parsing.
        var readPath = path;
        string? snapshot = null;
        if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            snapshot = Path.GetTempFileName();
            try
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = new FileStream(snapshot, FileMode.Create, FileAccess.Write, FileShare.None);
                var remaining = Math.Min(source.Length, 256L * 1024 * 1024);
                var buffer = new byte[65536];
                while (remaining > 0)
                {
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) break;
                    target.Write(buffer, 0, read); remaining -= read;
                }
                readPath = snapshot;
            }
            catch { File.Delete(snapshot); throw; }
        }
        BehaviorRecognizer.Abstractions.Stroke.RecordingSession session;
        try { session = StrokeBinaryReader.Read(readPath); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
        return new(null, session.Segments.SelectMany(s => s.Strokes).Select(s => new InputStroke(s.StrokeId.ToString(),
            s.StartTimestampMs, s.Points.Select(p => new InputPoint(p.X, p.Y, p.InContact)).ToArray())).ToArray());
    }

    private static StrokeFile ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        InputSpace? space = null;
        if (root.TryGetProperty("coordinateSpace", out var declared))
            space = declared.GetString() switch
            {
                "canvas" => InputSpace.CanvasPixels, "screen" => InputSpace.ScreenPixels, "tablet" => InputSpace.TabletRaw,
                _ => throw new InvalidDataException("coordinateSpace 须为 canvas、screen 或 tablet。")
            };
        var strokes = new List<InputStroke>();
        IEnumerable<JsonElement> source;
        if (root.TryGetProperty("segments", out var segments))
        {
            if (root.GetProperty("header").GetProperty("version").GetUInt32() != 1)
                throw new InvalidDataException("不支持的笔迹 JSON 版本。");
            source = segments.EnumerateArray().SelectMany(s => s.GetProperty("strokes").EnumerateArray());
        }
        else source = root.GetProperty("strokes").EnumerateArray();
        foreach (var stroke in source)
        {
            var id = stroke.TryGetProperty("strokeId", out var idElement) ? idElement.ToString() : (strokes.Count + 1).ToString();
            ulong timestamp = 0;
            if (stroke.TryGetProperty("startTimestamp", out var time))
                timestamp = time.ValueKind == JsonValueKind.Number ? time.GetUInt64() :
                    checked((ulong)DateTimeOffset.Parse(time.GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds());
            var points = stroke.GetProperty("points").EnumerateArray().Select(p => new InputPoint(
                p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(),
                !p.TryGetProperty("inContact", out var contact) || contact.GetBoolean())).ToArray();
            strokes.Add(new(id, timestamp, points));
        }
        if (strokes.Select(s => s.Id).Distinct().Count() != strokes.Count)
            throw new InvalidDataException("同一文件的 strokeId 必须唯一。");
        return new(space, strokes);
    }
}

public sealed class StrokeFolderFeed : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    public Task Completion { get; }
    public StrokeFolderFeed(string directory, Action<string, StrokeFile> onFile, Action<string> onError)
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Completion = Task.Run(async () =>
        {
            var signatures = new Dictionary<string, (long Size, DateTime Write)>(StringComparer.OrdinalIgnoreCase);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var newest = new DirectoryInfo(directory).EnumerateFiles().Where(f =>
                        f.Name.EndsWith(".strokebin", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.EndsWith(".strokebin.part", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                    if (newest is not null)
                    {
                        var stamp = (newest.Length, newest.LastWriteTimeUtc);
                        if (!signatures.TryGetValue(newest.FullName, out var previous) || previous != stamp)
                        {
                            var file = StrokeFiles.Read(newest.FullName);
                            onFile(newest.FullName, file with { Strokes = file.Strokes.Where(s => s.StartTimestampMs >= (ulong)since).ToArray() });
                            signatures[newest.FullName] = stamp;
                            if (signatures.Count > 100) signatures.Clear();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                { onError($"等待笔迹文件完整写入：{ex.Message}"); }
                try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _ = Completion.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
    }
}
