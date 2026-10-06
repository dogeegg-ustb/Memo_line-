using System.Drawing.Imaging;
using System.Text.Json;
using BehaviorRecognizer.Realtime;
using CanvasLayerWatcher;
using DirtyMatrix.Core;

internal static class DiffChecks
{
    private static RecorderRealtimeEvent Event(string channel, string kind, long ticks, object data, ulong? op = null)
        => new(1, "test", ticks, channel, kind, false, 0, (ulong)ticks, ticks, ticks, ticks, op, null, null,
            JsonSerializer.SerializeToElement(data));
    private static RecorderRealtimeEvent Core(string channel, long ticks, object state)
        => Event("core." + channel, "stateUpdated", ticks, new { status = "changed", state, evidence = new { capturedTicks = ticks } });
    private static object Brush(double value, string unit = "px", bool fixedSize = false) => new
    {
        name = "Test brush", properties = new object[]
        { new { key = "brush_size", value, unit }, new { key = "brush_size.specify_by_size_on_screen", value = fixedSize } }
    };
    private static RecorderRealtimeEvent Pen(long ticks, string kind, double x, double y, ulong op = 1)
        => Event("tablet", kind, ticks, new { screenX = x, screenY = y, inCsp = true, pressure = .5, heldKeys = Array.Empty<int>() }, op);
    private static RecognitionEvidence Ready()
    {
        var evidence = new RecognitionEvidence(); evidence.Reset("test", 1000, 0);
        evidence.Configuration(0, JsonSerializer.SerializeToElement(new
        { settings = new { navigatorOcrSelection = new { layout = new { regions = new Dictionary<string, int[]> { ["画布视口"] = [100, 200, 128, 128] } } } } }));
        evidence.Accept(Core("currentLayerState", 1, "Ink"));
        evidence.Accept(Core("canvasViewState", 1, new
        { canvasOriginScreenPx = new { x = 100, y = 200 }, ocrScalePercent = 50, ocrRotationDegrees = 0 }));
        evidence.Accept(Core("brushState", 1, Brush(10)));
        evidence.Accept(Core("colorState", 1, new { rgb = new[] { 1, 2, 3 }, hex = "#010203" }));
        evidence.Accept(Core("clipState", 1, new { canvas = new { resolution = 254 }, layers = new[] { new { id = 3, name = "Ink", uuid = "ink" } } }));
        evidence.Accept(Event("tablet", "driverConfiguration", 1, new { snapshotId = "driver-1", pressureMappingStatus = "available" }));
        return evidence;
    }
    private static CaptureRequest Request(long ticks = 20, string name = "Ink", long generation = 1)
        => new(generation, ticks, name, new(100, 200, 50, 0), [1], new(3, name, name == "Ink" ? "ink" : "other"));
    private static string Png(string root, string name, Action<Bitmap>? paint = null, int width = 256, int height = 256)
    {
        string path = Path.Combine(root, name + ".png");
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb); paint?.Invoke(image); image.Save(path, ImageFormat.Png); return path;
    }
    internal static void Run(Action<bool, string> check, string artifacts)
    {
        string root = Path.Combine(artifacts, "diff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var evidence = Ready(); evidence.Accept(Pen(5, "penBegin", 120, 220)); evidence.Accept(Pen(6, "penSample", 130, 220));
        evidence.Accept(Core("brushState", 7, Brush(1, "mm", true)));
        evidence.Accept(Pen(8, "penSample", 140, 220)); evidence.Accept(Pen(9, "penEnd", 140, 220));
        evidence.Accept(Event("keyboard", "keyDown", 3, new { heldKeys = new[] { 65 } }));
        var window = evidence.Snapshot(0, 10); var dirty = RecognitionEvidence.Build(window, Request(), new(256, 256));
        check(dirty.Labels.Length == 2, "Brush state changes split labels while preserving stroke continuity");
        check(dirty.Labels[0].Coverage.Segments[0].Start == new PointD(40, 40), "Recognizer screen coordinates convert to full-canvas pixels");
        check(dirty.Labels[1].Coverage.Segments[0] == new StrokeSegment(new(60, 40), new(80, 40)), "State change does not lose the connecting stroke segment");
        check(Math.Abs(dirty.Labels[1].Coverage.Radius - 40) < .001, "Recognizer mm brush and screen-fixed size use canvas DPI and zoom");
        check(dirty.Labels.All(l => l.StateIds.Any(id => id.StartsWith("core.colorState")) && l.StateIds.Any(id => id.StartsWith("tablet.driverConfiguration"))), "Dirty labels link color and driver state");
        check(window.Complete && window.Inputs.All(i => J.Number(i.Data, "pressure") == .5) && window.States.Any(s => s.Channel == "keyboard"), "Original pressure, keyboard and full state payloads are bundled");
        evidence.Trim(6); var continuity = evidence.Snapshot(6, 10);
        var continuous = RecognitionEvidence.Build(continuity, Request(), new(256, 256));
        check(continuity.Inputs[0].Continuity && continuous.Labels[0].Coverage.Segments[0].Start == new PointD(60, 40), "Save cutoff preserves an ongoing contact without redrawing old segments");
        var changedView = Ready(); changedView.Accept(Pen(5, "penBegin", 120, 220));
        changedView.Accept(Core("canvasViewState", 6, new { canvasOriginScreenPx = new { x = 110, y = 200 }, ocrScalePercent = 50, ocrRotationDegrees = 0 }));
        changedView.Accept(Pen(8, "penSample", 140, 220)); changedView.Trim(7);
        check(RecognitionEvidence.Build(changedView.Snapshot(7, 10), Request(), new(256, 256)).Labels[0].Coverage.Segments[0].Start == new PointD(40, 40), "Continuity retains the transform that mapped its earlier contact point");
        var passive = Ready(); passive.Accept(Event("tablet", "penBegin", 5, new { x = 120, y = 220, heldKeys = Array.Empty<int>() }, 1));
        check(RecognitionEvidence.Build(passive.Snapshot(0, 10), Request(), new(256, 256)).Labels[0].Coverage.Segments[0].Start == new PointD(40, 40), "Passive pen x/y coordinates also drive the matrix");
        check(RecognizerMonitor.ViewArea(JsonSerializer.SerializeToElement(new { rawResult = new { canvasWindowRoiScreenPx = new { left = 100, top = 200, width = 128, height = 128 } } })) == new Rectangle(100, 200, 128, 128), "Current Recognizer canvasWindowRoiScreenPx is recognized");
        var late = Ready(); late.Accept(Core("brushState", 9, Brush(30))); late.Accept(Core("brushState", 7, Brush(20)));
        late.Accept(Pen(10, "penBegin", 120, 220));
        check(Math.Abs(RecognitionEvidence.Build(late.Snapshot(0, 11), Request(), new(256, 256)).Labels[0].Coverage.Radius - 45) < .001, "Late recognition is ordered by captured time rather than arrival");

        var mouse = Ready();
        void Mouse(long ticks, string kind, int x, int y, ulong op, int[]? keys = null)
            => mouse.Accept(Event("mouse", kind, ticks, new { x, y, button = "left", heldKeys = keys ?? [] }, op));
        Mouse(5, "mouseDown", 80, 220, 2); Mouse(6, "mouseUp", 120, 220, 2);
        Mouse(7, "mouseDown", 120, 220, 3, [32]); Mouse(8, "mouseUp", 120, 220, 3);
        Mouse(9, "mouseDown", 120, 220, 4); Mouse(10, "mouseDrag", 130, 220, 4); Mouse(11, "mouseUp", 135, 220, 4);
        var mouseDirty = RecognitionEvidence.Build(mouse.Snapshot(0, 12), Request(), new(256, 256));
        check(mouseDirty.Labels.Length == 1 && mouseDirty.Labels[0].OperationId == 4, "Canvas mouse fallback excludes panel starts and navigation releases");
        check(mouseDirty.Labels[0].Coverage.Segments[^1].End == new PointD(70, 40), "Mouse drag and release position complete coverage");
        mouse.Accept(Core("currentLayerState", 13, "Other")); Mouse(14, "mouseDown", 120, 220, 5);
        check(RecognitionEvidence.Build(mouse.Snapshot(12, 15), Request(), new(256, 256)).Labels.Length == 0, "Input on another layer is excluded from the current layer matrix");
        var missing = Ready(); missing.Reset("test", 1000, 5); missing.Accept(Pen(6, "penBegin", 120, 220));
        var missingDirty = RecognitionEvidence.Build(missing.Snapshot(0, 10), Request(), new(256, 256));
        check(missingDirty.Labels.Length == 0 && missingDirty.Warnings.Contains("inputHistoryIncomplete") && missingDirty.Warnings.Contains("canvasTransformUnavailable"), "Missing history or transform is explicit and uses coarse fallback");
        var rotated = Ready();
        rotated.Accept(Event("core.canvasViewState", "stateUpdated", 3, new
        {
            status = "changed", state = new { canvasOriginScreenPx = new { x = 100, y = 200 }, ocrScalePercent = 50, ocrRotationDegrees = 90 },
            rawResult = new { snapshot = new { screenToCanvas = new { m0 = 0.0, m1 = .01, m2 = -2.0, m3 = -.01, m4 = 0.0, m5 = 2.0 } } }
        }));
        rotated.Accept(Pen(4, "penBegin", 150, 250));
        check(RecognitionEvidence.Build(rotated.Snapshot(0, 5), Request(), new(256, 256)).Labels[0].Coverage.Segments[0].Start == new PointD(128, 128), "Exposed normalized affine maps rotated input to full-canvas pixels");
        rotated.Accept(Core("canvasViewState", 6, new { canvasOriginScreenPx = new { x = 100, y = 200 }, ocrScalePercent = 50, ocrRotationDegrees = 90 }));
        rotated.Accept(Pen(7, "penSample", 155, 250));
        check(RecognitionEvidence.Build(rotated.Snapshot(5, 8), Request(), new(256, 256)).Warnings.Contains("rotatedCanvasWithoutAffineMapping"), "Rotation without exposed affine is not assigned invented coordinates");
        var geometry = Ready(); geometry.Accept(Core("canvasViewState", 3, new
        { canvasOriginScreenPx = new { x = 100, y = 200 }, ocrScalePercent = 50, ocrRotationDegrees = 90,
            transform = new { rotationDegrees = 90 } }));
        geometry.Accept(Pen(4, "penBegin", 80, 230));
        var mappedGeometry = RecognitionEvidence.Build(geometry.Snapshot(0, 5), Request(), new(512, 256)).Labels[0].Coverage.Segments[0].Start;
        check(Math.Abs(mappedGeometry.X - 60) < 1e-8 && Math.Abs(mappedGeometry.Y - 40) < 1e-8, "Current Recognizer native geometry rotation, canvas origin and zoom map a rotated non-square canvas");

        var cutoff = new WatchState(); cutoff.Reset();
        cutoff.Accept(Core("currentLayerState", 1, "Ink")); cutoff.Accept(Core("canvasViewState", 2, new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 }));
        cutoff.Accept(Pen(3, "penBegin", 10, 10));
        var capture = cutoff.Accept(Core("canvasViewState", 4, new { canvasOriginScreenPx = new { x = 10, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 }));
        cutoff.Accept(Pen(5, "penSample", 20, 10)); cutoff.Complete(capture!, true);
        check(cutoff.Dirty, "Same operation samples after trigger remain dirty even before a later save dispatch");
        var nextContact = cutoff.Accept(Core("canvasViewState", 6, new { canvasOriginScreenPx = new { x = 20, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 }));
        check(nextContact is not null && nextContact.Operations.Contains(1), "Post-trigger continuation belongs to the next viewport diff");

        var frozenEvidence = Ready();
        frozenEvidence.Accept(Event("core.brushState", "stateUpdated", 30, new { status = "changed", state = Brush(20),
            evidence = new { triggerTicks = 3, capturedTicks = 20, completedTicks = 29 } }));
        frozenEvidence.Accept(Pen(4, "penBegin", 120, 220)); var frozen = frozenEvidence.Freeze(5);
        frozenEvidence.Accept(Pen(6, "penSample", 130, 220));
        frozenEvidence.Accept(Event("core.brushState", "stateUpdated", 40, new { status = "changed", state = Brush(50),
            evidence = new { triggerTicks = 4, capturedTicks = 35, completedTicks = 39 } }));
        var frozenWindow = frozen.Snapshot(0, 5);
        check(frozenWindow.Inputs.Length == 1 && frozenWindow.States.Where(s => s.Channel == "core.brushState").MaxBy(s => s.Ticks)!.Ticks == 3
            && J.Number(J.Get(J.Get(frozenWindow.States.Where(s => s.Channel == "core.brushState").MaxBy(s => s.Ticks)!.Data, "state"), "properties")[0], "value") == 20,
            "Frozen state uses API triggerTicks and does not absorb later inputs or late analysis completions");
        try { frozen.Snapshot(0, 6); throw new Exception("Frozen evidence exceeded its trigger cutoff"); }
        catch (InvalidOperationException) { check(true, "Frozen evidence rejects reads beyond triggerTicks"); }

        var minimalWindow = new EvidenceWindow("test", 1000, 0, 10, true, window.States, [], window.CaptureStateIds);
        var minimal = new DirtyEvidence(minimalWindow, [new("stroke", 1, 1, 9, "tablet", window.CaptureStateIds,
            new([new(new(4, 4), new(6, 6))], 2), [])], []);
        string after = Png(root, "before", image => { image.SetPixel(4, 4, Color.Red); image.SetPixel(6, 6, Color.FromArgb(128, 7, 19, 31)); });
        string now = Png(root, "current", image =>
        {
            image.SetPixel(5, 5, Color.Blue); image.SetPixel(6, 6, Color.FromArgb(128, 9, 19, 31));
            using var g = Graphics.FromImage(image); g.FillRectangle(Brushes.Lime, 200, 200, 16, 16);
        });
        using (var loaded = LayerDiff.Load(after)) check(loaded.GetPixel(6, 6).ToArgb() == Color.FromArgb(128, 7, 19, 31).ToArgb(), "Full-resolution load preserves translucent RGBA exactly");
        var diff = LayerDiff.Compare(after, now, minimal, Path.Combine(root, "patches"), CancellationToken.None);
        check(diff.Images.Length == 2 && diff.ChangedPixels == 259, "Precise dirty diff and coarse outside hit both produce image differences");
        check(diff.FullResolutionComparedPixels == 8192 && diff.CoarseComparedPixels > 0, "Only predicted and coarse-hit tiles are searched at full resolution");
        var fine = diff.Images.Single(i => i.LabelIds.Contains("stroke"));
        var coarse = diff.Images.Single(i => !i.LabelIds.Contains("stroke"));
        check(fine.Bounds == new PixelBox(4, 4, 7, 7), "Diff patch bounds use full-canvas coordinates");
        using (var mask = (Bitmap)Image.FromFile(Path.Combine(root, "patches", fine.MaskImage)))
        using (var before = (Bitmap)Image.FromFile(Path.Combine(root, "patches", fine.AfterImage)))
        using (var current = (Bitmap)Image.FromFile(Path.Combine(root, "patches", fine.NowImage)))
        {
            check(mask.GetPixel(0, 0).A == 255 && before.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb() && current.GetPixel(0, 0).A == 0, "Erasure keeps before pixels and a separate changed-pixel mask");
            check(mask.GetPixel(2, 0).A == 0 && before.GetPixel(2, 0).A == 0 && current.GetPixel(2, 0).A == 0, "Unchanged pixels in bounding patches stay transparent");
        }
        check(diff.Labels.Single(l => l.Source == "low-resolution-fallback").ImageIds.Contains(coarse.Id), "Changes outside predicted coverage get a matrix label with state references");
        check(diff.Labels.Single(l => l.Id == "stroke").ImageIds.Contains(fine.Id), "Matrix label and diff image links are bidirectional");
        var viewerPacket = new DiffViewPacket(Path.Combine(root, "patches"), false, new(256, 256), "Ink", diff.Images, diff.Labels, window.States);
        int fineSelection = Array.IndexOf(diff.Images, fine) + 1;
        var beforeFrame = DiffDisplay.Render(viewerPacket, fineSelection, DiffDisplayMode.After, CancellationToken.None);
        using (beforeFrame.Image)
        {
            check(beforeFrame.Image.Size == new Size(3, 3) && beforeFrame.Image.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "Diff viewer loads original before pixels without resizing");
            check(beforeFrame.CanvasBounds.Contains(beforeFrame.ImageBounds) && beforeFrame.CanvasBounds.Width < 128
                && beforeFrame.ImageBounds == new Rectangle(4, 4, 3, 3), "Diff viewer keeps full-canvas placement and focuses on the selected change");
        }
        var nowFrame = DiffDisplay.Render(viewerPacket, fineSelection, DiffDisplayMode.Now, CancellationToken.None);
        using (nowFrame.Image) check(nowFrame.Image.GetPixel(0, 0).A == 0 && nowFrame.Image.GetPixel(1, 1).ToArgb() == Color.Blue.ToArgb(), "Viewer represents both erasure and new pixels in the now view");
        var focused = DiffDisplay.Focus(viewerPacket, fineSelection, DiffDisplayMode.Now, CancellationToken.None);
        using (focused.Image) check(focused.Image.Size == new Size(3, 3) && focused.Image.GetPixel(2, 2).ToArgb() == Color.FromArgb(128, 9, 19, 31).ToArgb()
            && focused.Image.GetPixel(2, 0).A == 0 && focused.CanvasBounds.Width < 16,
            "Large diff view contains exact changed RGBA pixels without filling unchanged space or shrinking to the full canvas");
        var allFocused = DiffDisplay.Focus(viewerPacket, 0, DiffDisplayMode.Now, CancellationToken.None);
        using (allFocused.Image) check(allFocused.Image.GetPixel(2, 2).ToArgb() == Color.FromArgb(128, 9, 19, 31).ToArgb()
            && allFocused.ImageBounds == new Rectangle(4, 4, 212, 212), "All-change view fits the patch union and retains translucent original pixels");
        var completeLocator = DiffDisplay.Locator(viewerPacket with { CanvasImagePath = now }, CancellationToken.None);
        using (completeLocator.Image) check(completeLocator.CanvasBounds == new Rectangle(0, 0, 256, 256)
            && completeLocator.Image.GetPixel(205, 205).ToArgb() == Color.Lime.ToArgb(), "Locator displays the complete now canvas independently of the selected diff");
        var maskFrame = DiffDisplay.Render(viewerPacket, fineSelection, DiffDisplayMode.Mask, CancellationToken.None);
        using (maskFrame.Image) check(maskFrame.Image.GetPixel(0, 0).A == 255 && maskFrame.Image.GetPixel(2, 0).A == 0, "Viewer mask distinguishes erasure from unchanged transparent space");
        var overviewFrame = DiffDisplay.Render(viewerPacket, 0, DiffDisplayMode.Now, CancellationToken.None);
        using (overviewFrame.Image) check(overviewFrame.Image.GetPixel(5, 5).ToArgb() == Color.Blue.ToArgb() && overviewFrame.Image.GetPixel(205, 205).ToArgb() == Color.Lime.ToArgb(), "Overview places multiple changes at their original full-canvas coordinates");
        // A transparent bounding box may overlap another component's pixels.
        string clearPatch = Png(root, "overlap-clear", width: 16, height: 16);
        string coloredPatch = Png(root, "overlap-color", image => image.SetPixel(1, 1, Color.Red), 8, 8);
        var overlap = new DiffViewPacket(root, false, new(32, 32), "Ink",
            [new("small", new(0, 0, 8, 8), 1, "overlap-color.png", "overlap-color.png", "overlap-color.png", "overlap-color.png", []),
             new("large", new(0, 0, 16, 16), 0, "overlap-clear.png", "overlap-clear.png", "overlap-clear.png", "overlap-clear.png", [])], [], []);
        var overlapFrame = DiffDisplay.Render(overlap, 0, DiffDisplayMode.Now, CancellationToken.None);
        using (overlapFrame.Image) check(overlapFrame.Image.GetPixel(1, 1).ToArgb() == Color.Red.ToArgb(), "Transparent overlapping patch bounds do not erase another displayed difference");
        var largeOverview = DiffDisplay.Render(viewerPacket with { Canvas = new(4096, 8192) }, 0, DiffDisplayMode.Difference, CancellationToken.None);
        using (largeOverview.Image) check(largeOverview.Image.Height == DiffDisplay.OverviewEdge && largeOverview.CanvasBounds.Height == 8192, "Large canvas overview bounds remain full size while preview memory is bounded");
        var unchanged = LayerDiff.Compare(now, now, minimal, Path.Combine(root, "unchanged"), CancellationToken.None);
        check(unchanged.Images.Length == 0 && unchanged.ChangedPixels == 0, "Unchanged snapshots do not fabricate image changes");
        string hiddenA = Png(root, "hidden-a", image => image.SetPixel(4, 4, Color.FromArgb(0, 255, 0, 0)));
        string hiddenB = Png(root, "hidden-b", image => image.SetPixel(4, 4, Color.FromArgb(0, 0, 0, 255)));
        check(LayerDiff.Compare(hiddenA, hiddenB, minimal, Path.Combine(root, "hidden"), CancellationToken.None).ChangedPixels == 0, "RGB beneath zero alpha does not count as visual difference");

        string historyRoot = Path.Combine(root, "history"); var history = new SnapshotHistory(historyRoot);
        CaptureResult Result(string name, long ticks, string layer = "Ink", int width = 256) => new(Png(root, name,
            image => { if (ticks > 10) image.SetPixel(4, 4, Color.Red); }, width), JsonSerializer.Serialize(new
            { width, height = 256, fullCanvas = true, layerId = layer == "Ink" ? 3 : 4, layerUuid = layer == "Ink" ? "ink" : "other" }), layer, ticks + 1000,
            JsonSerializer.SerializeToElement(new { success = true, saveInputDispatched = true, saveInputDispatchedTicks = ticks + 1000 }));
        var historyEvidence = Ready(); historyEvidence.Accept(Pen(15, "penBegin", 102, 202));
        historyEvidence.Accept(Pen(16, "penEnd", 102, 202));
        EvidenceWindow Collect(long from, long to) => historyEvidence.Snapshot(from, to);
        var first = history.Advance(Result("one", 10), Request(10), "test", Collect, () => true, CancellationToken.None);
        string firstId = history.Pair.After!.Id;
        check(first.Baseline && history.Pair.Now is null && File.Exists(first.ImagePath), "First parsed image occupies after only");
        var secondRequest = Request(20) with { Context = new("test", new(256, 256), "test-control", historyEvidence.Freeze(20)) };
        historyEvidence.Accept(Pen(21, "penBegin", 120, 220, 2)); historyEvidence.Accept(Pen(22, "penEnd", 120, 220, 2));
        var second = history.Advance(Result("two", 20), secondRequest, "test", (_, _) => throw new Exception("Collected mutable evidence after parsing"), () => true, CancellationToken.None);
        var readPacket = DiffViewPacket.Read(Path.Combine(second.PacketDirectory, "manifest.json"));
        check(!readPacket.Baseline && readPacket.Canvas == new Size(256, 256) && readPacket.Images.Length == 1
            && readPacket.States.Length > 0 && readPacket.ImpactBoxes(readPacket.Images[0]).Length > 0, "Viewer reads committed image, matrix and Recognizer state data");
        check(readPacket.CanvasImagePath == Path.Combine(second.PacketDirectory, "canvas-preview.png") && File.Exists(readPacket.CanvasImagePath)
            && readPacket.Images[0].Image == readPacket.Images[0].NowImage, "Packet owns a full-canvas thumbnail and one primary original-pixel diff image");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(second.PacketDirectory, "manifest.json")))!.AsObject();
        legacy.Remove("canvasPreviewImage"); string legacyManifest = Path.Combine(second.PacketDirectory, "legacy-manifest.json");
        File.WriteAllText(legacyManifest, legacy.ToJsonString());
        check(DiffViewPacket.Read(legacyManifest).CanvasImagePath == Path.GetFullPath(second.ImagePath), "Legacy locator uses a retained snapshot of the same packet ID");
        string secondId = history.Pair.Now!.Id;
        check(!second.Baseline && history.Pair.After!.Id == firstId && second.DiffImages == 1, "Second image occupies now and diffs against after");
        var third = history.Advance(Result("three", 30), Request(30), "test", Collect, () => true, CancellationToken.None);
        check(history.Pair.After!.Id == secondId && history.Pair.Now!.TriggerTicks == 30 && history.Pair.Now.SavedTicks == 1030 && !File.Exists(first.ImagePath), "Third image shifts now to after and removes the oldest full-canvas PNG");
        check(Directory.GetFiles(Path.Combine(historyRoot, "snapshots"), "*.png").Length == 2 && File.Exists(Path.Combine(second.PacketDirectory, "image-0001-after.png")), "Only two full snapshots remain while historical diff pixels persist");
        string index = File.ReadAllText(Path.Combine(historyRoot, "current.json"));
        var rejected = Result("rejected", 40);
        try { history.Advance(rejected, Request(40), "test", Collect, () => false, CancellationToken.None); throw new Exception("Invalid capture committed"); }
        catch (InvalidOperationException) { check(File.ReadAllText(Path.Combine(historyRoot, "current.json")) == index && File.Exists(rejected.PngPath), "Invalidated capture preserves committed pair and incoming image"); }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var cancelled = Result("cancelled", 41);
            try { history.Advance(cancelled, Request(41), "test", Collect, () => true, cancellation.Token); throw new Exception("Cancelled capture committed"); }
            catch (OperationCanceledException) { check(File.ReadAllText(Path.Combine(historyRoot, "current.json")) == index, "Cancellation does not advance snapshot slots"); }
        }
        using (var packet = JsonDocument.Parse(File.ReadAllText(Path.Combine(second.PacketDirectory, "manifest.json"))))
        {
            var data = packet.RootElement;
            check(J.Text(data, "schema") == "dirty-matrix-image-diff/v1" && J.Get(data, "recognizer").GetProperty("states").GetArrayLength() >= 6
                && J.Get(data, "capture").GetProperty("controlResponse").GetProperty("saveInputDispatchedTicks").GetInt64() == 1020, "Published packet includes Recognizer state timeline and save response");
            check(J.Tick(J.Get(data, "capture"), "triggerTicks", -1) == 20 && J.Tick(J.Get(data, "now"), "triggerTicks", -1) == 20
                && J.Tick(J.Get(data, "recognizer"), "fromTicks", -1) == 10 && J.Tick(J.Get(data, "recognizer"), "toTicks", -1) == 20
                && J.Get(J.Get(data, "recognizer"), "inputs").EnumerateArray().All(i => J.Tick(i, "ticks", 0) <= 20)
                && secondId.StartsWith("trigger-00000000000000000020-"),
                "Snapshot identity, file name, diff window and bundled inputs use triggerTicks rather than save or parse completion");
            check(J.Get(data, "after").GetProperty("rasterMetadata").GetProperty("fullCanvas").GetBoolean(), "Packet preserves complete-canvas raster metadata");
            check(J.Get(data, "labels")[0].GetProperty("source").GetString() == "tablet"
                && J.Get(data, "images")[0].GetProperty("labelIds")[0].GetString()!.StartsWith("input-"), "Committed image diff carries a Recognizer input matrix label");
            check(J.Text(J.Get(data, "images")[0], "image") == J.Text(J.Get(data, "images")[0], "nowImage"), "Matrix-labelled primary image is the actual masked now diff, not the absolute channel-difference visualization");
        }
        var reboundHistory = new SnapshotHistory(Path.Combine(root, "rebound-history"));
        var reboundResult = Result("rebound",10) with { Metadata = JsonSerializer.Serialize(new
        { width=256,height=256,fullCanvas=true,layerId=19,layerUuid="replacement" }) };
        var rebound = reboundHistory.Advance(reboundResult,Request(10),"test",Collect,()=>true,CancellationToken.None);
        using (var reboundPacket=JsonDocument.Parse(File.ReadAllText(Path.Combine(rebound.PacketDirectory,"manifest.json"))))
        {
            var reboundCapture=J.Get(reboundPacket.RootElement,"capture");
            check(J.Tick(reboundCapture,"layerId",-1)==19 && J.Tick(J.Get(reboundCapture,"layer"),"id",-1)==19
                && J.Tick(J.Get(reboundCapture,"initialLayerHint"),"id",-1)==3
                && J.Text(reboundCapture,"layerMapping")=="currentLayerNameToSavedClipId",
                "Packet captures the freshly exported layer ID and keeps initial identity only as a hint");
        }
        historyEvidence.Accept(Core("currentLayerState",31,"Ink"));
        historyEvidence.Accept(Pen(40,"penBegin",103,203,88)); historyEvidence.Accept(Pen(41,"penEnd",103,203,88));
        historyEvidence.Accept(Core("currentLayerState",44,"Other"));
        historyEvidence.Accept(Pen(45,"penBegin",120,220,99)); historyEvidence.Accept(Pen(46,"penEnd",120,220,99));
        var otherLayer = history.Advance(Result("other", 50, "Other"), Request(50, name: "Other"), "test", Collect, () => true, CancellationToken.None);
        check(otherLayer.Baseline && history.Pair.Now is null && File.Exists(second.ImagePath) && File.Exists(third.ImagePath)
            && history.LayerStacks.Count==2 && history.LayerStacks[3].Now!.TriggerTicks==30,
            "A new layer creates its own baseline while retaining the other layer's two snapshots");
        check(history.EvidenceRetentionTicks==30,"Evidence retention follows the oldest latest layer snapshot");
        var historicalLocator = DiffDisplay.Locator(DiffViewPacket.Read(Path.Combine(second.PacketDirectory, "manifest.json")), CancellationToken.None);
        using (historicalLocator.Image) check(historicalLocator.Image.GetPixel(4, 4).ToArgb() == Color.Red.ToArgb(), "Historical complete-canvas locator survives deletion of its full source snapshot");
        check(DiffViewPacket.Read(legacyManifest).CanvasImagePath==Path.GetFullPath(second.ImagePath),
            "Legacy packets locate retained snapshots across inactive layer stacks by exact image ID");
        historyEvidence.Trim(history.EvidenceRetentionTicks);
        historyEvidence.Accept(Core("currentLayerState",52,"Ink"));
        var returnedResult=Result("returned-ink",55);
        using (var returnedPixels=LayerDiff.Load(returnedResult.PngPath))
        { returnedPixels.SetPixel(6,6,Color.Blue); returnedPixels.Save(returnedResult.PngPath,ImageFormat.Png); }
        var returnedRequest=Request(55) with { Context=new("test",new(256,256),"test-control",historyEvidence.Freeze(55)) };
        var returned=history.Advance(returnedResult,returnedRequest,"test",Collect,()=>true,CancellationToken.None);
        check(!returned.Baseline && history.Pair.After!.TriggerTicks==30 && history.Pair.Now!.TriggerTicks==55
            && history.LayerStacks[4].After!.TriggerTicks==50 && File.Exists(otherLayer.ImagePath),
            "A to B to A compares with the last A snapshot and leaves B unchanged");
        using(var returnedPacket=JsonDocument.Parse(File.ReadAllText(Path.Combine(returned.PacketDirectory,"manifest.json"))))
        {
            var data=returnedPacket.RootElement;
            check(J.Tick(J.Get(data,"recognizer"),"fromTicks",-1)==30 && J.Tick(J.Get(data,"search"),"changedPixels",-1)==1,
                "Returned layer uses its own time window and detects its actual changed pixel");
            var returnedLabels=J.Get(data,"labels").EnumerateArray().ToArray();
            check(returnedLabels.Any(label=>J.Tick(label,"operationId",-1)==88 && J.Get(label,"imageIds").GetArrayLength()>0)
                && returnedLabels.All(label=>J.Tick(label,"operationId",-1)!=99),
                "A stroke before the B packet retains matrix attribution while B strokes are excluded");
            check(J.Get(J.Get(data,"recognizer"),"inputs").EnumerateArray().Any(input=>J.Tick(input,"operationId",-1)==88),
                "Frozen evidence retains A input from before the intermediate B capture");
        }
        check(!File.Exists(second.ImagePath) && File.Exists(third.ImagePath) && File.Exists(returned.ImagePath)
            && Directory.GetFiles(Path.Combine(historyRoot,"snapshots"),"*.png").Length==3,
            "Only A's obsolete third snapshot rotates out; B's stack remains available");
        check(DiffViewPacket.Read(legacyManifest).CanvasImagePath is null,"Legacy lookup does not substitute a newer snapshot after its exact image rotates out");
        check(history.EvidenceRetentionTicks==50,"Updating A advances retention to B's still-needed boundary");
        var recreatedResult=Result("recreated-ink",57) with { Metadata=JsonSerializer.Serialize(new
            { width=256,height=256,fullCanvas=true,layerId=3,layerUuid="recreated-ink" }) };
        var recreated=history.Advance(recreatedResult,Request(57),"test",Collect,()=>true,CancellationToken.None);
        check(recreated.Baseline && history.LayerStacks[3].Now is null && history.LayerStacks[3].After!.Layer.Uuid=="recreated-ink"
            && !File.Exists(third.ImagePath) && !File.Exists(returned.ImagePath) && File.Exists(otherLayer.ImagePath),
            "Recreating the same layer ID with a new UUID resets only that layer's stack");
        var returnedOther=history.Advance(Result("returned-other",58,"Other"),Request(58,name:"Other"),"test",Collect,()=>true,CancellationToken.None);
        check(!returnedOther.Baseline && history.LayerStacks[4].After!.TriggerTicks==50 && history.LayerStacks[4].Now!.TriggerTicks==58
            && history.LayerStacks[3].After!.TriggerTicks==57,"B still resumes its own stack after A is recreated");
        using(var persisted=JsonDocument.Parse(File.ReadAllText(Path.Combine(historyRoot,"current.json"))))
            check(J.Get(persisted.RootElement,"layerStacks").EnumerateObject().Count()==2
                && J.Tick(J.Get(J.Get(persisted.RootElement,"layerStacks"),"4").GetProperty("now"),"triggerTicks",-1)==58,
                "The atomic history index persists separate stacks keyed by CLIP layer ID");
        var reconnect = history.Advance(Result("reconnected", 60, "Other"), Request(60, name: "Other", generation: 2), "test", Collect, () => true, CancellationToken.None);
        check(reconnect.Baseline && !File.Exists(otherLayer.ImagePath) && !File.Exists(returned.ImagePath)
            && !File.Exists(recreated.ImagePath) && !File.Exists(returnedOther.ImagePath)
            && history.LayerStacks.Count==1,"Recognizer reconnection starts a fresh set of layer stacks");
        var restarted = new SnapshotHistory(historyRoot);
        var restart = restarted.Advance(Result("restart", 70, "Other"), Request(70, name: "Other", generation: 2), "test", Collect, () => true, CancellationToken.None);
        check(restart.Baseline && Directory.GetFiles(Path.Combine(historyRoot, "snapshots"), "*.png").Length == 1, "App restart preserves packets and establishes a fresh baseline");
        var resized = restarted.Advance(Result("resize", 80, "Other", 128), Request(80, name: "Other", generation: 2), "test", Collect, () => true, CancellationToken.None);
        check(resized.Baseline && restarted.Pair.After!.Width == 128, "Canvas dimension changes rebaseline instead of comparing incompatible pixels");
        check(Directory.GetDirectories(historyRoot, ".pending-*").Length == 0 && Directory.GetFiles(historyRoot, "*.tmp").Length == 0, "Failed packet staging is cleaned up");
        Console.WriteLine("DIFF artifacts: " + root);
    }
}
