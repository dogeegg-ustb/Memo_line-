using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

public sealed record BundleSealResult(string Path, string MechanicalSha256, string AggregationSha256, string FixedSha256);
public sealed record BundleVerificationResult(string SessionId, long Frequency, string MechanicalSha256,
    string AggregationSha256, string FixedSha256, int PacketCount, int AssetCount, int DimensionSampleCount);

/// <summary>A sealed recording preserves its two source streams byte for byte. Only the active dimensions may be revised.</summary>
public static class BundleArchive
{
    public const string Schema = "memoline-csponly/v1";
    public const string MechanicalEntry = "mechanical/recording.memoline";
    public const string AggregationEntry = "aggregation/events.jsonl";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Continuities = ["actionPatternContinuity", "toolContinuity", "layerContinuity", "regionContinuity", "timeContinuity"];
    private sealed record EntryInfo(string Entry, string Sha256, long Size);
    private sealed record ArchiveIndex(string Schema, string SessionId, long Frequency, long OriginTicks,
        EntryInfo Mechanical, EntryInfo Aggregation, string FixedSha256, string ActiveDimensionsEntry, EntryInfo[] Dimensions);
    private sealed record MechanicalRecord(ulong AppendId, ulong EventId, ulong? OperationId, long Ticks, bool Hardware);
    private sealed record MechanicalInfo(string SessionId, long Frequency, long OriginTicks, long EndTicks,
        Dictionary<ulong, MechanicalRecord> Records);
    private sealed record AggregateInfo(string FixedSha256, int Packets, int Assets, int Dimensions);
    private sealed class AssetChunks(int count, string? totalSha256) : IDisposable
    {
        internal readonly int Count = count;
        internal readonly string? TotalSha256 = totalSha256;
        internal int Next;
        internal readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Dispose() => Hash.Dispose();
    }

    public static BundleSealResult Seal(string mechanicalPath, string aggregatePath, string destination,
        CancellationToken token = default)
    {
        mechanicalPath = ExistingPath(mechanicalPath); aggregatePath = ExistingPath(aggregatePath);
        destination = Destination(destination, mechanicalPath, aggregatePath);
        var mechanical = ValidateMechanical(mechanicalPath, token);
        AggregateInfo aggregate;
        using (var source = File.OpenRead(aggregatePath)) aggregate = ValidateAggregate(source, mechanical, false, null, token);
        var index = new ArchiveIndex(Schema, mechanical.SessionId, mechanical.Frequency, mechanical.OriginTicks,
            FileEntry(mechanicalPath, MechanicalEntry, token), FileEntry(aggregatePath, AggregationEntry, token),
            aggregate.FixedSha256, AggregationEntry, []);
        return WriteArchive(destination, index, archive =>
        {
            AddFile(archive, mechanicalPath, MechanicalEntry, token);
            AddFile(archive, aggregatePath, AggregationEntry, token);
        }, token);
    }

    public static BundleVerificationResult Verify(string path, CancellationToken token = default)
    {
        path = ExistingPath(path);
        using var archive = ZipFile.OpenRead(path);
        var index = ReadIndex(archive);
        ValidateArchiveEntries(archive, index);
        CheckEntry(archive, index.Mechanical, token); CheckEntry(archive, index.Aggregation, token);
        foreach (var dimension in index.Dimensions) CheckEntry(archive, dimension, token);
        var mechanical = WithMechanical(archive, temporary => ValidateMechanical(temporary, token), token);
        if (mechanical.SessionId != index.SessionId || mechanical.Frequency != index.Frequency || mechanical.OriginTicks != index.OriginTicks)
            throw new InvalidDataException("Container index does not match the native recording clock/session.");
        AggregateInfo aggregate;
        using (var source = RequiredEntry(archive, AggregationEntry).Open())
            aggregate = ValidateAggregate(source, mechanical, false, null, token);
        if (!HashEqual(aggregate.FixedSha256, index.FixedSha256)) throw new InvalidDataException("Fixed packets/assets hash mismatch.");
        int dimensionCount = aggregate.Dimensions;
        foreach (var revision in index.Dimensions)
        {
            using var source = RequiredEntry(archive, revision.Entry).Open();
            var revised = ValidateAggregate(source, mechanical, true, RevisionVersion(revision.Entry), token);
            if (revision.Entry == index.ActiveDimensionsEntry) dimensionCount = revised.Dimensions;
        }
        return new(mechanical.SessionId, mechanical.Frequency, index.Mechanical.Sha256, index.Aggregation.Sha256,
            index.FixedSha256, aggregate.Packets, aggregate.Assets, dimensionCount);
    }

