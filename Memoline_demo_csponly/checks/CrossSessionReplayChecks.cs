using System.Drawing;
using System.Text.Json;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal static class CrossSessionReplayChecks
{
    internal static int Run()
    {
        int count=0;
        void Check(bool condition,string reason)
        { if(!condition)throw new InvalidOperationException(reason); count++; }
        void Reject<T>(Action action,string reason) where T:Exception
        {
            try { action(); }
            catch(T) { count++; return; }
            throw new InvalidOperationException(reason);
        }
        var physical=new LayerKey("live",1,"ink");
        static SessionSnapshot Source(string id,long frequency,string brush,byte color,
            params (ulong Event,ulong Append,long Ticks)[] contacts)
        {
            var view=new CanvasViewSnapshot(100,0,100,100,new Rectangle(80,80,100,100));
            var drawings=contacts.Select(c=>new MemolineReplayStroke(c.Event,c.Ticks,c.Ticks+2,view,
                [new(c.Ticks,100,100,.5,0,0,true,c.Append,c.Event),
                 new(c.Ticks+1,101,100,.75,0,0,true,c.Append+1,c.Event+1),
                 new(c.Ticks+2,102,100,0,0,0,false,c.Append+2,c.Event+2)])).ToArray();
            var brushState=JsonSerializer.SerializeToElement(new {status="changed",state=new {
                name=brush,properties=new[] {new {key="size",type="number",status="ok",value=3}}}});
            var layerState=JsonSerializer.SerializeToElement(new {status="changed",state="source-"+id});
            var colorState=JsonSerializer.SerializeToElement(new {status="changed",state=new {red=color,green=0,blue=0}});
            return new(id+".memoline",id,frequency,[],new(id+".memoline",id,frequency,drawings),[],1000,100000) {
                History=[new(0,1,"brushState",brushState),new(0,2,"currentLayerState",layerState),
                    new(0,3,"colorState",colorState)]
            };
        }
        EditableStroke Stroke(SessionSnapshot source,int order,int nativeIndex)
        {
            var native=source.Replay.Strokes[nativeIndex];
            var first=native.Samples[0];
            return new(new(source.SessionId,first.AppendId),native.OperationId,native.StartTicks,native.EndTicks,
                native.Samples,"source brush",physical,true) {
                BeginEventId=first.EventId,SessionOrder=order,SourceLayer=new(source.SessionId,1,"ink")
            };
        }
        var first=Source("old-first",10000,"Historical Pen One",11,(10,100,90000),(20,200,90050));
        var second=Source("old-second",1000,"Historical Pen Two",22,(5,100,100),(10,200,150));
        var live=Source("live",500,"Current Pen",33,(3,100,10));
        var a=Stroke(first,0,0); var aNext=Stroke(first,0,1);
        var b=Stroke(second,1,0); var bNext=Stroke(second,1,1); var c=Stroke(live,2,0);
        var strokes=new[] {a,aNext,b,bNext,c}.ToDictionary(s=>s.Key);
        var index=new EditIndex(new Dictionary<LayerKey,LayerIndex>(),strokes,
            new Dictionary<DirtyImageKey,DirtyImage>(),[]);
        var session=new EditSession(live,index,physical) {
            Sources=new Dictionary<string,SessionSnapshot> {[first.SessionId]=first,[second.SessionId]=second}
        };
        var change=new EventChange(Numbers:new Dictionary<string,double> { ["size"]=7 });
        var request=new EditRequest(EditKind.EventEditing,physical,new PixelMask(1,1),
            new Dictionary<EditableStrokeKey,EventChange> {[a.Key]=change});
        var events=new ReplayEvent[] {new(c,null),new(b,null),new(a,change)};
        var schedules=WindowsCspEditingActions.BuildSchedules(session,request,events,ReplayMode.Concentrated,"Work","work-uuid");
        Check(schedules.Select(s=>s.Strokes[0].EditableKey.SessionId).SequenceEqual(["old-first","old-second","live"]),
            "Cross-session replay must use project order rather than restarted native event IDs or ticks.");
        Check(schedules.Select(s=>s.Frequency).SequenceEqual([10000L,1000L,500L]),
            "Each source recording must retain its own clock frequency.");
        Check(schedules.Select(s=>s.Strokes[0].EditableKey.BeginAppendId).SequenceEqual([100UL,100UL,100UL]),
            "Repeated native append IDs from different sessions must remain distinct editable events.");
        Check(schedules.Select(s=>s.Strokes[0].State.Brush!.Name).SequenceEqual(["Historical Pen One","Historical Pen Two","Current Pen"]),
            "Historical brush restoration must read the event's source session rather than the live state table.");
        Check(schedules.Select(s=>s.Strokes[0].State.Layer).SequenceEqual(["source-old-first","source-old-second","source-live"]),
            "Native historical layer names must remain in replay state while routing to the verified work layer.");
        Check(schedules.Select(s=>s.Strokes[0].State.Color.GetProperty("red").GetByte()).SequenceEqual([(byte)11,(byte)22,(byte)33]),
            "Historical colors must be resolved from the same native source as each contact.");
        Check(schedules.All(s=>s.WorkLayerName=="Work"&&s.WorkLayerUuid=="work-uuid"),
            "Every source recording must replay into the same transaction-confirmed work layer.");
        Check(ReferenceEquals(schedules[0].Strokes[0].Stroke,first.Replay.Strokes[0]) &&
            ReferenceEquals(schedules[1].Strokes[0].Stroke.Samples,second.Replay.Strokes[0].Samples),
            "Cross-session replay must preserve complete original samples and clocks without rebasing.");
        Check(schedules[0].Strokes[0].OverrideBrush!.Numbers.Single().Value==7 &&
            schedules[0].Strokes[0].State.Brush!.Numbers.Single().Value==3,
            "An override must derive from its historical source without changing the original brush snapshot.");
        Check(schedules.All(s=>s.Strokes.All(d=>d.OverrideColor.ValueKind==JsonValueKind.Undefined)),
            "A change without a colour keeps every stroke's recorded colour.");
        var recoloured=WindowsCspEditingActions.BuildSchedules(session,request,[new(a,change with {Rgb=[10,20,30]})],
            ReplayMode.Concentrated,"Work","work-uuid").Single().Strokes.Single();
        Check(ColorRestorer.Rgb(recoloured.OverrideColor) is [10,20,30] && ColorRestorer.Rgb(recoloured.Color) is [10,20,30] &&
              recoloured.StrictColor && recoloured.State.Color.GetProperty("red").GetByte()==11,
            "An edit's colour is replayed instead of the recorded one, strictly, without changing the historical state.");
        Check(schedules.All(s=>s.FollowingStrokeStartTicks is null),
            "Concentrated replay must not introduce native gaps between source recordings.");
        var undo=request with {Kind=EditKind.PreciseUndo,Changes=new Dictionary<EditableStrokeKey,EventChange> {
            [a.Key]=new(),[aNext.Key]=new(),[b.Key]=new(),[bNext.Key]=new(),[c.Key]=new()}};
        schedules=WindowsCspEditingActions.BuildSchedules(session,undo,events,ReplayMode.Discrete,"Work","work-uuid");
        Check(schedules.Select(s=>s.FollowingStrokeStartTicks).SequenceEqual(new long?[] {90050,150,null}),
            "Precise-undo batches may use only a following event from the same source clock.");
        schedules=WindowsCspEditingActions.BuildSchedules(session,request,
            [new(aNext,null),new(b,null),new(a,null)],ReplayMode.Concentrated,"Work","work-uuid");
        Check(schedules[0].Strokes.Select(s=>s.Stroke.OperationId).SequenceEqual([10UL,20UL]) && schedules.Count==2,
            "A source batch must sort complete contacts by native penBegin while preserving project session order.");
        Reject<InvalidDataException>(()=>WindowsCspEditingActions.BuildSchedules(session with {Sources=new Dictionary<string,SessionSnapshot>()},
            request,events,ReplayMode.Concentrated,"Work","work-uuid"),
            "A missing old recording must fail even when the live recording has the same append ID.");
        var mismatched=first with {Replay=first.Replay with {Frequency=live.Frequency}};
        Reject<InvalidDataException>(()=>WindowsCspEditingActions.BuildSchedules(session with {Sources=new Dictionary<string,SessionSnapshot> {
            [first.SessionId]=mismatched,[second.SessionId]=second}},request,events,ReplayMode.Concentrated,"Work","work-uuid"),
            "Source snapshot and native replay clocks must agree before CSP is prepared.");
        Reject<InvalidDataException>(()=>WindowsCspEditingActions.BuildSchedules(session,request,
            [new(a with {BeginEventId=11},null)],ReplayMode.Concentrated,"Work","work-uuid"),
            "Editable keys must match the source recording's original penBegin event.");
        Reject<InvalidDataException>(()=>WindowsCspEditingActions.BuildSchedules(session,request,
            [new(a,null),new(a,null)],ReplayMode.Concentrated,"Work","work-uuid"),
            "Cross-session batching must never hide duplicate native contacts.");
        Reject<InvalidDataException>(()=>WindowsCspEditingActions.BuildSchedules(session,request,
            [new(a,null),new(aNext with {SessionOrder=4},null)],ReplayMode.Concentrated,"Work","work-uuid"),
            "One source recording must not be assigned conflicting project positions.");
        Reject<InvalidDataException>(()=>WindowsCspEditingActions.BuildSchedules(session,request,
            [new(a,null),new(b with {SessionOrder=0},null)],ReplayMode.Concentrated,"Work","work-uuid"),
            "Different recordings must not share an ambiguous project position.");
        var absentState=first with {History=[]};
        Reject<ReplayStateException>(()=>WindowsCspEditingActions.BuildSchedules(session with {Sources=new Dictionary<string,SessionSnapshot> {
            [first.SessionId]=absentState,[second.SessionId]=second}},request,
            [new(a,null)],ReplayMode.Concentrated,"Work","work-uuid"),
            "A source recording without confirmed historical state must fail before any live action.");
        Check(ReferenceEquals(session.Source("live"),live) && ReferenceEquals(session.Source("old-first"),first),
            "Live and historical lookups must resolve their immutable native source snapshots.");
        return count;
    }
}
