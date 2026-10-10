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
}

internal static class EditingBoundaryCoordinator
{
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
            var boundary=await actions.PrepareAsync(requestId,token);
            if(boundary.SessionId!=session||actions.SessionId!=session||boundary.RequestId!=requestId||boundary.AppendId==0||boundary.TriggerTicks<0)
                throw new InvalidDataException("Recognizer 返回的编辑边界与本次会话不一致。");
            await actions.CaptureAndCommitAsync(boundary,token);
            if(actions.SessionId!=session)throw new InvalidDataException("捕获期间录制会话已改变。");
            return boundary;
        }
        finally{actions.CaptureRequestsPaused=previous;}
    }
    internal static bool HasPhysicalInput(IEnumerable<JsonElement> frames,ulong afterAppendId,ulong throughAppendId)
        =>frames.Any(frame=>J.Get(frame,"appendId") is {ValueKind:JsonValueKind.Number} n&&n.TryGetUInt64(out var id)&&
            id>afterAppendId&&id<=throughAppendId&&J.Text(frame,"path")=="hardware");
}
