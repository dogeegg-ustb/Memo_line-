using MemolineDemo.Editing;
using StrokeReplay;

namespace MemolineDemo;

internal static class ReconstructionChecks
{
    private const int Width = 8;
    private static PixelMask Mask(params int[] pixels)
    { var m = new PixelMask(Width, 1); foreach (int x in pixels) m.Set(x, 0); return m; }
    private static ImageRgba Pixels(params byte[] colors)
    {
        var bytes = new byte[Width * 4];
        for (int x = 0; x < Width; x++) { bytes[x * 4] = colors[x]; bytes[x * 4 + 3] = colors[x] == 0 ? (byte)0 : (byte)255; }
        return new(Width, 1, bytes);
    }
    private sealed class Scene
    {
        internal readonly LayerKey Layer = new("fixture", 1, "ink");
        internal readonly Dictionary<EditableStrokeKey, EditableStroke> Strokes = [];
        internal readonly List<LayerPacket> Packets = [];
        internal readonly Dictionary<EditableStrokeKey, byte> Colors = [];
        internal ImageRgba History = Pixels(0, 0, 0, 0, 0, 0, 0, 0);
        internal EditableStrokeKey Stroke(ulong id, byte color, PixelMask write, bool causal = false,
            PixelMask? probe = null, PixelMask? impact = null, bool reference = false, ulong? eventId = null)
        {
            var key = new EditableStrokeKey("fixture", id);
            Strokes.Add(key, new(key, id, (long)id, (long)id + 1,
                [new((long)id, .5, .5, 1, 0, 0, true, id, id), new((long)id+1, 7.5, .5, 0, 0, 0, false, id+1, id+1)],
                "G pen", Layer, causal, BrushNumbers: new Dictionary<string, double> { ["brush_size"] = 1 })
            { BeginEventId = eventId ?? id, WriteMask = write, ProbeMask = probe ?? write,
                ImpactMask = impact ?? (causal ? write : Mask()),
                Classification = new(true, causal, [], true, []) { ReadsReferenceLayers = reference } });
            Colors.Add(key, color); return key;
        }
        internal DirtyImage Image(string id, PixelMask mask, ImageRgba next, params EditableStrokeKey[] keys)
        {
            var w = Mask(); var p = Mask(); var impact = Mask();
            foreach (var key in keys) { w.UnionInPlace(Strokes[key].WriteMask); p.UnionInPlace(Strokes[key].ProbeMask); impact.UnionInPlace(Strokes[key].ImpactMask); }
            return new(new(Layer, "p" + Packets.Count, id), mask, History.Copy(), next.Copy(), keys,
                keys.Any(k => Strokes[k].IsCausal)) { W = w, Probe = p, Impact = impact };
        }
        internal void Group(params DirtyImage[] images)
        {
            var packet = new LayerPacket("p" + Packets.Count, Layer, Packets.Count * 10, null, null, images,
                AfterSnapshotId: "s" + Packets.Count, NowSnapshotId: "s" + (Packets.Count + 1),
                BaselineSource: Packets.Count == 0 ? "recordingStart" : null, CanvasWidth: Width, CanvasHeight: 1);
            foreach (var image in images) ImageCompositor.Replace(History, image);
            Packets.Add(packet);
        }
        internal EditIndex Index()
        {
            var images = Packets.SelectMany(p => p.Images).ToDictionary(i => i.Key);
            var strokes = Strokes.ToDictionary(kv => kv.Key, kv => kv.Value with
            { Images = images.Values.Where(i => i.Events.Contains(kv.Key)).Select(i => i.Key).ToArray() });
            var packets = Packets.ToArray(); packets[^1] = packets[^1] with { IsBoundary = true, Current = History.Copy() };
            return new(new Dictionary<LayerKey, LayerIndex> { [Layer] = new(Layer, packets, strokes.Values.ToArray(), true, [], Current: History.Copy()) }, strokes, images, []);
        }
        internal ReconstructionPlan Plan(params EditableStrokeKey[] deleted)
            => ReconstructionPlanner.Build(Index(), EditRequest.Event(Layer, Mask(0, 1, 2, 3, 4, 5, 6, 7),
                deleted.ToDictionary(k => k, _ => new EventChange(Delete: true))));
    }
    private sealed class Fake(Scene scene) : ICspReconstructionExecutor
    {
        internal readonly List<string> Calls = [];
        internal readonly List<EditableStrokeKey> Replayed = [];
        internal readonly Dictionary<LayerKey, ImageRgba> Installed = [];
        internal Func<ImageRgba, IReadOnlyList<ReplayEvent>, ImageRgba>? Paint;
        internal Action? BeforeFlush;
        internal Func<ImageRgba,ImageRgba>? Readback;
        public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BeforeFlush?.Invoke();
            Calls.Add("replay:" + string.Join(',', events.Select(e => e.Stroke.BeginEventId)));
            Replayed.AddRange(events.Select(e => e.Stroke.Key));
            if (Paint is not null) return Task.FromResult(Paint(seed, events));
            foreach (var e in events)
            {
                foreach (var (x, y) in e.Stroke.WriteMask.Pixels())
                { int offset = (y * Width + x) * 4; seed.Bytes[offset] = scene.Colors[e.Stroke.Key]; seed.Bytes[offset+3] = 255; }
            }
            return Task.FromResult(seed);
        }
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("install"); Installed[layer] = expected.Copy(); return Task.FromResult(Readback is null ? expected.Copy() : Readback(expected.Copy())); }
        internal readonly List<EditableStrokeKey> Deferred = [];
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected,
            IReadOnlyList<ReplayEvent> finalReplay, CancellationToken token)
        { Deferred.AddRange(finalReplay.Select(e => e.Stroke.Key)); return InstallReproductionLayerAsync(layer, expected, token); }
    }
    internal static async Task<int> RunAsync()
    {
        int n = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); n++; }
        void Bytes(ReconstructionResult r, LayerKey layer, params byte[] expected)
            => Check(r.Layers[layer].Bytes.AsSpan().SequenceEqual(Pixels(expected).Bytes), "RGBA mismatch: " + string.Join(',', expected));
        bool Untrusted(ReconstructionPlan p, DirtyImage image) => p.Layers.Single(l => l.Layer == image.Key.Layer).Untrusted.Contains(image.Key);
        async Task<ReconstructionResult> Run(Scene s, ReconstructionPlan p, Fake f)
        { Check(p.Issues.Count == 0, "Unexpected plan issue"); return await ReconstructionEngine.RunAsync(p, f, CancellationToken.None); }
        // 1, 2, 3: unrelated, different-color and invisible same-color overwrites.
        foreach (int mode in new[] { 1, 2, 3 })
        {
            var s = new Scene(); var a = s.Stroke(10, 20, Mask(0, 1));
            var first = s.Image("a", Mask(0, 1), Pixels(20, 20, 0, 0, 0, 0, 0, 0), a); s.Group(first);
            var b = s.Stroke(20, mode == 3 ? (byte)20 : (byte)40, mode == 1 ? Mask(4) : Mask(1, 2));
            var next = mode == 1 ? Pixels(20, 20, 0, 0, 40, 0, 0, 0) : mode == 2 ? Pixels(20, 40, 40, 0, 0, 0, 0, 0) : Pixels(20, 20, 20, 0, 0, 0, 0, 0);
            var image = s.Image("b", mode == 1 ? Mask(4) : mode == 2 ? Mask(1, 2) : Mask(2), next, b); s.Group(image);
            var plan = s.Plan(a); Check(Untrusted(plan, image) == (mode == 3), "scene " + mode + " trust");
            var f = new Fake(s); var r = await Run(s, plan, f);
            Check(f.Replayed.Count == 0 || r.Status == ReconstructionStatus.Executed, "Replay is Executed, without a target-pixel proof");
            Check(f.Replayed.SequenceEqual(mode == 3 ? new[] { b } : Array.Empty<EditableStrokeKey>()), "scene " + mode + " replay list");
            Bytes(r, s.Layer, mode == 1 ? [0, 0, 0, 0, 40, 0, 0, 0] : mode == 2 ? [0, 40, 40, 0, 0, 0, 0, 0] : [0, 20, 20, 0, 0, 0, 0, 0]);
        }
        // 4-6,12: intersecting/disjoint causal reads and propagation, unknown reads.
        foreach (int mode in new[] { 4, 5, 6, 12 })
        {
            var s = new Scene(); var a = s.Stroke(10, 10, Mask(0)); s.Group(s.Image("a", Mask(0), Pixels(10,0,0,0,0,0,0,0), a));
            var c = s.Stroke(20, 30, Mask(2), true, impact: mode == 5 ? Mask(7) : mode == 12 ? Mask(0,1,2,3,4,5,6,7) : Mask(0));
            var normal = s.Stroke(21, 35, Mask(3)); var ci = s.Image("c", Mask(2,3), Pixels(10,0,30,35,0,0,0,0), c, normal); s.Group(ci);
            EditableStrokeKey? e = null; DirtyImage? ei = null;
            if (mode == 6) { e = s.Stroke(30, 50, Mask(5), true, impact: Mask(2)); ei = s.Image("e", Mask(5), Pixels(10,0,30,35,0,50,0,0), e.Value); s.Group(ei); }
            var p = s.Plan(a); Check(Untrusted(p, ci) == (mode != 5), "causal trust " + mode);
            if (ei is not null) Check(Untrusted(p, ei), "causal propagation");
            var f = new Fake(s); var r = await Run(s, p, f);
            Check(f.Replayed.Contains(c) == (mode != 5) && f.Replayed.Contains(normal) == (mode != 5), "Replay entire causal image including normal contact");
            if (e is not null) Check(f.Replayed.Contains(e.Value), "downstream causal replay");
            Bytes(r,s.Layer,0,0,30,35,0,(byte)(mode == 6 ? 50 : 0),0,0);
        }
        // 7: order is native eventId, not keys, packet ids or images.
        {
            var s = new Scene(); var a=s.Stroke(10,10,Mask(0)); var b=s.Stroke(50,20,Mask(1),eventId:2); var c=s.Stroke(20,30,Mask(2),eventId:3);
            s.Group(s.Image("mixed",Mask(0,1,2),Pixels(10,20,30,0,0,0,0,0),c,a,b));
            var f=new Fake(s); var r=await Run(s,s.Plan(a),f); Check(f.Replayed.SequenceEqual(new[]{b,c}),"native event ordering"); Bytes(r,s.Layer,0,20,30,0,0,0,0,0);
        }
        // 8: shared contact closes backwards and forwards and replays once with complete samples.
        {
            var s=new Scene(); var b=s.Stroke(5,30,Mask(1,2)); s.Group(s.Image("early",Mask(1),Pixels(0,30,0,0,0,0,0,0),b));
            var a=s.Stroke(10,20,Mask(0)); var late=s.Image("late",Mask(0,2),Pixels(20,30,30,0,0,0,0,0),a,b); s.Group(late);
            var p=s.Plan(a); Check(Untrusted(p,s.Packets[0].Images[0]),"backwards contact closure");
            var f=new Fake(s); f.BeforeFlush=()=>Check(s.Strokes[b].Samples.Count==2,"complete samples including up");
            var r=await Run(s,p,f); Check(f.Replayed.SequenceEqual(new[]{b}),"cross-packet replay dedup"); Bytes(r,s.Layer,0,30,30,0,0,0,0,0);
        }
        // 9: same capture group: trusted compose must happen after replay.
        {
            var s=new Scene(); var a=s.Stroke(10,20,Mask(0)); var b=s.Stroke(20,30,Mask(1)); var c=s.Stroke(30,40,Mask(5));
            s.Group(s.Image("trusted",Mask(5),Pixels(20,30,0,0,0,40,0,0),c),s.Image("mixed",Mask(0,1),Pixels(20,30,0,0,0,40,0,0),a,b));
            var f=new Fake(s); f.Paint=(seed,events)=> { Check(seed.Bytes[5*4]==0,"future group composition happened before flush"); seed.Bytes[4]=30; seed.Bytes[7]=255; return seed; };
            var r=await Run(s,s.Plan(a),f); Bytes(r,s.Layer,0,30,0,0,0,40,0,0);
        }
        // 10/11: measured deviation promotes trusted history and detects backwards closure.
        foreach(bool closure in new[]{false,true})
        {
            var s=new Scene(); var shared=s.Stroke(5,50,Mask(7)); s.Group(s.Image("pre",Mask(7),Pixels(0,0,0,0,0,0,0,50),shared));
            var a=s.Stroke(10,10,Mask(0)); var b=s.Stroke(20,30,Mask(1)); s.Group(s.Image("mixed",Mask(0,1),Pixels(10,30,0,0,0,0,0,50),a,b));
            var c=s.Stroke(30,40,Mask(5),true,impact:Mask(4)); var later=s.Image("later",Mask(5),Pixels(10,30,0,0,0,40,0,50),closure?new[]{c,shared}:new[]{c}); s.Group(later);
            // Start before the shared image so it has genuinely been composed online.
            var p=s.Plan(a); Check(!Untrusted(p,later),"static later image should be trusted");
            var f=new Fake(s); int calls=0; f.Paint=(seed,events)=>{ calls++; foreach(var e in events) foreach(var(x,y) in e.Stroke.WriteMask.Pixels()){seed.Bytes[x*4]=s.Colors[e.Stroke.Key];seed.Bytes[x*4+3]=255;}
                if(calls==1){seed.Bytes[16]=99;seed.Bytes[19]=255;} return seed;};
            var r=await ReconstructionEngine.RunAsync(p,f,CancellationToken.None);
            if(closure) Check(r.Issues.Any(i=>i.Code==PlanIssueCode.RuntimeClosureViolation)&&f.Installed.Count==0,"runtime backwards closure must stop before install");
            else {Check(f.Replayed.Contains(c),"exact D promotion");Bytes(r,s.Layer,0,30,0,0,99,40,0,50);}
        }
        // An online-promoted shared event also invalidates its later images without replaying twice.
        {
            var s=new Scene();var a=s.Stroke(10,10,Mask(0));var b=s.Stroke(20,30,Mask(1));
            s.Group(s.Image("mixed",Mask(0,1),Pixels(10,30,0,0,0,0,0,0),a,b));
            var c=s.Stroke(30,40,Mask(4,5),true,impact:Mask(3));
            var early=s.Image("early",Mask(4),Pixels(10,30,0,0,40,0,0,0),c);s.Group(early);
            var late=s.Image("late",Mask(5),Pixels(10,30,0,0,40,40,0,0),c);s.Group(late);
            var f=new Fake(s);int calls=0;f.Paint=(seed,events)=>
            { foreach(var e in events)foreach(var(x,y) in e.Stroke.WriteMask.Pixels()){seed.Bytes[x*4]=++calls==1?(byte)31:(byte)41;seed.Bytes[x*4+3]=255;}
              seed.Bytes[12]=99;seed.Bytes[15]=255;return seed; };
            var p=s.Plan(a);Check(!Untrusted(p,early)&&!Untrusted(p,late),"forward closure starts trusted");
            var r=await Run(s,p,f);Check(r.Promoted.Contains(early.Key)&&r.Promoted.Contains(late.Key),"runtime forward shared-contact closure");
            Check(f.Replayed.Count(k=>k==c)==1,"runtime forward contact dedup");Bytes(r,s.Layer,0,31,0,99,41,41,0,0);
        }
        // A frozen size recipe expands the estimate; absent recipes or unknown numeric keys refuse planning.
        {
            var s=new Scene();var a=s.Stroke(10,10,Mask(0));s.Group(s.Image("a",Mask(0),Pixels(10,0,0,0,0,0,0,0),a));
            var c=s.Stroke(20,30,Mask(5),true,impact:Mask(2));var ci=s.Image("c",Mask(5),Pixels(10,0,0,0,0,30,0,0),c);s.Group(ci);
            var x=s.Index();var changes=new Dictionary<EditableStrokeKey,EventChange>{{a,new(Numbers:new Dictionary<string,double>{{"brush_size",5}})}};
            var req=EditRequest.Event(s.Layer,Mask(0),changes);
            Check(ReconstructionPlanner.Build(x,req).Issues.Any(i=>i.Code==PlanIssueCode.UnsupportedOverride),"missing frozen recipe refused");
            var recipe=new DirtyMatrix.Core.CoverageSettings{CanvasWidth=Width,CanvasHeight=1,BrushSize=1,ZoomPercent=100,AntiAliasRadius=0,SafetyRadius=0,CoveragePaddingScreenPixels=0};
            var label=new LabelEvidence("frozen",10,"tablet",10,11,.5,[new(.5,.5,.5,.5)],["a"]){CoverageSettings=recipe};
            var strokes=x.Strokes.ToDictionary();strokes[a]=strokes[a] with {CoverageLabels=[label]};x=x with{Strokes=strokes};
            var p=ReconstructionPlanner.Build(x,req);Check(p.Issues.Count==0&&Untrusted(p,ci),"size recipe expands changed area into downstream causal read");
            Check(recipe.BrushSize==1&&strokes[a].WriteMask.Equals(Mask(0)),"planner does not mutate frozen inputs");
            var invalid=req with{Changes=new Dictionary<EditableStrokeKey,EventChange>{{a,new(Numbers:new Dictionary<string,double>{{"invented",1}})}}};
            Check(ReconstructionPlanner.Build(x,invalid).Issues.Any(i=>i.Code==PlanIssueCode.UnsupportedOverride),"unknown override refused");
        }
        // 13: a reference-layer reader sees changed pixels in another layer.
        {
            var s=new Scene(); var a=s.Stroke(10,10,Mask(0)); s.Group(s.Image("a",Mask(0),Pixels(10,0,0,0,0,0,0,0),a));
            var x=s.Index(); var y=new LayerKey("fixture",1,"other"); var key=new EditableStrokeKey("fixture",30);
            var c=s.Strokes[a] with {Key=key,Layer=y,BeginEventId=30,IsCausal=true,WriteMask=Mask(2),ImpactMask=Mask(0),Classification=new(true,true,[],false,[]){ReadsReferenceLayers=true}};
            var yi=new DirtyImage(new(y,"y","i"),Mask(2),Pixels(0,0,0,0,0,0,0,0),Pixels(0,0,30,0,0,0,0,0),[key],true){W=Mask(2),Probe=Mask(2),Impact=Mask(0)};
            var yp=new LayerPacket("y",y,10,null,yi.Now,[yi],IsBoundary:true,BaselineSource:"recordingStart",AfterSnapshotId:"s0",NowSnapshotId:"s1",CanvasWidth:Width,CanvasHeight:1);
            var layers=x.Layers.ToDictionary(); layers[y]=new(y,[yp],[c],true,[],Current:yi.Now);
            var strokes=x.Strokes.ToDictionary(); strokes[key]=c; var images=x.Images.ToDictionary();images[yi.Key]=yi;
            var p=ReconstructionPlanner.Build(x with {Layers=layers,Strokes=strokes,Images=images},EditRequest.Event(s.Layer,Mask(0),new Dictionary<EditableStrokeKey,EventChange>{{a,new(Delete:true)}}));
            Check(Untrusted(p,yi),"cross-layer reference dependency"); s.Colors[key]=30; var f=new Fake(s); var r=await Run(s,p,f);Check(f.Replayed.Contains(key),"reference layer replay");Bytes(r,y,0,0,30,0,0,0,0,0);
        }
        // 14-16: errors stop before the executor is called.
        // An image with unreplayable parts no longer stops reconstruction; it replays what it can.
        foreach(var code in new[]{PlanIssueCode.UndoRedoInRange,PlanIssueCode.ChainLossy,PlanIssueCode.NoBaseline})
        {
            var s=new Scene();var a=s.Stroke(10,10,Mask(0));s.Group(s.Image("a",Mask(0),Pixels(10,0,0,0,0,0,0,0),a));var x=s.Index();
            if(code==PlanIssueCode.UnreplayableEventInMatrix){var image=x.Images.Values.Single() with {HasUnreplayableEvents=true};var l=x.Layers[s.Layer];x=x with {Images=new Dictionary<DirtyImageKey,DirtyImage>{{image.Key,image}},Layers=new Dictionary<LayerKey,LayerIndex>{{s.Layer,l with {Packets=[l.Packets[0] with {Images=[image]}]}}}};}
            else x=x with{Issues=[new(code,"fixture guard")]};
            var p=ReconstructionPlanner.Build(x,EditRequest.Event(s.Layer,Mask(0),new Dictionary<EditableStrokeKey,EventChange>{{a,new(Delete:true)}}));
            var f=new Fake(s);var r=await ReconstructionEngine.RunAsync(p,f,CancellationToken.None);Check(r.Issues.Any(i=>i.Code==code)&&f.Calls.Count==0,"fail closed "+code);
        }
        // 19/20: seed is before earliest affected group; a deletion-only group needs no empty flush.
        {
            var s=new Scene();var first=s.Stroke(5,60,Mask(7));s.Group(s.Image("prefix",Mask(7),Pixels(0,0,0,0,0,0,0,60),first));
            var a=s.Stroke(10,10,Mask(0));s.Group(s.Image("a",Mask(0),Pixels(10,0,0,0,0,0,0,60),a));
            var p=s.Plan(a);var f=new Fake(s);var r=await Run(s,p,f);Check(p.Layers[0].SeedPacketId=="p1","earliest affected seed");
            Check(f.Replayed.Count==0&&r.Status==ReconstructionStatus.PixelVerified,"empty replay skipped and exact readback verified");Bytes(r,s.Layer,0,0,0,0,0,0,0,60);
        }
        // Cancellation and install readback deviation are never successful.
        {
            var s=new Scene();var a=s.Stroke(1,10,Mask(0));s.Group(s.Image("a",Mask(0),Pixels(10,0,0,0,0,0,0,0),a));var f=new Fake(s);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();var r=await ReconstructionEngine.RunAsync(s.Plan(a),f,cancelled.Token);
            Check(r.Status==ReconstructionStatus.Cancelled&&f.Calls.Count==0,"cancel before CSP actions");
            f.Readback=expected=>{expected.Bytes[0]=123;return expected;};
            r=await ReconstructionEngine.RunAsync(s.Plan(a),f,CancellationToken.None);
            Check(r.Status==ReconstructionStatus.Failed&&r.Issues.Any(i=>i.Code==PlanIssueCode.PasteNotExact),"install readback deviation cannot claim PixelVerified");
            f.Readback=_=>new(1,1,new byte[4]);
            r=await ReconstructionEngine.RunAsync(s.Plan(a),f,CancellationToken.None);
            Check(r.Issues.Any(i=>i.Code==PlanIssueCode.CaptureMismatch),"install dimensions mismatch");
            var precise=s.Plan(a);precise=precise with{Request=precise.Request with{Kind=EditKind.PreciseUndo}};
            // Paste is validated by the transaction before discrete replay. Its final capture
            // includes the replayed selection and deliberately differs from the deletion seed.
            f.Readback=_=>Pixels(10,0,0,0,0,0,0,0);
            r=await ReconstructionEngine.RunAsync(precise,f,CancellationToken.None);
            Check(r.Status==ReconstructionStatus.Executed&&r.Issues.Count==0,"precise undo retains actual final discrete capture");
            Bytes(r,s.Layer,10,0,0,0,0,0,0,0);
        }
        // Precise undo works on its region: outside the selected footprint the current pixels stay,
        // inside it the snapshot from before the earliest selected contact; replay only comes last.
        {
            var s=new Scene();var a=s.Stroke(10,10,Mask(0));var b=s.Stroke(20,30,Mask(1));
            s.Group(s.Image("ab",Mask(0,1),Pixels(10,30,0,0,0,0,0,0),a,b));
            var precise=s.Plan(a);precise=precise with{Request=precise.Request with{Kind=EditKind.PreciseUndo}};
            var f=new Fake(s);var r=await ReconstructionEngine.RunAsync(precise,f,CancellationToken.None);
            Check(r.Status==ReconstructionStatus.Executed&&f.Calls.SequenceEqual(new[]{"install"})&&f.Deferred.Count==0,
                "precise undo installs region-rolled-back pixels without any replay before it");
            Bytes(r,s.Layer,0,30,0,0,0,0,0,0);
        }
        {
            // c is an earlier unselected contact under a in the same capture: the rollback removes it,
            // so it is redrawn in the final replay (before a, by original order).
            var s=new Scene();var c=s.Stroke(5,50,Mask(0,1));var a=s.Stroke(10,10,Mask(0));
            s.Group(s.Image("ca",Mask(0,1),Pixels(10,50,0,0,0,0,0,0),c,a));
            var precise=s.Plan(a);precise=precise with{Request=precise.Request with{Kind=EditKind.PreciseUndo}};
            var f=new Fake(s);var r=await ReconstructionEngine.RunAsync(precise,f,CancellationToken.None);
            Check(r.Status==ReconstructionStatus.Executed&&f.Deferred.SequenceEqual(new[]{c}),
                "an unselected contact reverted by the snapshot joins the final replay");
            Bytes(r,s.Layer,0,50,0,0,0,0,0,0);
        }
        n += ReconstructionRequestContractChecks.Run();
        return n;
    }
}
