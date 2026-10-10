namespace CanvasLayerWatcher;

internal sealed partial class MainWindow
{
    private sealed class RecordingEndActions(MainWindow owner, RecognizerMonitor monitor) : IRecordingEndActions
    {
        public Task<IReadOnlyList<CaptureRequest>> PrepareAsync(CancellationToken token)
            => monitor.PrepareRecordingEndLayersAsync(token);

        public async Task CaptureAndCommitAsync(IReadOnlyList<CaptureRequest> requests, CancellationToken token)
        {
            using var lease = await owner.CaptureCoordinator.EnterAsync(token);
            var context = requests[0].Context!;
            var history = owner._history ?? throw new InvalidOperationException("末尾图层快照历史尚未建立。");
            var completed = new HashSet<long>();
            bool Current() => monitor.SessionId == context.SessionId
                && requests.Where(request => !completed.Contains(request.Layer!.Id)).All(monitor.Current);
            foreach (var request in requests) owner._host?.CaptureQueued(request);
            IReadOnlyList<BoundaryLayerCapture> captures = [];
            try
            {
                captures = await SaveCapture.CaptureBoundaryAsync(owner._clip.Text,
                    Path.Combine(AppContext.BaseDirectory, "clip-layer-bridge.exe"),
                    new RecorderControlClient(context.ControlPipe, owner.WriteLog), context.PixelSize, requests,
                    Current, () => monitor.InContact, owner.Status, token);
                foreach (var capture in captures)
                {
                    var update = await Task.Run(() => history.Advance(capture.Result, capture.Request,
                        context.SessionId, context.Evidence.Snapshot, Current, token), token);
                    if (owner._host is not null) await owner._host.PacketCommittedAsync(capture.Request, update, token);
                    completed.Add(capture.Request.Layer!.Id);
                    monitor.Complete(capture.Request, true, history.EvidenceRetentionTicks);
                }
                owner.Status($"已保存 {captures.Count} 个图层的末尾差异，等待原生 footer 校验并封盘。");
            }
            catch
            {
                foreach (var request in requests)
                {
                    owner._host?.CaptureFailed(request, "末尾多层捕获或差异提交未完成。");
                    monitor.Complete(request, false);
                }
                throw;
            }
            finally { foreach (var capture in captures) SaveCapture.TryDelete(capture.Result.PngPath); }
        }
    }
}
