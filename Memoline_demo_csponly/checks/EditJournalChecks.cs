using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DirtyMatrixPaster;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class EditJournalChecks
{
    internal static int Run(string root)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("EditJournal: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
            { checks++; return; }
            throw new InvalidOperationException("EditJournal accepted " + message);
        }
        string fixtureRoot = Path.Combine(root, "checks", "artifacts", "edit-journal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        string Work(string name)
        {
            string path = Path.Combine(fixtureRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }
        var key = new EditableStrokeKey("session-a", 7);
        EditableStrokeKey[] keys = [key];
        EditJournalOverride[] overrides = [new(key, new EventChange(Numbers: new Dictionary<string, double> { ["brushSize"] = 12 }))];
        var layer = new LayerKey("session-a", 2, "original-layer");
        byte[] png = ClipboardImageCodec.EncodePng(2, 1, [19, 83, 211, 0, 29, 79, 199, 1]);
        JsonElement plan = JsonSerializer.SerializeToElement(new { kind = "eventEditing", selected = keys, changes = new[] { new { key, size = 12 } } });
        EditJournalCheckpoint CaptureCheckpoint(EditJournal journal, string segment, string uuid)
        {
            journal.StartSegment(segment, EditJournalOperation.Capture);
            EditJournalAsset captured = journal.Capture(segment, uuid, png, 2, 1);
            return journal.CheckpointFlushed(uuid, captured, keys, overrides);
        }
        void Begin(EditJournal journal, string id)
        {
            journal.Start(id, layer);
            journal.FreezePlan(plan);
        }
        string basic = Work("durable");
        string mechanical = Path.Combine(basic, "recording.memoline"), aggregation = Path.Combine(basic, "events.jsonl");
        File.WriteAllBytes(mechanical, [0, 91, 231, 18]);
        File.WriteAllBytes(aggregation, Encoding.UTF8.GetBytes("original aggregation\n"));
        string mechanicalHash = Hash(File.ReadAllBytes(mechanical)), aggregationHash = Hash(File.ReadAllBytes(aggregation));
        using (var journal = EditJournal.Open(basic))
        {
            Begin(journal, "tx-1");
            Reject(() => journal.FreezePlan(plan), "a second frozen plan");
            Reject(() => EditJournal.Open(basic).Dispose(), "a competing writer");
            journal.LayerHidden("original-layer");
            journal.StartSegment("compose-1", EditJournalOperation.Compose);
            EditJournalAsset composed = journal.Compose("compose-1", png, 2, 1);
            Check(composed.Sha256 == Hash(png), "asset hash must cover original PNG bytes");
            Check(composed.RelativePath == "edits/assets/" + composed.Sha256 + ".png", "future bundle asset entry convention");
            journal.StartSegment("replay-1", EditJournalOperation.Replay);
            journal.Replay("replay-1", keys, overrides);
            EditJournalCheckpoint checkpoint = CaptureCheckpoint(journal, "capture-1", "work-1");
            EditJournalSnapshot visible = EditJournal.Recover(basic);
            Check(visible.LastCheckpoint == checkpoint, "checkpoint durable receipt is readable before writer disposal");
            Check(visible.HasReplayed, "replay evidence survives recovery");
            Check(File.ReadAllBytes(Path.Combine(basic, composed.RelativePath)).SequenceEqual(png), "hidden RGB/alpha-one PNG bytes are unchanged");
            Check(Directory.GetFiles(Path.Combine(basic, "edits", "assets"), "*.png").Length == 1, "compose/capture use one deduplicated asset");
            Check(!Directory.GetFiles(Path.Combine(basic, "edits", "assets")).Any(p => p.EndsWith(".tmp", StringComparison.Ordinal)), "no temporary asset survives successful writes");
            Reject(() => journal.WorkLayerDeleted(checkpoint with { WorkLayerUuid = "other" }), "a forged layer deletion receipt");
            journal.WorkLayerDeleted(checkpoint);
            journal.ReproductionInstalled("final-1", composed);
            journal.Finish(EditJournalOutcome.Executed);
            Check(EditJournal.Recover(basic).ActiveTransactionId is null, "finished transaction is inactive");
            Reject(() => journal.LayerHidden("original-layer"), "an action after transaction ended");
        }
        EditJournalSnapshot complete = EditJournal.Recover(basic);
        Check(complete.Records.Select(r => r.Sequence).SequenceEqual(Enumerable.Range(1, complete.Records.Count).Select(i => (long)i)), "sequence is contiguous");
        Check(complete.Records.Single(r => r.Kind == "planFrozen").Data.GetProperty("planSha256").GetString() == Hash(JsonSerializer.SerializeToUtf8Bytes(plan)), "frozen plan hash binds the full plan");
        Check(complete.Records.Last().Data.GetProperty("outcome").GetString() == "Executed", "replayed success is Executed");
        Check(!File.ReadAllText(Path.Combine(basic, EditJournal.JournalFileName)).Contains("PixelVerified", StringComparison.Ordinal), "journal never claims PixelVerified");
        Check(Hash(File.ReadAllBytes(mechanical)) == mechanicalHash && Hash(File.ReadAllBytes(aggregation)) == aggregationHash, "mechanical and committed aggregate bytes remain unchanged");

        string tail = Work("crash-tail");
        byte[] committed;
        using (var journal = EditJournal.Open(tail)) { Begin(journal, "unfinished"); }
        string tailPath = Path.Combine(tail, EditJournal.JournalFileName);
        committed = File.ReadAllBytes(tailPath);
        using (var append = new FileStream(tailPath, FileMode.Append, FileAccess.Write)) { append.Write([123, 34, 255, 0]); append.Flush(true); }
        EditJournalSnapshot partial = EditJournal.Recover(tail);
        Check(partial.HasIncompleteTail && partial.ValidByteLength == committed.Length, "only the last unterminated row is uncommitted");
        Check(File.ReadAllBytes(tailPath).Length > committed.Length, "read-only recovery preserves the crash tail");
        using (var journal = EditJournal.Open(tail))
        {
            Check(ReadShared(tailPath).SequenceEqual(committed), "writer recovery truncates only uncommitted trailing bytes");
            Check(journal.Snapshot.ActiveTransactionId == "unfinished", "recovery retains incomplete transaction without replaying it");
            Reject(() => journal.Start("new", layer), "starting over an unfinished transaction");
            journal.Fail("RecoveredIncompleteTransaction", "explicit keep/cleanup decision is required");
            journal.Finish(EditJournalOutcome.Failed);
        }
        Check(EditJournal.Recover(tail).Records.Count == 4, "tail recovery appends failure/end once");

        string corruption = Work("complete-corruption");
        using (var journal = EditJournal.Open(corruption)) { Begin(journal, "tx-corrupt"); }
        string corruptPath = Path.Combine(corruption, EditJournal.JournalFileName);
        byte[] originalJournal = File.ReadAllBytes(corruptPath);
        using (var append = new FileStream(corruptPath, FileMode.Append, FileAccess.Write)) append.Write(Encoding.UTF8.GetBytes("{bad-json}\n"));
        Reject(() => EditJournal.Recover(corruption), "a newline-terminated corrupt last record");
        Reject(() => EditJournal.Open(corruption).Dispose(), "a corrupt complete record on writer open");
        Check(File.ReadAllBytes(corruptPath).Length > originalJournal.Length, "complete corrupt frames are not silently truncated");
        File.WriteAllBytes(corruptPath, originalJournal);
        string altered = Encoding.UTF8.GetString(originalJournal).Replace("brushSize", "brushSixe", StringComparison.Ordinal);
        if (altered == Encoding.UTF8.GetString(originalJournal)) altered = altered.Replace("eventEditing", "eventEditting", StringComparison.Ordinal);
        File.WriteAllText(corruptPath, altered, new UTF8Encoding(false));
        Reject(() => EditJournal.Recover(corruption), "a complete altered plan/hash chain");
        File.WriteAllBytes(corruptPath, originalJournal);
        byte[][] lines = originalJournal.SplitLines();
        File.WriteAllBytes(corruptPath, [.. lines[1], .. lines[0]]);
        Reject(() => EditJournal.Recover(corruption), "reordered complete records");
        File.WriteAllBytes(corruptPath, [.. originalJournal, (byte)'\n']);
        Reject(() => EditJournal.Recover(corruption), "an interior/committed blank row");
        JsonNode[] augmented = originalJournal.SplitLines().Select(bytes => JsonNode.Parse(bytes)!).ToArray();
        augmented[0]["ignoredOuterField"] = true;
        File.WriteAllText(corruptPath, string.Join("\n", augmented.Select(row => row.ToJsonString())) + "\n", new UTF8Encoding(false));
        Reject(() => EditJournal.Recover(corruption), "an unsigned extra outer journal field");

        string assets = Work("asset-integrity");
        EditJournalAsset asset;
        using (var journal = EditJournal.Open(assets))
        {
            Begin(journal, "tx-assets");
            journal.StartSegment("capture", EditJournalOperation.Capture);
            asset = journal.Capture("capture", "work", png, 2, 1);
            Reject(() => journal.CheckpointFlushed("other", asset, keys, overrides), "a checkpoint for another layer");
            Reject(() => journal.CheckpointFlushed("work", asset with { RelativePath = "../recording.memoline" }, keys, overrides), "a path-escaping asset reference");
            EditJournalCheckpoint checkpoint = journal.CheckpointFlushed("work", asset, keys, overrides);
            string assetPath = Path.Combine(assets, asset.RelativePath);
            byte[] changedPng = [.. png]; changedPng[^5] ^= 0x80;
            File.WriteAllBytes(assetPath, changedPng);
            Reject(() => EditJournal.Recover(assets), "a tampered referenced PNG");
            Reject(() => journal.WorkLayerDeleted(checkpoint), "deletion after checkpoint asset changed");
            File.WriteAllBytes(assetPath, png);
            File.Delete(assetPath);
            Reject(() => EditJournal.Recover(assets), "a missing referenced PNG");
            File.WriteAllBytes(assetPath, png);
            journal.Fail("CaptureMismatch", "keep the uncommitted work layer\nraw failure details");
            Reject(() => journal.WorkLayerDeleted(checkpoint), "deletion after failure");
            Reject(() => journal.StartSegment("later", EditJournalOperation.Replay), "replay after failure");
            Reject(() => journal.Finish(EditJournalOutcome.Executed), "success after failure");
            journal.Finish(EditJournalOutcome.Failed);
        }
        EditJournalRecord failure = EditJournal.Recover(assets).Records.Single(r => r.Kind == "failure");
        Check(failure.Data.GetProperty("code").GetString() == "CaptureMismatch", "failure code is durable");
        Check(failure.Data.GetProperty("message").GetString() == "keep the uncommitted work layer\nraw failure details", "multi-line failure details stay in one durable JSONL row");
        Check(failure.Data.GetProperty("lastCheckpoint").GetProperty("sequence").GetInt64() > 0, "failure identifies its last durable checkpoint");

        string validation = Work("validation");
        using (var journal = EditJournal.Open(validation))
        {
            journal.Start("validation", layer);
            Reject(() => journal.StartSegment("early", EditJournalOperation.Compose), "an action before the plan is frozen");
            journal.FreezePlan(plan);
            journal.StartSegment("compose", EditJournalOperation.Compose);
            Reject(() => journal.Compose("compose", png, 1, 1), "PNG dimension mismatch");
            Reject(() => journal.Compose("compose", [1, 2, 3], 2, 1), "invalid PNG bytes");
            journal.Compose("compose", png, 2, 1);
            journal.StartSegment("replay", EditJournalOperation.Replay);
            Reject(() => journal.Replay("replay", [key, key], overrides), "duplicate editable event keys");
            Reject(() => journal.Replay("replay", keys, [new(key, new EventChange(Numbers: new Dictionary<string, double> { ["size"] = double.NaN }))]), "non-finite replay parameters");
            journal.Fail("Cancelled", "user cancellation before replay completed");
            journal.Finish(EditJournalOutcome.Cancelled);
        }
        Check(EditJournal.Recover(validation).HasReplayed, "even an incomplete replay segment is recorded as replayed");
        string serial = Work("single-consumer");
        using (var journal = EditJournal.Open(serial))
        {
            Begin(journal, "serial");
            Parallel.For(0, 16, _ => journal.LayerHidden("original-layer"));
            Check(EditJournal.Recover(serial).Records.Count == 18, "concurrent producers serialize durable append rows");
            journal.Fail("Stopped", "finish fixture"); journal.Finish(EditJournalOutcome.Failed);
        }
        string multi = Work("multiple-target-layers");
        var secondLayer = new LayerKey("session-a", 2, "second-original");
        using (var journal = EditJournal.Open(multi))
        {
            Reject(() => journal.Start("empty", Array.Empty<LayerKey>()), "an empty layer transaction");
            Reject(() => journal.Start("duplicate", new[] { layer, layer }), "duplicate original layer identities");
            journal.Start("multi-layer", new[] { layer, secondLayer }); journal.FreezePlan(plan);
            journal.LayerHidden(layer.Uuid); journal.LayerHidden(secondLayer.Uuid);
            Check(EditJournal.Recover(multi).Records[0].Data.GetProperty("targetLayers").GetArrayLength() == 2, "all original layers are frozen in a multi-layer transaction");
            Reject(() => journal.LayerHidden("unrelated-original"), "hiding a layer outside the transaction");
            journal.StartSegment("first-capture", EditJournalOperation.Capture);
            Reject(() => journal.Capture("first-capture", secondLayer.Uuid, png, 2, 1), "an original target UUID used as a work layer");
            EditJournalAsset firstAsset = journal.Capture("first-capture", "first-work", png, 2, 1);
            EditJournalCheckpoint firstCheckpoint = journal.CheckpointFlushed("first-work", firstAsset, keys);
            journal.WorkLayerDeleted(firstCheckpoint);
            EditJournalCheckpoint secondCheckpoint = CaptureCheckpoint(journal, "second-capture", "second-work");
            Reject(() => journal.WorkLayerDeleted(firstCheckpoint), "deleting with a superseded checkpoint from another work layer");
            Reject(() => journal.WorkLayerDeleted(secondCheckpoint with { WorkLayerUuid = secondLayer.Uuid }), "deleting an original via a forged receipt");
            journal.WorkLayerDeleted(secondCheckpoint);
            Reject(() => journal.ReproductionInstalled(layer.Uuid, firstAsset), "using an original UUID as a reproduction layer");
            journal.ReproductionInstalled("multi-final", firstAsset); journal.Finish(EditJournalOutcome.Executed);
        }
        Check(EditJournal.Recover(multi).Records.Count(r => r.Kind == "workLayerDeleted") == 2, "both work-layer checkpoints are durably recorded");
        Console.WriteLine($"PASS {checks} edit-journal checks ({fixtureRoot})");
        return checks;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    private static byte[][] SplitLines(this byte[] bytes)
    {
        var rows = new List<byte[]>(); int start = 0;
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] == 10) { rows.Add(bytes.AsSpan(start, i - start + 1).ToArray()); start = i + 1; }
        return [.. rows];
    }
}
