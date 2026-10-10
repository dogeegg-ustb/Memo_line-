using System.Text.Json;
using System.Text.Json.Nodes;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal static class EditIndexHardeningChecks
{
    internal static SessionSnapshot NativeMatrixFixture(JsonElement view)
    {
        var fixture = new Fixture();
        fixture.ReplaceState("canvasViewState", view);
        fixture.Replace("penBegin", Fixture.PenFrame("penBegin", 2, 100, -10.75, -19.25, true));
        fixture.Replace("penSample", Fixture.PenFrame("penSample", 3, 110, -10.75, -17.75, true));
        return fixture.Snapshot();
    }

    internal static int Run()
    {
        // Undo/redo assertions here pin the optional strict gate; the product default is off.
        ReconstructionPlanner.BlockOnUnresolvedUndo = true;
        try { return RunStrict(); }
        finally { ReconstructionPlanner.BlockOnUnresolvedUndo = false; }
    }

    private static int RunStrict()
    {
        int count = 0;
        var failures = new List<string>();
        void Check(bool value, string message)
        {
            count++;
            if (!value) { failures.Add(message); Console.WriteLine("FAIL: " + message); }
        }
        void Excluded(Fixture fixture, string reason)
        {
            var index = EditIndexBuilder.Build(fixture.Snapshot());
            Check(index.Strokes.Count == 0 && index.Images.Values.All(i => i.HasUnreplayableEvents), reason);
        }
        ReconstructionPlan ContactPlan(SessionSnapshot snapshot)
        {
            var index = EditIndexBuilder.Build(snapshot);
            var selected = index.Strokes.Values.Single();
            return ReconstructionPlanner.Build(index, EditRequest.Event(selected.Layer, selected.WriteMask,
                new Dictionary<EditableStrokeKey, EventChange> { [selected.Key] = new(Delete: true) }));
        }
        var basic = new Fixture();
        var baseIndex = EditIndexBuilder.Build(basic.Snapshot());
        Check(baseIndex.Strokes.Count == 1 && baseIndex.IsValid,
            "The confirmed complete native contact remains eligible.");

        var middle = new Fixture();
        middle.Before("penSample", Transaction("editTransactionBegan", "middle", 105));
        middle.Before("penEnd", Transaction("editTransactionEnded", "middle", 115));
        Excluded(middle, "A transaction entirely inside a contact excludes the complete contact.");
        var open = new Fixture();
        open.Before("penSample", Transaction("editTransactionBegan", "open", 105));
        Excluded(open, "An unclosed transaction beginning after penBegin excludes the complete contact.");
        var spanning = new Fixture();
        spanning.Before("penBegin", Transaction("editTransactionBegan", "spanning", 99));
        spanning.Before("penEnd", Transaction("editTransactionEnded", "spanning", 115));
        Excluded(spanning, "A transaction active at penBegin excludes the contact even if it later closes.");
        var nested = new Fixture();
        nested.Before("penBegin", Transaction("editTransactionBegan", "outer", 98));
        nested.Before("penBegin", Transaction("editTransactionBegan", "inner", 99));
        nested.Before("penBegin", Transaction("editTransactionEnded", "inner", 99));
        Excluded(nested, "Closing another transaction cannot erase an open interval.");
        var mismatched = new Fixture();
        mismatched.Before("penSample", Transaction("editTransactionBegan", "pending", 105));
        mismatched.Before("penEnd", Transaction("editTransactionEnded", "different", 115));
        Excluded(mismatched, "A mismatched transaction end does not close the pending interval.");
        var before = new Fixture();
        before.Before("penBegin", Transaction("editTransactionBegan", "before", 98));
        before.Before("penBegin", Transaction("editTransactionEnded", "before", 99));
        Check(EditIndexBuilder.Build(before.Snapshot()).Strokes.Count == 1,
            "A transaction ending before penBegin does not exclude a later contact.");
        var after = new Fixture();
        after.Before("editBoundaryRequested", Transaction("editTransactionBegan", "after", 125));
        Check(EditIndexBuilder.Build(after.Snapshot()).Strokes.Count == 1,
            "A transaction beginning after penEnd does not exclude the prior contact.");
        var interrupted = new Fixture();
        interrupted.Replace("penEnd", Frame("penInterrupted", 120, new { reason = "cancelled" }, refs: [2, 3]));
        Check(EditIndexBuilder.Build(interrupted.Snapshot()).Strokes.Count == 1,
            "penInterrupted closes a complete contact through its relatedEventIds.");
        interrupted.Before("penInterrupted", Transaction("editTransactionBegan", "interrupted", 115));
        Excluded(interrupted, "Transaction intersection is checked through penInterrupted as well as penEnd.");
        var incomplete = new Fixture(); incomplete.Remove("penEnd");
        Excluded(incomplete, "A contact without penEnd or penInterrupted is not editable.");

        foreach (string status in new[] { "unknown", "ambiguous", "error" })
        {
            var invalid = new Fixture();
            invalid.Before("penBegin", Update("brushState", Fixture.Brush, status, anchor: 1));
            Excluded(invalid, "A newer " + status + " brush state must not reuse the old confirmed brush.");
        }
        foreach (string module in new[] { "brushState", "currentLayerState", "clipState", "canvasViewState" })
        {
            var ambiguous = new Fixture();
            ambiguous.Before("penBegin", Update(module, Fixture.State(module), ambiguous: true, anchor: 1));
            Excluded(ambiguous, "causalAmbiguous invalidates the latest " + module + " even when status is changed.");
            var invalidated = new Fixture();
            invalidated.Before("penBegin", Invalidate(module));
            Excluded(invalidated, "Actual panelUpdateRequested.invalidate forbids an older confirmed " + module + ".");
            var restored = new Fixture();
            restored.Before("penBegin", Invalidate(module));
            restored.Before("penBegin", Update(module, Fixture.State(module), anchor: 1));
            Check(EditIndexBuilder.Build(restored.Snapshot()).Strokes.Count == 1,
                "A later confirmed causal slot restores " + module + " after invalidation.");
        }
        var unconfirmedLayer = new Fixture();
        unconfirmedLayer.Before("penBegin", Update("currentLayerState", "Ink", "unknown", anchor: 1));
        Excluded(unconfirmedLayer, "A newer unconfirmed layer name cannot reuse its confirmed predecessor.");
        var staleClip = new Fixture();
        staleClip.Before("penBegin", Update("clipState", Fixture.Clip, "unknown", anchor: 1));
        Excluded(staleClip, "A newer unconfirmed clipState cannot reuse an old UUID mapping.");
        var sameAnchorStale = new Fixture();
        sameAnchorStale.Before("penBegin", Update("brushState", Fixture.Brush, anchor: 1));
        sameAnchorStale.Before("penBegin", Invalidate("brushState"));
        Excluded(sameAnchorStale, "A confirmed value published before a later same-anchor invalidation is stale.");
        var pending = new Fixture();
        pending.Before("penBegin", Frame("statePackageReserved", 95,
            new { packageId = "pending", afterEventId = 1, reservationOrder = 1, status = "pending" }));
        Excluded(pending, "A still unresolved native state package cannot fall back to older confirmations.");

        foreach (string command in new[] { "undo", "redo" })
        {
            var undo = new Fixture();
            undo.Before("editBoundaryRequested", Hardware("keyInput", 5, 140));
            undo.Before("editBoundaryRequested", Shortcut(command, 5, 140));
            var index = EditIndexBuilder.Build(undo.Snapshot());
            Check(ContactPlan(undo.Snapshot()).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
                "Actual shortcutResolved.matches " + command + " within the layer range blocks reconstruction.");
            Check(index.IsValid, "A recorded " + command + " remains request-scoped evidence rather than invalidating the layer index.");
        }
        var earlierUndo = new Fixture();
        earlierUndo.Before("penBegin", Shortcut("undo", 1, 95));
        Check(!ContactPlan(earlierUndo.Snapshot()).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "Undo before the first layer contact does not poison its later range.");
        var delayedEarlierUndo = new Fixture();
        delayedEarlierUndo.Before("editBoundaryRequested", Shortcut("undo", 1, 95));
        Check(!ContactPlan(delayedEarlierUndo.Snapshot()).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "A delayed shortcut result uses the related hardware event, not its publication appendId.");
        var otherLayerUndo = new Fixture();
        otherLayerUndo.Before("editBoundaryRequested", Hardware("keyInput", 5, 140));
        // Recognition is published after the shortcut, but captured before its hardware input.
        var otherLayerObservation = JsonNode.Parse(Update("currentLayerState", "Other", anchor: 4).GetRawText())!.AsObject();
        otherLayerObservation["ticks"] = 139;
        otherLayerObservation["data"]!["evidence"]!["capturedTicks"] = 139;
        otherLayerUndo.Before("editBoundaryRequested", JsonSerializer.SerializeToElement(otherLayerObservation));
        otherLayerUndo.Before("editBoundaryRequested", Shortcut("undo", 5, 140));
        Check(!ContactPlan(otherLayerUndo.Snapshot()).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "A confirmed other-layer undo is not attributed to the recorded Ink layer.");
        var unknownLayerUndo = new Fixture();
        unknownLayerUndo.Before("editBoundaryRequested", Hardware("keyInput", 5, 140));
        unknownLayerUndo.Before("editBoundaryRequested", Update("currentLayerState", "Ink", "unknown", anchor: 4));
        unknownLayerUndo.Before("editBoundaryRequested", Shortcut("undo", 5, 140));
        Check(ContactPlan(unknownLayerUndo.Snapshot()).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "Undo with unconfirmed target layer must conservatively block the overlapping range.");
        var orphanUndo = new Fixture();
        orphanUndo.Before("editBoundaryRequested", Frame("shortcutResolved", 140,
            new { matches = new[] { new { command = "undo" } } }));
        Check(ContactPlan(orphanUndo.Snapshot()).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "Undo without a native hardware reference cannot be assumed harmless.");
        var futureUndo = new Fixture();
        var futureSnapshot = futureUndo.Snapshot();
        var boundaryAppend = futureSnapshot.BoundaryAppendId;
        var futureFrames = futureSnapshot.Frames.Append(WithAppend(Hardware("keyInput", 5, 210), boundaryAppend + 1))
            .Append(WithAppend(Shortcut("undo", 5, 210), boundaryAppend + 2)).ToArray();
        Check(!ContactPlan(futureSnapshot with { Frames = futureFrames }).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "Commands after the durable editing boundary cannot change this fixed index prefix.");

        var probe = baseIndex.Strokes.Values.Single().ProbeMask;
        Check(probe.Contains(2, 1) && !probe.Contains(2, 4),
            "Guaranteed probe uses the native contact samples instead of the deliberately coarse label polyline.");
        Check(!probe.Contains(6, 6), "The distant penUp sample must not extend the guaranteed probe centerline.");
        var noView = new Fixture(); noView.RemoveState("canvasViewState");
        Excluded(noView, "Without a confirmed historical view native sample geometry cannot become editable.");
        var rotated = new Fixture();
        rotated.Before("penBegin", Update("canvasViewState", new { canvasOriginScreenPx = new { x = -10, y = -20 },
            ocrScalePercent = 50, ocrRotationDegrees = 90, transform = new { rotationDegrees = 90 } }, anchor: 1));
        rotated.Replace("penBegin", Fixture.PenFrame("penBegin", 2, 100, -10.75, -19.25, true));
        rotated.Replace("penSample", Fixture.PenFrame("penSample", 3, 110, -10.75, -17.75, true));
        var rotatedProbe = EditIndexBuilder.Build(rotated.Snapshot()).Strokes.Values.Single().ProbeMask;
        Check(rotatedProbe.Contains(2, 1) && !rotatedProbe.Contains(2, 4),
            "Native probe applies historical negative origin, clockwise rotation and zoom to physical screen samples.");

        var independent = new[]
        {
            WithAppend(Update("brushState", new { name = "First", properties = Array.Empty<object>() }, anchor: 0), 1),
            WithAppend(Update("brushState", new { name = "Second", properties = Array.Empty<object>() }, anchor: 2), 2),
            WithAppend(Update("brushState", Fixture.Brush, "unknown", anchor: 4), 3)
        };
        var history = StateHistory.Resolve(independent);
        Check(history.Count == 3 && StateHistory.At(history, 1).Brush?.Name == "First" &&
            StateHistory.At(history, 3).Brush?.Name == "Second" && StateHistory.At(history, 5).Brush is null,
            "Independent no-package coreStateUpdated records retain every causal slot including the final invalidation.");
        var unsorted = new Fixture();
        var snapshot = unsorted.Snapshot();
        Check(EditIndexBuilder.Build(snapshot with { History = StateHistory.Resolve(snapshot.Frames).Reverse().ToArray() }).Strokes.Count == 1,
            "Supplied history is resolved in Anchor/Order order rather than caller list order.");
        var mixed = new Fixture();
        mixed.Before("editBoundaryRequested", Update("currentLayerState", "Ink", "unknown", anchor: 4));
        mixed.Before("editBoundaryRequested", Frame("penBegin", 130, new { penDownLocation = new { region = "canvasViewport", status = "matched" } },
            5, "hardware", 5));
        mixed.Before("editBoundaryRequested", Frame("penEnd", 135, new { }, 6, "hardware", 5));
        var mixedSession = mixed.Snapshot();
        var mixedPacket = mixedSession.Packets.Single();
        var secondLabel = mixedPacket.Labels.Single() with { Id = "unknown-layer", OperationId = 5, FromTicks = 130, ToTicks = 135 };
        var mixedImage = mixedPacket.Images.Single() with { LabelIds = ["label", "unknown-layer"] };
        var mixedIndex = EditIndexBuilder.Build(mixedSession with { Packets = [mixedPacket with
            { Labels = [mixedPacket.Labels.Single(), secondLabel], Images = [mixedImage] }] });
        Check(mixedIndex.Strokes.Count == 1 && mixedIndex.Images.Values.Single().HasUnreplayableEvents,
            "An unknown-layer contact keeps a mixed image unreplayable even when another linked contact is eligible.");

        // A command earlier than the selected matrix must not poison all later
        // editing on the layer. Keep two independent capture groups so closure
        // does not pull the first contact into the second request's interval.
        var laterSelection = new Fixture();
        laterSelection.Before("editBoundaryRequested", Hardware("keyInput", 5, 130));
        laterSelection.Before("editBoundaryRequested", Shortcut("undo", 5, 130));
        laterSelection.Before("editBoundaryRequested", WithOperation(Fixture.PenFrame("penBegin", 6, 150, 5.5, 5.5, true), 6));
        laterSelection.Before("editBoundaryRequested", WithOperation(Fixture.PenFrame("penSample", 7, 155, 6.5, 5.5, true), 6));
        laterSelection.Before("editBoundaryRequested", WithOperation(Fixture.PenFrame("penEnd", 8, 160, 6.5, 5.5, false), 6));
        var laterSnapshot = laterSelection.Snapshot();
        var firstPacket = laterSnapshot.Packets.Single() with { TriggerTicks = 125, IsBoundary = false, NowSnapshotId = "first-now" };
        var laterLabel = firstPacket.Labels.Single() with { Id = "later-label", OperationId = 6, FromTicks = 150, ToTicks = 160,
            Segments = [new(5.5, 5.5, 6.5, 5.5)] };
        var laterImage = firstPacket.Images.Single() with { Key = new(firstPacket.Layer, "later-packet", "i"), LabelIds = [laterLabel.Id] };
        var laterPacket = firstPacket with { PacketId = "later-packet", TriggerTicks = 200, IsBoundary = true,
            AfterSnapshotId = "first-now", NowSnapshotId = "later-now", BaselineSource = null,
            Labels = [laterLabel], Images = [laterImage] };
        var laterSamples = laterSnapshot.Frames.Where(f => J.U64(f, "operationId") == 6 &&
            J.Text(f, "kind") is "penBegin" or "penSample" or "penEnd").Select(f =>
        {
            var data = J.Get(f, "data"); bool contact = J.Text(f, "kind") != "penEnd";
            return new MemolineReplaySample(J.I64(f, "ticks"), J.Number(J.Get(data, "x")) ?? 0,
                J.Number(J.Get(data, "y")) ?? 0, contact ? 1 : 0, 0, 0, contact, J.U64(f, "appendId"), J.U64(f, "eventId"));
        }).ToArray();
        laterSnapshot = laterSnapshot with { Packets = [firstPacket, laterPacket], Replay = laterSnapshot.Replay with
            { Strokes = laterSnapshot.Replay.Strokes.Concat([new MemolineReplayStroke(6, 150, 160, new(150, 0, 0, 0), laterSamples)]).ToArray() } };
        var laterIndex = EditIndexBuilder.Build(laterSnapshot);
        var selectedLater = laterIndex.Strokes.Values.Single(s => s.OperationId == 6);
        var laterRequest = EditRequest.Event(firstPacket.Layer, selectedLater.WriteMask,
            new Dictionary<EditableStrokeKey, EventChange> { [selectedLater.Key] = new(Delete: true) });
        Check(ReconstructionPlanner.Build(laterIndex, laterRequest).IsValid,
            "A confirmed undo before the selected later matrix does not block that request.");
        var selectedFirst = laterIndex.Strokes.Values.Single(s => s.OperationId == 2);
        var firstRequest = laterRequest with { Changes = new Dictionary<EditableStrokeKey, EventChange> { [selectedFirst.Key] = new(Delete: true) } };
        Check(ReconstructionPlanner.Build(laterIndex, firstRequest).Issues.Any(i => i.Code == PlanIssueCode.UndoRedoInRange),
            "The same undo still blocks a request beginning at the earlier affected contact.");

        if (failures.Count > 0) throw new InvalidOperationException(
            $"Edit index hardening checks: {count - failures.Count}/{count} passed.\n" + string.Join("\n", failures));
        Console.WriteLine($"Edit index hardening checks passed: {count}.");
        return count;
    }

    private static JsonElement Frame(string kind, long ticks, object data, ulong eventId = 0,
        string path = "state", ulong operation = 0, ulong[]? refs = null)
        => JsonSerializer.SerializeToElement(new { kind, ticks, path, eventId, operationId = operation,
            relatedEventIds = refs ?? [], deviceSource = new { deviceType = kind.StartsWith("pen") ? "pen" : "keyboard" }, data });
    private static JsonElement Hardware(string kind, ulong eventId, long ticks)
        => Frame(kind, ticks, new { }, eventId, "hardware");
    private static JsonElement Transaction(string kind, string id, long ticks)
        => Frame(kind, ticks, new { transactionId = id });
    private static JsonElement Shortcut(string command, ulong anchor, long ticks)
        => Frame("shortcutResolved", ticks, new { matches = new[] { new { command } } }, refs: [anchor]);
    private static JsonElement Invalidate(string module)
        => Frame("panelUpdateRequested", 95, new { modules = new[] { module }, phase = "invalidate",
            recognitionStatus = "pendingModule", reason = "shortcut" }, refs: [1]);
    private static JsonElement Update(string module, object state, string status = "changed", bool ambiguous = false, ulong anchor = 0)
        => Frame("coreStateUpdated", anchor == 0 ? 0 : 95, new { module, status, state,
            evidence = new { observedAfterEventId = anchor, causalAmbiguous = ambiguous } });
    private static JsonElement WithAppend(JsonElement frame, ulong append)
    {
        var node = JsonNode.Parse(frame.GetRawText())!.AsObject(); node["appendId"] = append;
        return JsonSerializer.SerializeToElement(node);
    }
    private static JsonElement WithOperation(JsonElement frame, ulong operation)
    {
        var node = JsonNode.Parse(frame.GetRawText())!.AsObject(); node["operationId"] = operation;
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed class Fixture
    {
        internal static object Brush => new { name = "G pen", properties = new[]
            { new { key = "brush_size", type = "number", status = "ok", value = 2 } } };
        internal static object Clip => new { layers = new[] { new { name = "Ink", uuid = "ink" }, new { name = "Other", uuid = "other" } } };
        internal static object View => new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 };
        internal static object State(string module) => module switch
        { "brushState" => Brush, "clipState" => Clip, "canvasViewState" => View, _ => "Ink" };
        private readonly List<JsonElement> _frames =
        [
            Frame("shortcutConfiguration", 0, new { toolCatalog = new { nodes = new[]
            { new { id = "t", kind = "tool", name = "Pen", parentId = "r", toolId = "t" },
              new { id = "s", kind = "subtool", name = "G pen", parentId = "t", toolId = "t" } } } }),
            Update("brushState", Brush), Update("currentLayerState", "Ink"), Update("clipState", Clip), Update("canvasViewState", View),
            Hardware("keyInput", 1, 95),
            PenFrame("penBegin", 2, 100, 1.5, 1.5, true),
            PenFrame("penSample", 3, 110, 4.5, 1.5, true),
            PenFrame("penEnd", 4, 120, 7.5, 7.5, false),
            Frame("editBoundaryRequested", 200, new { })
        ];
        internal static JsonElement PenFrame(string kind, ulong eventId, long ticks, double x, double y, bool contact)
            => Frame(kind, ticks, new { x, y, normalizedPressure = contact ? 1 : 0,
                penDownLocation = new { region = "canvasViewport", status = "matched" } }, eventId, "hardware", 2);
        internal void Before(string kind, JsonElement frame) => _frames.Insert(_frames.FindIndex(f => J.Text(f, "kind") == kind), frame);
        internal void Replace(string kind, JsonElement frame) => _frames[_frames.FindIndex(f => J.Text(f, "kind") == kind)] = frame;
        internal void Remove(string kind) => _frames.RemoveAll(f => J.Text(f, "kind") == kind);
        internal void RemoveState(string module) => _frames.RemoveAll(f => J.Text(J.Get(f, "data"), "module") == module);
        internal void ReplaceState(string module, JsonElement data) => _frames[_frames.FindIndex(f => J.Text(J.Get(f, "data"), "module") == module)]
            = Frame("coreStateUpdated", 0, data);
        internal SessionSnapshot Snapshot()
        {
            var frames = _frames.Select((f, i) => WithAppend(f, (ulong)i + 1)).ToArray();
            var samples = frames.Where(f => J.U64(f, "operationId") == 2 && J.Text(f, "kind") is "penBegin" or "penSample" or "penEnd").Select(f =>
            {
                var data = J.Get(f, "data"); bool contact = J.Text(f, "kind") != "penEnd";
                return new MemolineReplaySample(J.I64(f, "ticks"), J.Number(J.Get(data, "x")) ?? 0,
                    J.Number(J.Get(data, "y")) ?? 0, contact ? 1 : 0, 0, 0, contact, J.U64(f, "appendId"), J.U64(f, "eventId"));
            }).ToArray();
            var drawing = new MemolineReplayStroke(2, 100, 120, new(100, 0, 0, 0), samples);
            var replay = new MemolineReplayDocument("fixture.part", "stage4", 1000, [drawing]);
            var layer = new LayerKey("stage4", 1, "ink");
            var mask = new PixelMask(8, 8); mask.Set(2, 4);
            var image = new DirtyImage(new(layer, "p", "i"), mask,
                new(8, 8, new byte[256]), new(8, 8, new byte[256]), []) { LabelIds = ["label"] };
            var label = new LabelEvidence("label", 2, "tablet", 100, 120, 1, [new(1.5, 4.5, 4.5, 4.5)], ["i"]);
            var packet = new LayerPacket("p", layer, 200, null, new(8, 8, new byte[256]), [image],
                IsBoundary: true, AfterSnapshotId: "baseline", NowSnapshotId: "current", BaselineSource: "recordingStart",
                CanvasWidth: 8, CanvasHeight: 8) { Labels = [label] };
            return new("fixture.part", "stage4", 1000, frames, replay, [packet], J.U64(frames[^1], "appendId"), 200);
        }
    }
}
