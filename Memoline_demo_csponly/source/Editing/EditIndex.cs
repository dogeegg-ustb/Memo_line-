using System.Text.Json;
using PacketReplay;

namespace MemolineDemo.Editing;

internal static class EditIndexBuilder
{
    private sealed record Contact(LayerKey? Layer, EditableStroke? Stroke, ulong BeginAppend, ulong BeginEvent);
    private sealed record TransactionInterval(ulong Begin, ulong End);

    internal static EditIndex Build(SessionSnapshot session)
    {
        var history = (session.History.Count > 0 ? session.History : StateHistory.Resolve(session.Frames))
            .OrderBy(h => h.Anchor).ThenBy(h => h.Order).ToArray();
        var frames = session.Frames.Where(f => J.U64(f, "appendId") <= session.BoundaryAppendId).OrderBy(f => J.U64(f, "appendId")).ToArray();
        var controlFrames = frames.Where(f => J.Text(f, "kind") is "coreStateUpdated" or "panelUpdateRequested" or
            "statePackageReserved" or "statePackageResult").ToArray();
        var catalogs = frames.Where(f => J.Text(f, "kind") == "shortcutConfiguration").ToArray();
        var endings = frames.Where(f => J.Text(f, "kind") is "penEnd" or "penInterrupted").ToArray();
        var transactions = TransactionIntervals(frames, session.BoundaryAppendId);
        var classifier = new BrushClassifier();
        var replay = session.Replay.Strokes.ToDictionary(s => s.OperationId);
        var contacts = new Dictionary<ulong, Contact>();
        var strokes = new Dictionary<EditableStrokeKey, EditableStroke>();
        var undoProofs = new List<NativeUndoPenProof>();
        var strokeStage = EditingProfiler.Stage("index strokes");
        foreach (var begin in frames.Where(f => J.Text(f, "kind") == "penBegin"))
        {
            ulong beginAppend = J.U64(begin, "appendId"), beginEvent = J.U64(begin, "eventId");
            ulong operation = J.U64(begin, "operationId"); if (operation == 0) operation = beginEvent;
            if (!contacts.TryAdd(operation, new(null, null, beginAppend, beginEvent))) throw new InvalidDataException("原生 penBegin operationId 重复。");
            HistoricalState state; using (EditingProfiler.Part("state at")) state = StateHistory.At(history, beginEvent);
            var clip = Latest(history, "clipState", beginEvent);
            var usable = EditingProfiler.Part("usable/layer");
            bool layerConfirmed = UsableAt(history, controlFrames, "currentLayerState", beginEvent, beginAppend, Ticks(begin)) &&
                UsableAt(history, controlFrames, "clipState", beginEvent, beginAppend, Ticks(begin));
            LayerKey? layer = layerConfirmed ? ResolveLayer(session.Packets, state.Layer, clip, session.SessionId) : null;
            usable.Dispose();
            contacts[operation] = new(layer, null, beginAppend, beginEvent);
            var brushData = Latest(history, "brushState", beginEvent);
            var catalogFrame = catalogs.LastOrDefault(f => J.U64(f, "appendId") < beginAppend);
            BrushClassification classification; using (EditingProfiler.Part("classify")) classification = classifier.Classify(brushData, ToolCatalog.FromJson(J.Get(catalogFrame, "data")));
            var end = endings.FirstOrDefault(f => J.U64(f, "appendId") >= beginAppend &&
                (J.Text(f, "kind") == "penEnd" && J.U64(f, "operationId") == operation ||
                 J.Text(f, "kind") == "penInterrupted" && J.Array(J.Get(f, "relatedEventIds")).Any(e => e.TryUnsigned() == beginEvent)));
            ulong endAppend = end.ValueKind == JsonValueKind.Object ? J.U64(end, "appendId") : session.BoundaryAppendId;
            var location = J.Get(J.Get(begin, "data"), "penDownLocation");
            bool undoUnit = end.ValueKind == JsonValueKind.Object && J.Text(end, "kind") == "penEnd" &&
                layer is not null && classification.Eligible && state.Brush is not null &&
                UsableAt(history, controlFrames, "brushState", beginEvent, beginAppend, Ticks(begin)) &&
                J.Text(location, "region") == "canvasViewport" && J.Text(location, "status") == "matched" &&
                !transactions.Any(t => t.Begin <= endAppend && t.End >= beginAppend);
            undoProofs.Add(new(beginAppend, operation, layer, undoUnit, false));
            bool valid = end.ValueKind == JsonValueKind.Object && layer is not null && classification.Eligible && state.Brush is not null &&
                UsableAt(history, controlFrames, "brushState", beginEvent, beginAppend, Ticks(begin)) && state.View is not null &&
                UsableAt(history, controlFrames, "canvasViewState", beginEvent, beginAppend, Ticks(begin)) &&
                J.Text(J.Get(begin, "deviceSource"), "deviceType") == "pen" && J.Text(location, "region") == "canvasViewport" &&
                J.Text(location, "status") == "matched" && J.Get(J.Get(begin, "data"), "injected").ValueKind != JsonValueKind.True &&
                !transactions.Any(t => t.Begin <= endAppend && t.End >= beginAppend) && replay.ContainsKey(operation);
            if (!valid) continue;
            var drawing = replay[operation];
            if (drawing.Samples.Any(s => s.AppendId > session.BoundaryAppendId)) continue;
            var labels = session.Packets.Where(p => p.Layer == layer).SelectMany(p => p.Labels).Where(l => l.OperationId == operation).ToArray();
            var packet = session.Packets.First(p => p.Layer == layer);
            int width = packet.CanvasWidth, height = packet.CanvasHeight;
            if (!Footprints.TryMapContactSamples(drawing.Samples, Latest(history, "canvasViewState", beginEvent), width, height, out var points)) continue;
            var raster = EditingProfiler.Part("footprints");
            var write = Footprints.RasterizeCoverageBounded(labels, width, height);
            var probe = classification.ProbeGuaranteed
                ? Footprints.RasterizeCenterlineBounded(points, width, height)
                : write.Clone();
            var impact = classification.Causal ? (classification.ReadsUnknownSource ? PixelMask.Filled(0, 0, width, height) : write.Clone())
                : new PixelMask(0, 0);
            raster.Dispose();
            var key = new EditableStrokeKey(session.SessionId, beginAppend);
            var editable = new EditableStroke(key, operation, drawing.StartTicks, drawing.EndTicks, drawing.Samples,
                state.Brush?.Name, layer!.Value, classification.Causal, BrushNumbers: state.Brush?.Numbers.ToDictionary(n => n.Key, n => n.Value))
            { BeginEventId = beginEvent, BrushState = J.Get(brushData, "state").Clone(), Classification = classification,
                WriteMask = write, ProbeMask = probe, ImpactMask = impact, CoverageLabels = labels };
            contacts[operation] = new(layer, editable, beginAppend, beginEvent);
            strokes.Add(key, editable);
            undoProofs[^1] = undoProofs[^1] with { ReplayEligible = true };
        }

        strokeStage.Dispose(); EditingProfiler.Report();
        var imageStage = EditingProfiler.Stage("index images + chain");
        var images = new Dictionary<DirtyImageKey, DirtyImage>();
        var updatedPackets = new List<LayerPacket>();
        foreach (var packet in session.Packets)
        {
            var labels = packet.Labels.ToDictionary(l => l.Id);
            var updated = new List<DirtyImage>();
            foreach (var image in packet.Images)
            {
                var linked = new List<EditableStroke>(); bool unreplayable = image.HasUnreplayableEvents;
                foreach (var labelId in image.LabelIds)
                {
                    if (!labels.TryGetValue(labelId, out var label)) throw new InvalidDataException("图片引用不存在的标签。");
                    if (label.Source == "low-resolution-fallback" || label.OperationId is null ||
                        !contacts.TryGetValue(label.OperationId.Value, out var contact)) { unreplayable = true; continue; }
                    if (contact.Layer is null) { unreplayable = true; continue; }
                    if (contact.Layer != packet.Layer) continue;
                    if (contact.Stroke is null) { unreplayable = true; continue; }
                    linked.Add(contact.Stroke);
                }
                linked = linked.DistinctBy(s => s.Key).ToList();
                if (linked.Count == 0) unreplayable = true;
                var write = Footprints.UnionBounded(linked.Select(s=>s.WriteMask));
                var probe = Footprints.UnionBounded(linked.Select(s=>s.ProbeMask));
                var impact = Footprints.UnionBounded(linked.Select(s=>s.ImpactMask));
                var resolved = image with { Events = linked.Select(s => s.Key).ToArray(), IsCausal = linked.Any(s => s.IsCausal),
                    HasUnreplayableEvents = unreplayable, W = write, Probe = probe, Impact = impact };
                images.Add(image.Key, resolved); updated.Add(resolved);
            }
            updatedPackets.Add(packet with { Images = updated });
        }
        foreach (var key in strokes.Keys.ToArray())
            strokes[key] = strokes[key] with { Images = images.Values.Where(i => i.Events.Contains(key)).Select(i => i.Key).ToArray() };
        var layers = new Dictionary<LayerKey, LayerIndex>(); var allIssues = new List<PlanIssue>();
        foreach (var group in updatedPackets.GroupBy(p => p.Layer))
        {
            var packets = group.OrderBy(p => p.TriggerTicks).ToArray();
            var layerStrokes = strokes.Values.Where(s => s.Layer == group.Key).OrderBy(s => s.BeginEventId).ToArray();
            var issues = VerifyChain(packets, layerStrokes);
            layers.Add(group.Key, new(group.Key, packets, layerStrokes, issues.Count == 0, issues, Current: packets.Last().Current)
            { UndoRedo = ReadUndoRedo(group.Key, history, frames, controlFrames) });
            allIssues.AddRange(issues);
        }
        var index = new EditIndex(layers, strokes, images, allIssues);
        imageStage.Dispose();
        NativeUndoTimelineResult undoTimeline;
        using (EditingProfiler.Stage("undo timeline")) undoTimeline = NativeUndoTimeline.Build(session, undoProofs);
        using (EditingProfiler.Stage("effective undo history"))
            return EffectiveUndoHistory.Apply(session, index, undoTimeline) with { UndoTimeline = undoTimeline };
    }

