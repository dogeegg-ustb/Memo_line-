namespace MemolineDemo.Editing;

internal enum ReconstructionSegmentKind { Compose, Replay }
internal sealed record TrustReason(string Rule, IReadOnlyList<(int X, int Y)> Pixels);
internal sealed record ReconstructionSegment(ReconstructionSegmentKind Kind, IReadOnlyList<DirtyImageKey> Images,
    IReadOnlyList<EditableStrokeKey> Events, IReadOnlyList<TrustReason> Reasons);
internal sealed record LayerReconstructionPlan(LayerKey Layer, IReadOnlyList<LayerPacket> Groups, string SeedPacketId,
    IReadOnlySet<DirtyImageKey> Untrusted, IReadOnlyDictionary<DirtyImageKey, TrustReason> Reasons,
    IReadOnlyList<ReconstructionSegment> Segments);
internal sealed record ReconstructionPlan(EditIndex Index, EditRequest Request, IReadOnlyList<LayerReconstructionPlan> Layers,
    IReadOnlyList<PlanIssue> Issues)
{
    internal bool IsValid => Issues.Count == 0;
}

/// <summary>Pure dependency analysis. Capture feedback belongs exclusively to the online engine.</summary>
internal static class ReconstructionPlanner
{
    internal static ReconstructionPlan Build(EditIndex index, EditRequest request)
    {
        // Layer-local capture/chain failures must not disable an unrelated selection.
        // Retain genuinely global evidence; affected-layer issues are added after closure.
        LayerKey? IssueLayer(PlanIssue issue) => issue.Image?.Layer ??
            (issue.Stroke is { } key && index.Strokes.TryGetValue(key, out var stroke) ? stroke.Layer : null);
        var globalIssues = index.Issues.Where(issue => !index.Layers.Values.Any(layer => layer.Issues.Contains(issue))).ToArray();
        var issues = new List<PlanIssue>(globalIssues.Where(issue => IssueLayer(issue) is null));
        ReconstructionPlan Invalid() => new(index, request, [], issues.Distinct().ToArray());
        if (request.Kind == EditKind.PreciseUndo && (request.CurrentLayer is not { } current ||
            !index.Layers.ContainsKey(current) || string.IsNullOrWhiteSpace(current.Uuid)))
        { issues.Add(new(PlanIssueCode.LayerIdentityUnconfirmed, "当前图层无法唯一对应保存后的 UUID。")); return Invalid(); }
        if (request.Selection.IsEmpty || request.Changes.Count == 0)
        { issues.Add(new(PlanIssueCode.NoSelection, "没有选中可修改的笔接触。")); return Invalid(); }
        foreach (var (key, change) in request.Changes)
        {
            if (!index.Strokes.TryGetValue(key, out var stroke) || !stroke.Eligible || stroke.IsUndone ||
                !index.Layers.ContainsKey(stroke.Layer) || stroke.Images.Count == 0 ||
                request.Kind == EditKind.PreciseUndo && stroke.Layer != request.CurrentLayer)
            { issues.Add(new(PlanIssueCode.UnreplayableEventInMatrix, "选中事件不是目标图层的完整合格笔接触。", Stroke: key)); continue; }
            if (change.Delete) continue;
            if (new ReplayEvent(stroke, change).RequiresReferenceScene && index.Layers.Values.Any(l => l.UndoCheckpoints.Count > 0))
                issues.Add(new(PlanIssueCode.UndoRedoInRange,
                    "撤销后的有效检查点缺少逐步参考场景，无法将笔刷改为读取其他图层。", Stroke: key));
            var numericKeys = stroke.Numbers.Keys.ToHashSet(StringComparer.Ordinal);
            if (ChangesSubtool(stroke, change) && change.NumberValues.Count > 0)
            {
                var target = PacketReplay.BrushSnapshot.Parse(change.ReplacementBrushState);
                numericKeys = target is not null && PacketReplay.BrushSnapshot.SameName(target.Name, change.BrushName!) &&
                    target.Numbers.All(number => double.IsFinite(number.Value)) &&
                    target.Numbers.Select(number => number.Key).Distinct(StringComparer.Ordinal).Count() == target.Numbers.Count
                    ? target.Numbers.Select(number => number.Key).ToHashSet(StringComparer.Ordinal) : [];
            }
            if (change.BrushName is not null && string.IsNullOrWhiteSpace(change.BrushName) || change.NumberValues.Any(n =>
                !numericKeys.Contains(n.Key) || !double.IsFinite(n.Value) || n.Key == "brush_size" && n.Value <= 0))
                issues.Add(new(PlanIssueCode.UnsupportedOverride, "参数不属于已确认的目标子工具数值属性，或没有有效笔刷几何。", Stroke: key));
            if (!ChangesSubtool(stroke, change) && change.NumberValues.TryGetValue("brush_size", out double size) &&
                (!stroke.Numbers.TryGetValue("brush_size", out double oldSize) || size != oldSize) &&
                (stroke.CoverageLabels.Count == 0 || stroke.CoverageLabels.Any(l => l.CoverageSettings is null && stroke.FrozenCoverageSettings is null)))
                issues.Add(new(PlanIssueCode.UnsupportedOverride, "改笔刷尺寸需要录制时冻结的 CoverageSettings。", Stroke: key));
        }
        if (issues.Count > 0) return Invalid();
        var ordered = index.Layers.Values.SelectMany(l => l.Packets.Where(p => !p.IsRecordingBaseline))
            .OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
        var forced = new HashSet<DirtyImageKey>();
        var reasons = new Dictionary<DirtyImageKey, TrustReason>();
        bool changed;
        do
        {
            changed = false;
            var rest = index.Layers.ToDictionary(kv => kv.Key, kv => Empty(kv.Value));
            var grown = new HashSet<EditableStrokeKey>();
            foreach (var group in ordered)
            {
                var deferred = new List<DirtyImage>();
                foreach (var image in group.Images)
                {
                    TrustReason? reason = forced.Contains(image.Key) ? reasons.GetValueOrDefault(image.Key) ?? new("sharedContact", [])
                        : Assess(index, request, image, rest);
                    if (reason is null) { deferred.Add(image); continue; }
                    if (forced.Add(image.Key)) changed = true;
                    reasons.TryAdd(image.Key, reason);
                    Grow(index, request, image, rest[group.Layer], grown);
                    foreach (var key in image.Events)
                        if (index.Strokes.TryGetValue(key, out var stroke))
                            foreach (var linked in stroke.Images)
                                if (forced.Add(linked)) { changed = true; reasons[linked] = new("sharedContact", []); }
                }
                // Images of one capture group are disjoint; apply trusted masks after its replay growth.
                foreach (var image in deferred) rest[group.Layer].ExceptInPlace(CanvasMask(image.Mask, rest[group.Layer]));
            }
        } while (changed);
        var plans = new List<LayerReconstructionPlan>();
        var firstAffected = ordered.FirstOrDefault(p => p.Images.Any(i => forced.Contains(i.Key)));
        foreach (var layer in index.Layers.Values)
        {
            var groups = layer.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
            int first = Array.FindIndex(groups, p => p.Images.Any(i => forced.Contains(i.Key)));
            // A reference-layer reader can become affected only after exact replay feedback.
            // Keep its later timeline available even when the static estimate does not intersect it.
            bool prospective = first < 0;
            if (prospective && firstAffected is { } affected && layer.Strokes.Any(s => s.Classification?.ReadsReferenceLayers == true))
                first = Array.FindIndex(groups, p => (p.SessionOrder,p.TriggerTicks).CompareTo((affected.SessionOrder,affected.TriggerTicks)) >= 0);
            if (first < 0) continue;
            if (!prospective) issues.AddRange(layer.Issues);
            if (!prospective && !layer.ChainValid && layer.Issues.Count == 0) issues.Add(new(PlanIssueCode.ChainBroken, "受影响图层的差异链无效。"));
            if (!prospective)
            {
                // Closure can move the reconstruction start backwards, so compute
                // the command range only after the affected image set is frozen.
                var affectedEvents = groups.SelectMany(group => group.Images).Where(image => forced.Contains(image.Key))
                    .SelectMany(image => image.Events);
                if (UndoRedoIssue(index, layer, affectedEvents) is { } undoIssue) issues.Add(undoIssue);
            }
            if (!prospective && layer.Current is null) issues.Add(new(PlanIssueCode.CaptureMismatch, "编辑边界没有整层捕获。"));
            foreach (var image in groups.Skip(first).SelectMany(g => g.Images))
            {
                if (!image.FullResolution) issues.Add(new(PlanIssueCode.ChainLossy, "差异图片不是全分辨率。", image.Key));
                if (forced.Contains(image.Key) && (image.HasUnreplayableEvents || image.Events.Any(k => !index.Strokes.TryGetValue(k, out var s) || !s.Eligible)))
                    issues.Add(new(PlanIssueCode.UnreplayableEventInMatrix, image.UnknownHistoryInterval
                        ? $"修改碰到了两次录制之间未被记录的变化（画布区域 {image.Mask.Left},{image.Mask.Top} 起 {image.Mask.Width}×{image.Mask.Height}），该处无法重建。"
                        : "必须重放的矩阵含有无法重放的修改。", image.Key));
            }
            var relevant = groups.Skip(first).ToArray();
            var untrusted = relevant.SelectMany(g => g.Images).Where(i => forced.Contains(i.Key)).Select(i => i.Key).ToHashSet();
            plans.Add(new(layer.Layer, relevant, groups[first].PacketId, untrusted,
                reasons.Where(kv => kv.Key.Layer == layer.Layer).ToDictionary(), Segments(index, request, relevant, untrusted, reasons)));
        }
        if (plans.Count == 0) issues.Add(new(PlanIssueCode.NoSelection, "选中事件没有受影响的差异矩阵。"));
        var affectedLayers = plans.Where(plan => plan.Untrusted.Count > 0).Select(plan => plan.Layer).ToHashSet();
        issues.AddRange(globalIssues.Where(issue => IssueLayer(issue) is { } layer && affectedLayers.Contains(layer)));
        return new(index, request, plans, issues.Distinct().ToArray());
    }

