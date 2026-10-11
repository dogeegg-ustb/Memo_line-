using System.Text.Json;

namespace MemolineDemo.Editing;

internal sealed record ReplayEvent(EditableStroke Stroke, EventChange? Change)
{
    internal JsonElement BrushEvidence => Change?.BrushName is not null&&Change.ReplacementBrushState.ValueKind==System.Text.Json.JsonValueKind.Object
        ? Change.ReplacementBrushState : Stroke.BrushState;
    internal bool RequiresReferenceScene => ReadsReferenceLayers&&ReferenceReadEvidence.OffKeys(BrushEvidence,Change?.BrushName??Stroke.BrushName??"") is null;
    internal bool ReadsReferenceLayers => Stroke.Classification?.ReadsReferenceLayers==true ||
        Change?.BrushName is { } name &&
        (Change.ReplacementBrushState.ValueKind==System.Text.Json.JsonValueKind.Undefined
            ? !PacketReplay.BrushSnapshot.SameName(name,Stroke.BrushName??"")
            : new BrushClassifier().Classify(Change.ReplacementBrushState).ReadsReferenceLayers);
}
internal interface ICspReconstructionExecutor
{
    // Returned RGBA is the actual captured result. The executor must not synthesize it from the request.
    Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
        IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token);
    // The transaction verifies the paste before any discrete replay. PreciseUndo returns
    // the capture after discrete replay, which intentionally differs from the deletion seed.
    Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token);
    // PreciseUndo never replays before installation: contacts that would need replay are handed
    // over here and replayed last, merged in original order with the undone strokes.
    Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected,
        IReadOnlyList<ReplayEvent> finalReplay, CancellationToken token)
        => finalReplay.Count == 0 ? InstallReproductionLayerAsync(layer, expected, token)
            : throw new NotSupportedException("This executor cannot replay deferred contacts.");
}
internal enum ReconstructionStatus { Executed, PixelVerified, Failed, Cancelled }
internal sealed record ReconstructionResult(ReconstructionStatus Status, IReadOnlyDictionary<LayerKey, ImageRgba> Layers,
    IReadOnlyList<PlanIssue> Issues, IReadOnlySet<DirtyImageKey> Promoted);

