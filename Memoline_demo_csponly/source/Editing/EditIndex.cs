using System.Text.Json;
using PacketReplay;

namespace MemolineDemo.Editing;

internal static class EditIndexBuilder
{
    private sealed record Contact(LayerKey? Layer, EditableStroke? Stroke);

    internal static EditIndex Build(SessionSnapshot session)
    {
        var history = session.History.Count > 0 ? session.History : StateHistory.Resolve(session.Frames);
        var classifier = new BrushClassifier();
        var replay = session.Replay.Strokes.ToDictionary(s => s.OperationId);
        var contacts = new Dictionary<ulong, Contact>();
        var strokes = new Dictionary<EditableStrokeKey, EditableStroke>();
        foreach (var begin in session.Frames.Where(f => J.Text(f, "kind") == "penBegin").OrderBy(f => J.U64(f, "appendId")))
        {
            ulong beginAppend = J.U64(begin, "appendId"), beginEvent = J.U64(begin, "eventId");
            ulong operation = J.U64(begin, "operationId"); if (operation == 0) operation = beginEvent;
            if (!contacts.TryAdd(operation, new(null, null))) throw new InvalidDataException("原生 penBegin operationId 重复。");
            var state = StateHistory.At(history, beginEvent);
            var clip = Latest(history, "clipState", beginEvent);
            LayerKey? layer = ResolveLayer(session.Packets, state.Layer, clip, session.SessionId);
            contacts[operation] = new(layer, null);
            var brushData = Latest(history, "brushState", beginEvent);
            var catalogFrame = session.Frames.LastOrDefault(f => J.Text(f, "kind") == "shortcutConfiguration" && J.U64(f, "appendId") < beginAppend);
            var classification = classifier.Classify(brushData, ToolCatalog.FromJson(J.Get(catalogFrame, "data")));
            bool complete = session.Frames.Any(f => J.U64(f, "appendId") >= beginAppend && J.U64(f, "appendId") <= session.BoundaryAppendId &&
                (J.Text(f, "kind") == "penEnd" && J.U64(f, "operationId") == operation ||
                 J.Text(f, "kind") == "penInterrupted" && J.Array(J.Get(f, "relatedEventIds")).Any(e => e.TryUnsigned() == beginEvent)));
            var location = J.Get(J.Get(begin, "data"), "penDownLocation");
            bool valid = complete && beginAppend <= session.BoundaryAppendId && layer is not null && classification.Eligible &&
                J.Text(J.Get(begin, "deviceSource"), "deviceType") == "pen" && J.Text(location, "region") == "canvasViewport" &&
                J.Text(location, "status") == "matched" && J.Get(J.Get(begin, "data"), "injected").ValueKind != JsonValueKind.True &&
                !InsideTransaction(session.Frames, beginAppend, session.BoundaryAppendId) && replay.ContainsKey(operation);
            if (!valid) continue;
            var drawing = replay[operation];
            var labels = session.Packets.Where(p => p.Layer == layer).SelectMany(p => p.Labels).Where(l => l.OperationId == operation).ToArray();
            var packet = session.Packets.First(p => p.Layer == layer);
            int width = packet.CanvasWidth, height = packet.CanvasHeight;
            var write = Footprints.RasterizeCoverage(labels, width, height);
            var probe = classification.ProbeGuaranteed ? Footprints.RasterizeCoverage(labels, width, height, centerline: true) : write.Clone();
            var impact = classification.Causal ? (classification.ReadsUnknownSource ? PixelMask.FromPredicate(0, 0, width, height, (_, _) => true) : write.Clone())
                : new PixelMask(width, height);
            var key = new EditableStrokeKey(session.SessionId, beginAppend);
            var editable = new EditableStroke(key, operation, drawing.StartTicks, drawing.EndTicks, drawing.Samples,
                state.Brush?.Name, layer!.Value, classification.Causal, BrushNumbers: state.Brush?.Numbers.ToDictionary(n => n.Key, n => n.Value))
            { BeginEventId = beginEvent, BrushState = J.Get(brushData, "state").Clone(), Classification = classification,
                WriteMask = write, ProbeMask = probe, ImpactMask = impact };
            contacts[operation] = new(layer, editable);
            strokes.Add(key, editable);
        }

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
                    if (contact.Layer != packet.Layer) continue;
                    if (contact.Stroke is null) { unreplayable = true; continue; }
                    linked.Add(contact.Stroke);
                }
                linked = linked.DistinctBy(s => s.Key).ToList();
                if (linked.Count == 0) unreplayable = true;
                var write = new PixelMask(packet.CanvasWidth, packet.CanvasHeight);
                var probe = new PixelMask(packet.CanvasWidth, packet.CanvasHeight);
                var impact = new PixelMask(packet.CanvasWidth, packet.CanvasHeight);
                foreach (var stroke in linked)
                { write.UnionInPlace(stroke.WriteMask); probe.UnionInPlace(stroke.ProbeMask); impact.UnionInPlace(stroke.ImpactMask); }
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
            layers.Add(group.Key, new(group.Key, packets, layerStrokes, issues.Count == 0, issues, Current: packets.Last().Current));
            allIssues.AddRange(issues);
        }
        return new(layers, strokes, images, allIssues);
    }

    private static JsonElement Latest(IReadOnlyList<HistorySlot> history, string module, ulong beginEvent)
        => history.LastOrDefault(h => h.Module == module && h.Anchor < beginEvent)?.Data ?? default;

    private static LayerKey? ResolveLayer(IReadOnlyList<LayerPacket> packets, string? name, JsonElement clip, string sessionId)
    {
        if (name is null || !J.Confirmed(clip)) return null;
        var matches = J.Array(J.Get(J.Get(clip, "state"), "layers")).Where(l => J.Text(l, "name") is { } n && J.Name(n) == J.Name(name)).ToArray();
        if (matches.Length != 1 || J.Text(matches[0], "uuid") is not { } uuid) return null;
        var layers = packets.Where(p => p.Layer.SessionId == sessionId && p.Layer.Uuid == uuid).Select(p => p.Layer).Distinct().ToArray();
        return layers.Length == 1 ? layers[0] : null;
    }

    private static bool InsideTransaction(IReadOnlyList<JsonElement> frames, ulong beginAppend, ulong boundary)
    {
        var active = new HashSet<string>();
        foreach (var frame in frames.Where(f => J.U64(f, "appendId") < beginAppend && J.U64(f, "appendId") <= boundary).OrderBy(f => J.U64(f, "appendId")))
        {
            var kind = J.Text(frame, "kind"); string id = J.Text(J.Get(frame, "data"), "transactionId") ?? "unknown";
            if (kind == "editTransactionBegan") active.Add(id);
            if (kind == "editTransactionEnded") active.Remove(id);
        }
        return active.Count > 0;
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