    private static JsonElement Latest(IReadOnlyList<HistorySlot> history, string module, ulong beginEvent)
        => history.LastOrDefault(h => h.Module == module && h.Anchor < beginEvent)?.Data ?? default;

    private static LayerKey? ResolveLayer(IReadOnlyList<LayerPacket> packets, string? name, JsonElement clip, string sessionId)
    {
        if (ResolveLayerUuid(name, clip) is not { } uuid) return null;
        var layers = packets.Where(p => p.Layer.SessionId == sessionId && p.Layer.Uuid == uuid).Select(p => p.Layer).Distinct().ToArray();
        return layers.Length == 1 ? layers[0] : null;
    }

    private static string? ResolveLayerUuid(string? name, JsonElement clip)
    {
        if (name is null || !Confirmed(clip)) return null;
        var matches = J.Array(J.Get(J.Get(clip, "state"), "layers"))
            .Where(l => J.Text(l, "name") is { } n && J.Name(n) == J.Name(name)).ToArray();
        return matches.Length == 1 ? J.Text(matches[0], "uuid") : null;
    }

    private static bool Confirmed(JsonElement data) => J.Confirmed(data) &&
        J.Get(J.Get(data, "evidence"), "causalAmbiguous").ValueKind != JsonValueKind.True;

    private static bool Usable(IReadOnlyList<HistorySlot> history, IReadOnlyList<JsonElement> frames,
        string module, ulong eventId, ulong appendId)
        => UsableAt(history, frames, module, eventId, appendId,
            Ticks(frames.FirstOrDefault(f => J.U64(f, "appendId") == appendId)));

