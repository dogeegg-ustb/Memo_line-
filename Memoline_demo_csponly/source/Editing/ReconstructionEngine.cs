namespace MemolineDemo.Editing;

internal sealed record ReplayEvent(EditableStroke Stroke, EventChange? Change);
internal interface ICspReconstructionExecutor
{
    // Returned RGBA is the actual captured result. The executor must not synthesize it from the request.
    Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
        IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token);
    // The transaction verifies the paste before any discrete replay. PreciseUndo returns
    // the capture after discrete replay, which intentionally differs from the deletion seed.
    Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token);
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
        internal bool Started, Touched;
        internal long LastGroupTicks;
    }
    internal static async Task<ReconstructionResult> RunAsync(ReconstructionPlan plan, ICspReconstructionExecutor executor,
        CancellationToken token)
    {
        var layers = new Dictionary<LayerKey, ImageRgba>(); var promoted = new HashSet<DirtyImageKey>();
        bool hasReplayed = false;
        ReconstructionResult Fail(params PlanIssue[] issues) => new(ReconstructionStatus.Failed,layers,issues,promoted);
        if (!plan.IsValid) return Fail(plan.Issues.ToArray());
        var cursors = new Dictionary<LayerKey, Cursor>();
        try
        {
            token.ThrowIfCancellationRequested();
            foreach (var lp in plan.Layers)
            {
                var layer = plan.Index.Layers[lp.Layer];
                var seed = new LayerTimeline(layer.Packets, layer.Current!).BeforePacket(lp.SeedPacketId);
                var cursor = new Cursor(lp, seed);
                // Seed pixels already contain every earlier composed image. A later runtime
                // dependency may not replay a contact whose earlier output is baked into it.
                foreach (var packet in layer.Packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.TriggerTicks))
                {
                    if (packet.PacketId == lp.SeedPacketId) break;
                    foreach (var key in packet.Images.SelectMany(i => i.Events)) cursor.Composed.Add(key);
                }
                cursors.Add(lp.Layer,cursor);
            }
            IReadOnlyDictionary<LayerKey, PixelMask> Differences() => plan.Index.Layers.ToDictionary(kv=>kv.Key,
                kv=>cursors.TryGetValue(kv.Key,out var c)&&c.Started?c.Difference.Union(c.PendingGrowth):ReconstructionPlanner.Empty(kv.Value));
            ImageRgba ReferenceAt(LayerKey key,long ticks)
            {
                if(cursors.TryGetValue(key,out var c)&&c.Started)
                {
                    if(c.LastGroupTicks>ticks)throw new ReconstructionFailure(new(PlanIssueCode.RuntimeClosureViolation,"参考层游标已经超过重放时间，不能使用未来像素。"));
                    return c.Actual;
                }
                var source=plan.Index.Layers[key];
                var next=source.Packets.Where(p=>!p.IsRecordingBaseline&&p.TriggerTicks>ticks).OrderBy(p=>p.TriggerTicks).FirstOrDefault();
                return next is null?source.Current!:new LayerTimeline(source.Packets,source.Current!).BeforePacket(next.PacketId);
            }
            async Task Flush(Cursor c)
            {
                token.ThrowIfCancellationRequested();
                if(c.Pending.Count>0)
                {
                    var events=c.Pending.Values.OrderBy(e=>e.Stroke.BeginEventId).ThenBy(e=>e.Stroke.Key.BeginAppendId)
                        .Select(e=>e with {Stroke=e.Stroke with {Samples=e.Stroke.Samples.OrderBy(s=>s.AppendId).ToArray()}}).ToArray();
                    var references=events.Any(e=>e.Stroke.Classification?.ReadsReferenceLayers==true)
                        ? plan.Index.Layers.ToDictionary(kv=>kv.Key,kv=>kv.Key==c.Plan.Layer?c.Actual:ReferenceAt(kv.Key,c.LastGroupTicks))
                        : new Dictionary<LayerKey,ImageRgba>{{c.Plan.Layer,c.Actual}};
                    var actual=await executor.FlushAsync(c.Plan.Layer,c.Actual.Copy(),events,references,token);
                    hasReplayed = true;
                    actual.Validate();
                    if(actual.Width!=c.Actual.Width||actual.Height!=c.Actual.Height) throw new ReconstructionFailure(new(PlanIssueCode.CaptureMismatch,"重放捕获的尺寸与图层不符。"));
                    c.Actual=actual; c.Pending.Clear();
                }
                c.Difference=ImageCompositor.Difference(c.Actual,c.Historical);
                c.PendingGrowth=new(c.Actual.Width,c.Actual.Height);
            }
            PlanIssue? Queue(Cursor c,DirtyImage image,bool runtime)
            {
                var layerIndex=plan.Index.Layers[c.Plan.Layer];
                if(!layerIndex.ChainValid)return layerIndex.Issues.FirstOrDefault()??new(PlanIssueCode.ChainBroken,"在线新增受影响图层的差异链无效。",image.Key);
                if(image.HasUnreplayableEvents||image.Events.Any(k=>!plan.Index.Strokes.TryGetValue(k,out var s)||!s.Eligible))
                    return new(PlanIssueCode.UnreplayableEventInMatrix,"在线判定必须重放的矩阵含有不合格修改。",image.Key);
                if(runtime&&image.Events.Any(c.Composed.Contains))
                    return new(PlanIssueCode.RuntimeClosureViolation,"实际捕获新增依赖指向已合成的共享接触，已停止。",image.Key);
                if(runtime)
                    foreach(var key in image.Events)
                        foreach(var linked in plan.Index.Strokes[key].Images)c.RuntimeUntrusted.Add(linked);
                c.Touched=true;
                foreach(var key in image.Events)
                {
                    if(!c.Added.Add(key))continue;
                    plan.Request.Changes.TryGetValue(key,out var change);
                    if(change?.Delete!=true)c.Pending.Add(key,new(plan.Index.Strokes[key],change));
                }
                ReconstructionPlanner.Grow(plan.Index,plan.Request,image,c.PendingGrowth,c.Grown);
                ImageCompositor.Replace(c.Historical,image);
                if(runtime)promoted.Add(image.Key);
                return null;
            }
            var groups=plan.Layers.SelectMany(lp=>lp.Groups.Select(g=>(Layer:lp.Layer,Group:g))).OrderBy(g=>g.Group.TriggerTicks).ToArray();
            foreach(var (layer,group) in groups)
            {
                token.ThrowIfCancellationRequested(); var c=cursors[layer];
                c.Started=true;c.LastGroupTicks=group.TriggerTicks;
                // Reference-layer reads need actual pixels from earlier pending replay in those layers.
                if(group.Images.Any(i=>i.Events.Any(k=>plan.Index.Strokes.TryGetValue(k,out var s)&&s.Classification?.ReadsReferenceLayers==true)))
                    foreach(var other in cursors.Values.Where(other=>other!=c&&other.Pending.Count>0))await Flush(other);
                var trusted=new List<DirtyImage>();
                foreach(var image in group.Images)
                {
                    bool isStatic=c.Plan.Untrusted.Contains(image.Key);
                    bool runtime=!isStatic&&(c.RuntimeUntrusted.Contains(image.Key)||ReconstructionPlanner.Assess(plan.Index,plan.Request,image,Differences()) is not null);
                    if(!isStatic&&!runtime){trusted.Add(image);continue;}
                    var issue=Queue(c,image,runtime); if(issue is not null)return Fail(issue);
                }
                if(trusted.Count==0)
                {
                    // A reference reader may not remain pending across a later layer change.
                    if(c.Pending.Values.Any(e=>e.Stroke.Classification?.ReadsReferenceLayers==true))await Flush(c);
                    continue;
                }
                await Flush(c);
                // Exact feedback may invalidate another member of this same capture group.
                var deferred=new List<DirtyImage>();
                foreach(var image in trusted)
                {
                    if(c.RuntimeUntrusted.Contains(image.Key)||ReconstructionPlanner.Assess(plan.Index,plan.Request,image,Differences()) is not null)
                    {var issue=Queue(c,image,true);if(issue is not null)return Fail(issue);}
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
                        {var issue=Queue(c,image,true);if(issue is not null)return Fail(issue);deferred.Remove(image);reconsider=true;}
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
                var observed=await executor.InstallReproductionLayerAsync(c.Plan.Layer,c.Actual.Copy(),token);
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
    private sealed class ReconstructionFailure(PlanIssue issue):Exception(issue.Message)
    {internal PlanIssue Issue=issue;}
}
