using System.Drawing.Imaging;
using System.Text.Json;
using CanvasLayerWatcher;
using BehaviorRecognizer.Realtime;

internal static class Stage12Checks
{
    internal static void Run(Action<bool, string> check, string artifacts)
    {
        string root = Path.Combine(artifacts, "stage12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Png(string name, bool changed = false)
        {
            string path = Path.Combine(root, name + ".png");
            using var image = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            if (changed) image.SetPixel(29, 29, Color.FromArgb(1, 4, 5, 6));
            image.Save(path, ImageFormat.Png);
            return path;
        }
        var layer = new LayerReference(7, "Ink", "ink-stage12");
        var metadata = JsonSerializer.SerializeToElement(new { width = 32, height = 32, fullCanvas = true, layerId = layer.Id, layerUuid = layer.Uuid });
        var evidence = new RecognitionEvidence(); evidence.Reset("stage12", 1000, 0);
        var history = new SnapshotHistory(Path.Combine(root, "history"));
        var request = new CaptureRequest(1, 10, layer.Name, new(0, 0, 100, 0), [], layer);
        var result = new CaptureResult(Png("unseeded"), metadata.GetRawText(), layer.Name, 10, JsonSerializer.SerializeToElement(new { success = true }));
        history.Advance(result, request, "stage12", evidence.Snapshot, () => true, CancellationToken.None);
        var missingBaseline = history.VerifyChain(layer.Uuid!);
        check(!missingBaseline.Valid && missingBaseline.Code == "NoBaseline", "An unproven first capture is rejected as NoBaseline");

        var seeded = new SnapshotHistory(Path.Combine(root, "seeded"));
        seeded.SeedBaseline(new("recording-seed", "external", "stage12", 1, 0, "", 32, 32, layer, metadata, [], 0, "recordingStart"), Png("seed"));
        var diff = seeded.Advance(new(Png("first-window", true), metadata.GetRawText(), layer.Name, 10, result.ControlResponse),
            request, "stage12", evidence.Snapshot, () => true, CancellationToken.None);
        check(!diff.Baseline && diff.DiffImages == 1, "A one-pixel alpha=1 change outside all predicted coverage is retained");
        check(seeded.VerifyChain(layer.Uuid!).Valid, "A proven recording-start baseline supports a precise chain");
        string manifest = Path.Combine(diff.PacketDirectory, "manifest.json");
        string original = File.ReadAllText(manifest);
        File.WriteAllText(manifest, "{broken");
        var corrupt = seeded.VerifyChain(layer.Uuid!);
        check(!corrupt.Valid && corrupt.Code == "ChainBroken" && corrupt.Breakpoint == diff.PacketDirectory,
            "A corrupt committed manifest cannot disappear from chain verification");
        File.WriteAllText(manifest, original);
        seeded.RegisterBaseline(new(8, "Reproduction", "reproduction-stage12"), Png("reproduction"), 20);
        check(seeded.LayerStacks[8].After is { BaselineSource: "reproduction", SessionId: "stage12", Generation: 1 },
            "Reproduction baseline retains the active session and has a distinct source");
        var reproduction = seeded.Advance(new(Png("reproduction-next", true),
            JsonSerializer.Serialize(new { width = 32, height = 32, fullCanvas = true, layerId = 8, layerUuid = "reproduction-stage12" }),
            "Reproduction", 30, result.ControlResponse), request with { Ticks = 30, LayerName = "Reproduction", Layer = new(8, "Reproduction", "reproduction-stage12") },
            "stage12", evidence.Snapshot, () => true, CancellationToken.None);
        check(!reproduction.Baseline && reproduction.DiffImages == 1 && seeded.VerifyChain("reproduction-stage12").Valid,
            "The first capture after reproduction baseline is a valid diff");
        var createdLayer = new LayerReference(9, "图层 1", "created-stage12");
        var createdMetadata = JsonSerializer.SerializeToElement(new { width = 32, height = 32, fullCanvas = true, layerId = 9, layerUuid = createdLayer.Uuid });
        check(!seeded.HasLayer(createdLayer.Uuid!, "stage12", 1), "A layer created during the recording has no snapshot yet");
        seeded.SeedBaselines([new(new("baseline-created", "external", "stage12", 1, 40, "", 32, 32, createdLayer, createdMetadata, [], 40, "layerCreated"), Png("created"))]);
        check(seeded.HasLayer(createdLayer.Uuid!, "stage12", 1) && !seeded.HasLayer(createdLayer.Uuid!, "other-session", 1),
            "A seeded new-layer baseline belongs to its own session only");
        var created = seeded.Advance(new(Png("created-next", true), createdMetadata.GetRawText(), createdLayer.Name, 50, result.ControlResponse),
            request with { Ticks = 50, LayerName = createdLayer.Name, Layer = createdLayer }, "stage12", evidence.Snapshot, () => true, CancellationToken.None);
        check(!created.Baseline && created.DiffImages == 1 && seeded.VerifyChain(createdLayer.Uuid!).Valid,
            "Strokes drawn on a new layer after its first save become a diff against that save");
        Console.WriteLine("STAGE 1/2 artifacts: " + root);
    }

    internal static async Task RunBoundaryAsync(Action<bool, string> check, string artifacts)
    {
        var state = new WatchState(); state.Reset();
        RecorderRealtimeEvent Event(string channel, string kind, long ticks, object data, ulong? operation = null)
            => new(1, "boundary", ticks, channel, kind, false, 0, (ulong)ticks, ticks, ticks, ticks, operation,
                null, null, JsonSerializer.SerializeToElement(data));
        void Layer(long ticks, string name) => state.Accept(Event("core.currentLayerState", "stateUpdated", ticks,
            new { status = "changed", state = name }));
        state.Accept(Event("core.canvasViewState", "stateUpdated", 1, new { status = "changed", state = new
        { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 } }));
        Layer(2, "A");
        state.Accept(Event("tablet", "penBegin", 3, new { penDownLocation = new { region = "outside", name = "panel", status = "matched" } }, 3));
        state.Accept(Event("tablet", "penEnd", 4, new { }, 3));
        Layer(5, "B");
        state.Accept(Event("tablet", "penBegin", 6, new { }, 6));
        state.Accept(Event("tablet", "penEnd", 7, new { }, 6));
        Layer(8, "C");
        LayerReference[] available = [new(1, "A", "uuid-a"), new(2, "B", "uuid-b"), new(3, "C", "uuid-c")];
        var boundary = state.ReserveEditBoundary("boundary-1", available, 10);
        check(boundary.Count == 3 && boundary.All(request => request.TriggerTicks == 10 && request.TriggerKind == "editBoundary"),
            "Boundary includes current layer and every stroke layer, including ineligible panel input");
        check(boundary.All(state.Valid) && boundary.Single(request => request.LayerName == "A").Operations.SequenceEqual([3L])
            && boundary.Single(request => request.LayerName == "B").Operations.SequenceEqual([6L]),
            "Boundary freezes operations by their pen-begin historical layer");
        string boundaryRoot = Path.Combine(artifacts, "boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(boundaryRoot);
        var backend = new FakeBoundaryBackend(boundaryRoot, available);
        var results = await SaveCapture.CaptureBoundaryAsync(backend, new(16, 16), boundary,
            () => true, () => false, _ => { }, CancellationToken.None);
        check(backend.SaveCount == 1 && backend.ExportCount == 3 && results.Count == 3,
            "One boundary save exports each requested layer once");
        check(results.All(item => item.Request.TriggerTicks == 10 && item.Result.SaveDispatchedTicks == 100),
            "Multi-layer boundary results retain shared trigger time and save response");
        var history = new SnapshotHistory(Path.Combine(boundaryRoot, "history"));
        var boundaryEvidence = new RecognitionEvidence(); boundaryEvidence.Reset("boundary", 1000, 0);
        var seeds = new List<BaselineSeed>();
        foreach (var layer in available)
        {
            string seedPath = Path.Combine(boundaryRoot, "seed-" + layer.Id + ".png");
            using (var image = new Bitmap(16, 16)) image.Save(seedPath, ImageFormat.Png);
            var metadata = JsonSerializer.SerializeToElement(new { width = 16, height = 16, fullCanvas = true, layerId = layer.Id, layerUuid = layer.Uuid });
            seeds.Add(new(new("seed-" + layer.Id, "external", "boundary", state.Generation, 0, "", 16, 16, layer,
                metadata, [], 0, "recordingStart"), seedPath));
        }
        history.SeedBaselines(seeds);
        foreach (var item in results)
        {
            var update = history.Advance(item.Result, item.Request, "boundary", boundaryEvidence.Snapshot, () => true, CancellationToken.None);
            check(!update.Baseline && update.DiffImages == 1 && history.VerifyChain(item.Request.Layer!.Uuid!).Valid,
                "Boundary produces a valid diff packet for layer " + item.Request.LayerName);
        }
        var groupTicks = Directory.EnumerateFiles(Path.Combine(history.Root, "packets"), "manifest.json", SearchOption.AllDirectories)
            .Select(path => { using var parsed = JsonDocument.Parse(File.ReadAllText(path)); return J.Tick(J.Get(parsed.RootElement, "now"), "triggerTicks", -1); }).ToArray();
        check(groupTicks.Length == 3 && groupTicks.All(ticks => ticks == 10), "Every boundary packet shares the native boundary ticks");
        string failedRoot = Path.Combine(boundaryRoot, "failed"); Directory.CreateDirectory(failedRoot);
        var invalidBackend = new FakeBoundaryBackend(failedRoot, available, badExportId: boundary[1].Layer!.Id);
        try
        {
            await SaveCapture.CaptureBoundaryAsync(invalidBackend, new(16, 16), boundary, () => true, () => false, _ => { }, CancellationToken.None);
            throw new Exception("Invalid boundary export accepted");
        }
        catch (InvalidDataException)
        {
            check(invalidBackend.SaveCount == 1 && invalidBackend.ExportCount == 2 && Directory.GetFiles(failedRoot, "*.png").Length == 0,
                "A failed multi-layer export removes all uncommitted PNGs and stops later exports");
        }
        foreach (var request in boundary) state.Complete(request, true);
        var next = state.ReserveEditBoundary("boundary-2", available, 20);
        check(next.Count == 3 && next.Select(r=>r.LayerName).ToHashSet().SetEquals(["A","B","C"]),
            "A fresh edit boundary includes previously captured layers so their dirty images remain editable across layers");
        check(next.All(r=>r.Operations.Length==0),"Repeated boundaries must not attribute already captured contacts to new packets");
        foreach (var request in next) state.Complete(request, true);

        // 2026-10-16 live session: with the canvas ROI still unknown, the layer-panel click that
        // left the paper layer counted as a contact on it and every later boundary failed.
        var paper = new WatchState(); paper.Reset();
        paper.Accept(Event("core.canvasViewState", "stateUpdated", 1, new { status = "changed", state = new
        { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 } }));
        paper.Accept(Event("core.clipState", "stateUpdated", 1, new { status = "changed", state = new { layers = new object[] {
            new { id = 4, name = "纸张", type_raw = 1584, is_paper = true, uuid = "uuid-paper" },
            new { id = 3, name = "图层 1", type_raw = 1, is_paper = false, uuid = "uuid-1" } } } }));
        void PaperLayer(long ticks, string name) => paper.Accept(Event("core.currentLayerState", "stateUpdated", ticks,
            new { status = "changed", state = name }));
        PaperLayer(2, "图层1"); PaperLayer(3, "纸张");
        paper.Accept(Event("mouse", "mouseDown", 4, new { button = "left", x = 2360, y = 1172 }, 4));
        paper.Accept(Event("mouse", "mouseUp", 5, new { button = "left", x = 2360, y = 1172 }, 4));
        PaperLayer(5, "图层1");
        var paperBoundary = paper.ReserveEditBoundary("paper-1", paper.KnownLayersAt(10), 10);
        check(paperBoundary is [{ LayerName: "图层 1", Layer.Uuid: "uuid-1" }],
            "A contact made while the paper layer is selected adds no layer to the boundary");
        foreach (var request in paperBoundary) paper.Complete(request, true);
        PaperLayer(11, "纸张");
        var onPaper = paper.ReserveEditBoundary("paper-2", paper.KnownLayersAt(12), 12);
        check(onPaper is [{ LayerName: "图层 1", Operations.Length: 0 }],
            "A boundary taken on the paper layer still refreshes previously captured layers");
        foreach (var request in onPaper) paper.Complete(request, true);
        // 2026-10-17 live session: an edit replaced the captured 图层 1 by 图层 2, then a paste
        // became a new 图层 1. The next boundary must not refresh that unrelated layer by name.
        PaperLayer(13, "图层2");
        LayerReference[] replaced = [new(14, "图层 2", "uuid-2"), new(15, "图层 1", "uuid-paste")];
        var afterEdit = paper.ReserveEditBoundary("paper-4", replaced, 14);
        check(afterEdit is [{ LayerName: "图层 2", Layer.Uuid: "uuid-2" }],
            "A captured layer that was deleted is not revived through another layer with its name");
        foreach (var request in afterEdit) paper.Complete(request, true);
        var unseeded = new WatchState(); unseeded.Reset();
        unseeded.Accept(Event("core.canvasViewState", "stateUpdated", 1, new { status = "changed", state = new
        { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 } }));
        unseeded.Accept(Event("core.clipState", "stateUpdated", 1, new { status = "changed", state = new { layers = new object[] {
            new { id = 4, name = "纸张", type_raw = 1584, is_paper = true, uuid = "uuid-paper" } } } }));
        unseeded.Accept(Event("core.currentLayerState", "stateUpdated", 2, new { status = "changed", state = "纸张" }));
        try { unseeded.ReserveEditBoundary("paper-3", unseeded.KnownLayersAt(3), 3); throw new Exception("Paper-only boundary accepted"); }
        catch (InvalidDataException error) { check(error.Message.Contains("是纸张"), "A boundary with only the paper layer names the paper layer"); }
        // 2026-10-18 live session: 图层 1 was created during the recording and only captured at the next
        // Alt+M, after 19 strokes, so none of them could be undone. The save that first lists it is its baseline.
        var appearing = new WatchState(); appearing.Reset();
        var appearances = new List<LayerAppearance>();
        appearing.LayerAppeared += appearances.Add;
        void Clip(long ticks, ulong appendId, string? snapshotPath, params object[] layers)
            => appearing.Accept(new(1, "created", ticks, "core.clipState", "stateUpdated", false, appendId, 0, ticks, ticks, ticks, null, null, null,
                JsonSerializer.SerializeToElement(new { status = "changed", state = new { layers },
                    rawResult = new { snapshotPath }, evidence = new { triggerTicks = ticks } })));
        object Raster(int id, string name, string uuid) => new { id, name, type_raw = 1, uuid };
        object PaperRow = new { id = 4, name = "纸张", type_raw = 1584, is_paper = true, uuid = "uuid-paper" };
        Clip(1, 1, "s1.clip", PaperRow, Raster(14, "图层 2", "uuid-old"));
        check(appearances.Count == 0, "Layers listed by the first CLIP state are covered by the recording baseline");
        Clip(5, 0, "edit.clip", PaperRow, Raster(17, "图层 1", "uuid-reproduction"));
        check(appearances.Count == 0, "A layer first listed inside an edit transaction is left to its reproduction baseline");
        Clip(9, 9, "s9.clip", PaperRow, Raster(14, "图层 2", "uuid-old"), Raster(19, "图层 1", "uuid-new"));
        check(appearances is [{ TriggerTicks: 9, SnapshotPath: "s9.clip", Layers: [{ Id: 19, Uuid: "uuid-new" }] }],
            "The first save listing a new layer becomes its baseline, at that save's trigger time");
        Clip(12, 12, "s12.clip", PaperRow, Raster(14, "图层 2", "uuid-old"), Raster(19, "图层 1", "uuid-new"));
        check(appearances.Count == 1, "A layer is seeded from the first save that lists it only");
        Clip(15, 15, null, PaperRow, Raster(20, "图层 3", "uuid-unsaved"));
        check(appearances.Count == 1, "A save whose file the recorder did not keep cannot be a baseline");
        using var coordinator = new CaptureCoordinator();
        using var first = await coordinator.EnterAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { using var ignored = await coordinator.EnterAsync(cancelled.Token); throw new Exception("Capture mutex accepted a cancelled waiter"); }
        catch (OperationCanceledException) { check(true, "Capture coordinator cancellation preserves the active owner"); }
    }

    private sealed class FakeBoundaryBackend(string root, LayerReference[] layers, long? badExportId = null) : ILayerCaptureBackend
    {
        public int SaveCount { get; private set; }
        public int ExportCount { get; private set; }
        public bool InputIdle => true;
        public Task<SavedLayerDocument> SaveSnapshotAsync(long triggerTicks, CancellationToken token)
        {
            SaveCount++;
            return Task.FromResult(new SavedLayerDocument("fake.clip", 100, JsonSerializer.SerializeToElement(new { success = true })));
        }
        public Task<IReadOnlyList<LayerReference>> InspectAsync(SavedLayerDocument document, CancellationToken token)
            => Task.FromResult<IReadOnlyList<LayerReference>>(layers);
        public Task<CaptureResult> ExportAsync(SavedLayerDocument document, LayerReference layer, Size size, CancellationToken token)
        {
            ExportCount++;
            string path = Path.Combine(root, layer.Id + ".png");
            using var image = new Bitmap(size.Width, size.Height); image.SetPixel(1, 1, Color.Red); image.Save(path, ImageFormat.Png);
            return Task.FromResult(new CaptureResult(path, JsonSerializer.Serialize(new { width = size.Width, height = size.Height,
                fullCanvas = true, layerId = layer.Id, layerUuid = layer.Id == badExportId ? "mismatched-uuid" : layer.Uuid }), layer.Name, document.SaveDispatchedTicks, document.ControlResponse));
        }
        public void Cleanup(SavedLayerDocument document) { File.Delete(Path.Combine(root, "fake.clip")); }
    }
}