/// <summary>Advances actual and historical pixels together; replay capture alone determines exact differences.</summary>
internal static class ReconstructionEngine
{
    private sealed class Cursor(LayerReconstructionPlan plan, ImageRgba seed)
    {
        internal LayerReconstructionPlan Plan = plan;
        internal ImageRgba Actual = seed;
        internal ImageRgba Historical = seed.Copy();
        internal PixelMask Difference = new(seed.Width, seed.Height);
        internal PixelMask PendingGrowth = new(seed.Width, seed.Height);
        internal readonly Dictionary<EditableStrokeKey, ReplayEvent> Pending = [];
        internal readonly HashSet<EditableStrokeKey> Added = [];
        internal readonly HashSet<EditableStrokeKey> Grown = [];
        internal readonly HashSet<EditableStrokeKey> Composed = [];
        internal readonly HashSet<DirtyImageKey> RuntimeUntrusted = [];
        // Recorded pixels of replayed images that no replayable contact explains (mouse input,
        // unrecorded changes, labels without contacts). They are pasted after the replay.
        internal readonly List<(DirtyImage Image, PixelMask Pixels)> Residuals = [];
        // PreciseUndo: contacts collected for the single final replay.
        internal readonly List<ReplayEvent> Deferred = [];
        internal bool Started, Touched;
        internal long LastGroupTicks;
        internal int LastGroupSessionOrder;
    }
    internal static async Task<ReconstructionResult> RunAsync(ReconstructionPlan plan, ICspReconstructionExecutor executor,
        CancellationToken token)
    {
        var layers = new Dictionary<LayerKey, ImageRgba>(); var promoted = new HashSet<DirtyImageKey>();
        bool hasReplayed = false;
        ReconstructionResult Fail(params PlanIssue[] issues) => new(ReconstructionStatus.Failed,layers,issues,promoted);
        if (!plan.IsValid) return Fail(plan.Issues.ToArray());
        if (plan.Request.Kind == EditKind.PreciseUndo) return await RunPreciseUndoAsync(plan, executor, token);
        var cursors = new Dictionary<LayerKey, Cursor>();
        var validatedUndo = new HashSet<(LayerKey Layer, string Packet)>();
        try
        {
            token.ThrowIfCancellationRequested();
            foreach (var lp in plan.Layers)
            {
                var layer = plan.Index.Layers[lp.Layer];
                if(lp.Untrusted.Count==0 && (!layer.ChainValid || layer.Current is null))continue;
                var seed = new LayerTimeline(layer.Packets, layer.Current!).BeforePacket(lp.SeedPacketId);
                var cursor = new Cursor(lp, seed);
                // Seed pixels already contain every earlier composed image. A later runtime
                // dependency may not replay a contact whose earlier output is baked into it.
                foreach (var packet in layer.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks))
                {
                    if (packet.PacketId == lp.SeedPacketId) break;
                    foreach (var key in packet.Images.SelectMany(i => i.Events)) cursor.Composed.Add(key);
                }
                cursors.Add(lp.Layer,cursor);
            }
            IReadOnlyDictionary<LayerKey, PixelMask> Differences() => plan.Index.Layers.ToDictionary(kv=>kv.Key,
                kv=>cursors.TryGetValue(kv.Key,out var c)&&c.Started?c.Difference.Union(c.PendingGrowth):ReconstructionPlanner.Empty(kv.Value));
            ImageRgba ReferenceAt(LayerKey key,int sessionOrder,long ticks)
            {
                if(cursors.TryGetValue(key,out var c)&&c.Started)
                {
                    if((c.LastGroupSessionOrder,c.LastGroupTicks).CompareTo((sessionOrder,ticks))>0)throw new ReconstructionFailure(new(PlanIssueCode.RuntimeClosureViolation,"参考层游标已经超过重放时间，不能使用未来像素。"));
                    return c.Actual;
                }
                var source=plan.Index.Layers[key];
                if(!source.ChainValid||source.Current is null)
                    throw new ReconstructionFailure(source.Issues.FirstOrDefault()??
                        new(PlanIssueCode.CaptureMismatch,"实际参考场景需要缺少可信边界捕获的图层："+key.Uuid));
                var next=source.Packets.Where(p=>!p.IsRecordingBaseline&&(p.SessionOrder,p.TriggerTicks).CompareTo((sessionOrder,ticks))>0)
                    .OrderBy(p=>p.SessionOrder).ThenBy(p=>p.TriggerTicks).FirstOrDefault();
                return next is null?source.Current!:new LayerTimeline(source.Packets,source.Current!).BeforePacket(next.PacketId);
            }
            async Task Flush(Cursor c)
            {
                token.ThrowIfCancellationRequested();
                if(c.Pending.Count>0&&plan.Request.Kind==EditKind.PreciseUndo)
                {
                    // Event replay is the last step of a precise undo: defer, never flush into a work layer.
                    c.Deferred.AddRange(c.Pending.Values); c.Pending.Clear();
                }
                if(c.Pending.Count>0)
                {
                    var events=c.Pending.Values.OrderBy(e=>e.Stroke.SessionOrder).ThenBy(e=>e.Stroke.BeginEventId).ThenBy(e=>e.Stroke.Key.BeginAppendId)
                        .Select(e=>e with {Stroke=e.Stroke with {Samples=e.Stroke.Samples.OrderBy(s=>s.AppendId).ToArray()}}).ToArray();
                    var references=events.Any(e=>e.RequiresReferenceScene)
                        ? plan.Index.Layers.ToDictionary(kv=>kv.Key,kv=>kv.Key==c.Plan.Layer?c.Actual:ReferenceAt(kv.Key,c.LastGroupSessionOrder,c.LastGroupTicks))
                        : new Dictionary<LayerKey,ImageRgba>{{c.Plan.Layer,c.Actual}};
                    var actual=await executor.FlushAsync(c.Plan.Layer,c.Actual.Copy(),events,references,token);
                    hasReplayed = true;
                    actual.Validate();
                    if(actual.Width!=c.Actual.Width||actual.Height!=c.Actual.Height) throw new ReconstructionFailure(new(PlanIssueCode.CaptureMismatch,"重放捕获的尺寸与图层不符。"));
                    c.Actual=actual; c.Pending.Clear();
                }
                foreach(var (image,pixels) in c.Residuals)
                    foreach(var (x,y) in pixels.Pixels())
                    {
                        int source=((y-image.Mask.Top)*image.Mask.Width+x-image.Mask.Left)*4;
                        image.Now.Bytes.AsSpan(source,4).CopyTo(c.Actual.Bytes.AsSpan((y*c.Actual.Width+x)*4,4));
                    }
                c.Residuals.Clear();
                c.Difference=ImageCompositor.Difference(c.Actual,c.Historical);
                c.PendingGrowth=new(c.Actual.Width,c.Actual.Height);
            }
            async Task<PlanIssue?> Queue(Cursor c,DirtyImage image,bool runtime)
            {
                var layerIndex=plan.Index.Layers[c.Plan.Layer];
                if(ReconstructionPlanner.UndoRedoIssue(plan.Index,layerIndex,image.Events.Concat(c.Added)) is { } undoIssue)return undoIssue;
                if(!layerIndex.ChainValid)return layerIndex.Issues.FirstOrDefault()??new(PlanIssueCode.ChainBroken,"在线新增受影响图层的差异链无效。",image.Key);
                var replayable=image.Events.Where(k=>plan.Index.Strokes.TryGetValue(k,out var s)&&s.Eligible).ToArray();
                if(image.HasUnreplayableEvents||replayable.Length!=image.Events.Count)
                {
                    // Replay what can be replayed; keep the rest of this image as recorded.
                    var writes=replayable.Select(k=>plan.Index.Strokes[k].WriteMask).ToArray();
                    var residual=new PixelMask(image.Mask.Left,image.Mask.Top,image.Mask.Width,image.Mask.Height);
                    foreach(var (x,y) in image.Mask.Pixels())
                        if(!writes.Any(w=>w.Contains(x,y)))residual.Set(x,y);
                    if(!residual.IsEmpty)c.Residuals.Add((image,residual));
                }
                if(runtime&&image.Events.Any(c.Composed.Contains))
                    return new(PlanIssueCode.RuntimeClosureViolation,"实际捕获新增依赖指向已合成的共享接触，已停止。",image.Key);
                foreach (var checkpoint in layerIndex.UndoCheckpoints.Where(p => p.PacketId == image.Key.PacketId))
                {
                    if (!validatedUndo.Add((c.Plan.Layer, checkpoint.PacketId))) continue;
                    var originalEvents = checkpoint.Events.Select(key => new ReplayEvent(plan.Index.Strokes[key], null)).ToArray();
                    if (originalEvents.Any(e => e.RequiresReferenceScene))
                        return new(PlanIssueCode.UndoRedoInRange, "撤销后的有效历史包含尚未确认的跨图层参考，无法验证。", image.Key);
                    var observed = originalEvents.Length == 0 ? checkpoint.Seed.Copy() :
                        await executor.FlushAsync(c.Plan.Layer, checkpoint.Seed.Copy(), originalEvents,
                            new Dictionary<LayerKey, ImageRgba> { [c.Plan.Layer] = checkpoint.Seed.Copy() }, token);
                    observed.Validate();
                    if (observed.Width != checkpoint.Expected.Width || observed.Height != checkpoint.Expected.Height ||
                        !observed.Bytes.AsSpan().SequenceEqual(checkpoint.Expected.Bytes))
                        return new(PlanIssueCode.CaptureMismatch,
                            "撤销历史原样重放与保存的整层像素不一致，无法确认撤销目标，已停止编辑。", image.Key);
                    hasReplayed |= originalEvents.Length > 0;
                }
                if(runtime)
                    foreach(var key in image.Events)
                        foreach(var linked in plan.Index.Strokes[key].Images)c.RuntimeUntrusted.Add(linked);
                c.Touched=true;
                foreach(var key in replayable)
                {
                    if(!c.Added.Add(key))continue;
                    plan.Request.Changes.TryGetValue(key,out var change);
                    // Undone contacts left no ink in the effective history: skip them like deletions.
                    if(change?.Delete!=true&&!plan.Index.Strokes[key].IsUndone)c.Pending.Add(key,new(plan.Index.Strokes[key],change));
                }
                ReconstructionPlanner.Grow(plan.Index,plan.Request,image,c.PendingGrowth,c.Grown);
                ImageCompositor.Replace(c.Historical,image);
                if(runtime)promoted.Add(image.Key);
                return null;
            }
            var groups=plan.Layers.SelectMany(lp=>lp.Groups.Select(g=>(Layer:lp.Layer,Group:g)))
                .OrderBy(g=>g.Group.SessionOrder).ThenBy(g=>g.Group.TriggerTicks).ToArray();
            foreach(var (layer,group) in groups)
            {
                token.ThrowIfCancellationRequested();
                if(!cursors.TryGetValue(layer,out var c))
                {
                    foreach(var pending in cursors.Values.Where(cursor=>cursor.Pending.Count>0))await Flush(pending);
                    if(group.Images.Any(image=>ReconstructionPlanner.Assess(plan.Index,plan.Request,image,Differences()) is not null))
                        return Fail(plan.Index.Layers[layer].Issues.FirstOrDefault()??
                            new(PlanIssueCode.CaptureMismatch,"实际变化影响到缺少可信边界捕获的图层："+layer.Uuid));
                    continue;
                }
                c.Started=true;c.LastGroupTicks=group.TriggerTicks;c.LastGroupSessionOrder=group.SessionOrder;
                // Reference-layer reads need actual pixels from earlier pending replay in those layers.
                if(group.Images.Any(i=>i.Events.Any(k=>plan.Index.Strokes.TryGetValue(k,out var s)&&new ReplayEvent(s,plan.Request.Changes.GetValueOrDefault(k)).ReadsReferenceLayers)))
                    foreach(var other in cursors.Values.Where(other=>other!=c&&other.Pending.Count>0))await Flush(other);
                var trusted=new List<DirtyImage>();
                foreach(var image in group.Images)
                {
                    bool isStatic=c.Plan.Untrusted.Contains(image.Key);
                    bool runtime=!isStatic&&(c.RuntimeUntrusted.Contains(image.Key)||ReconstructionPlanner.Assess(plan.Index,plan.Request,image,Differences()) is not null);
                    if(!isStatic&&!runtime){trusted.Add(image);continue;}
                    var issue=await Queue(c,image,runtime); if(issue is not null)return Fail(issue);
                }
                if(trusted.Count==0)
                {
                    // A reference reader may not remain pending across a later layer change.
                    if(c.Pending.Values.Any(e=>e.ReadsReferenceLayers))await Flush(c);
                    continue;
                }
                await Flush(c);
                // Exact feedback may invalidate another member of this same capture group.
                var deferred=new List<DirtyImage>();
                foreach(var image in trusted)
                {
                    if(c.RuntimeUntrusted.Contains(image.Key)||ReconstructionPlanner.Assess(plan.Index,plan.Request,image,Differences()) is not null)
                    {var issue=await Queue(c,image,true);if(issue is not null)return Fail(issue);}
                    else deferred.Add(image);
                }
                if(c.Pending.Count>0)await Flush(c);
                // A second capture can itself reveal a new dependency. Promote monotonically until stable.
                bool reconsider;
                do
                {
                    reconsider=false;
                    foreach(var image in deferred.ToArray())
                        if(c.RuntimeUntrusted.Contains(image.Key)||ReconstructionPlanner.Assess(plan.Index,plan.Request,image,Differences()) is not null)
                        {var issue=await Queue(c,image,true);if(issue is not null)return Fail(issue);deferred.Remove(image);reconsider=true;}
                    if(reconsider)await Flush(c);
                }while(reconsider);
                foreach(var image in deferred)
                {
                    ImageCompositor.Replace(c.Actual,image);ImageCompositor.Replace(c.Historical,image);
                    c.Difference.ExceptInPlace(ReconstructionPlanner.CanvasMask(image.Mask,c.Difference));
                    foreach(var key in image.Events)c.Composed.Add(key);
                }
            }
            foreach(var c in cursors.Values)await Flush(c);
            foreach(var c in cursors.Values.Where(c=>c.Touched))
            {
                token.ThrowIfCancellationRequested();
                var deferred=c.Deferred.OrderBy(e=>e.Stroke.SessionOrder).ThenBy(e=>e.Stroke.BeginEventId).ThenBy(e=>e.Stroke.Key.BeginAppendId)
                    .Select(e=>e with {Stroke=e.Stroke with {Samples=e.Stroke.Samples.OrderBy(s=>s.AppendId).ToArray()}}).ToArray();
                var observed=deferred.Length==0
                    ? await executor.InstallReproductionLayerAsync(c.Plan.Layer,c.Actual.Copy(),token)
                    : await executor.InstallReproductionLayerAsync(c.Plan.Layer,c.Actual.Copy(),deferred,token);
                hasReplayed|=deferred.Length>0;
                if(observed.Width!=c.Actual.Width||observed.Height!=c.Actual.Height)
                    return Fail(new PlanIssue(PlanIssueCode.CaptureMismatch,"复现层读回尺寸与期望不一致。"));
                if(plan.Request.Kind!=EditKind.PreciseUndo&&!ImageCompositor.Difference(observed,c.Actual).IsEmpty)
                    return Fail(new PlanIssue(PlanIssueCode.PasteNotExact,"复现层读回与期望 RGBA 不一致。"));
                layers.Add(c.Plan.Layer,observed);
            }
            return new(hasReplayed||plan.Request.Kind==EditKind.PreciseUndo ? ReconstructionStatus.Executed : ReconstructionStatus.PixelVerified,layers,[],promoted);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested)
        {return new(ReconstructionStatus.Cancelled,layers,[],promoted);}
        catch(ReconstructionFailure failure){return Fail(failure.Issue);}
    }
    /// <summary>Which dirty images precise undo replays and which it composites, and from which packet.</summary>
    internal sealed record PreciseUndoLayout(string SeedPacketId, IReadOnlySet<DirtyImageKey> Replayed,
        IReadOnlyList<EditableStroke> Reverted, int Composited);

    /// <summary>
    /// Precise undo, in order: the full layer before the earliest packet holding a replayed image is the
    /// snapshot; every later dirty image outside the replayed range is composited onto it inside Memoline;
    /// the result is pasted into CSP; only then are events replayed, discretely: first the unselected
    /// contacts of the replayed images (the snapshot removed their ink), then the selected contacts last,
    /// so CSP's own undo steps back through exactly the selection.
    /// </summary>
    private static async Task<ReconstructionResult> RunPreciseUndoAsync(ReconstructionPlan plan,
        ICspReconstructionExecutor executor, CancellationToken token)
    {
        var layers = new Dictionary<LayerKey, ImageRgba>();
        ReconstructionResult Fail(PlanIssue issue) => new(ReconstructionStatus.Failed, layers, [issue], new HashSet<DirtyImageKey>());
        try
        {
            token.ThrowIfCancellationRequested();
            var index = plan.Index;
            if (plan.Request.CurrentLayer is not { } layerKey || !index.Layers.TryGetValue(layerKey, out var layer) || layer.Current is not { } current)
                return Fail(new(PlanIssueCode.LayerIdentityUnconfirmed, "精确撤销需要已确认的当前图层及其边界整层捕获。"));
            var selected = plan.Request.Changes.Keys.Where(index.Strokes.ContainsKey).Select(k => index.Strokes[k]).ToArray();
            if (selected.Length == 0) return Fail(new(PlanIssueCode.NoSelection, "没有可撤销的笔画。"));
            if (LayoutPreciseUndo(index, layer, selected) is not { } layout)
                return Fail(new(PlanIssueCode.NoSelection, "被选笔画没有对应的脏矩阵。"));
            var target = ComposePreciseUndo(index, layer, current, layout);
            var reverted = layout.Reverted.Select(s => new ReplayEvent(s with { Samples = s.Samples.OrderBy(p => p.AppendId).ToArray() }, null)).ToArray();
            var observed = reverted.Length == 0
                ? await executor.InstallReproductionLayerAsync(layerKey, target, token)
                : await executor.InstallReproductionLayerAsync(layerKey, target, reverted, token);
            if (observed.Width != current.Width || observed.Height != current.Height)
                return Fail(new(PlanIssueCode.CaptureMismatch, "复现层读回尺寸与期望不一致。"));
            layers.Add(layerKey, observed);
            return new(ReconstructionStatus.Executed, layers, [], new HashSet<DirtyImageKey>());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(ReconstructionStatus.Cancelled, layers, [], new HashSet<DirtyImageKey>()); }
    }

    /// <summary>
    /// The replayed range starts as the images of the selected contacts. A replayed image brings in all its
    /// contacts, and a replayed contact all its images. A later image that overlaps where a replayed contact
    /// wrote is replayed too: composited first and drawn over afterwards, its pixels would end up underneath.
    /// </summary>
    internal static PreciseUndoLayout? LayoutPreciseUndo(EditIndex index, LayerIndex layer, IReadOnlyList<EditableStroke> selected)
    {
        var packets = layer.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
        var packetOf = new Dictionary<DirtyImageKey, int>();
        var byKey = new Dictionary<DirtyImageKey, DirtyImage>();
        for (int p = 0; p < packets.Length; p++) foreach (var image in packets[p].Images) { packetOf[image.Key] = p; byKey[image.Key] = image; }
        static (int, ulong) Order(EditableStroke s) => (s.SessionOrder, s.BeginEventId);
        bool Replayable(EditableStrokeKey key) => index.Strokes.TryGetValue(key, out var s) && s.Eligible && !s.IsUndone && s.Layer == layer.Layer;
        var images = selected.SelectMany(s => s.Images).Where(packetOf.ContainsKey).ToHashSet();
        if (images.Count == 0) return null;
        var strokes = new Dictionary<EditableStrokeKey, EditableStroke>();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var key in images.ToArray().SelectMany(k => byKey[k].Events).Where(Replayable))
                if (strokes.TryAdd(key, index.Strokes[key]))
                    foreach (var image in strokes[key].Images.Where(packetOf.ContainsKey)) grew |= images.Add(image);
            foreach (var stroke in selected) strokes.TryAdd(stroke.Key, stroke);
            int start = images.Min(k => packetOf[k]);
            for (int p = start; p < packets.Length; p++)
                foreach (var image in packets[p].Images.Where(i => !images.Contains(i.Key)))
                {
                    bool covers = strokes.Values.Any(s =>
                    {
                        int at = s.Images.Where(packetOf.ContainsKey).Select(k => packetOf[k]).DefaultIfEmpty(-1).Max();
                        bool later = p > at || p == at && image.Events.Any(k => index.Strokes.TryGetValue(k, out var e) && Order(e).CompareTo(Order(s)) > 0);
                        return later && image.Mask.Intersects(s.WriteMask);
                    });
                    if (covers) grew |= images.Add(image.Key);
                }
        }
        var selectedKeys = selected.Select(s => s.Key).ToHashSet();
        int seed = images.Min(k => packetOf[k]);
        int composited = packets.Skip(seed).SelectMany(p => p.Images).Count(i => !images.Contains(i.Key));
        var reverted = strokes.Values.Where(s => !selectedKeys.Contains(s.Key))
            .OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
        return new(packets[seed].PacketId, images, reverted, composited);
    }

    /// <summary>The snapshot before the seed packet, then every later image in time order: outside the
    /// replayed range its recorded pixels; inside it only the pixels no replayed contact explains.</summary>
    internal static ImageRgba ComposePreciseUndo(EditIndex index, LayerIndex layer, ImageRgba current, PreciseUndoLayout layout)
    {
        var target = new LayerTimeline(layer.Packets, current).BeforePacket(layout.SeedPacketId);
        bool started = false;
        foreach (var packet in layer.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks))
        {
            started |= packet.PacketId == layout.SeedPacketId;
            if (!started) continue;
            foreach (var image in packet.Images)
            {
                if (!layout.Replayed.Contains(image.Key)) { ImageCompositor.Replace(target, image); continue; }
                // Mouse input, unrecorded changes and undone contacts cannot be replayed: keep their result.
                var writes = image.Events.Where(k => index.Strokes.TryGetValue(k, out var s) && s.Eligible && !s.IsUndone)
                    .Select(k => index.Strokes[k].WriteMask).ToArray();
                foreach (var (x, y) in image.Mask.Pixels())
                {
                    if (writes.Any(w => w.Contains(x, y))) continue;
                    int source = ((y - image.Mask.Top) * image.Mask.Width + x - image.Mask.Left) * 4;
                    image.Now.Bytes.AsSpan(source, 4).CopyTo(target.Bytes.AsSpan((y * target.Width + x) * 4, 4));
                }
            }
        }
        return target;
    }

    private sealed class ReconstructionFailure(PlanIssue issue):Exception(issue.Message)
    {internal PlanIssue Issue=issue;}
}
