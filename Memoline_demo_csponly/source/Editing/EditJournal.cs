using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemolineDemo.Editing;

public enum EditJournalOperation { Compose, Replay, Capture }
// The journal records execution, not proof that a later brush replay matched pixels.
public enum EditJournalOutcome { Executed, Failed, Cancelled }
public sealed record EditJournalOverride(EditableStrokeKey Key, EventChange Change);
public sealed record EditJournalAsset(string Sha256, int Width, int Height, long ByteLength, string RelativePath);
public sealed record EditJournalCheckpoint(long Sequence, string RecordSha256, EditJournalAsset Asset, string WorkLayerUuid);
public sealed record EditJournalRecord(string Schema, long Sequence, string TransactionId, string Kind,
    DateTimeOffset WrittenUtc, JsonElement Data, string PreviousSha256, string Sha256);
public sealed record EditJournalSnapshot(IReadOnlyList<EditJournalRecord> Records, IReadOnlyList<EditJournalAsset> Assets,
    long ValidByteLength, bool HasIncompleteTail, string? ActiveTransactionId,
    EditJournalCheckpoint? LastCheckpoint, bool HasReplayed);

/// <summary>
/// One durable writer for an append-only editing ledger. Owns only its journal
/// and content-addressed PNGs; mechanical recordings and aggregate packets are
/// never opened for writing. CSP actions and their identity checks belong to the
/// transaction adapter. A recovered active transaction must be handled explicitly.
/// </summary>
public sealed class EditJournal : IDisposable
{
    public const string Schema = "memoline-edit-journal/v1";
    public const string JournalFileName = "edit-journal.jsonl";
    public const string AssetEntryPrefix = "edits/assets/";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _sync = new();
    private readonly FileStream _stream;
    private State _state;
    private bool _disposed, _poisoned;
    public string WorkingDirectory { get; }
    public string JournalPath => Path.Combine(WorkingDirectory, JournalFileName);

    private EditJournal(string directory, FileStream stream, EditJournalSnapshot snapshot)
    {
        WorkingDirectory = directory; _stream = stream;
        _state = new State();
        foreach (EditJournalRecord record in snapshot.Records) _state = Apply(_state, record, _ => { });
    }

