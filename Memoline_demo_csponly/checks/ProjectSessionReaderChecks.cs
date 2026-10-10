using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Storage.Memoline;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class ProjectSessionReaderChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        void Reject(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { count++; return; }
            throw new InvalidOperationException(message);
        }
        string directory = Path.Combine(root, "project-session-reader");
        Directory.CreateDirectory(directory);
        byte[] empty = new byte[12];
        byte[] first = [10, 20, 30, 255, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] last = [10, 20, 30, 255, 90, 80, 70, 255, 0, 0, 0, 0];
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        async Task<(string Bundle, string Native, string Session, List<JsonObject> Rows)> Make(int ordinal)
        {
            var writer = new MemolineWriter(Path.Combine(directory, "native-" + ordinal), new { test = "sealed-reader" });
            var begin = writer.AppendStateRecord("editBoundaryRequested", writer.NowTicks, [], new { requestId = "first" })!;
            var end = writer.AppendStateRecord("editBoundaryRequested", writer.NowTicks, [], new { requestId = "last" })!;
            await writer.DisposeAsync();
            var header = MemolineReader.Read(writer.FilePath).First();
            long frequency = header.GetProperty("frequency").GetInt64();
            string session = writer.SessionId;
            JsonObject Node(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
            var rows = new List<JsonObject>
            {
                Node(new { kind = "header", sessionId = session, frequency })
            };
            void Asset(string id, byte[] bytes, bool split = false)
            {
                int chunks = split ? 2 : 1, offset = 0;
                for (int index = 0; index < chunks; index++)
                {
                    int length = index == chunks - 1 ? bytes.Length - offset : bytes.Length / chunks;
                    byte[] piece = bytes.AsSpan(offset, length).ToArray(); offset += length;
                    rows.Add(Node(new { kind = "asset", sessionId = session, assetId = id,
                        chunkIndex = index, chunkCount = chunks, base64 = Convert.ToBase64String(piece),
                        sha256 = Hash(piece), totalSha256 = Hash(bytes), encoding = "png", byteLength = bytes.Length }));
                }
            }
            byte[] mask = DirtyMatrixPaster.ClipboardImageCodec.EncodePng(1, 1, new byte[] { 255, 255, 255, 255 });
            Asset("shared-mask", mask, split: true);
            object Descriptor(string id, long ticks) => new { id, sessionId = session, generation = 1, triggerTicks = ticks,
                baselineSource = "recordingStart", width = 3, height = 1, layer = new { uuid = "ink", name = "Ink" } };
            void Packet(int number, long ticks, ulong append, int pixel, byte[] current, string afterId, long afterTicks, string boundary)
            {
                string id = "packet-" + number;
                byte[] after = DirtyMatrixPaster.ClipboardImageCodec.EncodePng(1, 1, new byte[4]);
                byte[] now = DirtyMatrixPaster.ClipboardImageCodec.EncodePng(1, 1, current.AsSpan(pixel * 4, 4));
                byte[] canvas = DirtyMatrixPaster.ClipboardImageCodec.EncodePng(3, 1, current);
                Asset(id + "/after", after); Asset(id + "/now", now); Asset(id + "/canvas", canvas, split: true);
                var manifest = new
                {
                    schema = "dirty-matrix-image-diff/v1", comparison = "fullResolution/v2", kind = "diff", id,
                    after = Descriptor(afterId, afterTicks), now = Descriptor("snapshot-" + number, ticks),
                    capture = new { triggerKind = boundary, triggerTicks = ticks },
                    recognizer = new { sessionId = session, frequency, fromTicks = afterTicks, toTicks = ticks },
                    labels = new[] { new { id = "label", source = "tablet", operationId = 1,
                        fromTicks = afterTicks, toTicks = ticks, coverage = new { radius = 1, segments = Array.Empty<object>() },
                        imageIds = new[] { "image" } } },
                    images = new[] { new { id = "image", bounds = new { left = pixel, top = 0, right = pixel + 1, bottom = 1, width = 1, height = 1 },
                        changedPixels = 1, afterImage = "after.png", nowImage = "now.png", maskImage = "mask.png", labelIds = new[] { "label" } } }
                };
                rows.Add(Node(new { kind = "packet", sessionId = session, id, fromTicks = afterTicks, toTicks = ticks, triggerTicks = ticks,
                    boundaryKind = boundary, status = "captured", observationAppendId = append,
                    eventPointers = Array.Empty<object>(), dirtyMatrices = Array.Empty<object>(),
                    imageAssets = new[] { new { path = "after.png", assetId = id + "/after" },
                        new { path = "now.png", assetId = id + "/now" }, new { path = "mask.png", assetId = "shared-mask" } },
                    fullCanvasAssetId = id + "/canvas", manifest }));
            }
            Packet(1, begin.Ticks, begin.AppendId, 0, first, "baseline", 0, "canvasViewportChanged");
            Packet(2, end.Ticks, end.AppendId, 1, last, "snapshot-1", begin.Ticks, "recordingEnd");
            rows.Add(Node(new { kind = "footer", sessionId = session }));
            string aggregation = Path.Combine(directory, "events-" + ordinal + ".jsonl");
            File.WriteAllLines(aggregation, rows.Select(row => row.ToJsonString()));
            string bundle = Path.Combine(directory, "session-" + ordinal + ".memoline");
            BundleArchive.Seal(writer.FilePath, aggregation, bundle);
            return (bundle, writer.FilePath, session, rows);
        }
        var one = await Make(1); var two = await Make(2);
        string project = Path.Combine(directory, "project.memoline"), clip = Path.Combine(directory, "project.clip");
        BundleArchive.AppendProjectSession(project, one.Bundle, clip);
        BundleArchive.AppendProjectSession(project, two.Bundle, clip);
        byte[] originalProject = File.ReadAllBytes(project);
        var restored = await ProjectSessionReader.ReadProjectAsync(project, Path.Combine(directory, "cache"));
        Check(restored.Count == 2 && restored.Select(session => session.SessionId).SequenceEqual(new[] { one.Session, two.Session }),
            "Sealed native sessions are restored in append order with their original identity.");
        Check(restored.All(session => session.Frequency > 0 && session.Packets.Count == 2), "Native session clocks and packet counts survive extraction.");
        Check(File.ReadAllBytes(restored[0].MechanicalPath).SequenceEqual(File.ReadAllBytes(one.Native)) &&
            File.ReadAllBytes(restored[1].MechanicalPath).SequenceEqual(File.ReadAllBytes(two.Native)), "Extraction retains every original native byte and append ID.");
        Check(restored.All(session => session.Packets[0].Before is { } before && before.Bytes.AsSpan().SequenceEqual(empty)),
            "Original recording-start canvas is reconstructed from complete reversed RGBA differences.");
        Check(restored.All(session => session.Packets[^1].Current is { } current && current.Bytes.AsSpan().SequenceEqual(last)),
            "Chunked embedded fullCanvasAssetId restores the final complete layer.");
        Check(restored.All(session => session.Packets[^1].IsBoundary), "Successful sealed recordingEnd is a trusted capture boundary.");
        Check(restored.All(session => session.Packets[0].Images[0].Mask.Count == 1 && session.Packets[1].Images[0].Mask.Count == 1),
            "Deduplicated earlier PNG assets are available to every packet reference.");
        Check(restored.All(session => session.Packets[^1].NowDescriptor.GetProperty("sessionId").GetString() == session.SessionId),
            "Snapshot descriptors preserve source session identity.");
        Check(File.ReadAllBytes(project).SequenceEqual(originalProject), "Reading history leaves the sealed source unchanged.");

        string Changed(string name, Action<List<JsonObject>> change)
        {
            var rows = one.Rows.Select(row => row.DeepClone().AsObject()).ToList(); change(rows);
            string aggregation = Path.Combine(directory, name + ".jsonl"), bundle = Path.Combine(directory, name + ".memoline");
            File.WriteAllLines(aggregation, rows.Select(row => row.ToJsonString()));
            BundleArchive.Seal(one.Native, aggregation, bundle); return bundle;
        }
        var invalidPath = Changed("escaped-path", rows => rows.First(row => row["kind"]!.GetValue<string>() == "packet")["imageAssets"]![0]!["path"] = "../escape.png");
        Reject(() => ProjectSessionReader.ReadProject(invalidPath, Path.Combine(directory, "escaped-cache")), "Embedded PNG paths cannot leave their packet directory.");
        Check(!File.Exists(Path.Combine(directory, "escaped-cache", "session-0", "diff", "packets", "escape.png")), "Rejected asset path creates no escaped output.");
        var wrongClock = Changed("wrong-manifest-clock", rows => rows.First(row => row["kind"]!.GetValue<string>() == "packet")["manifest"]!["recognizer"]!["frequency"] = 1);
        Reject(() => ProjectSessionReader.ReadProject(wrongClock, Path.Combine(directory, "wrong-clock-cache")), "Manifest clock must agree with native session frequency.");
        var wrongPixels = Changed("wrong-final-canvas", rows => rows.Last(row => row["kind"]!.GetValue<string>() == "packet")["fullCanvasAssetId"] = "packet-1/canvas");
        var broken = ProjectSessionReader.ReadProject(wrongPixels, Path.Combine(directory, "wrong-pixels-cache"));
        Check(broken[0].Packets[0].Before is null, "Contradictory final pixels produce no trusted reconstructed baseline.");
        var incomplete = Changed("empty-recording-end", rows =>
        {
            var packet = rows.Last(row => row["kind"]!.GetValue<string>() == "packet");
            packet["boundaryKind"] = "canvasViewportChanged"; packet["manifest"]!["capture"]!["triggerKind"] = "stateUpdated";
        });
        Check(!ProjectSessionReader.ReadProject(incomplete, Path.Combine(directory, "no-end-cache"))[0].Packets[^1].IsBoundary,
            "A previous viewport capture is not relabeled as recordingEnd.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { ProjectSessionReader.ReadProject(project, Path.Combine(directory, "cancelled-cache"), cancel.Token); }
            catch (OperationCanceledException) { count++; }
            Check(!Directory.Exists(Path.Combine(directory, "cancelled-cache")), "Pre-cancellation creates no extraction directory.");
        }
        string corrupted = Path.Combine(directory, "corrupted-stream.memoline"); File.Copy(one.Bundle, corrupted);
        using (var zip = ZipFile.Open(corrupted, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(BundleArchive.AggregationEntry + ".br")!;
            entry.Delete();
            using var bytes = zip.CreateEntry(BundleArchive.AggregationEntry + ".br").Open();
            bytes.Write(new byte[] { 1, 2, 3, 4 });
        }
        Reject(() => ProjectSessionReader.ReadProject(corrupted, Path.Combine(directory, "corrupt-cache")), "Corrupt indexed sealed stream is rejected before use.");
        Console.WriteLine($"Project session reader checks passed: {count}.");
        return count;
    }
}
