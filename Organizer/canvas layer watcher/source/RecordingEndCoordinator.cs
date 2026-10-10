namespace CanvasLayerWatcher;

internal interface IRecordingEndActions
{
    Task<IReadOnlyList<CaptureRequest>> PrepareAsync(CancellationToken token);
    Task CaptureAndCommitAsync(IReadOnlyList<CaptureRequest> requests, CancellationToken token);
}

// Uses the same multi-layer save/export path as explicit editing boundaries.
// The host still drains native shutdown and performs the final publication.
internal static class RecordingEndCoordinator
{
    internal static async Task RunAsync(IRecordingEndActions actions, CancellationToken token)
    {
        var requests = await actions.PrepareAsync(token);
        if (requests.Count == 0 || requests[0].TriggerKind != "recordingEnd"
            || requests.Any(request => request.Context is null || request.Layer is null))
            throw new InvalidDataException("末尾保存没有完整的图层身份和固定上下文。");
        await actions.CaptureAndCommitAsync(requests, token);
    }
}
