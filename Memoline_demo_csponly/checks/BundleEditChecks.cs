using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Storage.Memoline;
using DirtyMatrixPaster;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class BundleEditChecks
{
    internal static int Run(string temporaryDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(temporaryDirectory), "bundle-edits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Bundle edits: " + message); checks++; }
        void Reject(Action operation, string message)
        {
            try { operation(); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
            { checks++; return; }
            throw new InvalidOperationException("Bundle edits accepted " + message);
        }
        var writer = new MemolineWriter(Path.Combine(root, "native"), new { purpose = "bundle-edit-checks" });
        var begin = writer.AppendHardwareRecord("penBegin", new { x = 2, y = 3 }, new HardwareDeviceSource("pen", "test", "fixture", "test"));
        writer.AppendHardware("penEnd", new { x = 2, y = 3 }, new HardwareDeviceSource("pen", "test", "fixture", "test"), begin.EventId);
        writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        string native = writer.FilePath;
        JsonElement[] frames = MemolineReader.Read(native).ToArray();
        string session = frames[0].GetProperty("sessionId").GetString()!;
        long frequency = frames[0].GetProperty("frequency").GetInt64(), end = frames[^1].GetProperty("ticks").GetInt64();
        string aggregate = Path.Combine(root, "events.jsonl");
        object[] rows = [new { kind = "header", sessionId = session, frequency },
            new { kind = "packet", sessionId = session, packetId = "p", fromTicks = 0L, toTicks = end,
                eventPointers = Array.Empty<object>(), dirtyMatrices = Array.Empty<object>() },
            new { kind = "footer", sessionId = session }];
        WriteRows(aggregate, rows);
        byte[] mechanicalBytes = File.ReadAllBytes(native), aggregateBytes = File.ReadAllBytes(aggregate);
        string work = Path.Combine(root, "work");
        var layer = new LayerKey(session, 1, "original");
        var key = new EditableStrokeKey(session, begin.AppendId);
        byte[] png = ClipboardImageCodec.EncodePng(2, 1, [101, 211, 17, 0, 37, 91, 223, 1]);
        EditJournalAsset asset;
        using (var journal = EditJournal.Open(work))
        {
            journal.Start("edit-1", layer);
            journal.FreezePlan(JsonSerializer.SerializeToElement(new { kind = "eventEditing", selected = new[] { key }, padding = new string('x', 50_000) }));
            journal.StartSegment("compose", EditJournalOperation.Compose); journal.Compose("compose", png, 2, 1);
            journal.StartSegment("replay", EditJournalOperation.Replay); journal.Replay("replay", [key]);
            journal.StartSegment("capture", EditJournalOperation.Capture); asset = journal.Capture("capture", "work", png, 2, 1);
            journal.WorkLayerDeleted(journal.CheckpointFlushed("work", asset, [key]));
            journal.ReproductionInstalled("final", asset); journal.Finish(EditJournalOutcome.Executed);
        }
        string journalPath = Path.Combine(work, EditJournal.JournalFileName), assetPath = Path.Combine(work, asset.RelativePath);
        byte[] journalBytes = File.ReadAllBytes(journalPath);
        string output = Path.Combine(root, "with-edits.memoline");
        BundleSealResult seal = BundleArchive.SealWithEdits(native, aggregate, work, output);
        BundleVerificationResult info = BundleArchive.Verify(output);
        JsonNode index = Index(output);
        Check(index["schema"]!.GetValue<string>() == BundleArchive.EditSchema, "v3 schema is explicit");
        Check(index["edits"]!["entry"]!.GetValue<string>() == BundleArchive.EditJournalEntry, "journal entry convention");
        Check(index["edits"]!["sha256"]!.GetValue<string>() == Hash(journalBytes)
            && index["edits"]!["size"]!.GetValue<long>() == journalBytes.LongLength, "decoded full journal digest/size");
        Check(index["edits"]!["assetCount"]!.GetValue<int>() == 1, "PNG assets are deduplicated in index");
        Check(info.EditAssetCount == 1 && info.EditJournalSha256 == Hash(journalBytes), "verification exposes edit integrity metadata");
        EditJournalSnapshot snapshot = BundleArchive.ReadEditJournal(output)!;
        Check(snapshot.Records.Count == EditJournal.Recover(work).Records.Count && snapshot.HasReplayed && !snapshot.HasIncompleteTail, "bundle journal reader uses actual ledger validation");
        Check(EntryBytes(output, BundleArchive.EditJournalEntry).SequenceEqual(journalBytes), "journal source bytes preserved");
        Check(EntryBytes(output, asset.RelativePath).SequenceEqual(png), "PNG hidden RGB and alpha one source bytes preserved");
        Check(EntryBytes(output, BundleArchive.MechanicalEntry).SequenceEqual(mechanicalBytes)
            && EntryBytes(output, BundleArchive.AggregationEntry).SequenceEqual(aggregateBytes), "original streams stay byte equal");
        Check(File.ReadAllBytes(native).SequenceEqual(mechanicalBytes) && File.ReadAllBytes(aggregate).SequenceEqual(aggregateBytes)
            && File.ReadAllBytes(journalPath).SequenceEqual(journalBytes) && File.ReadAllBytes(assetPath).SequenceEqual(png), "sealing leaves all source streams/assets unchanged");
        using (var zip = ZipFile.OpenRead(output)) Check(zip.GetEntry(asset.RelativePath) is not null
            && zip.GetEntry(asset.RelativePath + ".br") is null, "PNG assets are plain PNG entries");
        string v2 = Path.Combine(root, "without-edits.memoline");
        BundleArchive.Seal(native, aggregate, v2);
        Check(Index(v2)["schema"]!.GetValue<string>() == BundleArchive.Schema && BundleArchive.ReadEditJournal(v2) is null, "existing v2 seal/read API remains usable");

        string revision = Path.Combine(root, "dimensions-v-next.jsonl");
        WriteRows(revision, [rows[0], new { kind = "dimensionSample", sessionId = session, ticks = end,
            canvasViewportChanged = 0, actionPatternContinuity = (double?)null, toolContinuity = (double?)null,
            layerContinuity = (double?)null, regionContinuity = (double?)null, timeContinuity = (double?)null, algorithmVersion = "next" }, rows[^1]]);
        string upgraded = Path.Combine(root, "upgraded.memoline");
        BundleArchive.UpgradeDimensions(output, revision, "next", upgraded);
        Check(Index(upgraded)["schema"]!.GetValue<string>() == BundleArchive.EditSchema && BundleArchive.Verify(upgraded).EditAssetCount == 1, "dimension upgrade retains v3 and edits");
        Check(EntryBytes(upgraded, BundleArchive.EditJournalEntry).SequenceEqual(journalBytes)
            && EntryBytes(upgraded, asset.RelativePath).SequenceEqual(png), "upgrade preserves edit journal/assets");
        Check(EntryBytes(upgraded, BundleArchive.MechanicalEntry).SequenceEqual(mechanicalBytes)
            && EntryBytes(upgraded, BundleArchive.AggregationEntry).SequenceEqual(aggregateBytes), "upgrade preserves mechanical/aggregation");

        string Mutate(string name, Action<ZipArchive> change)
        {
            string path = Path.Combine(root, name + ".memoline"); File.Copy(output, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) change(zip);
            return path;
        }
        void PatchIndex(ZipArchive zip, Action<JsonNode> change)
        {
            JsonNode json;
            using (var source = zip.GetEntry("index.json")!.Open()) json = JsonNode.Parse(source)!;
            change(json); zip.GetEntry("index.json")!.Delete();
            using var target = zip.CreateEntry("index.json").Open(); JsonSerializer.Serialize(target, json);
        }
        string plain = Mutate("plain-journal", zip =>
        {
            zip.GetEntry(BundleArchive.EditJournalEntry + ".br")!.Delete();
            using (var target = zip.CreateEntry(BundleArchive.EditJournalEntry, CompressionLevel.NoCompression).Open()) target.Write(journalBytes);
            PatchIndex(zip, json => json["edits"]!["codec"] = null);
        });
        Check(BundleArchive.ReadEditJournal(plain)!.Records.Count == snapshot.Records.Count, "uncompressed v3 journal is readable");
        string compact = Path.Combine(root, "compact.memoline");
        BundleArchive.Compact(plain, compact);
        Check(new FileInfo(compact).Length < new FileInfo(plain).Length, "compaction really compresses a plain edit ledger");
        Check(EntryBytes(compact, BundleArchive.EditJournalEntry).SequenceEqual(journalBytes)
            && EntryBytes(compact, asset.RelativePath).SequenceEqual(png), "compaction retains edit bytes");
        Check(EntryBytes(compact, BundleArchive.MechanicalEntry).SequenceEqual(mechanicalBytes)
            && EntryBytes(compact, BundleArchive.AggregationEntry).SequenceEqual(aggregateBytes), "v3 compaction retains source streams");
        Check(BundleArchive.Verify(compact).EditJournalSha256 == info.EditJournalSha256 && Index(compact)["schema"]!.GetValue<string>() == BundleArchive.EditSchema, "compaction retains digest/schema");
        string compactUpgrade = Path.Combine(root, "compact-upgrade.memoline"); BundleArchive.Compact(upgraded, compactUpgrade);
        Check(EntryBytes(compactUpgrade, "dimensions/next.jsonl").SequenceEqual(File.ReadAllBytes(revision))
            && EntryBytes(compactUpgrade, asset.RelativePath).SequenceEqual(png), "compaction preserves dimension revisions and edits together");

        Reject(() => BundleArchive.Verify(Mutate("missing-png", zip => zip.GetEntry(asset.RelativePath)!.Delete())), "a missing bundle asset");
        Reject(() => BundleArchive.Verify(Mutate("tampered-png", zip =>
        {
            zip.GetEntry(asset.RelativePath)!.Delete(); using var target = zip.CreateEntry(asset.RelativePath).Open();
            byte[] changed = [.. png]; changed[^5] ^= 0x80; target.Write(changed);
        })), "a tampered bundle asset");
        Reject(() => BundleArchive.Verify(Mutate("wrong-asset-count", zip => PatchIndex(zip, json => json["edits"]!["assetCount"] = 2))), "wrong asset count");
        Reject(() => BundleArchive.Verify(Mutate("wrong-journal-hash", zip => PatchIndex(zip, json => json["edits"]!["sha256"] = new string('0', 64)))), "wrong journal hash");
        Reject(() => BundleArchive.Verify(Mutate("unknown-edit-codec", zip => PatchIndex(zip, json => json["edits"]!["codec"] = "unknown"))), "unknown edit codec");
        Reject(() => BundleArchive.Verify(Mutate("edits-in-v2", zip => PatchIndex(zip, json => json["schema"] = BundleArchive.Schema))), "edit metadata under v2");
        Reject(() => BundleArchive.Verify(Mutate("orphan-png", zip =>
        {
            using (var target = zip.CreateEntry(EditJournal.AssetEntryPrefix + new string('0', 64) + ".png").Open()) target.Write(png);
            PatchIndex(zip, json => json["edits"]!["assetCount"] = 2);
        })), "an unreferenced PNG with a forged count");
        Reject(() => BundleArchive.Verify(Mutate("partial-bundle-ledger", zip =>
        {
            zip.GetEntry(BundleArchive.EditJournalEntry + ".br")!.Delete();
            byte[] tail = [.. journalBytes, (byte)'{'];
            using (var target = zip.CreateEntry(BundleArchive.EditJournalEntry).Open()) target.Write(tail);
            PatchIndex(zip, json => { json["edits"]!["codec"] = null; json["edits"]!["sha256"] = Hash(tail); json["edits"]!["size"] = tail.Length; });
        })), "a partial ledger even when outer hash/size are recomputed");

        string failedOutput = Path.Combine(root, "partial-work.memoline");
        using (var append = new FileStream(journalPath, FileMode.Append, FileAccess.Write)) append.Write("{"u8);
        Reject(() => BundleArchive.SealWithEdits(native, aggregate, work, failedOutput), "sealing a work ledger crash tail");
        Check(!File.Exists(failedOutput) && File.ReadAllBytes(journalPath).Length == journalBytes.Length + 1, "failed seal preserves the recoverable tail");
        File.WriteAllBytes(journalPath, journalBytes);
        File.Delete(assetPath);
        Reject(() => BundleArchive.SealWithEdits(native, aggregate, work, Path.Combine(root, "missing-work.memoline")), "missing work asset");
        File.WriteAllBytes(assetPath, [1, 2, 3]);
        Reject(() => BundleArchive.SealWithEdits(native, aggregate, work, Path.Combine(root, "tampered-work.memoline")), "tampered work asset");
        File.WriteAllBytes(assetPath, png);
        using (var journal = EditJournal.Open(work)) Reject(() => BundleArchive.SealWithEdits(native, aggregate, work, Path.Combine(root, "active-writer.memoline")), "a ledger still open for writing");
        Check(File.ReadAllBytes(native).SequenceEqual(mechanicalBytes) && File.ReadAllBytes(aggregate).SequenceEqual(aggregateBytes), "all failed v3 seals preserve original streams");
        string multiWork = Path.Combine(root, "multi-work");
        using (var journal = EditJournal.Open(multiWork))
        {
            journal.Start("multi", new[] { layer, new LayerKey(session, 1, "second-original") });
            journal.FreezePlan(JsonSerializer.SerializeToElement(new { kind = "preciseUndo", targetCount = 2 }));
            journal.LayerHidden(layer.Uuid); journal.LayerHidden("second-original");
            journal.StartSegment("capture", EditJournalOperation.Capture); EditJournalAsset captured = journal.Capture("capture", "multi-work", png, 2, 1);
            journal.WorkLayerDeleted(journal.CheckpointFlushed("multi-work", captured, [key]));
            journal.ReproductionInstalled("multi-final", captured); journal.Finish(EditJournalOutcome.Executed);
        }
        string multiBundle = Path.Combine(root, "multiple-layers.memoline");
        BundleArchive.SealWithEdits(native, aggregate, multiWork, multiBundle);
        Check(BundleArchive.ReadEditJournal(multiBundle)!.Records[0].Data.GetProperty("targetLayers").GetArrayLength() == 2, "bundle preserves all participating layer identities");
        string foreignWork = Path.Combine(root, "foreign-layer-work");
        using (var journal = EditJournal.Open(foreignWork))
        {
            journal.Start("foreign", new[] { layer, new LayerKey("wrong-session", 1, "foreign-original") });
            journal.Fail("Stopped", "preserve fixture"); journal.Finish(EditJournalOutcome.Failed);
        }
        Reject(() => BundleArchive.SealWithEdits(native, aggregate, foreignWork, Path.Combine(root, "foreign-layer.memoline")), "a foreign session in the second original layer");
        string foreignKeyWork = Path.Combine(root, "foreign-key-work");
        using (var journal = EditJournal.Open(foreignKeyWork))
        {
            journal.Start("bad-event-key", layer); journal.FreezePlan(JsonSerializer.SerializeToElement(new { kind = "preciseUndo" }));
            journal.StartSegment("capture", EditJournalOperation.Capture); EditJournalAsset captured = journal.Capture("capture", "key-work", png, 2, 1);
            EditableStrokeKey notBegin = new(session, frames.Single(frame => frame.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "penEnd").GetProperty("appendId").GetUInt64());
            journal.CheckpointFlushed("key-work", captured, [notBegin]);
            journal.Fail("Stopped", "preserve fixture"); journal.Finish(EditJournalOutcome.Failed);
        }
        Reject(() => BundleArchive.SealWithEdits(native, aggregate, foreignKeyWork, Path.Combine(root, "pen-end-key.memoline")), "a checkpoint key that resolves to penEnd instead of penBegin");
        Console.WriteLine($"PASS {checks} bundle edit checks ({root})");
        return checks;
    }
    private static void WriteRows(string path, IEnumerable<object> rows) => File.WriteAllText(path,
        string.Join("\n", rows.Select(row => JsonSerializer.Serialize(row))) + "\n", new UTF8Encoding(false));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonNode Index(string path) => JsonNode.Parse(EntryBytes(path, "index.json"))!;
    private static byte[] EntryBytes(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = zip.GetEntry(name) ?? zip.GetEntry(name + ".br")!;
        using Stream raw = entry.Open();
        using Stream source = entry.FullName.EndsWith(".br", StringComparison.Ordinal) ? new BrotliStream(raw, CompressionMode.Decompress) : raw;
        using var output = new MemoryStream(); source.CopyTo(output); return output.ToArray();
    }
}
