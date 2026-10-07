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
        check(next.Count == 1 && next[0].LayerName == "C", "Completed boundary consumes captured historical layer input");
        foreach (var request in next) state.Complete(request, true);
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
