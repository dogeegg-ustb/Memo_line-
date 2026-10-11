using MemolineDemo.Editing;
using StrokeReplay;
using System.Text.Json;

namespace MemolineDemo;

internal static class ProjectEditIndexChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); count++; }
        void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { count++; return; } throw new InvalidOperationException(message); }
        var blank = new ImageRgba(8, 8, new byte[8 * 8 * 4]);
        var first = Session("old-a", 11, 1000, 100000, blank, 1);
        var second = Session("old-b", 22, 10000000, 100, first.Packets[0].Current!, 5);
        var current = Session("live", 33, 1000, 10, second.Packets[0].Current!);
        var route = current.Packets[0].Layer;
        var index = ProjectEditIndex.Build([first, second], current);
        Check(index.IsValid, "Two sealed sessions and an empty live session form one verified pixel timeline.");
        Check(index.Layers.Count == 1 && index.Layers.ContainsKey(route), "Historical UUIDs route to the current layer generation.");
        Check(index.Strokes.Count == 2 && index.Strokes.Keys.Select(k => k.BeginAppendId).Distinct().Count() == 1,
            "Repeated append/event/operation IDs in different sessions retain two separate native stroke identities.");
        Check(index.Images.Count == 2 && index.Images.Keys.Select(k => k.PacketId).Distinct().Count() == 2,
            "Repeated native packet/image IDs receive separate project keys.");
        var old = index.Strokes.Values.Single(s => s.Key.SessionId == first.SessionId);
        Check(old.Layer == route && old.SourceLayer == first.Packets[0].Layer && old.SessionOrder == 0,
            "Current physical route and original session/generation layer identity both survive joining.");
        Check(old.StartTicks == 100000 && old.Samples.Select(s => s.Ticks).SequenceEqual(first.Replay.Strokes[0].Samples.Select(s => s.Ticks)) &&
            old.Samples.Select(s => s.AppendId).SequenceEqual(first.Replay.Strokes[0].Samples.Select(s => s.AppendId)),
            "Joining never rescales or offsets native ticks, append IDs, or replay samples.");
        Check(index.Layers[route].Packets.Select(p => p.SessionOrder).SequenceEqual([0, 1, 2]),
            "Project order follows sealing order despite different frequencies and reset clocks.");
        var selected = new PixelMask(8, 8); selected.Set(1, 1);
        var undo = PreciseUndoSelector.Select(index, route, selected);
        Check(undo.IsValid && undo.Selected.Count == 1 && undo.Selected[0].Key == old.Key,
            "Selection in a zero-stroke recording finds an earlier sealed-session stroke.");
        var request = EditRequest.Event(route, selected,
            new Dictionary<EditableStrokeKey, EventChange> { [old.Key] = new(Delete: true) });
        var plan = ReconstructionPlanner.Build(index, request);
        Check(plan.IsValid && plan.Layers.Single().Groups.First().SessionOrder == 0,
            "A historical event reaches a valid reconstruction plan through the current layer route.");
        var timeline = new LayerTimeline(index.Layers[route].Packets, index.Layers[route].Current!);
        Check(timeline.BeforePacket(index.Layers[route].Packets[0].PacketId).Bytes.AsSpan().SequenceEqual(blank.Bytes) && timeline.VerifyRoundTrip(),
            "Reverse reconstruction respects session order rather than native tick magnitude.");
        Check(first.Frequency == 1000 && second.Frequency == 10000000 && first.Packets[0].PacketId == "packet" &&
            first.Packets[0].Layer.SessionId == "old-a" && first.Replay.Strokes[0].OperationId == 2,
            "All original session snapshots remain unchanged.");

        var noBoundary = first with { Packets = [first.Packets[0] with { IsBoundary = false }] };
        var continued = ProjectEditIndex.Build([noBoundary, second], current);
        Check(continued.IsValid, "An exact later baseline certifies a sealed endpoint lacking an editBoundary marker.");
        var missingBaseline = second with { Packets = [second.Packets[0] with { Before = null }] };
        Check(ProjectEditIndex.Build([first, missingBaseline], current).Layers[route].Issues.Any(i => i.Code == PlanIssueCode.CaptureMismatch),
            "Missing full baseline evidence prevents inheritance even when cropped patches exist.");
        var changedPixels = first.Packets[0].Current!.Copy(); changedPixels.Bytes[0] = 90;
        var gap = Session("gap", 17, 1000, 30, changedPixels);
        var gapIndex = ProjectEditIndex.Build([first, gap, second], current);
        var bridges = gapIndex.Images.Values.Where(i => i.Events.Count == 0 && i.IsCausal && i.HasUnreplayableEvents).ToArray();
        Check(gapIndex.IsValid && bridges.Length == 2 && bridges.All(i => i.Mask.Count == 1 && i.Mask.Contains(0, 0) &&
            i.Mask.Width == 1 && i.Mask.Height == 1 && i.W.IsEmpty && i.Probe.IsEmpty && i.Impact.Equals(i.Mask) &&
            i.UnknownHistoryInterval),
            "A no-stroke session's changed pixels become exact cropped observed bridges with no invented contacts.");
        var separateGapPlan = ReconstructionPlanner.Build(gapIndex, request);
        Check(separateGapPlan.IsValid && separateGapPlan.Layers.SelectMany(l => l.Reasons.Values).All(r => r.Rule != "unrecordedInterval"),
            "Deleting a stroke away from the unrecorded pixels composes the gap and stays executable.");
        Check(!first.Packets[0].Images[0].UnknownHistoryInterval &&
            (bridges[0] with { OperationId = "copied-proof" }).UnknownHistoryInterval,
            "The unknown-interval proof defaults to false and survives record copying.");
        var right = new PixelMask(1, 0, 1, 1); right.Set(1, 0);
        var twoPixelBridge = new DirtyImage(new(route, "two-pixel-gap", "right"), right,
            new(1, 1, new byte[4]), new(1, 1, new byte[] { 90, 0, 0, 255 }), [],
            IsCausal: true, HasUnreplayableEvents: true)
        { UnknownHistoryInterval = true, Impact = right.Clone() };
        var left = new PixelMask(2, 1); left.Set(0, 0);
        Check(!right.Intersects(left) && ReconstructionPlanner.Assess(gapIndex, request, twoPixelBridge,
            new Dictionary<LayerKey, PixelMask> { [route] = left }) is null,
            "Unrecorded pixels compose like a dirty image: an earlier change elsewhere keeps them trusted.");
        var both = new PixelMask(2, 1); both.Set(1, 0);
        Check(ReconstructionPlanner.Assess(gapIndex, request, twoPixelBridge,
            new Dictionary<LayerKey, PixelMask> { [route] = both }) is null,
            "Unrecorded pixels are always pasted as recorded, even where an earlier change reaches them.");
        var otherRoute = route with { Uuid = "reference" };
        Check(ReconstructionPlanner.Assess(gapIndex, request, twoPixelBridge,
            new Dictionary<LayerKey, PixelMask> { [route] = new(2, 1), [otherRoute] = left }) is null,
            "Changes in another layer do not invalidate this layer's unrecorded pixels.");
        Check(ReconstructionPlanner.Assess(gapIndex, request, twoPixelBridge,
            new Dictionary<LayerKey, PixelMask> { [route] = new(2, 1), [otherRoute] = new(2, 1) }) is null,
            "An unchanged earlier scene does not require replay of an unknown interval.");
        var gapMask = new PixelMask(8, 8); gapMask.Set(0, 0);
        var gapSelection = PreciseUndoSelector.Select(gapIndex, route, gapMask);
        Check(!gapSelection.IsValid && gapSelection.Selected.Count == 0,
            "Unknown observed change pixels cannot become selectable fake strokes.");
        var touchedPixels = first.Packets[0].Current!.Copy(); touchedPixels.Bytes[(1 + 8) * 4] = 99;
        var touching = Session("touch-gap", 17, 1000, 30, touchedPixels);
        var touchedIndex = ProjectEditIndex.Build([first, touching, second], current);
        Check(ReconstructionPlanner.Build(touchedIndex, request).IsValid,
            "Editing whose difference reaches an observed bridge pastes the bridge pixels instead of refusing.");
        Check(PreciseUndoSelector.Select(touchedIndex, route, selected).Selected.Count > 0,
            "A later unknown bridge does not stop the undo depth: precise undo is never refused for it.");
        var gapTimeline = new LayerTimeline(gapIndex.Layers[route].Packets, gapIndex.Layers[route].Current!);
        Check(gapTimeline.BeforePacket(gapIndex.Layers[route].Packets[0].PacketId).Bytes.AsSpan().SequenceEqual(blank.Bytes) && gapTimeline.VerifyRoundTrip(),
            "Exact gap before/now pixels participate in reverse and forward timeline reconstruction.");
        var later = gapIndex.Strokes.Values.Single(s => s.Key.SessionId == second.SessionId);
        var laterSelection = new PixelMask(8, 8); laterSelection.Set(5, 1);
        var laterRequest = EditRequest.Event(route, laterSelection,
            new Dictionary<EditableStrokeKey, EventChange> { [later.Key] = new(Delete: true) });
        var laterPlan = ReconstructionPlanner.Build(gapIndex, laterRequest);
        Check(laterPlan.IsValid && laterPlan.Layers.Single().Groups.All(group =>
            group.SessionOrder >= later.SessionOrder && !group.Images.Any(image => image.UnknownHistoryInterval)),
            "A new stroke after the unknown interval remains editable without replaying that earlier interval.");
        var laterResult = ReconstructionEngine.RunAsync(laterPlan, new CompositionOnlyExecutor(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(laterResult.Status == ReconstructionStatus.PixelVerified && laterResult.Layers[route].Bytes.AsSpan()
            .SequenceEqual(second.Packets[0].Before!.Bytes),
            "Deleting a later recorded stroke reconstructs its exact prior pixels while preserving earlier interval pixels.");

        var referenceMask = new PixelMask(8, 8); referenceMask.Set(0, 0);
        var referenceNow = blank.Copy(); referenceNow.Bytes[0] = 120; referenceNow.Bytes[3] = 255;
        var referenceKey = new EditableStrokeKey("reference-source", 6);
        var referenceImageKey = new DirtyImageKey(otherRoute, "reference-first", "write");
        var referenceStroke = old with { Key = referenceKey, Layer = otherRoute, Images = [referenceImageKey],
            WriteMask = referenceMask.Clone(), ProbeMask = new(8, 8), ImpactMask = new(8, 8), SessionOrder = 0 };
        var referenceImage = new DirtyImage(referenceImageKey, referenceMask, blank.Copy(), referenceNow, [referenceKey]);
        var referencePacket = new LayerPacket("reference-first", otherRoute, 100000, blank.Copy(), referenceNow,
            [referenceImage], CanvasWidth: 8, CanvasHeight: 8) { SessionOrder = 0 };
        var referenceLayer = new LayerIndex(otherRoute, [referencePacket], [referenceStroke], true, [], blank, referenceNow);
        var crossLayers = gapIndex.Layers.ToDictionary(); crossLayers.Add(otherRoute, referenceLayer);
        var crossStrokes = gapIndex.Strokes.ToDictionary(); crossStrokes.Add(referenceKey, referenceStroke);
        var crossImages = gapIndex.Images.ToDictionary(); crossImages.Add(referenceImageKey, referenceImage);
        var crossIndex = new EditIndex(crossLayers, crossStrokes, crossImages, []);
        var referenceRequest = EditRequest.Event(otherRoute, referenceMask,
            new Dictionary<EditableStrokeKey, EventChange> { [referenceKey] = new(Delete: true) });
        var crossPlan = ReconstructionPlanner.Build(crossIndex, referenceRequest);
        Check(crossPlan.IsValid && crossPlan.Layers.All(layer => !layer.Untrusted.Any(key => crossIndex.Images[key].UnknownHistoryInterval)),
            "An edit in another layer leaves this layer's unrecorded pixels composed as recorded.");

        var overwritten = referenceNow.Copy(); overwritten.Bytes[0] = 240;
        var overwriteKey = new EditableStrokeKey("reference-source", 9);
        var overwriteImageKey = new DirtyImageKey(otherRoute, "reference-overwrite", "write");
        var overwriteStroke = referenceStroke with { Key = overwriteKey, Images = [overwriteImageKey], BeginEventId = 5 };
        var overwriteImage = new DirtyImage(overwriteImageKey, referenceMask.Clone(), referenceNow.Copy(), overwritten, [overwriteKey]);
        var overwritePacket = new LayerPacket("reference-overwrite", otherRoute, 100100, referenceNow.Copy(), overwritten,
            [overwriteImage], CanvasWidth: 8, CanvasHeight: 8) { SessionOrder = 0 };
        crossLayers[otherRoute] = referenceLayer with { Packets = [referencePacket, overwritePacket],
            Strokes = [referenceStroke, overwriteStroke], Current = overwritten };
        crossStrokes.Add(overwriteKey, overwriteStroke); crossImages.Add(overwriteImageKey, overwriteImage);
        var clearedIndex = new EditIndex(crossLayers, crossStrokes, crossImages, []);
        var clearedPlan = ReconstructionPlanner.Build(clearedIndex, referenceRequest);
        Check(clearedPlan.IsValid && clearedPlan.Layers.All(layer => !layer.Untrusted.Any(key =>
            clearedIndex.Images[key].UnknownHistoryInterval)),
            "A trusted later write that clears every earlier difference makes the following unknown interval irrelevant.");
        var clearedResult = ReconstructionEngine.RunAsync(clearedPlan, new CompositionOnlyExecutor(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(clearedResult.Status == ReconstructionStatus.PixelVerified && clearedResult.Layers[otherRoute].Bytes.AsSpan()
            .SequenceEqual(overwritten.Bytes),
            "When a trusted overwrite erases the edit's visible effect, the unchanged scene still reconstructs exactly.");
        var incompletePixels = gap with { Packets = [gap.Packets[0] with { Before = new(8, 8, new byte[4]) }] };
        Check(ProjectEditIndex.Build([first, incompletePixels, second], current).Layers[route].Issues.Any(i =>
            i.Code == PlanIssueCode.ChainBroken && i.Message.Contains("衔接像素")),
            "Incomplete RGBA evidence cannot be replaced by an observed bridge.");
        var incompatibleSize = Session("other-size", 17, 1000, 30, new(4, 4, new byte[64]));
        Check(ProjectEditIndex.Build([first, incompatibleSize, second], current).Layers[route].Issues.Any(i =>
            i.Code == PlanIssueCode.ChainBroken && i.Message.Contains("衔接像素")),
            "Different complete canvas sizes cannot be bridged.");
        var missingCapture = first with { Packets = [] };
        Check(ProjectEditIndex.Build([missingCapture], current).Issues.Any(i => i.Code == PlanIssueCode.ChainBroken),
            "A contact-bearing session without captures is retained as an explicit missing-history failure.");
        var emptyUncaptured = Session("empty", 17, 1000, 0, blank) with { Packets = [] };
        Check(ProjectEditIndex.Build([first, emptyUncaptured, second], current).IsValid,
            "A truly empty uncaptured session may be skipped without discarding recorded contacts.");
        var corrupt = second.Packets[0].Images[0].After.Copy(); corrupt.Bytes[(5 + 8) * 4] = 70;
        var corruptSession = second with { Packets = [second.Packets[0] with
            { Images = [second.Packets[0].Images[0] with { After = corrupt }] }] };
        Check(ProjectEditIndex.Build([first, corruptSession], current).Layers[route].Issues.Any(i => i.Message.Contains("差异补丁的前像素")),
            "Matching session endpoints cannot hide an internally corrupt before-pixel patch.");
        var wrongFinal = second.Packets[0].Current!.Copy(); wrongFinal.Bytes[20] = 30;
        var incompleteDiff = second with { Packets = [second.Packets[0] with { Current = wrongFinal }] };
        Check(ProjectEditIndex.Build([first, incompleteDiff], current).Layers[route].Issues.Any(i => i.Message.Contains("重建结果")),
            "Missing recorded delta pixels are detected against the authoritative complete final layer.");
        Reject(() => ProjectEditIndex.Build([first, first], current), "Duplicate sessions were silently merged.");
        Reject(() => ProjectEditIndex.Build([first with { Frequency = 0 }], current), "An invalid native clock was accepted.");
        var priorOnlyUuid = first with { Packets = [first.Packets[0] with { Layer = first.Packets[0].Layer with { Uuid = "unrouted" } }] };
        // No editable contact is resolved for that UUID; its capture still remains visible as unavailable history.
        priorOnlyUuid = priorOnlyUuid with { Packets = [priorOnlyUuid.Packets[0] with { Images = [] }], Replay = priorOnlyUuid.Replay with { Strokes = [] } };
        Check(ProjectEditIndex.Build([priorOnlyUuid], current).Layers.Values.Any(l => l.Layer.Uuid == "unrouted" &&
            l.Layer.SessionId == "old-a" && l.Issues.Any(i => i.Code == PlanIssueCode.LayerIdentityUnconfirmed)),
            "A historical layer with no current route retains its original identity and an explicit local issue.");
        var cached = new Dictionary<string, EditIndex>
        { [first.SessionId] = EditIndexBuilder.Build(first), [second.SessionId] = EditIndexBuilder.Build(second),
            [current.SessionId] = new(new Dictionary<LayerKey, LayerIndex>(), new Dictionary<EditableStrokeKey, EditableStroke>(),
                new Dictionary<DirtyImageKey, DirtyImage>(), []) };
        var cachedIndex = ProjectEditIndex.Build([first, second], current, cached);
        Check(cachedIndex.IsValid && cachedIndex.Strokes.Keys.OrderBy(k => k.SessionId).SequenceEqual(index.Strokes.Keys.OrderBy(k => k.SessionId)) &&
            cachedIndex.Layers[route].Packets.Count == 3,
            "Cached historical native indexes may be reused while the live session always builds afresh.");
        Console.WriteLine($"Project edit index checks passed: {count}.");
        return count;
    }

    private sealed class CompositionOnlyExecutor : ICspReconstructionExecutor
    {
        public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token)
            => throw new InvalidOperationException("This pixel-composition check must not simulate native brush replay.");
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(expected.Copy()); }
    }

    private static SessionSnapshot Session(string id, long generation, long frequency, long start, ImageRgba baseline, int? drawX = null)
    {
        var layer = new LayerKey(id, generation, "ink");
        JsonElement Update(string module, object state) => JsonSerializer.SerializeToElement(new
        { kind = "coreStateUpdated", data = new { module, status = "changed", state, evidence = new { observedAfterEventId = 0, causalAmbiguous = false } } });
        JsonElement Pen(string kind, ulong eventId, long ticks) => JsonSerializer.SerializeToElement(new
        { kind, eventId, operationId = 2, ticks, path = "hardware", deviceSource = new { deviceType = "pen" },
            data = new { penDownLocation = new { region = "canvasViewport", status = "matched" } } });
        var frames = new List<JsonElement>
        {
            JsonSerializer.SerializeToElement(new { kind = "shortcutConfiguration", data = new { toolCatalog = new { nodes = new[]
            { new { id = "tool", kind = "tool", name = "Pen", parentId = "root", toolId = "tool" },
              new { id = "subtool", kind = "subtool", name = "G pen", parentId = "tool", toolId = "tool" } } } } }),
            Update("brushState", new { name = "G pen", properties = new[] { new { key = "brush_size", type = "number", status = "ok", value = 2 } } }),
            Update("currentLayerState", "Ink"), Update("clipState", new { layers = new[] { new { name = "Ink", uuid = "ink" } } }),
            Update("canvasViewState", new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 })
        };
        if (drawX is not null) frames.AddRange([Pen("penBegin", 2, start), Pen("penSample", 3, start + 10), Pen("penEnd", 4, start + 20)]);
        frames.Add(JsonSerializer.SerializeToElement(new { kind = "editBoundaryRequested", ticks = start + 200, data = new { } }));
        var nativeFrames = frames.Select((f, i) =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(f.GetRawText())!.AsObject(); node["appendId"] = i + 1;
            return JsonSerializer.SerializeToElement(node);
        }).ToArray();
        var final = baseline.Copy();
        var drawings = new List<MemolineReplayStroke>();
        var images = new List<DirtyImage>(); var labels = new List<LabelEvidence>();
        if (drawX is { } x)
        {
            var samples = new MemolineReplaySample[] { new(start, x + .5, 1.5, 1, 0, 0, true, 6, 2),
                new(start + 10, x + .5, 1.5, 1, 0, 0, true, 7, 3), new(start + 20, x + .5, 1.5, 0, 0, 0, false, 8, 4) };
            drawings.Add(new(2, start, start + 20, new(start, 0, 0, 0), samples));
            var mask = new PixelMask(8, 8); mask.Set(x, 1);
            int offset = (x + 8) * 4; final.Bytes[offset] = 120; final.Bytes[offset + 3] = 255;
            images.Add(new(new(layer, "packet", "image"), mask, baseline.Copy(), final.Copy(), []) { LabelIds = ["label"] });
            labels.Add(new("label", 2, "tablet", start, start + 20, 1, [new(x + .5, 1.5, x + .5, 1.5)], ["image"]));
        }
        var packet = new LayerPacket("packet", layer, start + 200, baseline.Copy(), final, images, IsBoundary: true,
            AfterSnapshotId: "baseline", NowSnapshotId: "now", BaselineSource: "recordingStart", CanvasWidth: baseline.Width, CanvasHeight: baseline.Height)
        { Labels = labels };
        var replay = new MemolineReplayDocument("fixture-" + id, id, frequency, drawings);
        return new("fixture-" + id, id, frequency, nativeFrames, replay, [packet], (ulong)nativeFrames.Length, start + 200);
    }
}
