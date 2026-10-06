using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class EndPacketChecks
{
    public static int Run(string root)
    {
        int checks = 0;
        void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("End packet: " + reason); checks++; }
        foreach (string mode in new[] { "captured", "failed", "footerOnly", "noViewport", "noInput" })
        {
            string directory = Path.Combine(root, "end-" + mode);
            var records = new List<MemolineEvent>();
            var writer = new MemolineWriter(Path.Combine(directory, "mechanical"), new { mode });
            writer.RecordAppended += records.Add;
            var hello = new RecorderRealtimeEvent(1, writer.SessionId, 0, "system", "hello", false,
                0, 0, 0, 0, 0, null, null, null, JsonSerializer.SerializeToElement(new
                { frequency = Stopwatch.Frequency, originTicks = writer.ClockOriginTicks }));
            string aggregatePath = Path.Combine(directory, "aggregate.jsonl");
            using var aggregate = new AggregateSession(aggregatePath, writer.FilePath, hello);
            RecorderRealtimeEvent Message(MemolineEvent frame, string kind) => new(1, writer.SessionId, 0,
                kind == "recordingEndRequested" ? "system" : "core.canvasViewState", kind, false,
                frame.AppendId, frame.EventId, frame.Ticks, frame.AppendedTicks, writer.NowTicks,
                frame.OperationId, frame.RelatedEventIds, frame.DeviceSource, frame.Data);
            MemolineEvent Append(string kind)
            {
                writer.AppendState(kind, writer.NowTicks, [], new { module = "canvasViewState", status = "changed", state = new { } });
                return records[^1];
            }
            MemolineEvent Pen(string kind)
            {
                writer.AppendHardware(kind, new { x = 5, y = 5, inCsp = true }, new("tablet", "fixture", "fixture", "fixture"), 77);
                return records[^1];
            }
            try
            {
                var initial = Append("coreStateUpdated");
                aggregate.ObserveViewport(new(Message(initial, "stateUpdated"), initial.Ticks, 0, true, false));
                MemolineEvent? begin = mode == "noInput" ? null : Pen("penBegin");
                long from = 0;
                if (mode is not ("noViewport" or "noInput"))
                {
                    var view = Append("coreStateUpdated");
                    from = view.Ticks;
                    aggregate.ObserveViewport(new(Message(view, "stateUpdated"), view.Ticks, 1, false, true));
                }
                MemolineEvent? sample = begin is null ? null : Pen("penSample");
                MemolineEvent? marker = null;
                CaptureRequest? request = null;
                byte[]? imageBytes = null;
                if (mode != "footerOnly")
                {
                    marker = Append("recordingEndRequested");
                    var observation = new ViewportObservation(Message(marker, "recordingEndRequested"), marker.Ticks, 0, false, begin is not null);
                    aggregate.ObserveRecordingEnd(observation);
                    aggregate.ObserveRecordingEnd(observation);
                    request = new(1, marker.Ticks, "Ink", new(0, 0, 100, 0), begin is null ? [] : [77], TriggerKind: "recordingEnd");
                    aggregate.CaptureQueued(request);
                    if (mode == "captured")
                    {
                        string packetDirectory = Path.Combine(directory, "final-diff");
                        Directory.CreateDirectory(packetDirectory);
                        string png = Path.Combine(packetDirectory, "patch.png");
                        using (var pixels = new Bitmap(4, 4))
                        { pixels.SetPixel(1, 2, Color.Crimson); pixels.Save(png, ImageFormat.Png); }
                        imageBytes = File.ReadAllBytes(png);
                        var manifest = new
                        {
                            recognizer = new { sessionId = writer.SessionId, frequency = Stopwatch.Frequency, fromTicks = from,
                                toTicks = marker.Ticks, inputs = new[] { new { ticks = sample!.Ticks, operationId = 77,
                                    appendId = sample.AppendId, eventId = sample.EventId } }, states = Array.Empty<object>() },
                            capture = new { triggerTicks = marker.Ticks },
                            labels = new[] { new { id = "tail-stroke", operationId = 77L, source = "tablet", fromTicks = sample!.Ticks,
                                toTicks = sample.Ticks, stateIds = Array.Empty<string>(), imageIds = new[] { "tail-image" },
                                impactRange = new { canvasWidth = 4, canvasHeight = 4, rowRuns = new[] { new { row = 0, startColumn = 0, endColumnExclusive = 1 } } } } },
                            images = new[] { new { id = "tail-image", image = "patch.png" } }
                        };
                        File.WriteAllText(Path.Combine(packetDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, MemolineWriter.Json));
                        var update = new SnapshotUpdate(png, packetDirectory, false, 1, 1);
                        aggregate.CaptureCommitted(request, update);
                        using var prefix = new StreamReader(new FileStream(aggregatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                        Check(!prefix.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Any(l => J.Text(JsonDocument.Parse(l).RootElement, "kind") == "packet"),
                            "Final diff publication waits for the native footer");
                    }
                    else aggregate.CaptureFailed(request, "fixtureFinalSaveFailed");
                }
                else aggregate.RecordingEndFailed("fixtureControlUnavailable");
                if (begin is not null) { Pen("penSample"); Pen("penInterrupted"); }
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                byte[] hash = SHA256.HashData(File.ReadAllBytes(writer.FilePath));
                aggregate.Complete(); aggregate.Complete(); aggregate.Dispose();
                var rows = File.ReadLines(aggregatePath).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
                var packets = rows.Where(r => J.Text(r, "kind") == "packet").ToArray();
                var final = packets.Single(r => J.Text(r, "boundaryKind") == "recordingEnd");
                long footer = J.Tick(MemolineReader.Read(writer.FilePath).Last(), "ticks", -1);
                Check(J.Tick(final, "fromTicks", -1) == from && J.Tick(final, "toTicks", -1) == footer,
                    mode + " owns the full tail through the native footer");
                Check(J.Tick(final, "triggerTicks", -1) == (marker?.Ticks ?? footer), "Trigger ticks remain distinct from shutdown completion");
                Check(J.Tick(rows[^1], "lastBoundaryTicks", -1) == footer, "Aggregate footer closes the final range");
                var hardware = records.Where(r => r.Path == "hardware").ToArray();
                var pointers = packets.SelectMany(p => J.Get(p, "eventPointers").EnumerateArray()).ToArray();
                Check(pointers.Length == hardware.Length && pointers.Select(p => J.Tick(p, "appendId", -1)).Distinct().Count() == hardware.Length,
                    "Every native hardware event belongs to exactly one packet");
                if (begin is not null)
                {
                    var contact = J.Get(final, "penContacts").EnumerateArray().Single();
                    Check(J.Tick(J.Get(contact, "beginEventPointer"), "appendId", -1) == (long)begin.AppendId,
                        "A contact crossing the end boundary retains its actual penBegin");
                    Check(J.Get(final, "eventPointers").EnumerateArray().Any(p => J.Tick(p, "appendId", -1) == (long)hardware[^1].AppendId),
                        "Late interrupted-contact events are retained");
                }
                if (imageBytes is not null)
                {
                    var asset = rows.Single(r => J.Text(r, "kind") == "asset");
                    Check(Convert.FromBase64String(J.Text(asset, "base64")!).SequenceEqual(imageBytes), "Final diff PNG is embedded unchanged");
                    Check(J.Text(final, "status") == "captured" && J.Get(final, "dirtyMatrices").GetArrayLength() == 1,
                        "Final package contains its image matrix");
                    var matrix = J.Get(final, "dirtyMatrices")[0];
                    Check(J.Tick(J.Get(matrix, "eventPointers")[0], "appendId", -1) == (long)sample!.AppendId,
                        "Final matrix points to the native stroke sample");
                }
                else Check(J.Text(final, "reason") == (mode == "footerOnly" ? "fixtureControlUnavailable" : "fixtureFinalSaveFailed")
                    && J.Get(final, "dirtyMatrices").GetArrayLength() == 0, "Failed save still produces a truthful final package");
                BundleArchive.Seal(writer.FilePath, aggregatePath, Path.Combine(directory, "sealed.memoline"));
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(writer.FilePath))), "Native bytes are never rewritten");
                string missing = Path.Combine(directory, "missing-final.jsonl");
                File.WriteAllLines(missing, rows.Where(r => J.Text(r, "boundaryKind") != "recordingEnd").Select(r => r.GetRawText()));
                try { BundleArchive.Seal(writer.FilePath, missing, Path.Combine(directory, "invalid.memoline"));
                    throw new InvalidOperationException("Seal accepted a missing final packet"); }
                catch (InvalidDataException) { checks++; }
            }
            finally { writer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        return checks;
    }
}