    public static IEnumerable<JsonElement> ReadMechanical(string path)
    {
        Verify(path);
        using var archive = ZipFile.OpenRead(path);
        string directory = Path.Combine(Path.GetTempPath(), "memoline-read-" + Guid.NewGuid().ToString("N"));
        string temporary = Path.Combine(directory, "recording.memoline");
        Directory.CreateDirectory(directory);
        try
        {
            using (var source = RequiredEntry(archive, MechanicalEntry).Open())
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(target);
            foreach (var frame in MemolineReader.Read(temporary)) yield return frame;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); Directory.Delete(directory); }
    }

    public static IReadOnlyList<JsonElement> ReadDimensions(string path)
    {
        Verify(path);
        using var archive = ZipFile.OpenRead(path);
        var index = ReadIndex(archive);
        using var source = RequiredEntry(archive, index.ActiveDimensionsEntry).Open();
        return ReadRows(source, CancellationToken.None).Where(row => Text(row.Value, "kind") == "dimensionSample")
            .Select(row => row.Value.Clone()).OrderBy(row => Integer(row, "ticks")).ToArray();
    }

    public static BundleSealResult UpgradeDimensions(string input, string dimensionJsonl, string version, string output,
        CancellationToken token = default)
    {
        input = ExistingPath(input); dimensionJsonl = ExistingPath(dimensionJsonl);
        output = Destination(output, input, dimensionJsonl);
        if (!Regex.IsMatch(version ?? "", "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Version must be a safe, 1–64 character identifier.", nameof(version));
        Verify(input, token);
        using var original = ZipFile.OpenRead(input);
        var index = ReadIndex(original);
        string entry = "dimensions/" + version + ".jsonl";
        if (original.GetEntry(entry) is not null) throw new IOException("This dimensions version already exists.");
        WithMechanical(original, temporary =>
        {
            var mechanical = ValidateMechanical(temporary, token);
            using var source = File.OpenRead(dimensionJsonl);
            ValidateAggregate(source, mechanical, true, version, token);
            return true;
        }, token);
        var updated = index with { ActiveDimensionsEntry = entry,
            Dimensions = [.. index.Dimensions, FileEntry(dimensionJsonl, entry, token)] };
        return WriteArchive(output, updated, archive =>
        {
            foreach (var source in original.Entries.Where(e => e.FullName != "index.json"))
            {
                token.ThrowIfCancellationRequested();
                var target = archive.CreateEntry(source.FullName, CompressionLevel.Fastest);
                using var read = source.Open(); using var write = target.Open(); Copy(read, write, token);
            }
            AddFile(archive, dimensionJsonl, entry, token);
        }, token);
    }

    private static BundleSealResult WriteArchive(string destination, ArchiveIndex index, Action<ZipArchive> writeEntries,
        CancellationToken token)
    {
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string stage = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N") + ".memoline");
        try
        {
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    writeEntries(archive);
                    using var json = archive.CreateEntry("index.json", CompressionLevel.Fastest).Open();
                    JsonSerializer.Serialize(json, index, Json);
                }
                stream.Flush(flushToDisk: true);
            }
            Verify(stage, token);
            token.ThrowIfCancellationRequested();
            File.Move(stage, destination); // Never overwrite either a previous seal or a source stream.
            return new(destination, index.Mechanical.Sha256, index.Aggregation.Sha256, index.FixedSha256);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private static MechanicalInfo ValidateMechanical(string path, CancellationToken token)
    {
        string? session = null; long frequency = 0, origin = 0, end = -1, lastHardwareTicks = -1;
        ulong lastAppend = 0; bool footer = false;
        var records = new Dictionary<ulong, MechanicalRecord>(); var hardwareIds = new HashSet<ulong>();
        foreach (var frame in MemolineReader.Read(path))
        {
            token.ThrowIfCancellationRequested();
            string kind = Text(frame, "kind");
            if (footer) throw new InvalidDataException("Native recording contains data after its footer.");
            if (session is null)
            {
                if (kind != "header") throw new InvalidDataException("Native recording is missing its header.");
                session = Text(frame, "sessionId"); frequency = Integer(frame, "frequency");
                origin = Integer(frame, "originTicks");
                if (string.IsNullOrWhiteSpace(session) || frequency <= 0 || origin < 0) throw new InvalidDataException("Invalid native session clock.");
                continue;
            }
            if (kind == "header") throw new InvalidDataException("Duplicate native header.");
            if (kind == "footer")
            {
                end = Integer(frame, "ticks"); footer = true;
                if (end < lastHardwareTicks) throw new InvalidDataException("Native footer predates hardware input.");
                continue;
            }
            ulong append = Unsigned(frame, "appendId"), eventId = Unsigned(frame, "eventId");
            long ticks = Integer(frame, "ticks"); bool hardware = Text(frame, "path") == "hardware";
            ulong? operation = OptionalUnsigned(frame, "operationId");
            if (append == 0 || append <= lastAppend || ticks < 0) throw new InvalidDataException("Invalid native append order or event ticks.");
            if (hardware && (ticks < lastHardwareTicks || eventId == 0 || !hardwareIds.Add(eventId)))
                throw new InvalidDataException("Native hardware events violate the one-way timeline.");
            if (hardware) lastHardwareTicks = ticks;
            records.Add(append, new(append, eventId, operation, ticks, hardware)); lastAppend = append;
        }
        if (session is null || !footer) throw new InvalidDataException("Native recording has not been finalized.");
        if (records.Values.Any(record => record.Ticks > end)) throw new InvalidDataException("Native event is after the session footer.");
        return new(session, frequency, origin, end, records);
    }

    private static AggregateInfo ValidateAggregate(Stream source, MechanicalInfo mechanical, bool dimensionsOnly,
        string? version, CancellationToken token)
    {
        bool header = false, footer = false; int packets = 0, assets = 0, dimensions = 0;
        using var fixedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = new Dictionary<string, AssetChunks>();
        try
        {
        foreach (var (raw, row) in ReadRows(source, token))
        {
            string kind = Text(row, "kind");
            if (footer) throw new InvalidDataException("Aggregation contains data after its footer.");
            if (!header)
            {
                if (kind != "header" || Text(row, "sessionId") != mechanical.SessionId || Integer(row, "frequency") != mechanical.Frequency)
                    throw new InvalidDataException("Aggregation header does not match the native session/clock.");
                if (row.TryGetProperty("originTicks", out _) && Integer(row, "originTicks") != mechanical.OriginTicks)
                    throw new InvalidDataException("Aggregation clock origin does not match the native session.");
                header = true; continue;
            }
            if (kind == "header") throw new InvalidDataException("Duplicate aggregation header.");
            if (kind == "footer")
            {
                if (row.TryGetProperty("sessionId", out _) && Text(row, "sessionId") != mechanical.SessionId)
                    throw new InvalidDataException("Aggregation footer session mismatch.");
                footer = true; continue;
            }
            if (kind == "dimensionSample")
            {
                ValidateDimension(row, mechanical, version); dimensions++; continue;
            }
            if (dimensionsOnly) throw new InvalidDataException("A dimensions revision may only contain dimension samples.");
            if (kind == "packet")
            {
                if (Text(row, "sessionId") != mechanical.SessionId) throw new InvalidDataException("Packet session mismatch.");
                long from = Integer(row, "fromTicks"), to = Integer(row, "toTicks");
                if (from < 0 || to < from || to > mechanical.EndTicks) throw new InvalidDataException("Invalid packet time range.");
                ValidatePointers(Array(row, "eventPointers"), mechanical, from, to, hardware: true);
                foreach (var matrix in Array(row, "dirtyMatrices").EnumerateArray())
                {
                    ValidatePointers(Array(matrix, "eventPointers"), mechanical, from, to, hardware: true);
                    if (matrix.TryGetProperty("statePointers", out var statePointers))
                    {
                        if (statePointers.ValueKind != JsonValueKind.Array) throw new InvalidDataException("statePointers must be an array.");
                        ValidatePointers(statePointers, mechanical, from, to, hardware: false);
                    }
                }
                packets++;
            }
            else if (kind == "asset")
            {
                if (row.TryGetProperty("sessionId", out _) && Text(row, "sessionId") != mechanical.SessionId)
                    throw new InvalidDataException("Asset session mismatch.");
                string id = Text(row, "assetId"); if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Empty asset ID.");
                byte[] decoded;
                try { decoded = Convert.FromBase64String(Text(row, "base64")); }
                catch (FormatException ex) { throw new InvalidDataException("Invalid asset encoding.", ex); }
                if (!HashEqual(Hash(decoded), Text(row, "sha256"))) throw new InvalidDataException("Asset hash mismatch.");
                int chunk = checked((int)OptionalInteger(row, "chunkIndex", 0));
                int count = checked((int)OptionalInteger(row, "chunkCount", 1));
                if (count <= 0 || chunk < 0 || chunk >= count) throw new InvalidDataException("Invalid asset chunk range.");
                string? totalSha = row.TryGetProperty("totalSha256", out var total) && total.ValueKind != JsonValueKind.Null
                    ? Text(row, "totalSha256") : null;
                if (!chunks.TryGetValue(id, out var existing)) chunks.Add(id, existing = new(count, totalSha));
                if (existing.Count != count || existing.Next != chunk || existing.TotalSha256 != totalSha)
                    throw new InvalidDataException("Duplicate, unordered or inconsistent asset chunks.");
                existing.Hash.AppendData(decoded); existing.Next++;
                assets++;
            }
            else throw new InvalidDataException("Unknown aggregation record: " + kind);
            // The fixed digest is over the original packet/asset UTF-8 JSON line, followed by LF.
            fixedHash.AppendData(raw); fixedHash.AppendData([10]);
        }
        if (!header || !footer) throw new InvalidDataException("Aggregation has not been finalized.");
        if (chunks.Values.Any(chunk => chunk.Next != chunk.Count)) throw new InvalidDataException("Asset is missing one or more chunks.");
        foreach (var chunk in chunks.Values)
            if (chunk.TotalSha256 is not null && !HashEqual(Convert.ToHexString(chunk.Hash.GetHashAndReset()), chunk.TotalSha256))
                throw new InvalidDataException("Complete asset hash mismatch.");
        return new(Convert.ToHexString(fixedHash.GetHashAndReset()).ToLowerInvariant(), packets, assets, dimensions);
        }
        finally { foreach (var chunk in chunks.Values) chunk.Dispose(); }
    }

    private static void ValidateDimension(JsonElement row, MechanicalInfo mechanical, string? version)
    {
        if (Text(row, "sessionId") != mechanical.SessionId) throw new InvalidDataException("Dimension sample session mismatch.");
        long ticks = Integer(row, "ticks");
        if (ticks < 0 || ticks > mechanical.EndTicks) throw new InvalidDataException("Dimension sample is outside the native session timeline.");
        long viewport = Integer(row, "canvasViewportChanged");
        if (viewport is not (0 or 1)) throw new InvalidDataException("canvasViewportChanged must be 0 or 1.");
        string algorithm = Text(row, "algorithmVersion");
        if (string.IsNullOrWhiteSpace(algorithm) || (version is not null && algorithm != version))
            throw new InvalidDataException("Dimension algorithm version mismatch.");
        foreach (string name in Continuities)
        {
            var value = Property(row, name);
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double numeric) || !double.IsFinite(numeric))
                throw new InvalidDataException(name + " must be null or a finite numeric value.");
        }
    }

    private static void ValidatePointers(JsonElement pointers, MechanicalInfo mechanical, long from, long to, bool hardware)
    {
        foreach (var pointer in pointers.EnumerateArray())
        {
            ulong append = Unsigned(pointer, "appendId");
            if (Text(pointer, "sessionId") != mechanical.SessionId || !mechanical.Records.TryGetValue(append, out var original))
                throw new InvalidDataException("Mechanical event pointer cannot be resolved.");
            if (original.Hardware != hardware) throw new InvalidDataException("Event pointer is in the wrong hardware/state category.");
            long ticks = Integer(pointer, "ticks");
            bool context = pointer.TryGetProperty("context", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (ticks != original.Ticks || ticks > to || (!context && ticks < from))
                throw new InvalidDataException("Mechanical event pointer ticks do not match its native record/window.");
            if (pointer.TryGetProperty("eventId", out var eventId) && eventId.ValueKind != JsonValueKind.Null
                && (!eventId.TryGetUInt64(out ulong id) || id != original.EventId))
                throw new InvalidDataException("Mechanical event pointer eventId mismatch.");
            if (pointer.TryGetProperty("operationId", out var operation) && operation.ValueKind != JsonValueKind.Null
                && (!operation.TryGetUInt64(out ulong operationId) || original.OperationId != operationId))
                throw new InvalidDataException("Mechanical event pointer operationId mismatch.");
        }
    }

    private static IEnumerable<(byte[] Raw, JsonElement Value)> ReadRows(Stream source, CancellationToken token)
    {
        using var reader = new StreamReader(source, Utf8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Blank JSONL records are not permitted.");
            if (line.Length > 64 * 1024 * 1024) throw new InvalidDataException("Aggregation record exceeds 64 MiB.");
            byte[] raw = Utf8.GetBytes(line);
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Aggregation record must be a JSON object.");
            yield return (raw, document.RootElement.Clone());
        }
    }

    private static ArchiveIndex ReadIndex(ZipArchive archive)
    {
        if (archive.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Duplicate container entries.");
        var entry = RequiredEntry(archive, "index.json");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("Container index exceeds 1 MiB.");
        using var stream = entry.Open();
        var index = JsonSerializer.Deserialize<ArchiveIndex>(stream, Json) ?? throw new InvalidDataException("Missing container index.");
        if (index.Schema != Schema || index.Mechanical is null || index.Aggregation is null || index.Dimensions is null
            || index.Mechanical.Entry != MechanicalEntry || index.Aggregation.Entry != AggregationEntry)
            throw new InvalidDataException("Unsupported container schema or stream entries.");
        return index;
    }

    private static void ValidateArchiveEntries(ZipArchive archive, ArchiveIndex index)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { "index.json", MechanicalEntry, AggregationEntry };
        foreach (var revision in index.Dimensions)
        {
            if (revision is null) throw new InvalidDataException("Missing dimensions entry metadata.");
            RevisionVersion(revision.Entry);
            if (!expected.Add(revision.Entry)) throw new InvalidDataException("Duplicate dimensions entry metadata.");
        }
        if (!expected.Contains(index.ActiveDimensionsEntry) || index.ActiveDimensionsEntry is "index.json" or MechanicalEntry)
            throw new InvalidDataException("Invalid active dimensions entry.");
        if (archive.Entries.Count != expected.Count || archive.Entries.Any(entry => !expected.Contains(entry.FullName)))
            throw new InvalidDataException("Unexpected or missing container entries.");
    }

    private static string RevisionVersion(string entry)
    {
        var match = Regex.Match(entry ?? "", "^dimensions/([A-Za-z0-9][A-Za-z0-9._-]{0,63})\\.jsonl$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("Unsafe dimensions entry name.");
        return match.Groups[1].Value;
    }

    private static void CheckEntry(ZipArchive archive, EntryInfo expected, CancellationToken token)
    {
        var entry = RequiredEntry(archive, expected.Entry);
        if (expected.Size < 0 || entry.Length != expected.Size) throw new InvalidDataException("Container entry size mismatch.");
        using var stream = entry.Open();
        if (!HashEqual(Hash(stream, token), expected.Sha256)) throw new InvalidDataException("Container entry hash mismatch: " + expected.Entry);
    }

    private static T WithMechanical<T>(ZipArchive archive, Func<string, T> read, CancellationToken token = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), "memoline-read-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "recording.memoline");
        Directory.CreateDirectory(directory);
        try
        {
            using (var source = RequiredEntry(archive, MechanicalEntry).Open())
            using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Copy(source, target, token);
            return read(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
    }

    private static EntryInfo FileEntry(string path, string entry, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        return new(entry, Hash(stream, token), stream.Length);
    }
    private static void AddFile(ZipArchive archive, string path, string name, CancellationToken token)
    {
        using var source = File.OpenRead(path);
        using var target = archive.CreateEntry(name, name == MechanicalEntry ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
        Copy(source, target, token);
    }
    private static void Copy(Stream source, Stream target, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024]; int count;
        while ((count = source.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); target.Write(buffer, 0, count); }
    }
    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024]; int count;
        while ((count = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool HashEqual(string first, string second) => !string.IsNullOrWhiteSpace(second)
        && first.Equals(second, StringComparison.OrdinalIgnoreCase);
    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) => archive.GetEntry(name)
        ?? throw new InvalidDataException("Missing container entry: " + name);
    private static string ExistingPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Recording input does not exist.", path);
        return path;
    }
    private static string Destination(string path, params string[] sources)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Destination already exists.");
        if (sources.Any(source => path.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Destination must differ from every input.");
        return path;
    }
    private static JsonElement Property(JsonElement row, string name) => row.TryGetProperty(name, out var value)
        ? value : throw new InvalidDataException("Missing field: " + name);
    private static JsonElement Array(JsonElement row, string name)
    {
        var value = Property(row, name);
        return value.ValueKind == JsonValueKind.Array ? value : throw new InvalidDataException(name + " must be an array.");
    }
    private static string Text(JsonElement row, string name)
    {
        var value = Property(row, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException(name + " must be a string.");
    }
    private static long Integer(JsonElement row, string name)
    {
        var value = Property(row, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number : throw new InvalidDataException(name + " must be an integer.");
    }
    private static ulong Unsigned(JsonElement row, string name)
    {
        var value = Property(row, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)
            ? number : throw new InvalidDataException(name + " must be an unsigned integer.");
    }
    private static ulong? OptionalUnsigned(JsonElement row, string name) => !row.TryGetProperty(name, out var value)
        || value.ValueKind == JsonValueKind.Null ? null : Unsigned(row, name);
    private static long OptionalInteger(JsonElement row, string name, long fallback) => !row.TryGetProperty(name, out var value)
        || value.ValueKind == JsonValueKind.Null ? fallback : Integer(row, name);
}
