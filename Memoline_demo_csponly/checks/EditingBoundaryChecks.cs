using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class EditingBoundaryChecks
{
    private sealed class Fake : IEditingBoundaryActions
    {
        internal readonly List<string> Calls=[];
        internal readonly List<string> Prepared=[], Statuses=[];
        internal bool Paused, FailCapture, CancelCapture, ChangeSession, BadMarker;
        internal int InputInterruptions;
        public string SessionId => ChangeSession ? "reconnected" : "session";
        public bool CaptureRequestsPaused { get=>Paused; set{Paused=value;Calls.Add(value?"pause":"resume");} }
        public Task DrainPendingAsync(CancellationToken token){token.ThrowIfCancellationRequested();Calls.Add("drain");return Task.CompletedTask;}
        public Task<IDisposable> EnterCaptureAsync(CancellationToken token){token.ThrowIfCancellationRequested();Calls.Add("lock");return Task.FromResult<IDisposable>(new Lease(()=>Calls.Add("unlock")));}
        public Task<PreparedEditingBoundary> PrepareAsync(string id,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add("prepare");Prepared.Add(id);return Task.FromResult(new PreparedEditingBoundary("session",id,BadMarker?0UL:42UL,100));}
        public Task CaptureAndCommitAsync(PreparedEditingBoundary marker,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add("capture");if(FailCapture)throw new InvalidDataException("capture failed");
         if(CancelCapture)throw new OperationCanceledException();
         if(InputInterruptions>0){InputInterruptions--;throw new EditingBoundaryInputException();}Calls.Add("commit");return Task.CompletedTask;}
        public void Abandon()=>Calls.Add("abandon");
        public void Status(string text)=>Statuses.Add(text);
        private sealed class Lease(Action release):IDisposable{public void Dispose()=>release();}
    }
    internal static async Task<int> RunAsync()
    {
        int n=0;void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);n++;}
        var f=new Fake();var boundary=await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);
        Check(boundary.AppendId==42&&boundary.TriggerTicks==100,"native marker must remain the reader limit");
        Check(f.Calls.SequenceEqual(new[]{"pause","drain","lock","prepare","capture","commit","unlock","resume"}),"drain precedes capture lock and commit precedes release");
        f=new Fake{FailCapture=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("capture error swallowed");}catch(InvalidDataException){Check(!f.Paused&&f.Calls.Contains("unlock")&&!f.Calls.Contains("commit")&&f.Calls.Contains("abandon"),"failure restores capture, releases lease and reservation");}
        f=new Fake{BadMarker=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("bad marker accepted");}catch(InvalidDataException){Check(!f.Calls.Contains("capture")&&!f.Paused&&f.Calls.Contains("abandon"),"invalid boundary stops before capture and releases its reservation");}
        f=new Fake{Paused=true};await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);Check(f.Paused,"preexisting pause policy is restored");
        f=new Fake{ChangeSession=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("session mismatch accepted");}catch(InvalidDataException){Check(!f.Calls.Contains("capture")&&!f.Paused,"foreign-session marker prevents capture");}
        f=new Fake{InputInterruptions=1};boundary=await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);
        Check(f.Prepared.SequenceEqual(new[]{"request","request-retry2"})&&boundary.RequestId=="request-retry2"&&f.Statuses.Count==1&&!f.Paused&&
            f.Calls.SequenceEqual(new[]{"pause","drain","lock","prepare","capture","abandon","prepare","capture","commit","unlock","resume"}),
            "input during the save rebuilds the boundary under a new request ID inside the same capture lease");
        f=new Fake{InputInterruptions=int.MaxValue};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("boundary interrupted on every attempt accepted");}
        catch(EditingBoundaryInputException){Check(f.Prepared.Count==EditingBoundaryCoordinator.Attempts&&!f.Calls.Contains("commit")&&!f.Paused&&f.Calls.Contains("unlock"),"input on every attempt fails after the attempt limit");}
        f=new Fake{FailCapture=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);}catch(InvalidDataException){Check(f.Prepared.Count==1&&f.Statuses.Count==0,"only physical input retries a boundary");}
        // Leaving the panel while the save runs cancels the capture; the reservation must not outlive it
        // or every later boundary is refused until the recording stops.
        f=new Fake{CancelCapture=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("capture cancel swallowed");}
        catch(OperationCanceledException){Check(f.Calls.Contains("abandon")&&!f.Paused&&f.Calls.Contains("unlock"),"cancelled capture releases its reservation");}
        static System.Text.Json.JsonElement Frame(string text)=>System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        var frames=new[]{Frame("{\"appendId\":41,\"path\":\"hardware\",\"kind\":\"penBegin\"}"),Frame("{\"appendId\":43,\"path\":\"hardware\",\"kind\":\"mouseDown\"}"),Frame("{\"appendId\":44,\"path\":\"state\",\"kind\":\"clipSaved\"}")};
        Check(EditingBoundaryCoordinator.HasPhysicalInput(frames,42,44),"physical input during save/export rejects boundary");
        Check(!EditingBoundaryCoordinator.HasPhysicalInput(frames,43,44),"earlier input and save state do not invalidate boundary");
        f=new Fake();using var ct=new CancellationTokenSource();ct.Cancel();try{await EditingBoundaryCoordinator.RunAsync(f,"request",ct.Token);throw new InvalidOperationException("cancel swallowed");}catch(OperationCanceledException){Check(!f.Paused&&!f.Calls.Contains("prepare"),"cancel has no native action");}
        return n;
    }
}
