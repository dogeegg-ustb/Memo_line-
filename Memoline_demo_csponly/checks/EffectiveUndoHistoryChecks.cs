using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using MemolineDemo.Editing;
using StrokeReplay;

namespace MemolineDemo;

internal static class EffectiveUndoHistoryChecks
{
    private const int Width=4;
    internal static async Task<int> RunAsync()
    {
        int checks=0;
        void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);checks++;}
        static bool Same(ImageRgba actual,ImageRgba expected)=>actual.Width==expected.Width&&actual.Height==expected.Height&&
            actual.Bytes.AsSpan().SequenceEqual(expected.Bytes);

        var undone=new Fixture();
        string nativeBefore=string.Join('\n',undone.Session.Frames.Select(frame=>frame.GetRawText()));
        var effective=undone.Effective();var layer=effective.Layers[undone.Layer];
        Check(undone.Timeline.Commands.Single().Target?.BeginAppendId==undone.B.BeginAppendId&&
            undone.Timeline.Pens[undone.B.BeginAppendId].State==NativeUndoPenState.Undone,
            "The integration fixture must resolve the actual native Undo to the second B contact.");
        Check(layer.UndoRedo.Count==0&&layer.UndoCheckpoints.Count==0&&layer.Packets.Count==4,
            "A resolved Undo keeps the recorded dirty images instead of collapsing the layer history.");
        var reversal=effective.Images[new(undone.Layer,"boundary","undo-pixels")];
        Check(reversal.Events.SequenceEqual(new[]{undone.B})&&!reversal.HasUnreplayableEvents,
            "The image that recorded the Undo is explained by the cancelled contact.");
        Check(effective.Strokes[undone.B].IsUndone&&
            effective.Strokes[undone.B].Images.ToHashSet().SetEquals(new[]{undone.RawBImage,reversal.Key})&&
            effective.Strokes[undone.B].Samples.Count==2&&effective.Strokes[undone.B].Layer==undone.Layer,
            "Cancelled B links its original image and its reversal image, so both share one trust decision.");
        Check(!undone.Raw.Strokes[undone.B].IsUndone&&undone.Raw.Images[undone.RawBImage].Now.Bytes[4]==40&&
            nativeBefore==string.Join('\n',undone.Session.Frames.Select(frame=>frame.GetRawText())),
            "Deriving effective history must preserve the original B pixels and native frame bytes.");
        var refreshedPackets=undone.Raw.Layers[undone.Layer].Packets.Select(packet=>packet.IsBoundary?packet with
        {
            PacketId="fresh-edit-boundary",TriggerTicks=packet.TriggerTicks+100,NowSnapshotId="fresh-edit-snapshot",
            Images=packet.Images.Select(old=>old with{Key=new(undone.Layer,"fresh-edit-boundary",old.Key.ImageId)}).ToArray()
        }:packet).ToArray();
        var refreshedRaw=undone.Raw with
        {
            Layers=new Dictionary<LayerKey,LayerIndex>{{undone.Layer,undone.Raw.Layers[undone.Layer] with{Packets=refreshedPackets}}},
            Images=refreshedPackets.SelectMany(packet=>packet.Images).ToDictionary(old=>old.Key)
        };
        var refreshedSource=undone.Session with{Packets=refreshedPackets,BoundaryTicks=undone.Session.BoundaryTicks+100};
        var refreshedEffective=EffectiveUndoHistory.Apply(refreshedSource,refreshedRaw,undone.Timeline);
        var shown=undone.EditSession(effective);
        var recaptured=new EditSession(refreshedSource,refreshedEffective,undone.Layer);
        Check(DemoEditingHost.EditingBoundaryMatches(shown,recaptured),
            "Recapturing unchanged pixels and native pen states with a new packet name preserves the reviewed edit boundary.");
        var changedStates=refreshedEffective.Strokes.ToDictionary();
        changedStates[undone.B]=changedStates[undone.B] with{IsUndone=false};
        Check(!DemoEditingHost.EditingBoundaryMatches(shown,recaptured with{Index=refreshedEffective with{Strokes=changedStates}}),
            "Identical pixels alone cannot preserve a reviewed edit if B's effective history state has changed.");

        var bSelection=PreciseUndoSelector.Select(effective,undone.Layer,Mask(1));
        var aSelection=PreciseUndoSelector.Select(effective,undone.Layer,Mask(0));
        Check(bSelection.Selected.Count==0&&bSelection.Issues.Any(issue=>issue.Code==PlanIssueCode.NoSelection)&&
            aSelection.IsValid&&aSelection.Selected.Select(stroke=>stroke.Key).SequenceEqual(new[]{undone.A}),
            "A region over cancelled B cannot select it, while a region over active A remains editable.");
        var interaction=new CanvasInteraction(undone.EditSession(effective));
        interaction.SetRegion([new PointF(0,0),new PointF(400,0),new PointF(400,100),new PointF(0,100)]);
        interaction.SelectImage(interaction.Candidates.Single(candidate=>candidate.CanSelect).Image.Key);
        Check(interaction.EligibleStrokes.Select(stroke=>stroke.Key).SequenceEqual(new[]{undone.A}),
            "The actual image-first event selection path must exclude cancelled B too.");

        var plan=undone.DeleteA(effective);
        Check(plan.IsValid&&plan.Layers.Single().Groups.Count==3,
            "Editing A across a resolved Undo plans over the recorded dirty images.");
        var matching=new FakeExecutor(undone);
        var result=await ReconstructionEngine.RunAsync(plan,matching,CancellationToken.None);
        Check(result.Status is ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified&&
            result.Issues.Count==0&&matching.Installed.Count==1&&Same(result.Layers[undone.Layer],Pixels(0,0,0,0)),
            "Deleting A after Undo B must produce a transparent layer without resurrecting B.");
        Check(matching.Flushes.Count==0&&matching.Calls.SequenceEqual(new[]{"install"}),
            "B's untouched images compose directly; deleting A alone needs no CSP replay.");

        // When B's original image must be replayed, its reversal image follows by shared contact and B is skipped.
        var touchB=ReconstructionPlanner.Build(effective,EditRequest.Event(undone.Layer,Mask(0,1),
            new Dictionary<EditableStrokeKey,EventChange>{{undone.A,new(Numbers:new Dictionary<string,double>{{"brush_size",1}})}}));
        Check(touchB.IsValid,"Changing A after Undo B remains a valid plan.");
        var bForced=new HashSet<DirtyImageKey>{undone.RawBImage,reversal.Key};
        var forcedPlan=touchB with{Layers=touchB.Layers.Select(l=>l with{Untrusted=l.Untrusted.Union(bForced).ToHashSet()}).ToArray()};
        var forcedExecutor=new FakeExecutor(undone);
        result=await ReconstructionEngine.RunAsync(forcedPlan,forcedExecutor,CancellationToken.None);
        Check(result.Status==ReconstructionStatus.Executed&&forcedExecutor.Flushes.All(f=>!f.Events.Contains(undone.B))&&
            Same(result.Layers[undone.Layer],Pixels(20,0,0,0)),
            "Replaying B's images never redraws the cancelled contact.");

        var redone=new Fixture(redo:true);effective=redone.Effective();layer=effective.Layers[redone.Layer];
        Check(!effective.Strokes[redone.B].IsUndone&&layer.UndoCheckpoints.Count==0&&
            effective.Images[new(redone.Layer,"boundary","undo-pixels")].Events.SequenceEqual(new[]{redone.B}),
            "Redo restores B as an active contact linked to the images its commands changed.");
        Check(PreciseUndoSelector.Select(effective,redone.Layer,Mask(1)).Selected.Select(stroke=>stroke.Key).SequenceEqual(new[]{redone.B}),
            "Redo makes B's visible write range selectable again.");
        var redoExecutor=new FakeExecutor(redone);
        result=await ReconstructionEngine.RunAsync(redone.DeleteA(effective),redoExecutor,CancellationToken.None);
        Check(result.Status is ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified&&
            result.Issues.Count==0&&Same(result.Layers[redone.Layer],Pixels(0,40,0,0)),
            "Deleting A after Redo preserves only B's now-active pixels.");

        foreach(bool ambiguous in new[]{false,true})
        {
            var unknown=new Fixture(unknown:true,ambiguous:ambiguous);effective=unknown.Effective();
            Check(unknown.Timeline.Barriers.Count>0&&effective.Layers[unknown.Layer].UndoCheckpoints.Count==0&&
                effective.Layers[unknown.Layer].UndoRedo.Count>0,
                "Unknown or ambiguous document commands must retain the original refusal evidence.");
            var unknownExecutor=new FakeExecutor(unknown);
            result=await ReconstructionEngine.RunAsync(unknown.DeleteA(effective),unknownExecutor,CancellationToken.None);
            Check(result.Status==ReconstructionStatus.Failed&&result.Issues.Any(issue=>issue.Code==PlanIssueCode.UndoRedoInRange)&&
                unknownExecutor.Calls.Count==0,
                "A pixel endpoint cannot turn an unknown document command into an executable edit.");
        }

        var stale=new Fixture();
        var staleLayer=stale.Raw.Layers[stale.Layer];
        var stalePackets=staleLayer.Packets.Select(packet=>packet.IsBoundary?packet with{TriggerTicks=49}:packet).ToArray();
        var staleIndex=stale.Raw with{Layers=new Dictionary<LayerKey,LayerIndex>{{stale.Layer,staleLayer with{Packets=stalePackets}}}};
        effective=EffectiveUndoHistory.Apply(stale.Session,staleIndex,stale.Timeline);
        Check(effective.Layers[stale.Layer].UndoCheckpoints.Count==0&&effective.Layers[stale.Layer].UndoRedo.Count>0&&
            !stale.DeleteA(effective).IsValid,
            "A full capture taken before shortcut resolution cannot authorize the effective Undo checkpoint.");
        return checks;
    }

    private static PixelMask Mask(params int[] columns)
    {var mask=new PixelMask(Width,1);foreach(int x in columns)mask.Set(x,0);return mask;}
    private static ImageRgba Pixels(params byte[] colors)
    {
        var bytes=new byte[Width*4];
        for(int x=0;x<Width;x++){bytes[x*4]=colors[x];bytes[x*4+3]=colors[x]==0?(byte)0:(byte)255;}
        return new(Width,1,bytes);
    }
    private sealed record FlushObservation(ImageRgba Seed,IReadOnlyList<EditableStrokeKey> Events,bool Unchanged);
    private sealed class FakeExecutor(Fixture fixture):ICspReconstructionExecutor
    {
        internal readonly List<string> Calls=[];
        internal readonly List<FlushObservation> Flushes=[];
        internal readonly Dictionary<LayerKey,ImageRgba> Installed=[];
        internal bool CorruptOriginal,CorruptAlpha;
        public Task<ImageRgba> FlushAsync(LayerKey layer,ImageRgba seed,IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey,ImageRgba> referenceLayers,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add("flush");
            Flushes.Add(new(seed.Copy(),events.Select(e=>e.Stroke.Key).ToArray(),events.All(e=>e.Change is null)));
            foreach(var replay in events)
                foreach(var (x,y) in replay.Stroke.WriteMask.Pixels())
                {
                    int offset=(y*Width+x)*4;seed.Bytes[offset]=replay.Stroke.Key==fixture.A?(byte)20:(byte)40;
                    seed.Bytes[offset+3]=255;
                }
            if(CorruptOriginal&&Flushes.Count==1)seed.Bytes[CorruptAlpha?3:0]^=1;
            return Task.FromResult(seed);
        }
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer,ImageRgba expected,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add("install");Installed.Add(layer,expected.Copy());return Task.FromResult(expected.Copy());}
    }
    private sealed class Fixture
    {
        internal readonly LayerKey Layer=new("effective-undo-fixture",1,"f0c28909-973a-4087-a75b-325cba9c8247");
        internal readonly EditableStrokeKey A=new("effective-undo-fixture",1),B=new("effective-undo-fixture",3);
        internal readonly DirtyImageKey RawBImage;
        internal readonly SessionSnapshot Session;
        internal readonly NativeUndoTimelineResult Timeline;
        internal readonly EditIndex Raw;
        internal Fixture(bool redo=false,bool unknown=false,bool ambiguous=false)
        {
            var frames=new List<JsonElement>();ulong append=0,eventId=0;
            JsonElement Add(string kind,object data,bool hardware=false,ulong? operation=null,ulong[]? references=null)
            {
                var frame=JsonSerializer.SerializeToElement(new{sessionId=Layer.SessionId,appendId=++append,
                    eventId=hardware?++eventId:0,ticks=(long)append*10,kind,path=hardware?"hardware":"state",
                    operationId=operation,relatedEventIds=references??[],data});
                frames.Add(frame);return frame;
            }
            void Pen(ulong operation)=>Add("penBegin",new{inCsp=true,injected=false,heldKeys=Array.Empty<int>(),
                penDownLocation=new PenDownLocation("canvasViewport","canvasViewport",20,20,"windowsCursor","matched")},hardware:true);
            Pen(1);Add("penEnd",new{inCsp=true,injected=false,reason="penUp"},hardware:true,operation:1);
            Pen(3);Add("penEnd",new{inCsp=true,injected=false,reason="penUp"},hardware:true,operation:3);
            void Command(params string[] names)
            {
                var key=Add("keyInput",new{inCsp=true,vk=90,action="down",heldKeys=new[]{17,90},
                    injected=false,extraInfo="0x0",guardIntercepted=false,nextHookResult=0L},hardware:true);
                Add("shortcutResolved",new{resolution="matched",matches=names.Select(command=>new{command}).ToArray()},
                    references:[key.GetProperty("eventId").GetUInt64()]);
            }
            Command(unknown?(ambiguous?new[]{"undo","toolselectpen"}:new[]{"layermergeall"}):new[]{"undo"});
            if(redo)Command("redo");
            long captureTicks=(long)append*10;
            var blank=Pixels(0,0,0,0);var aPixels=Pixels(20,0,0,0);var both=Pixels(20,40,0,0);
            ImageRgba endpoint=redo||unknown?both:aPixels;
            EditableStroke Stroke(EditableStrokeKey key,int x)
            {
                ulong nativeEvent=key.BeginAppendId;
                return new(key,nativeEvent,(long)key.BeginAppendId*10,((long)key.BeginAppendId+1)*10,
                    [new((long)key.BeginAppendId*10,x+.5,.5,1,0,0,true,nativeEvent,key.BeginAppendId),
                     new(((long)key.BeginAppendId+1)*10,x+.5,.5,0,0,0,false,nativeEvent+1,key.BeginAppendId+1)],
                    "G pen",Layer,false,BrushNumbers:new Dictionary<string,double>{{"brush_size",1}})
                {BeginEventId=nativeEvent,WriteMask=Mask(x),ProbeMask=Mask(x),ImpactMask=Mask(),
                    Classification=new(true,false,[],true,[]),CoverageLabels=[new("label-"+x,nativeEvent,"pen",
                        (long)key.BeginAppendId*10,((long)key.BeginAppendId+1)*10,.5,[new(x+.5,.5,x+.5,.5)],["raw-"+x])]};
            }
            var a=Stroke(A,0);var b=Stroke(B,1);
            var ai=new DirtyImage(new(Layer,"raw-a","raw-a"),Mask(0),blank,aPixels,[A]){W=Mask(0),Probe=Mask(0)};
            var bi=new DirtyImage(new(Layer,"raw-b","raw-b"),Mask(1),aPixels,both,[B]){W=Mask(1),Probe=Mask(1)};
            var end=new DirtyImage(new(Layer,"boundary","undo-pixels"),Mask(1),both,endpoint,[]);
            RawBImage=bi.Key;a=a with{Images=[ai.Key]};b=b with{Images=[bi.Key]};
            LayerPacket[] packets=[
                new("baseline",Layer,0,null,blank,[],IsRecordingBaseline:true,BaselineSource:"recordingStart",CanvasWidth:Width,CanvasHeight:1),
                new("raw-a",Layer,20,blank,aPixels,[ai],AfterSnapshotId:"baseline",NowSnapshotId:"after-a",BaselineSource:"recordingStart",CanvasWidth:Width,CanvasHeight:1),
                new("raw-b",Layer,40,aPixels,both,[bi],AfterSnapshotId:"after-a",NowSnapshotId:"after-b",CanvasWidth:Width,CanvasHeight:1),
                new("boundary",Layer,captureTicks,both,endpoint,[end],IsBoundary:true,AfterSnapshotId:"after-b",NowSnapshotId:"boundary",CanvasWidth:Width,CanvasHeight:1)];
            Session=new("fixture.part",Layer.SessionId,1000,frames,new MemolineReplayDocument("fixture.part",Layer.SessionId,1000,[]),packets,append,captureTicks);
            Timeline=NativeUndoTimeline.Build(Session,[new(A.BeginAppendId,1,Layer,true,true),new(B.BeginAppendId,3,Layer,true,true)]);
            var commands=Timeline.Commands.Select(command=>new UndoRedoEvidence(command.InputAppendId,command.Command,
                command.State==NativeUndoCommandState.Unknown)).ToArray();
            Raw=new(new Dictionary<LayerKey,LayerIndex>{{Layer,new(Layer,packets,[a,b],true,[],blank,endpoint){UndoRedo=commands}}},
                new Dictionary<EditableStrokeKey,EditableStroke>{{A,a},{B,b}},packets.SelectMany(packet=>packet.Images).ToDictionary(image=>image.Key),[]);
        }
        internal EditIndex Effective()=>EffectiveUndoHistory.Apply(Session,Raw,Timeline);
        internal ReconstructionPlan DeleteA(EditIndex index)=>ReconstructionPlanner.Build(index,
            EditRequest.Event(Layer,Mask(0),new Dictionary<EditableStrokeKey,EventChange>{{A,new(Delete:true)}}));
        internal EditSession EditSession(EditIndex index)=>new(Session,index,Layer)
        {CanvasViewEvidence=JsonSerializer.SerializeToElement(new{sessionId=Layer.SessionId,status="changed",state=new{transform=new
            {canvasPixelWidth=Width,canvasPixelHeight=1,matrices=new{convention="sct-affine-row-major/v1",canvasSpace="normalized",
                screenToCanvas=new double[]{.0025,0,0,0,.01,0},canvasToScreen=new double[]{400,0,0,0,100,0}}}}})};
    }
}