    internal static TrustReason? Assess(EditIndex index, EditRequest request, DirtyImage image,
        IReadOnlyDictionary<LayerKey, PixelMask> differences)
    {
        if (image.Events.Any(request.Changes.ContainsKey)) return new("modifiedEvent", []);
        // Unknown operations may read unchanged pixels or any other layer. Their endpoint
        // difference mask cannot bound that read footprint. Later edits reach this point
        // with no earlier difference, and trusted overwrites can clear an earlier one.
        if (image.UnknownHistoryInterval)
        {
            // Pixels changed outside any recording are composed like an ordinary dirty image:
            // they only become untrustworthy where an earlier change reaches them.
            var gapLayer = differences[image.Key.Layer];
            var gapHit = CanvasMask(image.Impact, gapLayer).Intersect(gapLayer);
            return gapHit.IsEmpty ? null : new("unrecordedInterval", gapHit.Pixels().Take(8).ToArray());
        }
        var own = differences[image.Key.Layer];
        PixelMask hit;
        if (image.IsCausal)
        {
            hit = CanvasMask(image.Impact, own).Intersect(own);
            bool references = image.Events.Any(k => index.Strokes.TryGetValue(k, out var s) && s.Classification?.ReadsReferenceLayers == true);
            if (references)
                foreach (var (layer, mask) in differences)
                    if (layer != image.Key.Layer)
                        hit.UnionInPlace(CanvasMask(image.Impact, own).Intersect(CanvasMask(mask, own)));
            if (!hit.IsEmpty) return new("causalRead", hit.Pixels().Take(8).ToArray());
        }
        else
        {
            hit = CanvasMask(image.Probe, own).Intersect(own).Except(CanvasMask(image.Mask, own));
            if (!hit.IsEmpty) return new("sameColorProbe", hit.Pixels().Take(8).ToArray());
        }
        return null;
    }

