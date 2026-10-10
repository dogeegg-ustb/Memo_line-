using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class SealingTailChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Sealing tail: " + message); count++; }
        void Reject(Action action, string message)
        { try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { count++; return; } throw new InvalidOperationException(message); }

        foreach (bool twoLayers in new[] { false, true })
        {
            using var fixture = new Fixture(Path.Combine(root, twoLayers ? "sealing-tail-two" : "sealing-tail-one"));
            fixture.Draw(1, 101, 3); fixture.Draw(1, 102, 5);
            if (twoLayers) { fixture.Layer(2); fixture.Draw(2, 103, 7); }
            else fixture.Draw(1, 103, 7);
            fixture.LayerFailure(); // Stop panel stole foreground before the final refresh.
            Check(fixture.State.LayerName is null, "The stop focus failure really invalidates current layer.");
            fixture.Refresh();
            var requests = fixture.End();
            Check(requests.Count == (twoLayers ? 2 : 1), "Final reservation includes every uncaptured dirty layer.");
            Check(requests.All(request => request.Operations.Length > 0), "No viewport change is needed to reserve final native pen operations.");
            var backend = new PixelBackend(fixture.Directory, fixture.Layers, fixture.Pixels);
            var actions = new FixtureActions(fixture, requests, backend);
            await RecordingEndCoordinator.RunAsync(actions, CancellationToken.None);
            Check(backend.Saves == 1 && backend.Exports == requests.Count, "All final layers use one saved CLIP snapshot.");
            Check(!ReadRows(fixture.Aggregate.FilePath).Any(row => J.Text(row, "kind") == "packet"),
                "Final pixels and matrices remain pending until native footer proves the complete prefix.");
            await fixture.StopNativeAsync();
            byte[] nativeHash = SHA256.HashData(File.ReadAllBytes(fixture.Writer.FilePath));
            fixture.Aggregate.Complete();
            Check(fixture.Aggregate.IsComplete, "A stopped session with captured tail is complete.");
            fixture.Aggregate.Dispose();
            var rows = ReadRows(fixture.Aggregate.FilePath);
            var packets = rows.Where(row => J.Text(row, "kind") == "packet").ToArray();
            Check(packets.Length == requests.Count && packets.All(packet => J.Text(packet, "status") == "captured"),
                "Every pending tail layer commits a captured package, never a successful empty substitute.");
            Check(packets.Sum(packet => J.Get(packet, "dirtyMatrices").GetArrayLength()) >= 3,
                "The final three contacts own stored dirty matrices without a later shortcut or viewport trigger.");
            Check(packets.SelectMany(packet => J.Get(packet, "dirtyMatrices").EnumerateArray())
                .All(matrix => J.Get(matrix, "eventPointers").GetArrayLength() > 0), "Tail matrices resolve to real native hardware pointers.");
            Check(packets.All(packet => J.Get(packet, "fullCanvasAssetId").ValueKind == JsonValueKind.String),
                "Every final layer stores a complete embedded PNG asset.");
            var owned = packets.SelectMany(packet => J.Get(packet, "eventPointers").EnumerateArray())
                .Where(pointer => J.Get(pointer, "context").ValueKind != JsonValueKind.True).ToArray();
            Check(owned.Length == 9 && owned.Select(pointer => J.Tick(pointer, "appendId", -1)).Distinct().Count() == 9,
                "The main final window owns all three contacts exactly once across multiple layer packets.");
            string bundle = Path.Combine(fixture.Directory, "sealed.memoline");
            BundleArchive.Seal(fixture.Writer.FilePath, fixture.Aggregate.FilePath, bundle);
            var sessions = await ProjectSessionReader.ReadProjectAsync(bundle, Path.Combine(fixture.Directory, "readback"));
            Check(sessions[0].Packets.Count == requests.Count && sessions[0].Packets.All(packet => packet.IsBoundary && packet.Current is not null),
                "Sealed readback restores every final layer and recognizes its validated boundary.");
            Check(nativeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Writer.FilePath))), "Sealing never rewrites native pen records.");
        }

        foreach (string mode in new[] { "captureMissing", "pngMissing", "laterPen", "laterMouse", "secondLayerFailed", "unknownLayoutPen", "undoKey", "deleteKey", "layerClick", "externalInjectedPen", "externalInjectedUndo", "externalInjectedMouse" })
        {
            using var fixture = new Fixture(Path.Combine(root, "sealing-tail-" + mode));
            fixture.Draw(1, 201, 3);
            if (mode == "secondLayerFailed") { fixture.Layer(2); fixture.Draw(2, 202, 5); }
            fixture.Refresh(); var requests = fixture.End();
            if (mode == "captureMissing") fixture.Aggregate.RecordingEndFailed("fixtureFinalCaptureUnavailable");
            else
            {
                var backend = new PixelBackend(fixture.Directory, fixture.Layers, fixture.Pixels);
                await new FixtureActions(fixture, requests, backend).CaptureAndCommitAsync(requests, CancellationToken.None);
                if (mode == "pngMissing")
                {
                    string png = fixture.History.LayerStacks[requests[0].Layer!.Id].Now!.ImageFile;
                    File.Delete(Path.Combine(fixture.History.Root, png));
                }
                if (mode == "laterPen") fixture.Draw(1, 203, 7);
                if (mode == "laterMouse") fixture.MouseDraw(204);
                if (mode == "unknownLayoutPen") fixture.UnknownLayoutContact(205);
                if (mode == "undoKey") fixture.KeyInput("Z", false);
                if (mode == "deleteKey") fixture.KeyInput("Delete", false);
                if (mode == "layerClick") fixture.LayerClick(false);
                if (mode == "externalInjectedPen") fixture.InjectedContact();
                if (mode == "externalInjectedUndo") fixture.KeyInput("Z", true);
                if (mode == "externalInjectedMouse") fixture.LayerClick(true);
                if (mode == "secondLayerFailed") fixture.Aggregate.CaptureFailed(requests[1], "fixtureSecondaryLayerFailed");
            }
            await fixture.StopNativeAsync();
            Reject(() => fixture.Aggregate.Complete(), mode + " must prevent successful publication.");
            Check(!fixture.Aggregate.IsComplete && File.Exists(fixture.Writer.FilePath)
                && !ReadRows(fixture.Aggregate.FilePath).Any(row => J.Text(row, "kind") == "footer"),
                mode + " retains complete native bytes and unfinished aggregate for recovery.");
        }

        using (var empty = new Fixture(Path.Combine(root, "sealing-tail-empty")))
        {
            await empty.StopNativeAsync(); empty.Aggregate.Complete();
            Check(empty.Aggregate.IsComplete, "Cancelled initialization or a session with no canvas input can close truthfully.");
        }
        using (var panel = new Fixture(Path.Combine(root, "sealing-tail-panel")))
        {
            panel.PanelContact(); await panel.StopNativeAsync(); panel.Aggregate.Complete();
            Check(panel.Aggregate.IsComplete, "Panel pen contacts are not mistaken for uncaptured canvas drawing.");
        }
        using (var unknown = new Fixture(Path.Combine(root, "sealing-tail-unknown-only")))
        {
            unknown.UnknownLayoutContact(206); await unknown.StopNativeAsync();
            Reject(() => unknown.Aggregate.Complete(), "An unavailable pen layout cannot make an uncaptured CSP contact look like a no-drawing session.");
        }
        using (var unconfirmed = new Fixture(Path.Combine(root, "sealing-tail-unconfirmed-layer")))
        {
            unconfirmed.LayerFailure(); unconfirmed.Draw(1, 401, 3); unconfirmed.Refresh();
            Reject(() => unconfirmed.End(), "An unconfirmed dirty stroke layer cannot silently become the current layer.");
        }
        foreach (bool mouse in new[] { false, true })
        {
            using var interleaved = new Fixture(Path.Combine(root, mouse ? "sealing-tail-mouse-other-layer" : "sealing-tail-pen-other-layer"));
            if (mouse) interleaved.MouseOnCurrent(601); else interleaved.Draw(1, 601, 3);
            interleaved.Layer(2); interleaved.Draw(2, 602, 5);
            var ordinary = interleaved.CompleteCurrentViewport();
            Check(ordinary.Operations.SequenceEqual(new long[] { 602 }), "A regular capture of B consumes B contacts only.");
            interleaved.Refresh(); var final = interleaved.End();
            Check(final.Single(request => request.Layer!.Id == 1).Operations.SequenceEqual(new long[] { 601 }),
                "Final capture retains the uncaptured " + (mouse ? "mouse" : "pen") + " contact on A after successful capture of B.");
        }
        foreach (bool mouse in new[] { false, true })
        {
            using var unavailable = new Fixture(Path.Combine(root, mouse ? "sealing-tail-unknown-mouse-layer" : "sealing-tail-unknown-pen-layer"));
            if (mouse)
            {
                unavailable.InvalidateViewport();
                Check(!unavailable.State.HasCanvasArea, "An unconfirmed viewport clears the stale mouse ROI before input is classified.");
                unavailable.MouseOnCurrent(701);
            }
            else unavailable.UnknownLayoutContact(701);
            unavailable.Layer(2); unavailable.Draw(2, 702, 5); unavailable.Refresh();
            var final = unavailable.End();
            Check(final.Single(request => request.Layer!.Id == 1).Operations.SequenceEqual(new long[] { 701 }),
                "Unknown " + (mouse ? "mouse canvas ROI" : "pen layout") + " still reserves the historical dirty layer A before switching to B.");
        }
        var validResponse = JsonSerializer.SerializeToElement(new {
            success = true, sessionId = "session", requestId = "request", requestedTicks = 20,
            results = new { currentLayerState = new { status = "unchanged", state = "Ink", evidence = new { capturedTicks = 20 } } }
        });
        Check(RecorderControlClient.ParseStates(validResponse, "session", "request", ["currentLayerState"]).Count == 1,
            "Fresh confirmed explicit state response is accepted.");
        Reject(() => RecorderControlClient.ParseStates(validResponse, "other-session", "request", ["currentLayerState"]), "Foreign state session must be rejected.");
        var stale = JsonSerializer.SerializeToElement(new {
            success = true, sessionId = "session", requestId = "request", requestedTicks = 21,
            results = new { currentLayerState = new { status = "unchanged", state = "Ink", evidence = new { capturedTicks = 20 } } }
        });
        Reject(() => RecorderControlClient.ParseStates(stale, "session", "request", ["currentLayerState"]), "Old cached layer response must not satisfy the final refresh.");
        Console.WriteLine($"Sealing tail checks passed: {count}; no recorder process or CSP input started.");
        return count;
    }

    private static JsonElement Parse(string line) { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
    private static JsonElement[] ReadRows(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var rows = new List<JsonElement>();
        while (reader.ReadLine() is { } line) rows.Add(Parse(line));
        return rows.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; }
        internal MemolineWriter Writer { get; }
        internal AggregateSession Aggregate { get; }
        internal WatchState State { get; } = new(requireConfirmedViewChange: true);
        internal SnapshotHistory History { get; }
        internal LayerReference[] Layers { get; } = [new(1, "Ink A", "ink-a"), new(2, "Ink B", "ink-b")];
        internal Dictionary<long, List<int>> Pixels { get; } = [];
        private readonly RecognitionEvidence _evidence = new();
        private int _layer = 1;
        internal Fixture(string directory)
        {
            Directory = directory; System.IO.Directory.CreateDirectory(directory);
            Writer = new(Path.Combine(directory, "native"), new { test = "sealing-tail" }, new MemolineWriterOptions { SuccessfulStatesOnly = false });
            State.Reset(); State.SetCanvasArea(new(0, 0, 16, 16)); _evidence.Reset(Writer.SessionId, Stopwatch.Frequency, 0);
            Aggregate = new(Path.Combine(directory, "aggregate.jsonl"), Writer.FilePath,
                new(1, Writer.SessionId, 0, "system", "hello", false, 0, 0, 0, 0, 0, null, null, null,
                    JsonSerializer.SerializeToElement(new { frequency = Stopwatch.Frequency, originTicks = Writer.ClockOriginTicks })));
            History = new(Path.Combine(directory, "diffs"));
            Append("workspaceStatus", new { status = "ready", layout = new { regions = new Dictionary<string, int[]> {
                ["画布视口"] = [0, 0, 16, 16], ["图层"] = [20, 0, 10, 10]
            } } });
            Refresh();
            var seeds = Layers.Select(layer => {
                string png = Path.Combine(directory, "baseline-" + layer.Id + ".png");
                using (var bitmap = new Bitmap(16, 16)) bitmap.Save(png, ImageFormat.Png);
                return new BaselineSeed(new("baseline-" + layer.Id, "fixture", Writer.SessionId, State.Generation,
                    Writer.NowTicks, "", 16, 16, layer, Metadata(layer), [], Writer.NowTicks, "recordingStart"), png);
            }).ToArray();
            History.SeedBaselines(seeds);
        }
        private RecorderRealtimeEvent Message(MemolineEvent frame, string channel, string kind) => new(1, Writer.SessionId, checked((long)frame.AppendId),
            channel, kind, false, frame.AppendId, frame.EventId, frame.Ticks, frame.AppendedTicks, Writer.NowTicks,
            frame.OperationId, frame.RelatedEventIds, frame.DeviceSource, frame.Data);
        private MemolineEvent Append(string kind, object data, string? channel = null)
        {
            var frame = Writer.AppendStateRecord(kind, Writer.NowTicks, [], data)!;
            if (channel is not null) { var message = Message(frame, channel, "stateUpdated"); State.Accept(message); _evidence.Accept(message); }
            return frame;
        }
        internal void Layer(int id)
        {
            _layer = id;
            Append("coreStateUpdated", new { module = "currentLayerState", status = "changed", state = Layers[id - 1].Name }, "core.currentLayerState");
        }
        internal void LayerFailure() => Append("coreStateUpdated", new { module = "currentLayerState", status = "unknown", error = "CSP not foreground" }, "core.currentLayerState");
        internal void InvalidateViewport() => Append("coreStateUpdated", new {
            module = "canvasViewState", status = "unknown", error = "workspace layout unavailable"
        }, "core.canvasViewState");
        internal void Refresh()
        {
            Layer(_layer);
            Append("coreStateUpdated", new { module = "clipState", status = "changed", state = new { layers = Layers.Select(layer =>
                new { id = layer.Id, name = layer.Name, uuid = layer.Uuid, kind = 1 }).ToArray() } }, "core.clipState");
            Append("coreStateUpdated", new { module = "canvasViewState", status = "unchanged", state = new {
                canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0
            } }, "core.canvasViewState");
            Append("coreStateUpdated", new { module = "brushState", status = "unchanged", state = new { name = "G筆",
                properties = new[] { new { key = "brush_size", value = 1, unit = "px" } } } }, "core.brushState");
        }
        internal void Draw(int layer, ulong operation, int pixel)
        {
            if (layer != _layer) Layer(layer);
            if (!Pixels.TryGetValue(layer, out var pixels)) Pixels[layer] = pixels = [];
            pixels.Add(pixel);
            foreach (string kind in new[] { "penBegin", "penSample", "penEnd" }) Pen(kind, operation, pixel, "canvasViewport", false);
        }
        private void Pen(string kind, ulong operation, int pixel, string region, bool injected)
        {
            var frame = Writer.AppendHardwareRecord(kind, new { x = pixel, y = pixel, inCsp = true, injected,
                normalizedPressure = kind == "penEnd" ? 0.0 : 0.5, tiltX = 0, tiltY = 0, heldKeys = Array.Empty<int>(),
                penDownLocation = new PenDownLocation(region, region, pixel, pixel, "windowsCursor", "matched") },
                new("pen", "fixture", "fixture", "fixture"), operation)!;
            var message = Message(frame, "tablet", kind); State.Accept(message); _evidence.Accept(message);
        }
        internal void PanelContact() { Pen("penBegin", 501, 22, "layers", false); Pen("penSample", 501, 22, "layers", false); Pen("penEnd", 501, 22, "layers", false); }
        internal void InjectedContact() { Pen("penBegin", 502, 3, "canvasViewport", true); Pen("penSample", 502, 3, "canvasViewport", true); Pen("penEnd", 502, 3, "canvasViewport", true); }
        internal void UnknownLayoutContact(ulong operation)
        {
            foreach (string kind in new[] { "penBegin", "penSample", "penEnd" })
            {
                var frame = Writer.AppendHardwareRecord(kind, new { x = 3, y = 3, inCsp = true, injected = false,
                    normalizedPressure = kind == "penEnd" ? 0.0 : 0.5,
                    penDownLocation = new PenDownLocation("other", "其他", 3, 3, "windowsCursor", "layoutUnavailable") },
                    new("pen", "fixture", "fixture", "fixture"), operation);
                var message = Message(frame, "tablet", kind); State.Accept(message); _evidence.Accept(message);
            }
        }
        internal void KeyInput(string key, bool injected)
        {
            int vk = key == "Delete" ? 0x2e : key[0];
            int[] heldKeys = key == "Z" ? [17, vk] : [vk];
            Writer.AppendState("keyboardStateChanged", Writer.NowTicks, [], new {
                action = "keyDown", key, vk, pressed = true, injected, extraInfo = "0x0", heldKeys,
                deviceSource = new HardwareDeviceSource("keyboard", "fixture", "fixture", "fixture")
            });
            Writer.AppendHardware("keyInput", new {
                key, vk, injected, extraInfo = "0x0", heldKeys, modifiers = key == "Z" ? new[] { "Control" } : Array.Empty<string>(),
                combination = key == "Z", repeat = false, shortcutMatch = (string?)null
            }, new("keyboard", "fixture", "fixture", "fixture"));
        }
        internal void LayerClick(bool injected) => Writer.AppendHardware("mouseDown", new {
            x = 22, y = 3, button = "left", inCsp = true, injected, extraInfo = "0x0"
        }, new("mouse", "fixture", "fixture", "fixture"));
        internal void MouseDraw(ulong operation)
        {
            Writer.AppendHardware("mouseDown", new { x = 3, y = 3, button = "left", inCsp = true, injected = false },
                new("mouse", "fixture", "fixture", "fixture"), operation);
            Writer.AppendHardware("mouseDrag", new { x = 4, y = 4, button = "left", inCsp = true, injected = false },
                new("mouse", "fixture", "fixture", "fixture"), operation);
            Writer.AppendHardware("mouseUp", new { x = 4, y = 4, button = "left", inCsp = true, injected = false },
                new("mouse", "fixture", "fixture", "fixture"), operation);
        }
        internal void MouseOnCurrent(ulong operation)
        {
            foreach (string kind in new[] { "mouseDown", "mouseDrag", "mouseUp" })
            {
                var frame = Writer.AppendHardwareRecord(kind, new { x = 3, y = 3, button = "left", inCsp = true, injected = false },
                    new("mouse", "fixture", "fixture", "fixture"), operation);
                var message = Message(frame, "mouse", kind); State.Accept(message); _evidence.Accept(message);
            }
        }
        internal CaptureRequest CompleteCurrentViewport()
        {
            var frame = Writer.AppendStateRecord("coreStateUpdated", Writer.NowTicks, [], new {
                module = "canvasViewState", status = "changed", state = new {
                    canvasOriginScreenPx = new { x = 1, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0
                }
            })!;
            var message = Message(frame, "core.canvasViewState", "stateUpdated");
            var request = State.Accept(message) ?? throw new InvalidOperationException("Expected an ordinary viewport capture.");
            _evidence.Accept(message); State.Complete(request, true); return request;
        }
        internal IReadOnlyList<CaptureRequest> End()
        {
            var frame = Append("recordingEndRequested", new { boundaryKind = "recordingEnd" });
            var message = Message(frame, "system", "recordingEndRequested");
            Aggregate.ObserveRecordingEnd(new(message, frame.Ticks, 0, false, State.Dirty));
            var frozen = _evidence.Freeze(frame.Ticks);
            return State.ReserveRecordingEndLayers(message).Select(request => request with {
                Context = new(Writer.SessionId, new Size(16, 16), "offline", frozen)
            }).ToArray();
        }
        internal Task StopNativeAsync() => Writer.DisposeAsync().AsTask();
        internal static JsonElement Metadata(LayerReference layer) => JsonSerializer.SerializeToElement(new {
            fullCanvas = true, width = 16, height = 16, layerId = layer.Id, layerUuid = layer.Uuid
        });
        public void Dispose() { Writer.DisposeAsync().AsTask().GetAwaiter().GetResult(); Aggregate.Dispose(); }
    }

    private sealed class PixelBackend(string root, IReadOnlyList<LayerReference> layers, Dictionary<long, List<int>> pixels) : ILayerCaptureBackend
    {
        internal int Saves, Exports;
        public bool InputIdle => true;
        public Task<SavedLayerDocument> SaveSnapshotAsync(long ticks, CancellationToken token)
        { Saves++; return Task.FromResult(new SavedLayerDocument("offline.clip", ticks, JsonSerializer.SerializeToElement(new { success = true }))); }
        public Task<IReadOnlyList<LayerReference>> InspectAsync(SavedLayerDocument document, CancellationToken token) => Task.FromResult(layers);
        public Task<CaptureResult> ExportAsync(SavedLayerDocument document, LayerReference layer, Size size, CancellationToken token)
        {
            Exports++; string png = Path.Combine(root, "final-" + layer.Id + ".png");
            using (var bitmap = new Bitmap(16, 16)) {
                foreach (int pixel in pixels.GetValueOrDefault(layer.Id) ?? []) bitmap.SetPixel(pixel, pixel, Color.Black);
                bitmap.Save(png, ImageFormat.Png);
            }
            return Task.FromResult(new CaptureResult(png, Fixture.Metadata(layer).GetRawText(), layer.Name, document.SaveDispatchedTicks, document.ControlResponse));
        }
        public void Cleanup(SavedLayerDocument document) { }
    }

    private sealed class FixtureActions(Fixture fixture, IReadOnlyList<CaptureRequest> requests, PixelBackend backend) : IRecordingEndActions
    {
        public Task<IReadOnlyList<CaptureRequest>> PrepareAsync(CancellationToken token) => Task.FromResult(requests);
        public async Task CaptureAndCommitAsync(IReadOnlyList<CaptureRequest> captures, CancellationToken token)
        {
            foreach (var request in captures) fixture.Aggregate.CaptureQueued(request);
            var results = await SaveCapture.CaptureBoundaryAsync(backend, new Size(16, 16), captures,
                () => true, () => false, _ => { }, token);
            foreach (var result in results)
            {
                var update = fixture.History.Advance(result.Result, result.Request, fixture.Writer.SessionId,
                    result.Request.Context!.Evidence.Snapshot, () => true, token);
                fixture.Aggregate.CaptureCommitted(result.Request, update);
                fixture.State.Complete(result.Request, true);
            }
        }
    }
}
