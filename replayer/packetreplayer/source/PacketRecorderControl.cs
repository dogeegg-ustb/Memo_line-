using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PacketReplay;

internal sealed record ClipSaveDispatch(string RequestId, long DispatchedTicks, bool CompletionConfirmed);
internal interface IClipSaveControl
{
    Task<ClipSaveDispatch> SaveAsync(string clipPath, string requestId, long triggerTicks, CancellationToken token);
}
internal interface IPacketStateControl
{
    Task<IReadOnlyDictionary<string, JsonElement>> RequestAsync(string sessionId, string requestId, string[] modules, string? saveId, CancellationToken token);
}
internal sealed class PacketRecorderControl(int processId) : IClipSaveControl, IPacketStateControl
{
    internal string PipeName { get; } = processId > 0 ? "memoline-recorder-input-" + processId : throw new ArgumentOutOfRangeException(nameof(processId));
    public async Task<ClipSaveDispatch> SaveAsync(string clipPath, string requestId, long triggerTicks, CancellationToken token)
    {
        var response = await SendAsync(new { command = "requestClipSave", expectedClipPath = clipPath,
            requestId, triggerTicks, activateCsp = false }, TimeSpan.FromSeconds(5), token);
        return Parse(response, requestId, triggerTicks);
    }
    private async Task<JsonElement> SendAsync(object request, TimeSpan timeout, CancellationToken token)
    {
        try { return await ExchangeAsync(request, timeout, token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Memoline 主动控制请求超时。"); }
    }
    private async Task<JsonElement> ExchangeAsync(object request, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
        var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Memoline 保存接口未返回结果。");
        if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Memoline 控制接口返回结果过长。");
        using var json = JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }
    public async Task<IReadOnlyDictionary<string, JsonElement>> RequestAsync(string sessionId, string requestId, string[] modules, string? saveId, CancellationToken token)
    {
        var response = await SendAsync(new { command = "requestStates", requestId, sessionId, modules, saveId }, TimeSpan.FromSeconds(35), token);
        return ParseStates(response, sessionId, requestId, modules);
    }
    internal static IReadOnlyDictionary<string, JsonElement> ParseStates(JsonElement response, string sessionId, string requestId, string[] modules)
    {
        var results = J.Get(response, "results");
        if (J.Get(response, "success").ValueKind != JsonValueKind.True || J.Text(response, "sessionId") != sessionId
            || J.Text(response, "requestId") != requestId || results.ValueKind != JsonValueKind.Object)
            throw new IOException("Memoline 主动状态请求失败：" + (J.Text(response, "error") ?? "请求身份不匹配；请更新 Memoline 并重新录制。"));
        var values = results.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (values.Count != modules.Length || modules.Any(m => !values.ContainsKey(m))) throw new IOException("Memoline 本次请求未返回完整的模块结果。");
        return values;
    }
    internal static ClipSaveDispatch Parse(JsonElement response, string requestId, long triggerTicks)
    {
        var ticks = J.Get(response, "saveInputDispatchedTicks");
        if (J.Get(response, "success").ValueKind != JsonValueKind.True || J.Text(response, "requestId") != requestId
            || J.Get(response, "saveInputDispatched").ValueKind != JsonValueKind.True || ticks.ValueKind != JsonValueKind.Number
            || !ticks.TryGetInt64(out long dispatched) || dispatched < triggerTicks)
            throw new IOException("Memoline 未成功派发本次保存：" + (J.Text(response, "error") ?? "保存身份或派发时间不匹配"));
        return new(requestId, dispatched, J.Get(response, "saveCompletionConfirmed").ValueKind == JsonValueKind.True);
    }
}
internal static class PacketSessionPreparation
{
    internal static async Task PrepareAsync(IPacketStateFeed feed, IClipSaveControl control, string clipPath, long triggerTicks,
        Action<string> status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        feed.EnsureConnected();
        if (!Path.IsPathFullyQualified(clipPath) || !clipPath.EndsWith(".clip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前 Memoline 会话缺少有效的 .clip 路径；请重新初始化。");
        string id = "packet-replay-" + Guid.NewGuid().ToString("N");
        status("复现开始：自动保存当前 CSP 文档，主动索取本次图层结构与当前图层…");
        var dispatch = await control.SaveAsync(clipPath, id, triggerTicks, token);
        if (dispatch.RequestId != id) throw new IOException("保存接口返回了其他保存请求的结果。");
        await feed.RequestAsync(["clipState", "currentLayerState"], token, id);
        if (J.Text(feed.LayersEvidence, "saveId") != id || J.I64(feed.LayersEvidence, "observedTicks") < dispatch.DispatchedTicks)
            throw new IOException("主动请求的图层结构不属于本次保存。");
        var currentId = J.Get(J.Get(feed.Layers, "canvas"), "current_layer_id");
        if (currentId.ValueKind != JsonValueKind.Number || !currentId.TryGetInt64(out long currentLayerId))
            throw new InvalidOperationException("本次保存的图层结构缺少当前图层身份。");
        var currentLayers = J.Array(J.Get(feed.Layers, "layers")).Where(l => J.I64(l, "id") == currentLayerId).ToArray();
        if (currentLayers.Length != 1 || J.Text(currentLayers[0], "name") is not { Length: > 0 } currentName)
            throw new InvalidOperationException("本次保存的图层结构没有可唯一定位的当前图层。");
        if (feed.Layer is not { } layer || J.Name(layer) != J.Name(currentName))
            throw new InvalidOperationException("本次请求的当前图层与保存结构不一致，已停止。");
        status(dispatch.CompletionConfirmed ? "当前文档保存完成，图层结构和当前图层已确认。"
            : "保存已派发，图层结构和当前图层已确认（Memoline 未提供 CSP 落盘完成确认）。");
    }
}
