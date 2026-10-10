using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class EditingBoundaryChecks
{
    private sealed class Fake : IEditingBoundaryActions
    {
        internal readonly List<string> Calls=[];
        internal bool Paused, FailCapture, ChangeSession, BadMarker;
        public string SessionId => ChangeSession ? "reconnected" : "session";
        public bool CaptureRequestsPaused { get=>Paused; set{Paused=value;Calls.Add(value?"pause":"resume");} }
        public Task DrainPendingAsync(CancellationToken token){token.ThrowIfCancellationRequested();Calls.Add("drain");return Task.CompletedTask;}
        public Task<IDisposable> EnterCaptureAsync(CancellationToken token){token.ThrowIfCancellationRequested();Calls.Add("lock");return Task.FromResult<IDisposable>(new Lease(()=>Calls.Add("unlock")));}
        public Task<PreparedEditingBoundary> PrepareAsync(string id,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add("prepare");return Task.FromResult(new PreparedEditingBoundary("session",id,BadMarker?0UL:42UL,100));}
        public Task CaptureAndCommitAsync(PreparedEditingBoundary marker,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add("capture");if(FailCapture)throw new InvalidDataException("capture failed");Calls.Add("commit");return Task.CompletedTask;}
        private sealed class Lease(Action release):IDisposable{public void Dispose()=>release();}
    }
    internal static async Task<int> RunAsync()
    {
        int n=0;void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);n++;}
        var f=new Fake();var boundary=await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);
        Check(boundary.AppendId==42&&boundary.TriggerTicks==100,"native marker must remain the reader limit");
        Check(f.Calls.SequenceEqual(new[]{"pause","drain","lock","prepare","capture","commit","unlock","resume"}),"drain precedes capture lock and commit precedes release");
        f=new Fake{FailCapture=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("capture error swallowed");}catch(InvalidDataException){Check(!f.Paused&&f.Calls.Contains("unlock")&&!f.Calls.Contains("commit"),"failure restores capture and releases lease");}
        f=new Fake{BadMarker=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("bad marker accepted");}catch(InvalidDataException){Check(!f.Calls.Contains("capture")&&!f.Paused,"invalid boundary stops before capture");}
        f=new Fake{Paused=true};await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);Check(f.Paused,"preexisting pause policy is restored");
        f=new Fake{ChangeSession=true};try{await EditingBoundaryCoordinator.RunAsync(f,"request",CancellationToken.None);throw new InvalidOperationException("session mismatch accepted");}catch(InvalidDataException){Check(!f.Calls.Contains("capture")&&!f.Paused,"foreign-session marker prevents capture");}
        static System.Text.Json.JsonElement Frame(string text)=>System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        var frames=new[]{Frame("{\"appendId\":41,\"path\":\"hardware\",\"kind\":\"penBegin\"}"),Frame("{\"appendId\":43,\"path\":\"hardware\",\"kind\":\"mouseDown\"}"),Frame("{\"appendId\":44,\"path\":\"state\",\"kind\":\"clipSaved\"}")};
        Check(EditingBoundaryCoordinator.HasPhysicalInput(frames,42,44),"physical input during save/export rejects boundary");
        Check(!EditingBoundaryCoordinator.HasPhysicalInput(frames,43,44),"earlier input and save state do not invalidate boundary");
        f=new Fake();using var ct=new CancellationTokenSource();ct.Cancel();try{await EditingBoundaryCoordinator.RunAsync(f,"request",ct.Token);throw new InvalidOperationException("cancel swallowed");}catch(OperationCanceledException){Check(!f.Paused&&!f.Calls.Contains("prepare"),"cancel has no native action");}
        return n;
    }
}