    // Frame-derived lookups reused by every contact of one build. Without them each contact
    // rescans every control frame several times, which is quadratic in the recording length.
    private sealed class FrameMemo(IReadOnlyList<JsonElement> frames)
    {
        internal readonly JsonElement[] Reserved = frames.Where(f => J.Text(f, "kind") == "statePackageReserved").ToArray();
        internal readonly JsonElement[] Results = frames.Where(f => J.Text(f, "kind") == "statePackageResult").ToArray();
        internal readonly JsonElement[] Invalidations = frames.Where(f => J.Text(f, "kind") == "panelUpdateRequested" &&
            J.Text(J.Get(f, "data"), "phase") == "invalidate").ToArray();
        internal readonly Dictionary<HistorySlot, ConfirmationEvidence?> Confirmations = new(ReferenceEqualityComparer.Instance);
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<JsonElement>, FrameMemo> Memos = new();
    private static FrameMemo Memo(IReadOnlyList<JsonElement> frames) => Memos.GetValue(frames, f => new FrameMemo(f));

    private static bool UsableAt(IReadOnlyList<HistorySlot> history, IReadOnlyList<JsonElement> frames,
        string module, ulong eventId, ulong appendId, long? inputTicks)
    {
        var latest = history.LastOrDefault(h => h.Module == module && h.Anchor < eventId);
        if (latest is null || !Confirmed(latest.Data)) return false;
        var memo = Memo(frames);
        if (!memo.Confirmations.TryGetValue(latest, out var confirmation))
            memo.Confirmations.Add(latest, confirmation = Confirmation(latest, frames));
        if (confirmation is null || !BeforeInput(confirmation, appendId, inputTicks)) return false;
        foreach (var invalidation in memo.Invalidations.Where(f => J.U64(f, "appendId") < appendId &&
            J.Array(J.Get(J.Get(f, "data"), "modules")).Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == module)))
        {
            var refs = J.Array(J.Get(invalidation, "relatedEventIds")).Select(r => r.TryUnsigned()).Where(r => r > 0).ToArray();
            // An activation without input references invalidates every earlier observation,
            // but a fresh same-session observation can confirm the state before this contact.
            // Publication alone is insufficient when its screenshot predates the invalidation.
            if (refs.Length == 0)
            {
                if (!AfterInvalidation(confirmation, invalidation)) return false;
                continue;
            }
            ulong anchor = refs.Max();
            if (anchor < eventId && (latest.Anchor < anchor || latest.Anchor == anchor &&
                !AfterInvalidation(confirmation, invalidation))) return false;
        }
        HashSet<string?>? resolved = null;
        foreach (var pending in memo.Reserved.Where(f => J.U64(f, "appendId") < appendId))
        {
            var data = J.Get(pending, "data");
            ulong anchor = J.U64(data, "afterEventId");
            if (anchor >= eventId || latest.Anchor > anchor) continue;
            resolved ??= memo.Results
                .Where(f => BeforeInput(new(J.U64(f, "appendId"), CaptureTicksFromResult(f), Ticks(f)), appendId, inputTicks))
                .Select(f => J.Text(J.Get(f, "data"), "packageId")).ToHashSet();
            if (!resolved.Contains(J.Text(data, "packageId"))) return false;
        }
        return true;
    }

