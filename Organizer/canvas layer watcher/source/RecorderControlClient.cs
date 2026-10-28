using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CanvasLayerWatcher;

// The control pipe belongs to the same process as the selected realtime session.
internal sealed class RecorderControlClient(string pipeName, Action<string>? diagnostic = null)
{
    public static string PipeNameFor(int processId) => processId > 0
        ? $"memoline-recorder-input-{processId}" : throw new ArgumentOutOfRangeException(nameof(processId));

    public async Task<JsonElement> SendAsync(object request, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        string line = JsonSerializer.Serialize(request);
        await writer.WriteLineAsync(line.AsMemory(), deadline.Token);
        diagnostic?.Invoke($"控制请求已发送：pipe={pipeName}，{line}");
        string response = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Recognizer 控制接口未返回响应");
        diagnostic?.Invoke("控制接口响应：" + response);
        if (response.Length > 4 * 1024 * 1024) throw new InvalidDataException("Recognizer 控制响应超过大小上限");
        using var document = JsonDocument.Parse(response);
        if (J.Get(document.RootElement, "success").ValueKind != JsonValueKind.True)
            throw new IOException("Recognizer 控制请求失败：" + (J.Text(document.RootElement, "error") ?? "响应未确认 success"));
        return document.RootElement.Clone();
    }

    public Task<JsonElement> StatusAsync(CancellationToken token) => SendAsync(new { command = "getInputControlStatus" }, token);
    public async Task WaitForRecordingReadyAsync(string sessionId, CancellationToken token)
    {
        while (true)
        {
            var response = await SendAsync(new { command = "getRecordingStatus" }, token);
            if (J.Text(response, "sessionId") != sessionId)
                throw new InvalidDataException("等待初始化期间 Recognizer 录制会话已改变");
            if (J.Get(response, "inputCaptureAvailable").ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Recognizer 当前会话缺少输入捕获接口，请重新构建程序。");
            var ready = J.Get(response, "recordingReady").ValueKind;
            if (ready == JsonValueKind.True) return;
            if (ready != JsonValueKind.False)
                throw new InvalidDataException("Recognizer 未提供有效的录制就绪状态");
            await Task.Delay(100, token);
        }
    }
    public Task<JsonElement> PrepareRecordingEndAsync(CancellationToken token)
        => SendAsync(new { command = "prepareRecordingEnd" }, token, TimeSpan.FromSeconds(35));
    public async Task<IReadOnlyDictionary<string, JsonElement>> RequestStatesAsync(string sessionId,
        string[] modules, CancellationToken token, string? saveId = null)
    {
        string requestId = "recording-end-state-" + Guid.NewGuid().ToString("N");
        var response = await SendAsync(new { command = "requestStates", requestId, sessionId, modules, saveId },
            token, TimeSpan.FromSeconds(35));
        return ParseStates(response, sessionId, requestId, modules);
    }
    internal static IReadOnlyDictionary<string, JsonElement> ParseStates(JsonElement response, string sessionId,
        string requestId, string[] modules)
    {
        var results = J.Get(response, "results");
        long requestedTicks = J.Tick(response, "requestedTicks", -1);
        if (J.Get(response, "success").ValueKind != JsonValueKind.True || J.Text(response, "sessionId") != sessionId
            || J.Text(response, "requestId") != requestId || requestedTicks < 0 || results.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("末尾状态请求的会话或请求身份不一致。");
        var values = results.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (values.Count != modules.Length || modules.Any(module => !values.ContainsKey(module)))
            throw new InvalidDataException("末尾状态请求未返回全部模块。");
        foreach (string module in modules)
        {
            var value = values[module];
            long observedTicks = J.Tick(J.Get(value, "evidence"), module == "clipState" ? "observedTicks" : "capturedTicks", -1);
            if (!J.Confirmed(value) || observedTicks < requestedTicks)
                throw new InvalidDataException($"末尾的 {module} 未取得本次已确认状态：" + J.Text(value, "error"));
        }
        if (values.TryGetValue("canvasViewState", out var view) && View.Parse(view) is null)
            throw new InvalidDataException("末尾画布视口矩阵不完整。");
        if (values.TryGetValue("currentLayerState", out var layer)
            && (J.Get(layer, "state").ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(J.Get(layer, "state").GetString())))
            throw new InvalidDataException("末尾当前图层名称未确认。");
        return values;
    }
    public Task<JsonElement> PrepareEditBoundaryAsync(string requestId, CancellationToken token)
        => SendAsync(new { command = "prepareEditBoundary", requestId }, token, TimeSpan.FromSeconds(35));
    public Task<JsonElement> BeginEditTransactionAsync(string transactionId, CancellationToken token)
        => SendAsync(new { command = "beginEditTransaction", transactionId }, token);
    public Task<JsonElement> EndEditTransactionAsync(string transactionId, string outcome, CancellationToken token)
        => SendAsync(new { command = "endEditTransaction", transactionId, outcome }, token);
    /// <summary>Records in the native stream which layer a user-made layer continues (see the recorder's layerLineage).</summary>
    public Task<JsonElement> RecordLayerLineageAsync(string kind, string layer, string target, object evidence, CancellationToken token)
        => SendAsync(new { command = "recordLayerLineage", kind, layer, target, evidence }, token);
    public async Task<JsonElement> SaveClipAsync(string expectedClipPath, CancellationToken token, long? triggerTicks = null,
        bool activateCsp = false)
    {
        string id = "viewport-" + Guid.NewGuid().ToString("N");
        JsonElement response;
        // The recorder's own saves around a layer operation keep its save worker busy for a few dozen milliseconds.
        for (int attempt = 0; ; attempt++)
            try { response = await SendAsync(new { command = "requestClipSave", expectedClipPath, requestId = id, triggerTicks, activateCsp }, token); break; }
            catch (IOException error) when (attempt < 40 && error.Message.Contains("busy", StringComparison.OrdinalIgnoreCase))
            { await Task.Delay(25, token); }
        if (J.Text(response, "requestId") != id || J.Get(response, "saveInputDispatched").ValueKind != JsonValueKind.True
            || J.Tick(response, "saveInputDispatchedTicks", -1) < 0)
            throw new InvalidDataException("Recognizer 未确认本次保存输入已派发");
        return response;
    }
}