    internal static void Grow(EditIndex index, EditRequest request, DirtyImage image, PixelMask destination,
        ISet<EditableStrokeKey> grown)
    {
        foreach (var key in image.Events)
        {
            if (!grown.Add(key) || !index.Strokes.TryGetValue(key, out var stroke)) continue;
            if (request.Changes.TryGetValue(key, out var change))
            {
                var masks = new PixelMask(destination.Width, destination.Height);
                foreach (var linked in stroke.Images)
                    if (index.Images.TryGetValue(linked, out var source)) masks.UnionInPlace(CanvasMask(source.Mask, destination));
                destination.UnionInPlace(CanvasMask(stroke.WriteMask, destination).Intersect(masks));
                if (!change.Delete) destination.UnionInPlace(OverrideWriteMask(stroke, change, destination));
            }
            else if (stroke.IsCausal) destination.UnionInPlace(CanvasMask(stroke.WriteMask, destination));
        }
    }

    private static PixelMask OverrideWriteMask(EditableStroke stroke, EventChange change, PixelMask bounds)
    {
        // A different subtool can spray, scatter or read beyond the original recipe.
        // Its route and readback are checked by the restorer; planning assumes no
        // smaller geometric footprint until the replacement has verified metadata.
        if (ChangesSubtool(stroke, change))
            return PixelMask.FromPredicate(bounds.Left, bounds.Top, bounds.Width, bounds.Height, static (_, _) => true);
        if (!change.NumberValues.TryGetValue("brush_size", out var size) || stroke.Numbers.TryGetValue("brush_size", out var old) && size == old)
            return CanvasMask(stroke.WriteMask, bounds);
        var labels = stroke.CoverageLabels.Select(l => l with { Radius =
            ((l.CoverageSettings ?? stroke.FrozenCoverageSettings ?? throw new InvalidDataException("缺少冻结的覆盖设置。")) with { BrushSize = size }).Radius });
        return Footprints.RasterizeCoverage(labels, bounds.Width, bounds.Height);
    }

