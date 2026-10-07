using System.Text.Json;

namespace CanvasLayerWatcher;

internal sealed record BaselineBarrier(string SessionId, string RequestId, ulong AppendId, long TriggerTicks)
{
    internal static BaselineBarrier FromResponse(JsonElement response, string requestId)
    {
        var append = J.Get(response, "appendId");
        if (J.Get(response, "success").ValueKind != JsonValueKind.True || J.Text(response, "requestId") != requestId
            || J.Text(response, "sessionId") is not { Length: > 0 } session
            || append.ValueKind != JsonValueKind.Number || !append.TryGetUInt64(out ulong appendId) || appendId == 0
            || J.Tick(response, "triggerTicks", -1) < 0)
            throw new InvalidDataException("Recognizer 未确认开始 baseline 的队列边界");
        return new(session, requestId, appendId, J.Tick(response, "triggerTicks", -1));
    }
}

internal static class RecordingBaselineInitialization
{
    internal static async Task RunAsync(string sessionId,
        Func<string, CancellationToken, Task<BaselineBarrier>> barrier,
        Func<CancellationToken, Task<BaselineCaptureResult>> capture,
        Func<IReadOnlyList<JsonElement>> readOne,
        Action<BaselineCaptureResult, long> commit, Action<string> status, CancellationToken token)
    {
        ulong cursor = 0;
        async Task<bool> ReadThroughAsync(BaselineBarrier boundary)
        {
            if (boundary.SessionId != sessionId || boundary.AppendId <= cursor || boundary.TriggerTicks < 0)
                throw new InvalidDataException("开始 baseline 的边界会话或 appendId 不一致");
            bool penBegin = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (cursor < boundary.AppendId)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var frames = readOne();
                if (frames.Count > 1) throw new InvalidOperationException("baseline 边界读取必须每次只消费一个机械帧");
                if (frames.Count == 0) { await Task.Delay(20, deadline.Token); continue; }
                var frame = frames[0]; var value = J.Get(frame, "appendId");
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out ulong appendId))
                {
                    if (J.Text(frame, "kind") == "header") continue;
                    throw new InvalidDataException("机械流帧缺少 appendId");
                }
                if (appendId != cursor + 1 || appendId > boundary.AppendId)
                    throw new InvalidDataException("baseline 机械流 appendId 不连续");
                cursor = appendId;
                penBegin |= J.Text(frame, "kind") == "penBegin";
                if (cursor == boundary.AppendId && (J.Text(frame, "kind") != "editBoundaryRequested"
                    || J.Tick(frame, "ticks", -1) != boundary.TriggerTicks))
                    throw new InvalidDataException("baseline 机械帧与已刷盘的边界不一致");
            }
            return penBegin;
        }
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = await barrier("baseline-before-" + Guid.NewGuid().ToString("N"), token);
            await ReadThroughAsync(before);
            BaselineCaptureResult? exported = null;
            try
            {
                exported = await capture(token);
                var after = await barrier("baseline-after-" + Guid.NewGuid().ToString("N"), token);
                bool penDuringSave = await ReadThroughAsync(after);
                if (exported.SaveDispatchedTicks < before.TriggerTicks || exported.SaveDispatchedTicks > after.TriggerTicks)
                    throw new InvalidDataException("baseline 保存时间不在两个原生队列边界之间");
                if (penDuringSave)
                {
                    status($"录制开始期间检测到 penBegin，重新建立 baseline（{attempt}/3）…");
                    continue;
                }
                token.ThrowIfCancellationRequested();
                commit(exported, exported.SaveDispatchedTicks);
                status($"录制开始 baseline 已写入：layers={exported.Layers.Count}，attempt={attempt}，triggerTicks={exported.SaveDispatchedTicks}，beforeAppendId={before.AppendId}，afterAppendId={after.AppendId}");
                return;
            }
            finally
            {
                if (exported is not null) foreach (var layer in exported.Layers) SaveCapture.TryDelete(layer.PngPath);
            }
        }
        throw new InvalidOperationException("录制开始 baseline 连续 3 次被笔输入打断；已停止初始化，请抬笔后重试");
    }
}
