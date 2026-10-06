using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

public static class BundleChecks
{
    public static int Run(string tempDir)
    {
        string root = Path.Combine(Path.GetFullPath(tempDir), "bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException("Bundle check failed: " + message);
            checks++;
        }
        void Reject<T>(Action operation, string message) where T : Exception
        {
            try { operation(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException("Bundle check did not reject: " + message);
        }

        var nativeWriter = new MemolineWriter(Path.Combine(root, "native"), new { purpose = "bundle-check" });
        var source = new HardwareDeviceSource("pen", "test", "test-device", "test");
        var first = nativeWriter.AppendHardware("penBegin", new { x = 10, y = 12 }, source);
        nativeWriter.AppendHardware("penEnd", new { x = 11, y = 12 }, source, first.EventId);
        // A delayed state occurs before the last input, and must retain its original append position.
        nativeWriter.AppendState("coreStateUpdated", first.Ticks, [first.EventId], new
        { module = "currentLayerState", status = "changed", state = "Ink", padding = new string('x', 100_000) });
        nativeWriter.FlushAsync().GetAwaiter().GetResult();
        string partial = Path.Combine(root, "unfinished.memoline.part");
        File.Copy(nativeWriter.LiveFilePath, partial);
        nativeWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        string native = nativeWriter.FilePath;
        var frames = MemolineReader.Read(native).ToArray();
        var header = frames[0]; var footer = frames[^1];
        string session = header.GetProperty("sessionId").GetString()!;
        long frequency = header.GetProperty("frequency").GetInt64(), endTicks = footer.GetProperty("ticks").GetInt64();
        JsonObject Pointer(JsonElement frame, bool context = false) => Node(new
        {
            sessionId = session, appendId = frame.GetProperty("appendId").GetUInt64(),
            eventId = frame.GetProperty("eventId").GetUInt64(),
            operationId = frame.GetProperty("operationId"), ticks = frame.GetProperty("ticks").GetInt64(), context
        });
        JsonObject Dimension(long ticks, int changed, string version, double? continuity = null) => Node(new
        {
            kind = "dimensionSample", sessionId = session, ticks, canvasViewportChanged = changed,
            actionPatternContinuity = continuity, toolContinuity = (double?)null, layerContinuity = (double?)null,
            regionContinuity = (double?)null, timeContinuity = (double?)null, algorithmVersion = version
        });
        byte[] assetFirst = "first image bytes"u8.ToArray(), assetSecond = "remaining image bytes"u8.ToArray();
        string totalHash = Hash([.. assetFirst, .. assetSecond]);
        JsonObject Asset(byte[] bytes, int chunk) => Node(new
        {
            kind = "asset", assetId = "patch-1", chunkIndex = chunk, chunkCount = 2,
            base64 = Convert.ToBase64String(bytes), sha256 = Hash(bytes), totalSha256 = totalHash
        });
        var rows = new List<JsonObject>
        {
            Node(new { kind = "header", schema = "memoline-csponly-aggregation/v1", sessionId = session, frequency }),
            Dimension(endTicks, 1, "v1"), Dimension(0, 0, "v1"),
            Node(new { kind = "packet", packetId = "packet-1", sessionId = session, fromTicks = 0L, toTicks = endTicks,
                eventPointers = new[] { Pointer(frames[1]), Pointer(frames[2]) },
                dirtyMatrices = new[] { new { id = "matrix-1", eventPointers = new[] { Pointer(frames[1]), Pointer(frames[2]) },
                    statePointers = new[] { Pointer(frames[3], true) }, pixels = new { assetId = "patch-1" } } } }),
            Asset(assetFirst, 0), Asset(assetSecond, 1), Node(new { kind = "footer", sessionId = session })
        };
        string aggregate = Path.Combine(root, "aggregate.jsonl"); WriteRows(aggregate, rows);
        byte[] originalNative = File.ReadAllBytes(native), originalAggregate = File.ReadAllBytes(aggregate);
        string destination = Path.Combine(root, "sealed.memoline");
        var seal = BundleArchive.Seal(native, aggregate, destination);
        var verified = BundleArchive.Verify(destination);
        Check(seal.Path == destination && verified.SessionId == session && verified.Frequency == frequency, "session clock survives sealing");
        Check(verified.PacketCount == 1 && verified.AssetCount == 2 && verified.DimensionSampleCount == 2, "packet, asset chunks and dimensions are validated");
        Check(EntryBytes(destination, BundleArchive.MechanicalEntry).SequenceEqual(originalNative), "native bytes are unchanged");
        Check(EntryBytes(destination, BundleArchive.AggregationEntry).SequenceEqual(originalAggregate), "aggregate JSONL bytes including CRLF are unchanged");
        Check(File.ReadAllBytes(native).SequenceEqual(originalNative) && File.ReadAllBytes(aggregate).SequenceEqual(originalAggregate), "source streams are retained");
        Check(BundleArchive.ReadMechanical(destination).Select(frame => frame.GetRawText()).SequenceEqual(frames.Select(frame => frame.GetRawText())), "mechanical reader retains delayed state order and payload");
        Check(BundleArchive.ReadDimensions(destination).Select(row => row.GetProperty("ticks").GetInt64()).SequenceEqual(new[] { 0L, endTicks }), "dimension reader sorts the active timeline");
        Reject<IOException>(() => BundleArchive.Seal(native, aggregate, destination), "existing output is never overwritten");

        string Faulty(Action<List<JsonObject>> change, string name)
        {
            var altered = rows.Select(row => (JsonObject)row.DeepClone()).ToList(); change(altered);
            string path = Path.Combine(root, name + ".jsonl"); WriteRows(path, altered); return path;
        }
        void RejectAggregate(string invalid, string name)
        {
            string output = Path.Combine(root, name + ".memoline");
            byte[] previous = File.ReadAllBytes(invalid);
            Reject<InvalidDataException>(() => BundleArchive.Seal(native, invalid, output), name);
            Check(!File.Exists(output) && File.ReadAllBytes(invalid).SequenceEqual(previous)
                && File.ReadAllBytes(native).SequenceEqual(originalNative), name + " preserves inputs and creates no seal");
        }
        RejectAggregate(Faulty(list => list.RemoveAt(list.Count - 1), "no-footer"), "missing aggregate footer");
        RejectAggregate(Faulty(list => list[0]["sessionId"] = "wrong-session", "wrong-session"), "session mismatch");
        RejectAggregate(Faulty(list => list[0]["frequency"] = frequency + 1, "wrong-clock"), "clock mismatch");
        RejectAggregate(Faulty(list => list[0]["originTicks"] = header.GetProperty("originTicks").GetInt64() + 1, "wrong-origin"), "clock origin mismatch");
        RejectAggregate(Faulty(list => list[1]["canvasViewportChanged"] = 2, "wrong-viewport"), "nonbinary viewport sample");
        RejectAggregate(Faulty(list => list[1]["toolContinuity"] = "unknown", "wrong-continuity"), "nonnumeric continuity sample");
        RejectAggregate(Faulty(list => list[3]["eventPointers"]![0]!["appendId"] = ulong.MaxValue, "broken-pointer"), "unresolved packet pointer");
        RejectAggregate(Faulty(list => list[3]["dirtyMatrices"]![0]!["eventPointers"]![0]!["eventId"] = 999, "wrong-event"), "matrix hardware event mismatch");
        RejectAggregate(Faulty(list => list[3]["dirtyMatrices"]![0]!["statePointers"]![0]!["appendId"] = 999, "wrong-state"), "unresolved state pointer");
        RejectAggregate(Faulty(list => list[3]["dirtyMatrices"]![0]!["eventPointers"]![0] = Pointer(frames[3]), "state-as-input"), "state is not hardware evidence");
        RejectAggregate(Faulty(list => list[4]["sha256"] = new string('0', 64), "corrupt-chunk"), "asset chunk corruption");
        RejectAggregate(Faulty(list => list[4]["sessionId"] = "another-session", "asset-session"), "asset session mismatch");
        RejectAggregate(Faulty(list => { list[4]["totalSha256"] = new string('0', 64); list[5]["totalSha256"] = new string('0', 64); }, "corrupt-asset"), "whole asset corruption");
        RejectAggregate(Faulty(list => list.RemoveAt(5), "missing-chunk"), "missing asset chunk");
        RejectAggregate(Faulty(list => list[^1]["sessionId"] = "another-session", "footer-session"), "footer session mismatch");
        string partialOutput = Path.Combine(root, "unfinished-seal.memoline");
        Reject<InvalidDataException>(() => BundleArchive.Seal(partial, aggregate, partialOutput), "unfinalized native file");
        Check(File.Exists(partial) && !File.Exists(partialOutput), "unfinalized recording remains recoverable");

        string backwards = Path.Combine(root, "backwards.memoline");
        var invalidFrames = frames.Select(frame => JsonNode.Parse(frame.GetRawText())!).ToArray();
        invalidFrames[2]["ticks"] = Math.Max(0, first.Ticks - 1);
        WriteV1(backwards, invalidFrames);
        Reject<InvalidDataException>(() => BundleArchive.Seal(backwards, aggregate, Path.Combine(root, "backwards-seal.memoline")), "backwards hardware timeline");

        string revision = Path.Combine(root, "dimensions-v2.jsonl");
        WriteRows(revision, [rows[0], Dimension(0, 0, "v2", .25), Dimension(endTicks, 1, "v2", .75), rows[^1]]);
        string upgraded = Path.Combine(root, "upgraded.memoline");
        var upgrade = BundleArchive.UpgradeDimensions(destination, revision, "v2", upgraded);
        var upgradedInfo = BundleArchive.Verify(upgraded);
        Check(upgrade.MechanicalSha256 == seal.MechanicalSha256 && upgrade.AggregationSha256 == seal.AggregationSha256
            && upgrade.FixedSha256 == seal.FixedSha256 && upgradedInfo.FixedSha256 == verified.FixedSha256, "upgrade keeps every immutable digest");
        Check(EntryBytes(upgraded, BundleArchive.MechanicalEntry).SequenceEqual(originalNative)
            && EntryBytes(upgraded, BundleArchive.AggregationEntry).SequenceEqual(originalAggregate), "upgrade keeps both original streams byte for byte");
        Check(BundleArchive.ReadDimensions(upgraded).All(row => row.GetProperty("algorithmVersion").GetString() == "v2")
            && BundleArchive.ReadDimensions(upgraded)[0].GetProperty("actionPatternContinuity").GetDouble() == .25, "active dimensions come from the new algorithm");
        string changedDimensions = Faulty(list => list[1]["toolContinuity"] = .9, "changed-only-dimensions");
        var changedDimensionSeal = BundleArchive.Seal(native, changedDimensions, Path.Combine(root, "changed-only-dimensions.memoline"));
        Check(changedDimensionSeal.FixedSha256 == seal.FixedSha256 && changedDimensionSeal.AggregationSha256 != seal.AggregationSha256,
            "fixed packets/assets digest excludes only the mutable dimension samples");
        Reject<ArgumentException>(() => BundleArchive.UpgradeDimensions(destination, revision, "../escape", Path.Combine(root, "unsafe.memoline")), "unsafe dimensions version");
        Reject<InvalidDataException>(() => BundleArchive.UpgradeDimensions(destination, aggregate, "v1", Path.Combine(root, "alter-fixed.memoline")), "dimensions upgrade cannot include packets or assets");
        Reject<IOException>(() => BundleArchive.UpgradeDimensions(upgraded, revision, "v2", Path.Combine(root, "duplicate.memoline")), "existing dimensions version is immutable");
        Check(File.ReadAllBytes(destination).Length > 0 && File.ReadAllBytes(revision).Length > 0, "failed upgrades retain source files");

        string corrupt = Path.Combine(root, "tampered.memoline"); File.Copy(destination, corrupt);
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            zip.GetEntry(BundleArchive.AggregationEntry + ".br")!.Delete();
            using var target = zip.CreateEntry(BundleArchive.AggregationEntry + ".br").Open();
            using var encoder = new BrotliStream(target, CompressionLevel.Fastest); encoder.Write("{}\n"u8);
        }
        Reject<InvalidDataException>(() => BundleArchive.Verify(corrupt), "tampered sealed aggregate");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        string cancelledOutput = Path.Combine(root, "cancelled.memoline");
        Reject<OperationCanceledException>(() => BundleArchive.Seal(native, aggregate, cancelledOutput, cancelled.Token), "cancelled seal");
        Check(!File.Exists(cancelledOutput) && File.ReadAllBytes(native).SequenceEqual(originalNative), "cancellation leaves native bytes intact");

        // Exercise an actual old ZIP, with native v1 frames and no whole-stream
        // compression. Its decoded mechanical bytes must survive migration.
        string legacyNative = Path.Combine(root, "legacy-native.memoline");
        WriteV1(legacyNative, frames.Select(f => JsonNode.Parse(f.GetRawText())!).ToArray());
        byte[] legacyBytes = File.ReadAllBytes(legacyNative);
        var legacyIndex = JsonNode.Parse(Encoding.UTF8.GetString(EntryBytes(destination, "index.json")))!;
        legacyIndex["schema"] = BundleArchive.LegacySchema;
        legacyIndex["mechanical"]!["codec"] = null; legacyIndex["aggregation"]!["codec"] = null;
        legacyIndex["mechanical"]!["sha256"] = Hash(legacyBytes);
        legacyIndex["mechanical"]!["size"] = legacyBytes.Length;
        string legacy = Path.Combine(root, "legacy.memoline");
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Create))
        {
            using (var target = zip.CreateEntry(BundleArchive.MechanicalEntry, CompressionLevel.NoCompression).Open()) target.Write(legacyBytes);
            using (var target = zip.CreateEntry(BundleArchive.AggregationEntry, CompressionLevel.Fastest).Open()) target.Write(originalAggregate);
            using (var target = zip.CreateEntry("index.json").Open()) JsonSerializer.Serialize(target, legacyIndex);
        }
        byte[] legacyFileHash = SHA256.HashData(File.ReadAllBytes(legacy));
        Check(BundleArchive.Verify(legacy).PacketCount == verified.PacketCount, "old containers remain readable");
        string compacted = Path.Combine(root, "legacy-compact.memoline");
        var compactResult = BundleArchive.Compact(legacy, compacted);
        Check(new FileInfo(compacted).Length < new FileInfo(legacy).Length, "whole-stream compression reduces the real old container");
        Check(EntryBytes(compacted, BundleArchive.MechanicalEntry).SequenceEqual(legacyBytes)
            && EntryBytes(compacted, BundleArchive.AggregationEntry).SequenceEqual(originalAggregate),
            "compaction restores both original streams byte for byte");
        Check(compactResult.FixedSha256 == seal.FixedSha256
            && legacyFileHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(legacy))), "compaction keeps fixed packets and original file unchanged");
        string extracted = Path.Combine(root, "extracted.memoline");
        BundleArchive.ExtractMechanical(compacted, extracted);
        Check(File.ReadAllBytes(extracted).SequenceEqual(legacyBytes), "native extraction supports the new storage codec");
        Reject<IOException>(() => BundleArchive.Compact(legacy, legacy), "compaction cannot replace its input");
        string compactedAgain = Path.Combine(root, "compact-again.memoline");
        BundleArchive.Compact(compacted, compactedAgain);
        Check(new FileInfo(compactedAgain).Length <= new FileInfo(compacted).Length
            && EntryBytes(compactedAgain, BundleArchive.MechanicalEntry).SequenceEqual(legacyBytes),
            "already compact input never grows or loses native bytes");
        string mixedRevision = Path.Combine(root, "legacy-upgraded.memoline");
        BundleArchive.UpgradeDimensions(legacy, revision, "v2", mixedRevision);
        Check(EntryBytes(mixedRevision, BundleArchive.MechanicalEntry).SequenceEqual(legacyBytes)
            && BundleArchive.ReadDimensions(mixedRevision).All(r => r.GetProperty("algorithmVersion").GetString() == "v2"),
            "upgrading a legacy container reads plain and Brotli entries together");
        string compactedRevision = Path.Combine(root, "revision-compact.memoline");
        BundleArchive.Compact(upgraded, compactedRevision);
        Check(EntryBytes(compactedRevision, "dimensions/v2.jsonl").SequenceEqual(File.ReadAllBytes(revision))
            && BundleArchive.ReadDimensions(compactedRevision).All(r => r.GetProperty("algorithmVersion").GetString() == "v2"),
            "compaction keeps every dimension revision and active selection");
        string compactCancelled = Path.Combine(root, "compact-cancelled.memoline");
        Reject<OperationCanceledException>(() => BundleArchive.Compact(legacy, compactCancelled, cancelled.Token), "cancelled compaction");
        Check(!File.Exists(compactCancelled) && legacyFileHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(legacy))),
            "cancelled compaction leaves the original file intact");
        var missingAssetRows = rows.Select(row => (JsonObject)row.DeepClone()).ToList();
        missingAssetRows[3]["imageAssets"] = JsonSerializer.SerializeToNode(new[] { new { path = "patch.png", assetId = "missing" } });
        string missingAsset = Path.Combine(root, "missing-image.jsonl"); WriteRows(missingAsset, missingAssetRows);
        Reject<InvalidDataException>(() => BundleArchive.Seal(native, missingAsset, Path.Combine(root, "missing-image.memoline")),
            "a shared image reference must resolve to stored bytes");
        string unknownCodec = Path.Combine(root, "unknown-codec.memoline"); File.Copy(destination, unknownCodec);
        using (var zip = ZipFile.Open(unknownCodec, ZipArchiveMode.Update))
        {
            var unsupported = JsonNode.Parse(Encoding.UTF8.GetString(EntryBytes(destination, "index.json")))!;
            unsupported["mechanical"]!["codec"] = "unknown";
            zip.GetEntry("index.json")!.Delete();
            using var target = zip.CreateEntry("index.json").Open(); JsonSerializer.Serialize(target, unsupported);
        }
        Reject<InvalidDataException>(() => BundleArchive.Verify(unknownCodec), "unknown storage codecs are rejected");
        return checks;
    }

    private static JsonObject Node(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void WriteRows(string path, IEnumerable<JsonObject> rows) => File.WriteAllText(path,
        string.Join("\r\n", rows.Select(row => row.ToJsonString())) + "\r\n", new UTF8Encoding(false));
    private static byte[] EntryBytes(string path, string entry)
    {
        using var archive = ZipFile.OpenRead(path);
        var stored = archive.GetEntry(entry) ?? archive.GetEntry(entry + ".br")!;
        using var raw = stored.Open();
        using Stream stream = stored.FullName.EndsWith(".br", StringComparison.Ordinal) ? new BrotliStream(raw, CompressionMode.Decompress) : raw;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    private static void WriteV1(string path, IEnumerable<JsonNode> frames)
    {
        using var stream = File.Create(path); stream.Write("MEMOLINE"u8);
        byte[] header = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(header, 1); stream.Write(header, 0, 4);
        foreach (var frame in frames)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(frame.ToJsonString()); uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xEDB88320U : 0);
            }
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ~crc); stream.Write(header); stream.Write(bytes);
        }
    }
}
