using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class EditingCoordinatorChecks
{
    private sealed class Fake : IEditingTransaction
    {
        internal readonly List<string> Calls=[];
        internal bool TypedFailure,WrongPaste;
        public Task BeginAsync(CancellationToken token){Calls.Add("begin");return Task.CompletedTask;}
        public Task CompleteAsync(CancellationToken token){Calls.Add("complete");return Task.CompletedTask;}
        public Task AbortAsync(PlanIssue issue){Calls.Add("abort:"+issue.Code);return Task.CompletedTask;}
        public ValueTask DisposeAsync(){Calls.Add("dispose");return ValueTask.CompletedTask;}
        public Task<ImageRgba> FlushAsync(LayerKey layer,ImageRgba seed,IReadOnlyList<ReplayEvent> events,IReadOnlyDictionary<LayerKey,ImageRgba> refs,CancellationToken token)=>Task.FromResult(seed);
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer,ImageRgba expected,CancellationToken token)
        {Calls.Add("install");if(TypedFailure)throw new CspEditingException(new(PlanIssueCode.PasteNotExact,"fake paste error"));if(WrongPaste)expected.Bytes[0]=123;return Task.FromResult(expected);}
    }
    private static ReconstructionPlan Plan()
    {
        var l=new LayerKey("coordinator",1,"ink");var k=new EditableStrokeKey(l.SessionId,1);var m=new PixelMask(1,1);m.Set(0,0);
        var zero=new ImageRgba(1,1,new byte[4]);var now=new ImageRgba(1,1,new byte[]{10,0,0,255});
        var image=new DirtyImage(new(l,"p","i"),m,zero,now,[k]){W=m,Probe=m};
        var stroke=new EditableStroke(k,1,1,2,[],"pen",l,false){BeginEventId=1,WriteMask=m,Images=[image.Key]};
        var packet=new LayerPacket("p",l,2,null,now,[image],IsBoundary:true,BaselineSource:"recordingStart",AfterSnapshotId:"s0",NowSnapshotId:"s1",CanvasWidth:1,CanvasHeight:1);
        var index=new EditIndex(new Dictionary<LayerKey,LayerIndex>{{l,new(l,[packet],[stroke],true,[],Current:now)}},new Dictionary<EditableStrokeKey,EditableStroke>{{k,stroke}},new Dictionary<DirtyImageKey,DirtyImage>{{image.Key,image}},[]);
        return ReconstructionPlanner.Build(index,EditRequest.Event(l,m,new Dictionary<EditableStrokeKey,EventChange>{{k,new(Delete:true)}}));
    }
    internal static async Task<int> RunAsync()
    {
        int n=0;void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);n++;}
        var c=new EditingCoordinator();var f=new Fake();var result=await c.ExecuteAsync(Plan(),_=>f,CancellationToken.None);
        Check(result.Status==ReconstructionStatus.PixelVerified&&c.Status==EditingStatus.PixelVerified,"pure compose status");
        Check(f.Calls.SequenceEqual(new[]{"begin","install","complete","dispose"}),"transaction completion/disposal order");
        f=new Fake{TypedFailure=true};result=await c.ExecuteAsync(Plan(),_=>f,CancellationToken.None);
        Check(result.Issues.Single().Code==PlanIssueCode.PasteNotExact&&c.Status==EditingStatus.Failed,"typed failure remains original issue");
        Check(f.Calls.SequenceEqual(new[]{"begin","install","abort:PasteNotExact","dispose"}),"coordinator must abort typed executor failure before dispose");
        f=new Fake{WrongPaste=true};result=await c.ExecuteAsync(Plan(),_=>f,CancellationToken.None);
        Check(f.Calls.Contains("abort:PasteNotExact")&&!f.Calls.Contains("complete"),"failed engine result cannot complete native transaction");
        bool constructed=false;var invalid=Plan() with{Issues=[new(PlanIssueCode.NoBaseline,"fake missing baseline")]};
        result=await c.ExecuteAsync(invalid,_=>{constructed=true;return new Fake();},CancellationToken.None);
        Check(!constructed&&c.Status==EditingStatus.PlanUnconfirmed&&result.Status==ReconstructionStatus.Failed,"invalid plan has no CSP actions");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();try{await c.ExecuteAsync(Plan(),_=>{constructed=true;return new Fake();},cancelled.Token);throw new InvalidOperationException("pre-cancel accepted");}catch(OperationCanceledException){Check(!constructed,"cancellation before transaction construction");}
        return n;
    }
}
