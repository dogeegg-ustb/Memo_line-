using System.Text.Json;

namespace CanvasLayerWatcher;

internal sealed record PreparedEditingBoundary(string SessionId,string RequestId,ulong AppendId,long TriggerTicks);
internal interface IEditingBoundaryActions
{
    string SessionId { get; }
    bool CaptureRequestsPaused { get; set; }
    Task DrainPendingAsync(CancellationToken token);
    Task<IDisposable> EnterCaptureAsync(CancellationToken token);
    Task<PreparedEditingBoundary> PrepareAsync(string requestId,CancellationToken token);
    Task CaptureAndCommitAsync(PreparedEditingBoundary boundary,CancellationToken token);
    void Status(string text);
}

/// <summary>Physical input reached the native queue between the boundary and its validation barrier, so the
/// save may not show the document at the boundary. Nothing of that boundary was committed.</summary>
internal sealed class EditingBoundaryInputException():Exception("建立边界期间检测到新的物理输入；请重新打开编辑面板。");

internal static class EditingBoundaryCoordinator
{
    internal const int Attempts=3;
    internal static async Task<PreparedEditingBoundary> RunAsync(IEditingBoundaryActions actions,string requestId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string session=actions.SessionId;
        if(string.IsNullOrWhiteSpace(session)||string.IsNullOrWhiteSpace(requestId))throw new InvalidDataException("编辑边界缺少录制会话或请求 ID。");
        bool previous=actions.CaptureRequestsPaused;
        actions.CaptureRequestsPaused=true;
        try
        {
            await actions.DrainPendingAsync(token);
            using var lease=await actions.EnterCaptureAsync(token);
            if(actions.SessionId!=session)throw new InvalidDataException("等待捕获期间录制会话已改变。");
            for(int attempt=1;;attempt++)
            {
                // A boundary interrupted by input (e.g. a key pressed into CSP while it saves) is rebuilt after that
                // input, like the recording baseline; the same request ID would return the old native marker.
                string id=attempt==1?requestId:$"{requestId}-retry{attempt}";
                var boundary=await actions.PrepareAsync(id,token);
                if(boundary.SessionId!=session||actions.SessionId!=session||boundary.RequestId!=id||boundary.AppendId==0||boundary.TriggerTicks<0)
                    throw new InvalidDataException("Recognizer 返回的编辑边界与本次会话不一致。");
                try{await actions.CaptureAndCommitAsync(boundary,token);}
                catch(EditingBoundaryInputException)when(attempt<Attempts&&!token.IsCancellationRequested)
                {
                    actions.Status($"建立编辑边界期间检测到物理输入，重新保存并捕获（{attempt+1}/{Attempts}）…");
                    continue;
                }
                if(actions.SessionId!=session)throw new InvalidDataException("捕获期间录制会话已改变。");
                return boundary;
            }
        }
        finally{actions.CaptureRequestsPaused=previous;}
    }
    internal static bool HasPhysicalInput(IEnumerable<JsonElement> frames,ulong afterAppendId,ulong throughAppendId)
        =>frames.Any(frame=>J.Get(frame,"appendId") is {ValueKind:JsonValueKind.Number} n&&n.TryGetUInt64(out var id)&&
            id>afterAppendId&&id<=throughAppendId&&J.Text(frame,"path")=="hardware");
}
