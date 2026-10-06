using System.Diagnostics;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class AggregateChecks
{
    public static int Run(string temporaryDirectory)
    {
        int count = 0;
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException("Aggregate check: " + description); count++; }
        void Invalid(Action action, string description)
        {
            try { action(); }
            catch (InvalidDataException) { count++; return; }
            throw new InvalidOperationException("Aggregate check: " + description);
        }
        string root = Path.Combine(temporaryDirectory, "aggregate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var records = new List<MemolineEvent>();
        var writer = new MemolineWriter(Path.Combine(root, "mechanical"), new { test = "aggregate" });
        writer.RecordAppended += records.Add;
        MemolineEvent State(long ticks, string module = "canvasViewState")
        {
            writer.AppendState("coreStateUpdated", ticks, [], new { module, status = "changed", state = new { test = true } });
            return records[^1];
        }
        MemolineEvent Pen(string kind, ulong operation)
        {
            var location = PanelRegionMap.FromRegions(JsonSerializer.SerializeToElement(new Dictionary<string, int[]> { ["画布视口"] = [0, 0, 10, 10] })).Classify(5, 5);
            writer.AppendHardware(kind, new { x = 5, y = 5, inCsp = true, penDownLocation = location }, new("tablet", "test", "fixture", "fixture"), operation);
            return records[^1];
        }
        long NextTick()
        {
            // Real native clock, rather than synthesized hardware stamps.
            Thread.Sleep(1);
            return writer.NowTicks;
        }
        RecorderRealtimeEvent Realtime(MemolineEvent frame, bool snapshot = false) => new(1, writer.SessionId,
            99999999, "core.canvasViewState", "stateUpdated", snapshot, frame.AppendId, frame.EventId,
            frame.Ticks, frame.AppendedTicks, writer.NowTicks, frame.OperationId, frame.RelatedEventIds,
            frame.DeviceSource, frame.Data);
        var hello = new RecorderRealtimeEvent(1, writer.SessionId, 888888, "system", "hello", false,
            0, 0, 0, 0, 0, null, null, null, JsonSerializer.SerializeToElement(new {
                frequency = Stopwatch.Frequency, originTicks = writer.ClockOriginTicks,
                filePath = writer.FilePath, liveFilePath = writer.LiveFilePath }));
        string aggregatePath = Path.Combine(root, "events.aggregate.jsonl");
        using var aggregate = new AggregateSession(aggregatePath, writer.FilePath, hello);
        try
        {
            var baseline = State(0);
            aggregate.ObserveViewport(new(Realtime(baseline, snapshot: true), 0, 0, true, false));
            var begin = Pen("penBegin", 77);
            var historicalSample = Pen("penSample", 77);
            long firstTick = NextTick();
            var first = State(firstTick);
            aggregate.ObserveViewport(new(Realtime(first), firstTick, 1, false, true));
            var failed = new CaptureRequest(1, firstTick, "Ink", new(0, 0, 100, 0), [77]);
            aggregate.CaptureQueued(failed);
            aggregate.CaptureFailed(failed, "fixtureSaveFailed");

            var sample = Pen("penSample", 77);
            var brush = State(sample.Ticks, "brushState");
            var other = Pen("penSample", 88);
            long unchangedTick = NextTick();
            var unchanged = State(unchangedTick);
            aggregate.ObserveViewport(new(Realtime(unchanged), unchangedTick, 0, false, true));
            var end = Pen("penEnd", 77);
            long secondTick = NextTick();
            var second = State(secondTick);
            aggregate.ObserveViewport(new(Realtime(second), secondTick, 1, false, true));
            var request = new CaptureRequest(1, secondTick, "Ink", new(10, 0, 100, 0), [77]);
            aggregate.CaptureQueued(request);
            Invalid(aggregate.Complete, "Cannot seal without the mechanical footer");
            Check(!aggregate.IsComplete, "Rejected incomplete seal remains retryable");

            string packetRoot = Path.Combine(root, "packet");
            Directory.CreateDirectory(packetRoot);
            using (var patch = new Bitmap(3, 2, PixelFormat.Format32bppArgb))
            {
                patch.SetPixel(0, 0, Color.FromArgb(173, 89, 17, 232));
                patch.SetPixel(2, 1, Color.FromArgb(0, 0, 0, 0));
                patch.Save(Path.Combine(packetRoot, "patch.png"), ImageFormat.Png);
            }
            File.Copy(Path.Combine(packetRoot, "patch.png"), Path.Combine(packetRoot, "patch-alias.png"));
            // A genuine PNG above the chunk boundary, with incompressible pixels.
            using (var preview = new Bitmap(512, 512, PixelFormat.Format32bppArgb))
            {
                var random = new Random(37);
                for (int y = 0; y < preview.Height; y++) for (int x = 0; x < preview.Width; x++)
                    preview.SetPixel(x, y, Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
                preview.Save(Path.Combine(packetRoot, "canvas-preview.png"), ImageFormat.Png);
            }
            object Input(MemolineEvent native, bool context = false, ulong? append = null, ulong? operation = null) => new {
                ticks = native.Ticks, channel = "tablet", kind = native.Kind, operationId = operation ?? native.OperationId,
                eventId = native.EventId, appendId = append ?? native.AppendId, continuity = context };
            var impact = new { canvasWidth = 64, canvasHeight = 64, tileSize = 64, originX = 0, originY = 0,
                rowRuns = new[] { new { row = 0, startColumn = 0, endColumnExclusive = 1 } } };
            var labels = new object[] {
                new { id = "stroke", source = "tablet", operationId = (long?)77, fromTicks = historicalSample.Ticks, toTicks = end.Ticks,
                    stateIds = new[] { "brush", "diagnostic" }, coverage = new { fixture = true }, impactRange = impact,
                    warnings = System.Array.Empty<string>(), imageIds = new[] { "image-0001" } },
                new { id = "coarse", source = "low-resolution-fallback", operationId = (long?)null, fromTicks = firstTick, toTicks = secondTick,
                    stateIds = new[] { "brush" }, coverage = (object?)null, impactRange = impact,
                    warnings = new[] { "outsidePredictedStrokeCoverage" }, imageIds = new[] { "image-0001" } }
            };
            var manifest = new {
                schema = "dirty-matrix-image-diff/v1", kind = "diff", id = "fixture",
                recognizer = new { sessionId = writer.SessionId, frequency = Stopwatch.Frequency, fromTicks = 0L, toTicks = secondTick,
                    inputs = new[] { Input(begin, context: true), Input(historicalSample), Input(sample, append: 0), Input(end),
                        Input(other, operation: 77) },
                    states = new[] { new { id = "brush", appendId = brush.AppendId, ticks = brush.Ticks },
                        new { id = "diagnostic", appendId = 0UL, ticks = brush.Ticks } } },
                capture = new { triggerTicks = secondTick, saveDispatchedTicks = secondTick + 99999999 },
                labels, images = new[] { new { id = "image-0001", image = "patch.png", afterImage = "patch-alias.png", nowImage = "patch.png",
                    maskImage = "patch.png", differenceImage = "patch.png", labelIds = new[] { "stroke", "coarse" } } },
                canvasPreviewImage = "canvas-preview.png"
            };
            File.WriteAllText(Path.Combine(packetRoot, "manifest.json"), JsonSerializer.Serialize(manifest, MemolineWriter.Json));
            writer.FlushAsync().GetAwaiter().GetResult();
            aggregate.CaptureCommitted(request, new(Path.Combine(packetRoot, "patch.png"), packetRoot, false, 1, 2));
            aggregate.CaptureCommitted(request, new(Path.Combine(packetRoot, "patch.png"), packetRoot, false, 1, 2));

            long thirdTick = NextTick();
            var third = State(thirdTick);
            aggregate.ObserveViewport(new(Realtime(third), thirdTick, 1, false, false));
            long fourthTick = NextTick();
            var fourth = State(fourthTick);
            aggregate.ObserveViewport(new(Realtime(fourth), fourthTick, 1, false, true));
            var fourthRequest = new CaptureRequest(1, fourthTick, "Ink", new(20, 0, 100, 0), [77]);
            aggregate.CaptureQueued(fourthRequest);
            string unsafePacket = Path.Combine(root, "unsafe-packet");
            Directory.CreateDirectory(unsafePacket);
            using (var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(packetRoot, "manifest.json"))))
            {
                var altered = System.Text.Json.Nodes.JsonNode.Parse(original.RootElement.GetRawText())!;
                altered["recognizer"]!["toTicks"] = fourthTick;
                altered["capture"]!["triggerTicks"] = fourthTick;
                altered["canvasPreviewImage"] = "../packet/canvas-preview.png";
                File.WriteAllText(Path.Combine(unsafePacket, "manifest.json"), altered.ToJsonString());
            }
            File.Copy(Path.Combine(packetRoot, "patch.png"), Path.Combine(unsafePacket, "patch.png"));
            writer.FlushAsync().GetAwaiter().GetResult();
            Invalid(() => aggregate.CaptureCommitted(fourthRequest, new("", unsafePacket, false, 1, 2)), "Reject traversal before committing assets");
            aggregate.CaptureFailed(fourthRequest, "fixtureUnsafeAsset");
            aggregate.ObserveViewport(new(Realtime(second, snapshot: true), secondTick, 1, true, false));
            aggregate.ObserveViewport(new(Realtime(baseline, snapshot: true), 0, 0, true, false));
            long reconnectTick = NextTick();
            var reconnect = State(reconnectTick);
            aggregate.ObserveViewport(new(Realtime(reconnect, snapshot: true), reconnectTick, 0, true, false));
            // A later packet has identical PNG bytes under other paths. It must
            // retain all local references while storing no second image copy.
            writer.AppendState("recordingEndRequested", NextTick(), [], new { purpose = "shared-image-check" });
            var marker = records[^1];
            aggregate.ObserveRecordingEnd(new(Realtime(marker) with { Channel = "system", Kind = "recordingEndRequested" },
                marker.Ticks, 0, false, false));
            string finalRoot = Path.Combine(root, "final-shared-images"); Directory.CreateDirectory(finalRoot);
            File.Copy(Path.Combine(packetRoot, "patch.png"), Path.Combine(finalRoot, "tail-patch.png"));
            File.Copy(Path.Combine(packetRoot, "canvas-preview.png"), Path.Combine(finalRoot, "tail-preview.png"));
            var finalManifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(packetRoot, "manifest.json")))!;
            finalManifest["capture"]!["triggerTicks"] = marker.Ticks;
            finalManifest["recognizer"]!["toTicks"] = marker.Ticks;
            finalManifest["canvasPreviewImage"] = "tail-preview.png";
            foreach (string key in new[] { "image", "afterImage", "nowImage", "maskImage", "differenceImage" })
                finalManifest["images"]![0]![key] = "tail-patch.png";
            File.WriteAllText(Path.Combine(finalRoot, "manifest.json"), finalManifest.ToJsonString());
            aggregate.CaptureCommitted(new(1, marker.Ticks, "Ink", new(20, 0, 100, 0), [], TriggerKind: "recordingEnd"),
                new(Path.Combine(finalRoot, "tail-patch.png"), finalRoot, false, 1, 2));
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            byte[] mechanicalHash = SHA256.HashData(File.ReadAllBytes(writer.FilePath));
            aggregate.Complete();
            aggregate.Complete();
            Check(aggregate.IsComplete, "Complete is idempotent");
            Check(mechanicalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(writer.FilePath))), "Mechanical bytes remain unchanged");
            aggregate.Dispose();

            var lines = File.ReadLines(aggregatePath).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Check(J.Text(lines[0], "schema") == "memoline-aggregate/v1" && J.Text(lines[^1], "kind") == "footer", "Recoverable JSONL header and footer");
            var samples = lines.Where(e => J.Text(e, "kind") == "dimensionSample").ToArray();
            Check(samples.Length == 7 && samples.Select(e => J.Tick(e, "canvasViewportChanged", -1)).SequenceEqual(new long[] { 0, 1, 0, 1, 1, 1, 0 }), "Every observation emits its exact 0/1 value; replay snapshots are idempotent");
            Check(samples.All(s => new[] { "actionPatternContinuity", "toolContinuity", "layerContinuity", "regionContinuity", "timeContinuity" }
                .All(key => J.Get(s, key).ValueKind == JsonValueKind.Null)), "Future algorithms have empty numeric fields");
            Check(samples.All(s => J.Text(s, "algorithmVersion") == "csponly-dimensions/v1"), "Dimension algorithm is versioned separately");
            var packets = lines.Where(e => J.Text(e, "kind") == "packet" && J.Text(e, "boundaryKind") != "recordingEnd").ToArray();
            Check(packets.Length == 4, "Only true changes produce packets, including empty boundaries");
            var final = lines.Single(e => J.Text(e, "kind") == "packet" && J.Text(e, "boundaryKind") == "recordingEnd");
            Check(J.Tick(final, "toTicks", -1) == J.Tick(MemolineReader.Read(writer.FilePath).Last(), "ticks", -2),
                "Every session has a final packet closed at the native footer");
            var captured = packets.Single(p => J.Text(p, "status") == "captured");
            Check(J.Tick(captured, "fromTicks", -1) == firstTick && J.Tick(captured, "toTicks", -1) == secondTick, "Boundary ticks do not use save dispatch or commit time");
            Check(J.Tick(captured, "observationAppendId", -1) == (long)second.AppendId, "Observation points to native appendId, not Sequence");
            var wholeWindow = J.Get(captured, "eventPointers").EnumerateArray().ToArray();
            Check(wholeWindow.Length == 3 && wholeWindow.All(p => J.Tick(p, "ticks", -1) > firstTick && J.Tick(p, "ticks", -1) <= secondTick), "Packet references entire mechanical hardware window");
            Check(wholeWindow.All(p => J.Text(p, "sessionId") == writer.SessionId && J.Tick(p, "eventId", 0) > 0), "Packet hardware pointers retain native session and event IDs");
            var matrices = J.Get(captured, "dirtyMatrices").EnumerateArray().ToArray();
            var stroke = matrices.Single(m => J.Text(m, "id") == "stroke");
            var strokePointers = J.Get(stroke, "eventPointers").EnumerateArray().ToArray();
            Check(strokePointers.Length == 4 && strokePointers.All(p => J.Tick(p, "operationId", -1) == 77), "Matrix only references the matched native operation");
            Check(strokePointers.All(p => J.Tick(p, "appendId", -1) != (long)other.AppendId), "Forged input operation cannot change native attribution");
            Check(strokePointers.Any(p => J.Tick(p, "appendId", -1) == (long)sample.AppendId), "Legacy input eventId maps to the native appendId");
            Check(strokePointers.Single(p => J.Tick(p, "appendId", -1) == (long)begin.AppendId).GetProperty("context").GetBoolean(), "Pre-window continuation is context, not a new occurrence");
            Check(strokePointers.Single(p => J.Tick(p, "appendId", -1) == (long)historicalSample.AppendId).GetProperty("context").GetBoolean(),
                "A failed previous capture retains actual diff ownership before the latest boundary as context");
            var statePointers = J.Get(stroke, "statePointers").EnumerateArray().ToArray();
            Check(statePointers.Length == 1 && J.Tick(statePointers[0], "appendId", -1) == (long)brush.AppendId
                && statePointers[0].GetProperty("context").GetBoolean(), "State pointers exclude appendId=0 diagnostics");
            var fallback = matrices.Single(m => J.Text(m, "id") == "coarse");
            Check(J.Text(fallback, "attribution") == "unresolved" && J.Get(fallback, "eventPointers").GetArrayLength() == 0, "Coarse fallback never invents stroke ownership");
            Check(J.Get(stroke, "label").GetRawText() == J.Get(J.Get(captured, "manifest"), "labels")[0].GetRawText(), "Original dirty label is retained");
            Check(packets.Single(p => J.Tick(p, "toTicks", -1) == firstTick).GetProperty("reason").GetString() == "fixtureSaveFailed", "Failure reason is retained at final seal");
            var contact = J.Get(captured, "penContacts").EnumerateArray().Single();
            Check(J.Tick(contact, "operationId", -1) == 77 && PanelRegionMap.ReadLocation(contact)?.Region == "canvasViewport",
                "Packets retain native pen starting-region metadata");
            Check(J.Get(contact, "beginEventPointer").GetProperty("context").GetBoolean(), "A continued contact points to its original penBegin");
            Check(packets.Single(p => J.Tick(p, "toTicks", -1) == thirdTick).GetProperty("reason").GetString() == "noEditingInput", "No-edit viewport movement retains an empty packet");
            Check(packets.Single(p => J.Tick(p, "toTicks", -1) == fourthTick).GetProperty("reason").GetString() == "fixtureUnsafeAsset", "Unsafe asset failure seals without a fabricated diff");
            Check(packets.Where(p => J.Text(p, "status") == "empty").All(p => J.Get(p, "dirtyMatrices").GetArrayLength() == 0), "Empty packets have no invented matrix");
            var assets = lines.Where(e => J.Text(e, "kind") == "asset").ToArray();
            Check(assets.GroupBy(a => J.Text(a, "assetId")).Count() == 2, "All PNG references are deduplicated and contained");
            var firstMappings = J.Get(captured, "imageAssets").EnumerateArray().ToArray();
            Check(firstMappings.Single(a => J.Text(a, "path") == "patch.png").GetProperty("assetId").GetString()
                == firstMappings.Single(a => J.Text(a, "path") == "patch-alias.png").GetProperty("assetId").GetString(),
                "Identical PNG bytes with different paths share one asset");
            var finalMappings = J.Get(final, "imageAssets").EnumerateArray().ToArray();
            Check(finalMappings.Length == 2 && finalMappings.All(a => firstMappings.Any(b => J.Text(a, "assetId") == J.Text(b, "assetId"))),
                "A later packet can refer to original image bytes without rewriting the earlier packet");
            Check(assets.Any(a => J.Tick(a, "chunkCount", 0) > 1), "Large PNG is emitted in bounded chunks");
            foreach (var group in assets.GroupBy(a => J.Text(a, "assetId")))
            {
                using var reconstructed = new MemoryStream();
                foreach (var asset in group.OrderBy(a => J.Tick(a, "chunkIndex", -1)))
                {
                    byte[] bytes = Convert.FromBase64String(J.Text(asset, "base64")!);
                    Check(bytes.Length <= 512 * 1024 && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == J.Text(asset, "sha256"), "Chunk bytes and SHA256 are exact");
                    reconstructed.Write(bytes);
                }
                string relative = group.Key!.Split('/')[1];
                byte[] png = reconstructed.ToArray();
                Check(png.SequenceEqual(File.ReadAllBytes(Path.Combine(packetRoot, relative))), "Stored diff/preview pixels preserve the original PNG bytes");
                Check(Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() == J.Text(group.First(), "totalSha256"), "Whole image hash covers original asset bytes");
            }
            string sealedPath = Path.Combine(root, "integrated.memoline");
            BundleArchive.Seal(writer.FilePath, aggregatePath, sealedPath);
            var verified = BundleArchive.Verify(sealedPath);
            Check(verified.PacketCount == 5 && verified.DimensionSampleCount == 7 && verified.AssetCount == assets.Length,
                "Real aggregate pointers and chunked images pass final single-file sealing validation");
            var alteredContacts = File.ReadAllLines(aggregatePath).Select(line => System.Text.Json.Nodes.JsonNode.Parse(line)!).ToArray();
            var alteredPacket = alteredContacts.First(row => row["kind"]!.GetValue<string>() == "packet" && row["penContacts"]!.AsArray().Count > 0);
            alteredPacket["penContacts"]![0]!["penDownLocation"]!["region"] = "toolbar";
            string alteredPath = Path.Combine(root, "altered-region.aggregate.jsonl");
            File.WriteAllLines(alteredPath, alteredContacts.Select(row => row.ToJsonString()));
            Invalid(() => BundleArchive.Seal(writer.FilePath, alteredPath, Path.Combine(root, "altered-region.memoline")),
                "Seal rejects starting-region metadata changed relative to the native penBegin");

            // Some older native producers leave the begin frame's operation
            // null; its event ID is the operation anchor. Exercise the real
            // native reader using a separate compatibility fixture, without
            // changing a byte of the original recorder output.
            string legacyNative = Path.Combine(root, "legacy-anchor.memoline");
            var legacyFrames = MemolineReader.Read(writer.FilePath).Select(frame => System.Text.Json.Nodes.JsonNode.Parse(frame.GetRawText())!).ToArray();
            legacyFrames[0]["version"] = 1;
            foreach (var frame in legacyFrames.Where(frame => frame["appendId"] is not null))
            {
                ulong id = frame["appendId"]!.GetValue<ulong>();
                if (id == begin.AppendId) frame["operationId"] = null;
                else if (id == historicalSample.AppendId || id == sample.AppendId || id == end.AppendId)
                    frame["operationId"] = begin.EventId;
            }
            WriteV1(legacyNative, legacyFrames);
            var legacyManifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(packetRoot, "manifest.json")))!;
            legacyManifest["labels"]![0]!["operationId"] = begin.EventId;
            foreach (var input in legacyManifest["recognizer"]!["inputs"]!.AsArray())
                input!["operationId"] = begin.EventId;
            string legacyPacket = Path.Combine(root, "legacy-packet");
            Directory.CreateDirectory(legacyPacket);
            File.Copy(Path.Combine(packetRoot, "patch.png"), Path.Combine(legacyPacket, "patch.png"));
            File.Copy(Path.Combine(packetRoot, "patch-alias.png"), Path.Combine(legacyPacket, "patch-alias.png"));
            File.Copy(Path.Combine(packetRoot, "canvas-preview.png"), Path.Combine(legacyPacket, "canvas-preview.png"));
            File.WriteAllText(Path.Combine(legacyPacket, "manifest.json"), legacyManifest.ToJsonString());
            string legacyAggregatePath = Path.Combine(root, "legacy.aggregate.jsonl");
            using (var legacyAggregate = new AggregateSession(legacyAggregatePath, legacyNative, hello))
            {
                legacyAggregate.ObserveViewport(new(Realtime(baseline, snapshot: true), 0, 0, true, false));
                legacyAggregate.ObserveViewport(new(Realtime(second), secondTick, 1, false, true));
                legacyAggregate.CaptureCommitted(request, new("", legacyPacket, false, 1, 2));
                legacyAggregate.Complete();
            }
            var legacyPacketFrame = File.ReadLines(legacyAggregatePath).Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Single(frame => J.Text(frame, "kind") == "packet" && J.Text(frame, "boundaryKind") != "recordingEnd");
            var legacyPointers = J.Get(J.Get(legacyPacketFrame, "dirtyMatrices")[0], "eventPointers").EnumerateArray().ToArray();
            Check(legacyPointers.Length == 4, "Legacy begin anchor and following samples retain their exact native pointers");
            var legacyBegin = legacyPointers.Single(p => J.Tick(p, "appendId", -1) == (long)begin.AppendId);
            Check(J.Get(legacyBegin, "operationId").ValueKind == JsonValueKind.Null
                && J.Tick(legacyBegin, "eventId", -1) == (long)begin.EventId, "A null operation begin is associated by native eventId without rewriting it");
            Check(legacyPointers.All(p => J.Tick(p, "appendId", -1) != (long)other.AppendId), "Event anchor compatibility does not accept unrelated operations");
            Check(mechanicalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(writer.FilePath))), "Compatibility tests leave the native recording untouched");
        }
        finally { writer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        string interruptedPath = Path.Combine(root, "interrupted.aggregate.jsonl");
        using (var interrupted = new AggregateSession(interruptedPath, writer.FilePath, hello)) { }
        Check(!File.ReadLines(interruptedPath).Any(line => J.Text(JsonDocument.Parse(line).RootElement, "kind") == "footer"), "Dispose never announces a complete recording");
        return count;
    }

    private static void WriteV1(string path, IEnumerable<System.Text.Json.Nodes.JsonNode> frames)
    {
        using var stream = File.Create(path);
        stream.Write("MEMOLINE"u8);
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        stream.Write(header, 0, 4);
        foreach (var frame in frames)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(frame.ToJsonString());
            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xEDB88320U : 0);
            }
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ~crc);
            stream.Write(header);
            stream.Write(bytes);
        }
    }
}