    /// <summary>Exclusively opens a writer; only an unterminated final row is truncated.</summary>
    public static EditJournal Open(string workingDirectory)
    {
        string root = PrepareDirectory(workingDirectory);
        string path = Path.Combine(root, JournalFileName);
        RejectLink(path, root);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            EditJournalSnapshot snapshot = Recover(root);
            if (snapshot.HasIncompleteTail) { stream.SetLength(snapshot.ValidByteLength); stream.Flush(flushToDisk: true); }
            stream.Position = stream.Length;
            return new EditJournal(root, stream, snapshot);
        }
        catch { stream.Dispose(); throw; }
    }

    public EditJournalSnapshot Snapshot { get { lock (_sync) { ActiveWriter(); return Recover(WorkingDirectory); } } }

    /// <summary>Read-only recovery. A complete invalid row is always rejected, including the last row.</summary>
    public static EditJournalSnapshot Recover(string workingDirectory)
    {
        string root = Path.GetFullPath(workingDirectory);
        string path = Path.Combine(root, JournalFileName);
        RejectLink(path, root);
        if (!File.Exists(path)) return EmptySnapshot();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(input, asset => ReadAsset(root, asset));
    }

    /// <summary>Reads the same ledger from a bundle stream without extracting or rewriting original streams.</summary>
    public static EditJournalSnapshot Read(Stream input, Func<EditJournalAsset, byte[]> readAsset)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(readAsset);
        var records = new List<EditJournalRecord>();
        var assets = new Dictionary<string, EditJournalAsset>(StringComparer.Ordinal);
        var state = new State();
        using var row = new MemoryStream();
        long consumed = 0, validLength = 0;
        // Whole rows are cut from a buffered copy: reading byte by byte took most of a recovery.
        using var buffered = new MemoryStream();
        input.CopyTo(buffered);
        var journal = buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length));
        void Verify(EditJournalAsset asset)
        {
            ValidateAssetReference(asset);
            if (assets.TryGetValue(asset.Sha256, out EditJournalAsset? previous))
            {
                Require(previous == asset, "Inconsistent metadata for a deduplicated PNG asset");
                return;
            }
            byte[] bytes = readAsset(asset);
            // The SHA-256 proves these are the bytes that were decoded when the asset was stored; recovery reads
            // every asset of every earlier transaction and does not decode them again.
            ValidateAssetBytes(asset, bytes, decode: false);
            assets.Add(asset.Sha256, asset);
        }
        while (consumed < journal.Length)
        {
            int end = journal[(int)consumed..].IndexOf((byte)'\n');
            if (end < 0) { row.Write(journal[(int)consumed..]); consumed = journal.Length; break; }
            row.Write(journal.Slice((int)consumed, end)); consumed += end + 1;
            Require(row.Length > 0, "Committed journal row is empty");
            EditJournalRecord record;
            try
            {
                using JsonDocument document = JsonDocument.Parse(Utf8.GetString(row.GetBuffer(), 0, checked((int)row.Length)));
                RejectDuplicateProperties(document.RootElement);
                Require(document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.EnumerateObject().Count() == 8
                    && new[] { "schema", "sequence", "transactionId", "kind", "writtenUtc", "data", "previousSha256", "sha256" }
                        .All(name => document.RootElement.TryGetProperty(name, out _)), "Unknown or missing journal envelope field");
                record = document.RootElement.Deserialize<EditJournalRecord>(Json)
                    ?? throw new InvalidDataException("Journal row is null");
                Require(record.Schema == Schema && record.Sequence == state.Sequence + 1,
                    "Unsupported journal schema or discontinuous sequence");
                Require(record.PreviousSha256 == state.LastRecordSha256 && IsHash(record.Sha256)
                    && RecordHash(record) == record.Sha256, "Journal record hash chain is invalid");
                state = Apply(state, record, Verify);
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or KeyNotFoundException
                or InvalidOperationException or ArgumentException or OverflowException)
            { throw new InvalidDataException($"Invalid committed journal row {records.Count + 1}", error); }
            records.Add(record);
            validLength = consumed; row.SetLength(0); row.Position = 0;
        }
        return new EditJournalSnapshot(records.AsReadOnly(), assets.Values.ToList().AsReadOnly(), validLength,
            row.Length != 0, state.TransactionId, state.Checkpoint, state.HasReplayed);
    }

    public void Start(string transactionId, LayerKey targetLayer) => Start(transactionId, new[] { targetLayer });

    public void Start(string transactionId, IReadOnlyList<LayerKey> involvedLayers)
    {
        ArgumentNullException.ThrowIfNull(involvedLayers);
        lock (_sync) Append("transactionBegan", new { targetLayers = involvedLayers }, transactionId);
    }

    public string FreezePlan(JsonElement fullPlan)
    {
        lock (_sync)
        {
            string hash = Hash(JsonSerializer.SerializeToUtf8Bytes(fullPlan, Json));
            Append("planFrozen", new { plan = fullPlan, planSha256 = hash });
            return hash;
        }
    }

    public void StartSegment(string segmentId, EditJournalOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        lock (_sync) Append("segmentStarted", new { segmentId, operation = operation.ToString() });
    }

    public EditJournalAsset Compose(string segmentId, byte[] png, int width, int height)
    {
        lock (_sync)
        {
            ActiveWriter(); ExpectSegment(_state, segmentId, EditJournalOperation.Compose);
            EditJournalAsset asset = StorePng(png, width, height);
            Append("composeCompleted", new { segmentId, asset }); return asset;
        }
    }

    public void Replay(string segmentId, IReadOnlyList<EditableStrokeKey> eventKeys,
        IReadOnlyList<EditJournalOverride>? overrides = null)
    {
        lock (_sync) Append("replayCompleted", new { segmentId, eventKeys, overrides = OverrideData(overrides) });
    }

    public EditJournalAsset Capture(string segmentId, string workLayerUuid, byte[] png, int width, int height)
    {
        lock (_sync)
        {
            ActiveWriter(); ExpectSegment(_state, segmentId, EditJournalOperation.Capture); Text(workLayerUuid);
            EditJournalAsset asset = StorePng(png, width, height);
            Append("captureCompleted", new { segmentId, workLayerUuid, asset }); return asset;
        }
    }

    /// <summary>Returns only after durable append and whole-ledger/asset readback validation.</summary>
    public EditJournalCheckpoint CheckpointFlushed(string workLayerUuid, EditJournalAsset asset,
        IReadOnlyList<EditableStrokeKey> eventKeys, IReadOnlyList<EditJournalOverride>? overrides = null)
    {
        lock (_sync)
        {
            EditJournalRecord record = Append("checkpoint", new { workLayerUuid, asset, eventKeys, overrides = OverrideData(overrides) });
            var receipt = new EditJournalCheckpoint(record.Sequence, record.Sha256, asset, workLayerUuid);
            try { Require(Recover(WorkingDirectory).LastCheckpoint == receipt, "Checkpoint readback mismatch"); }
            catch { _poisoned = true; throw; }
            return receipt;
        }
    }

    public void LayerHidden(string layerUuid) { lock (_sync) Append("layerHidden", new { layerUuid }); }
    public void WorkLayerDeleted(EditJournalCheckpoint checkpoint)
    {
        lock (_sync) Append("workLayerDeleted", new { checkpoint });
    }
    /// <summary>The original target was removed from CSP; its pixels and properties are in the recycle bin.</summary>
    /// <param name="replacementUuid">Null when the original is deleted before its replacement is pasted.</param>
    public void OriginalDeleted(string layerUuid, string? replacementUuid)
    {
        lock (_sync) Append("originalDeleted", new { layerUuid, replacementUuid });
    }
    public void ReproductionInstalled(string layerUuid, EditJournalAsset asset)
    {
        lock (_sync) Append("reproductionInstalled", new { layerUuid, asset });
    }
    public void Fail(string code, string message)
    {
        lock (_sync) Append("failure", new { code, message, lastCheckpoint = _state.Checkpoint });
    }
    public void Finish(EditJournalOutcome outcome)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        lock (_sync) Append("transactionEnded", new { outcome = outcome.ToString(), hasReplayed = _state.HasReplayed });
    }

    private EditJournalRecord Append(string kind, object data, string? transactionId = null)
    {
        ActiveWriter();
        var record = new EditJournalRecord(Schema, checked(_state.Sequence + 1), transactionId ?? _state.TransactionId ?? "",
            kind, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data, Json), _state.LastRecordSha256, "");
        record = record with { Sha256 = RecordHash(record) };
        // Reject before touching the file. State advances only after the durable write succeeds.
        State next = Apply(_state, record, asset => ValidateAssetBytes(asset, ReadAsset(WorkingDirectory, asset)));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        try
        {
            _stream.Write(bytes); _stream.WriteByte((byte)'\n'); _stream.Flush(flushToDisk: true);
            _state = next;
        }
        catch { _poisoned = true; throw; }
        return record;
    }

    private EditJournalAsset StorePng(byte[] png, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        var asset = new EditJournalAsset(Hash(png), width, height, png.LongLength, AssetEntryPrefix + Hash(png) + ".png");
        ValidateAssetBytes(asset, png);
        string path = AssetPath(WorkingDirectory, asset);
        if (File.Exists(path)) { ValidateAssetBytes(asset, ReadAsset(WorkingDirectory, asset)); return asset; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(png); file.Flush(flushToDisk: true); }
            File.Move(temporary, path);
            ValidateAssetBytes(asset, ReadAsset(WorkingDirectory, asset));
            return asset;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record State(long Sequence = 0, string LastRecordSha256 = "", string? TransactionId = null,
        ImmutableHashSet<string>? OriginalLayerUuids = null, bool PlanFrozen = false, bool Failed = false, bool HasReplayed = false,
        string? SegmentId = null, EditJournalOperation? Operation = null, ImmutableHashSet<string>? SegmentIds = null,
        EditJournalAsset? CapturedAsset = null, string? CapturedLayerUuid = null,
        EditJournalCheckpoint? Checkpoint = null, bool Installed = false);

    private static State Apply(State state, EditJournalRecord record, Action<EditJournalAsset> verify)
    {
        Text(record.TransactionId); Text(record.Kind);
        Require(record.Data.ValueKind == JsonValueKind.Object, "Journal data must be an object");
        JsonElement data = record.Data;
        State next = state;
        if (record.Kind == "transactionBegan")
        {
            Require(state.TransactionId is null, "An unfinished edit transaction already exists");
            // Keep reading previously written single-target rows; new rows freeze all participants.
            bool multiple = data.TryGetProperty("targetLayers", out JsonElement targets);
            Require(multiple != data.TryGetProperty("targetLayer", out _), "Transaction needs exactly one target layer representation");
            LayerKey[] layers = multiple ? Required<LayerKey[]>(data, "targetLayers") : [Required<LayerKey>(data, "targetLayer")];
            Require(layers.Length > 0, "Transaction has no target layers");
            var originalUuids = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (LayerKey layer in layers)
            {
                Text(layer.SessionId); Text(layer.Uuid);
                Require(layer.Generation >= 0 && originalUuids.Add(layer.Uuid), "Invalid or duplicate original layer identity");
            }
            next = new State(TransactionId: record.TransactionId, OriginalLayerUuids: originalUuids.ToImmutable(),
                SegmentIds: ImmutableHashSet<string>.Empty);
        }
        else
        {
            Require(state.TransactionId == record.TransactionId, "Action does not belong to the active transaction");
            if (record.Kind is not ("failure" or "transactionEnded")) Require(!state.Failed, "No action is allowed after failure");
            if (record.Kind is not ("planFrozen" or "failure" or "transactionEnded"))
                Require(state.PlanFrozen, "Freeze the complete plan before execution");
            switch (record.Kind)
            {
                case "planFrozen":
                    Require(!state.PlanFrozen, "Plan was already frozen");
                    JsonElement plan = data.GetProperty("plan");
                    Require(plan.ValueKind == JsonValueKind.Object, "Full plan must be a JSON object");
                    RejectDuplicateProperties(plan);
                    Require(GetText(data, "planSha256") == Hash(JsonSerializer.SerializeToUtf8Bytes(plan, Json)), "Frozen plan hash mismatch");
                    next = state with { PlanFrozen = true }; break;
                case "segmentStarted":
                    Require(state.SegmentId is null, "Previous segment is unfinished");
                    string segment = GetText(data, "segmentId");
                    Require(Enum.TryParse(GetText(data, "operation"), out EditJournalOperation operation) && Enum.IsDefined(operation), "Unknown segment operation");
                    Require(!state.SegmentIds!.Contains(segment), "Segment cannot be resumed or repeated implicitly");
                    next = state with { SegmentId = segment, Operation = operation, SegmentIds = state.SegmentIds.Add(segment),
                        HasReplayed = state.HasReplayed || operation == EditJournalOperation.Replay }; break;
                case "composeCompleted":
                    ExpectSegment(state, GetText(data, "segmentId"), EditJournalOperation.Compose);
                    verify(Required<EditJournalAsset>(data, "asset"));
                    next = state with { SegmentId = null, Operation = null }; break;
                case "replayCompleted":
                    ExpectSegment(state, GetText(data, "segmentId"), EditJournalOperation.Replay);
                    ValidateEvents(data);
                    next = state with { SegmentId = null, Operation = null }; break;
                case "captureCompleted":
                    ExpectSegment(state, GetText(data, "segmentId"), EditJournalOperation.Capture);
                    EditJournalAsset captured = Required<EditJournalAsset>(data, "asset"); verify(captured);
                    Require(!state.OriginalLayerUuids!.Contains(GetText(data, "workLayerUuid")), "An original target cannot become a work layer");
                    next = state with { SegmentId = null, Operation = null, CapturedAsset = captured,
                        CapturedLayerUuid = GetText(data, "workLayerUuid") }; break;
                case "checkpoint":
                    Require(state.SegmentId is null, "Cannot checkpoint an unfinished segment");
                    EditJournalAsset asset = Required<EditJournalAsset>(data, "asset");
                    string uuid = GetText(data, "workLayerUuid");
                    Require(state.CapturedAsset == asset && state.CapturedLayerUuid == uuid, "Checkpoint needs the completed capture of this work layer");
                    ValidateEvents(data); verify(asset);
                    next = state with { Checkpoint = new EditJournalCheckpoint(record.Sequence, record.Sha256, asset, uuid) }; break;
                case "layerHidden":
                    Require(state.OriginalLayerUuids!.Contains(GetText(data, "layerUuid")), "Hidden layer is outside the frozen targets"); break;
                case "workLayerDeleted":
                    EditJournalCheckpoint checkpoint = Required<EditJournalCheckpoint>(data, "checkpoint");
                    Require(state.Checkpoint == checkpoint && state.SegmentId is null, "Deletion requires this transaction's latest durable checkpoint");
                    Require(!state.OriginalLayerUuids!.Contains(checkpoint.WorkLayerUuid), "An original target cannot be deleted as a work layer");
                    verify(checkpoint.Asset); break;
                case "originalDeleted":
                    Require(state.SegmentId is null, "Cannot delete an original during an unfinished segment");
                    Require(state.OriginalLayerUuids!.Contains(GetText(data, "layerUuid")), "Only a frozen original target can be deleted");
                    Require(data.GetProperty("replacementUuid").ValueKind == JsonValueKind.Null ||
                        !state.OriginalLayerUuids!.Contains(GetText(data, "replacementUuid")), "An original target cannot replace another"); break;
                case "reproductionInstalled":
                    Require(state.SegmentId is null, "Cannot install reproduction during an unfinished segment");
                    Require(!state.OriginalLayerUuids!.Contains(GetText(data, "layerUuid")), "An original target cannot become a reproduction layer");
                    verify(Required<EditJournalAsset>(data, "asset"));
                    next = state with { Installed = true }; break;
                case "failure":
                    Require(!state.Failed, "Failure was already recorded");
                    Text(GetText(data, "code"));
                    Require(data.GetProperty("message").ValueKind == JsonValueKind.String, "Failure message must be a string");
                    EditJournalCheckpoint? last = data.GetProperty("lastCheckpoint").ValueKind == JsonValueKind.Null
                        ? null : Required<EditJournalCheckpoint>(data, "lastCheckpoint");
                    Require(last == state.Checkpoint, "Failure references an incorrect checkpoint");
                    next = state with { Failed = true, SegmentId = null, Operation = null }; break;
                case "transactionEnded":
                    Require(Enum.TryParse(GetText(data, "outcome"), out EditJournalOutcome outcome) && Enum.IsDefined(outcome), "Unknown execution outcome");
                    Require(data.GetProperty("hasReplayed").GetBoolean() == state.HasReplayed, "Replay evidence cannot be removed from the final outcome");
                    if (outcome == EditJournalOutcome.Executed)
                        Require(!state.Failed && state.PlanFrozen && state.SegmentId is null && state.Installed, "Success needs a completed reproduction and no failure");
                    else Require(state.Failed, "A failed or cancelled transaction needs a failure record");
                    next = state with { TransactionId = null }; break;
                default: throw new InvalidDataException("Unsupported journal record kind: " + record.Kind);
            }
        }
        return next with { Sequence = record.Sequence, LastRecordSha256 = record.Sha256 };
    }

    private static void ExpectSegment(State state, string id, EditJournalOperation operation)
    {
        Require(!state.Failed && state.TransactionId is not null && state.PlanFrozen
            && state.SegmentId == id && state.Operation == operation, "Operation does not match the active segment");
    }

    private static object[] OverrideData(IReadOnlyList<EditJournalOverride>? overrides)
    {
        return overrides?.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(item.Change);
            foreach (var number in item.Change.NumberValues) { Text(number.Key); Require(double.IsFinite(number.Value), "Parameter override must be finite"); }
            return (object)new { key = item.Key, delete = item.Change.Delete, brushName = item.Change.BrushName, numbers = item.Change.NumberValues };
        }).ToArray() ?? [];
    }

    private static void ValidateEvents(JsonElement data)
    {
        JsonElement keys = data.GetProperty("eventKeys"), overrides = data.GetProperty("overrides");
        Require(keys.ValueKind == JsonValueKind.Array && overrides.ValueKind == JsonValueKind.Array, "Event keys/overrides must be arrays");
        var seen = new HashSet<EditableStrokeKey>();
        foreach (JsonElement item in keys.EnumerateArray())
        {
            EditableStrokeKey key = item.Deserialize<EditableStrokeKey>(Json);
            Text(key.SessionId); Require(key.BeginAppendId > 0 && seen.Add(key), "Invalid or duplicate editable event key");
        }
        var changed = new HashSet<EditableStrokeKey>();
        foreach (JsonElement item in overrides.EnumerateArray())
        {
            EditableStrokeKey key = Required<EditableStrokeKey>(item, "key");
            Require(seen.Contains(key) && changed.Add(key), "Override key is absent or duplicated");
            _ = item.GetProperty("delete").GetBoolean();
            JsonElement brush = item.GetProperty("brushName");
            Require(brush.ValueKind == JsonValueKind.Null || brush.ValueKind == JsonValueKind.String, "Invalid brush override");
            if (brush.ValueKind == JsonValueKind.String) Text(brush.GetString());
            JsonElement numbers = item.GetProperty("numbers"); Require(numbers.ValueKind == JsonValueKind.Object, "Number overrides must be an object");
            foreach (JsonProperty number in numbers.EnumerateObject())
            { Text(number.Name); Require(number.Value.TryGetDouble(out double value) && double.IsFinite(value), "Parameter override must be finite"); }
        }
    }

    private static string PrepareDirectory(string directory)
    {
        string root = Path.GetFullPath(directory);
        RejectLink(root, root); Directory.CreateDirectory(root); RejectLink(root, root);
        foreach (string relative in new[] { "edits", Path.Combine("edits", "assets") })
        {
            string path = Path.Combine(root, relative); RejectLink(path, root);
            Directory.CreateDirectory(path); RejectLink(path, root);
        }
        return root;
    }

    private static string AssetPath(string root, EditJournalAsset asset)
    {
        ValidateAssetReference(asset);
        string path = Path.Combine(root, asset.RelativePath);
        RejectLink(path, root); return path;
    }
    private static byte[] ReadAsset(string root, EditJournalAsset asset) => File.ReadAllBytes(AssetPath(root, asset));
    private static void RejectLink(string path, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = Path.GetFullPath(path);
        Require(path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Asset path escapes the journal directory");
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            if (File.Exists(part) || Directory.Exists(part))
                Require((File.GetAttributes(part) & FileAttributes.ReparsePoint) == 0, "Journal paths cannot be links/reparse points");
            if (part.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    private static void ValidateAssetReference(EditJournalAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Require(IsHash(asset.Sha256) && asset.Width > 0 && asset.Height > 0 && asset.ByteLength > 0
            && asset.RelativePath == AssetEntryPrefix + asset.Sha256 + ".png", "Invalid content-addressed PNG reference");
    }
    private static void ValidateAssetBytes(EditJournalAsset asset, byte[] png, bool decode = true)
    {
        ValidateAssetReference(asset); ArgumentNullException.ThrowIfNull(png);
        Require(asset.ByteLength == png.LongLength && Hash(png) == asset.Sha256, "PNG asset length/hash mismatch");
        Require(png.Length >= 33 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Asset is not a PNG");
        bool ihdr = false, idat = false, ended = false;
        for (int offset = 8; offset < png.Length;)
        {
            Require(png.Length - offset >= 12, "Truncated PNG chunk");
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            Require(rawLength <= int.MaxValue && rawLength <= png.Length - offset - 12, "Truncated PNG chunk data");
            int length = (int)rawLength;
            ReadOnlySpan<byte> typeAndData = png.AsSpan(offset + 4, length + 4);
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
            Require(Crc32(typeAndData) == expected, "PNG chunk CRC mismatch");
            string kind = Encoding.ASCII.GetString(typeAndData[..4]);
            if (!ihdr)
            {
                Require(kind == "IHDR" && length == 13, "PNG must start with IHDR");
                Require(BinaryPrimitives.ReadUInt32BigEndian(typeAndData.Slice(4, 4)) == asset.Width
                    && BinaryPrimitives.ReadUInt32BigEndian(typeAndData.Slice(8, 4)) == asset.Height, "PNG dimensions differ from asset metadata");
                ihdr = true;
            }
            else Require(kind != "IHDR", "Duplicate PNG header");
            if (kind == "IDAT") idat = true;
            offset += length + 12;
            if (kind == "IEND") { Require(length == 0 && offset == png.Length && idat, "Invalid PNG end/data"); ended = true; break; }
        }
        Require(ended, "PNG has no complete IEND");
        // Decode only for validity/dimensions. Keep the original PNG byte array and never re-encode it. The bytes
        // were just proven to have this SHA-256, so a hash this process already decoded is not decoded again:
        // every append and recovery re-validates each asset of the journal, ~150 ms per full-canvas PNG.
        if (!decode || DecodedAssets.ContainsKey(asset.Sha256)) return;
        try
        {
            using var input = new MemoryStream(png, writable: false);
            using var image = System.Drawing.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
            Require(image.Width == asset.Width && image.Height == asset.Height, "Decoded PNG dimensions differ");
        }
        catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.ExternalException)
        { throw new InvalidDataException("PNG decoder rejected asset data", error); }
        DecodedAssets.TryAdd(asset.Sha256, 0);
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> DecodedAssets = new(StringComparer.Ordinal);
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            { Require(seen.Add(property.Name), "Duplicate journal JSON property"); RejectDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private sealed record RecordCore(string Schema, long Sequence, string TransactionId, string Kind,
        DateTimeOffset WrittenUtc, JsonElement Data, string PreviousSha256);
    private static string RecordHash(EditJournalRecord record) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        new RecordCore(record.Schema, record.Sequence, record.TransactionId, record.Kind, record.WrittenUtc, record.Data, record.PreviousSha256), Json));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static T Required<T>(JsonElement data, string name) => data.GetProperty(name).Deserialize<T>(Json)
        ?? throw new InvalidDataException("Missing journal property: " + name);
    private static string GetText(JsonElement data, string name) { string? value = data.GetProperty(name).GetString(); Text(value); return value!; }
    private static void Text(string? value) => Require(!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl), "Required journal identifier/text is invalid");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static EditJournalSnapshot EmptySnapshot() => new([], [], 0, false, null, null, false);
    private void ActiveWriter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned) throw new InvalidOperationException("Journal write/readback failed; reopen and recover before any further action");
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _stream.Dispose(); }
    }
}