    private static bool ChangesSubtool(EditableStroke stroke, EventChange change)
        => change.BrushName is { } name && !PacketReplay.BrushSnapshot.SameName(name, stroke.BrushName ?? "");

    internal static PlanIssue? UndoRedoIssue(EditIndex index, LayerIndex layer, IEnumerable<EditableStrokeKey> affectedEvents)
    {
        var earliest = affectedEvents.Where(index.Strokes.ContainsKey).Select(key => index.Strokes[key])
            .OrderBy(s=>s.SessionOrder).ThenBy(s=>s.Key.BeginAppendId).FirstOrDefault();
        var command = layer.UndoRedo.FirstOrDefault(command => command.InputAppendId == 0 || earliest is not null &&
            (command.SessionOrder,command.InputAppendId).CompareTo((earliest.SessionOrder,earliest.Key.BeginAppendId)) >= 0);
        return command is null ? null : new(PlanIssueCode.UndoRedoInRange,
            $"图层 {layer.Layer.Uuid} 从最早受影响接触到编辑边界之间存在 {command.Command}，无法确认历史笔画是否仍生效。");
    }

    internal static PixelMask Empty(LayerIndex layer)
    {
        var current = layer.Current ?? layer.Packets.LastOrDefault()?.Current;
        return current is not null ? new(current.Width, current.Height) : new(layer.Packets[0].CanvasWidth, layer.Packets[0].CanvasHeight);
    }
    internal static PixelMask CanvasMask(PixelMask source, PixelMask bounds)
    {
        if (source.Left == bounds.Left && source.Top == bounds.Top && source.Width == bounds.Width && source.Height == bounds.Height) return source;
        var result = new PixelMask(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        foreach (var (x,y) in source.Pixels())
            if (x >= bounds.Left && y >= bounds.Top && x < bounds.Left + bounds.Width && y < bounds.Top + bounds.Height) result.Set(x,y);
            else throw new InvalidDataException("像素范围超过目标画布。 ");
        return result;
    }
    private static IReadOnlyList<ReconstructionSegment> Segments(EditIndex index, EditRequest request,
        IEnumerable<LayerPacket> groups, IReadOnlySet<DirtyImageKey> forced, IReadOnlyDictionary<DirtyImageKey, TrustReason> reasons)
    {
        var segments = new List<ReconstructionSegment>(); var replayed = new HashSet<EditableStrokeKey>();
        var pending = new List<DirtyImage>();
        void Flush()
        {
            if (pending.Count == 0) return;
            var events = pending.SelectMany(i=>i.Events).Distinct().Where(k=>!index.Strokes[k].IsUndone)
                .Where(k=>!request.Changes.TryGetValue(k,out var c)||!c.Delete)
                .Where(replayed.Add).OrderBy(k=>index.Strokes[k].SessionOrder).ThenBy(k=>index.Strokes[k].BeginEventId).ToArray();
            segments.Add(new(ReconstructionSegmentKind.Replay,pending.Select(i=>i.Key).ToArray(),events,pending.Select(i=>reasons[i.Key]).ToArray()));
            pending.Clear();
        }
        foreach (var group in groups)
        {
            pending.AddRange(group.Images.Where(i=>forced.Contains(i.Key)));
            var trusted = group.Images.Where(i=>!forced.Contains(i.Key)).ToArray();
            if (trusted.Length == 0) continue;
            Flush(); segments.Add(new(ReconstructionSegmentKind.Compose,trusted.Select(i=>i.Key).ToArray(),[],[]));
        }
        Flush(); return segments;
    }
}