    private sealed record ConfirmationEvidence(ulong Append, long? CapturedTicks, long? FrameTicks);

    private static long? Ticks(JsonElement frame)
        => J.Get(frame, "ticks").ValueKind == JsonValueKind.Number && J.Get(frame, "ticks").TryGetInt64(out long ticks) ? ticks : null;

    private static long? CaptureTicks(JsonElement data)
    {
        var value = J.Get(J.Get(data, "evidence"), "capturedTicks");
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long ticks) && ticks >= 0 ? ticks : null;
    }

    private static long? CaptureTicksFromResult(JsonElement frame)
    {
        var captures = J.Array(J.Get(J.Get(J.Get(frame, "data"), "result"), "updates"))
            .Select(CaptureTicks).Where(t => t is not null).Select(t => t!.Value).ToArray();
        return captures.Length > 0 ? captures.Max() : null;
    }

    private static bool BeforeInput(ConfirmationEvidence evidence, ulong appendId, long? inputTicks)
    {
        if (evidence.CapturedTicks is { } captured && inputTicks is { } input) return captured < input;
        // Old recordings without a capture timestamp need an observation published
        // before the native input; a delayed future frame cannot prove their old state.
        return evidence.Append < appendId &&
            (evidence.FrameTicks is not { } frameTicks || inputTicks is not { } ticks || frameTicks < ticks);
    }

    private static bool AfterInvalidation(ConfirmationEvidence evidence, JsonElement invalidation)
        => evidence.Append > J.U64(invalidation, "appendId") &&
            (Ticks(invalidation) is not { } invalidTicks ||
             (evidence.CapturedTicks ?? evidence.FrameTicks) is not { } observedTicks || observedTicks >= invalidTicks);

    private static ulong ConfirmationAppend(HistorySlot slot, IReadOnlyList<JsonElement> frames)
        => Confirmation(slot, frames)?.Append ?? 0;

    private static ConfirmationEvidence? Confirmation(HistorySlot slot, IReadOnlyList<JsonElement> frames)
    {
        ConfirmationEvidence? confirmation = null;
        string? slotPackage = J.Text(slot.Data, "packageId");
        var packages = frames.Where(f => J.Text(f, "kind") == "statePackageReserved")
            .Select(f => J.Get(f, "data")).Where(d => J.U64(d, "afterEventId") == slot.Anchor && J.U64(d, "reservationOrder") == slot.Order)
            .Where(d => slotPackage is null || J.Text(d, "packageId") == slotPackage)
            .Select(d => J.Text(d, "packageId")).ToHashSet();
        foreach (var frame in frames)
        {
            var data = J.Get(frame, "data");
            bool matches = J.Text(frame, "kind") == "coreStateUpdated" && JsonElement.DeepEquals(data, slot.Data) &&
                (packages.Contains(J.Text(data, "packageId")) || J.U64(frame, "appendId") == slot.Order);
            matches |= J.Text(frame, "kind") == "statePackageResult" && packages.Contains(J.Text(data, "packageId")) &&
                J.Array(J.Get(J.Get(data, "result"), "updates")).Any(u => JsonElement.DeepEquals(u, slot.Data));
            ulong append = J.U64(frame, "appendId");
            // Repeated identical publication must not move an old causal slot forward.
            if (matches && append > 0 && (confirmation is null || append < confirmation.Append))
                confirmation = new(append, CaptureTicks(slot.Data), Ticks(frame));
        }
        return confirmation;
    }

    private static IReadOnlyList<TransactionInterval> TransactionIntervals(IReadOnlyList<JsonElement> frames, ulong boundary)
    {
        var active = new Dictionary<string, ulong>();
        var intervals = new List<TransactionInterval>();
        foreach (var frame in frames)
        {
            var kind = J.Text(frame, "kind"); string id = J.Text(J.Get(frame, "data"), "transactionId") ?? "unknown";
            if (kind == "editTransactionBegan") active.TryAdd(id, J.U64(frame, "appendId"));
            if (kind == "editTransactionEnded" && active.Remove(id, out ulong start))
                intervals.Add(new(start, J.U64(frame, "appendId")));
        }
        intervals.AddRange(active.Values.Select(start => new TransactionInterval(start, boundary)));
        return intervals;
    }

    private static IReadOnlyList<UndoRedoEvidence> ReadUndoRedo(LayerKey layer,
        IReadOnlyList<HistorySlot> history, IReadOnlyList<JsonElement> frames, IReadOnlyList<JsonElement> controlFrames)
    {
        var hardware = frames.Where(f => J.Text(f, "path") == "hardware" && J.U64(f, "eventId") > 0)
            .ToDictionary(f => J.U64(f, "eventId"));
        var evidence = new List<UndoRedoEvidence>();
        foreach (var result in frames.Where(f => J.Text(f, "kind") == "shortcutResolved"))
        {
            var commands = J.Array(J.Get(J.Get(result, "data"), "matches")).Select(m => J.Text(m, "command"))
                .Where(c => c is "undo" or "redo").OfType<string>().Distinct().ToArray();
            if (commands.Length == 0) continue;
            var refs = J.Array(J.Get(result, "relatedEventIds")).Select(r => r.TryUnsigned()).Where(r => r > 0).ToArray();
            // A missing native reference has no provable occurrence position.
            // Retain it for every candidate layer; the request must reject it.
            if (refs.Length == 0 || refs.Any(r => !hardware.ContainsKey(r)))
                evidence.AddRange(commands.Select(command => new UndoRedoEvidence(0, command, true)));
            foreach (ulong anchor in refs.Where(hardware.ContainsKey))
            {
                ulong append = J.U64(hardware[anchor], "appendId");
                string? uuid = null;
                if (UsableAt(history, controlFrames, "currentLayerState", anchor, append, Ticks(hardware[anchor])) &&
                    UsableAt(history, controlFrames, "clipState", anchor, append, Ticks(hardware[anchor])))
                    uuid = ResolveLayerUuid(StateHistory.At(history, anchor).Layer, Latest(history, "clipState", anchor));
                if (uuid is null || uuid == layer.Uuid)
                    evidence.AddRange(commands.Select(command => new UndoRedoEvidence(append, command, uuid is null)));
            }
        }
        return evidence.Distinct().OrderBy(item => item.InputAppendId).ToArray();
    }

    private static List<PlanIssue> VerifyChain(IReadOnlyList<LayerPacket> packets, IReadOnlyList<EditableStroke> strokes)
    {
        var issues = new List<PlanIssue>();
        var diffs = packets.Where(p => !p.IsRecordingBaseline).ToArray();
        if (diffs.Length == 0) { issues.Add(new(PlanIssueCode.NoBaseline, "图层没有可重建的差异链。")); return issues; }
        var first = diffs[0];
        if (first.BaselineSource is not ("recordingStart" or "createdEmpty" or "reproduction")) issues.Add(new(PlanIssueCode.NoBaseline, "首个差异包没有可信的 baseline。"));
        if (diffs.Any(p => !p.FullResolution)) issues.Add(new(PlanIssueCode.ChainLossy, "旧录制没有使用全分辨率比较。"));
        for (int i = 0; i < diffs.Length; i++)
        {
            var packet = diffs[i];
            if (packet.Layer != first.Layer || packet.CanvasWidth != first.CanvasWidth || packet.CanvasHeight != first.CanvasHeight ||
                packet.AfterSnapshotId is null || packet.NowSnapshotId is null || i > 0 && packet.AfterSnapshotId != diffs[i - 1].NowSnapshotId)
                issues.Add(new(PlanIssueCode.ChainBroken, "图层快照链在捕获组断开：" + packet.PacketId));
            if (packet.AfterDescriptor.ValueKind == JsonValueKind.Object && packet.NowDescriptor.ValueKind == JsonValueKind.Object &&
                (J.I64(packet.AfterDescriptor, "width") != J.I64(packet.NowDescriptor, "width") || J.I64(packet.AfterDescriptor, "height") != J.I64(packet.NowDescriptor, "height") ||
                 J.I64(packet.AfterDescriptor, "generation") != J.I64(packet.NowDescriptor, "generation") ||
                 J.Text(packet.AfterDescriptor, "sessionId") != J.Text(packet.NowDescriptor, "sessionId") ||
                 J.Text(J.Get(packet.AfterDescriptor, "layer"), "uuid") != J.Text(J.Get(packet.NowDescriptor, "layer"), "uuid")))
                issues.Add(new(PlanIssueCode.ChainBroken, "捕获组前后图层身份或尺寸不同：" + packet.PacketId));
        }
        if (strokes.Count > 0 && first.AfterDescriptor.ValueKind == JsonValueKind.Object && J.I64(first.AfterDescriptor, "triggerTicks") >= strokes.Min(s => s.StartTicks))
            issues.Add(new(PlanIssueCode.NoBaseline, "baseline 不早于首个合格笔接触。"));
        if (!diffs[^1].IsBoundary || diffs[^1].Current is null) issues.Add(new(PlanIssueCode.CaptureMismatch, "编辑边界缺少最后一组的整层捕获。"));
        return issues;
    }
}
