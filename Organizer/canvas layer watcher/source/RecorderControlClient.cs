using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CanvasLayerWatcher;

// The control pipe belongs to the same process as the selected realtime session.
internal sealed class RecorderControlClient(string pipeName, Action<string>? diagnostic = null)
{
    public static string PipeNameFor(int processId) => processId > 0
        ? $"memoline-recorder-input-{processId}" : throw new ArgumentOutOfRangeException(nameof(processId));

    public async Task<JsonElement> SendAsync(object request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        string line = JsonSerializer.Serialize(request);
        await writer.WriteLineAsync(line.AsMemory(), deadline.Token);
        diagnostic?.Invoke($"控制请求已发送：pipe={pipeName}，{line}");
        string response = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Recognizer 控制接口未返回响应");
        diagnostic?.Invoke("控制接口响应：" + response);
        if (response.Length > 65536) throw new InvalidDataException("Recognizer 控制响应超过大小上限");
        using var document = JsonDocument.Parse(response);
        if (J.Get(document.RootElement, "success").ValueKind != JsonValueKind.True)
            throw new IOException("Recognizer 控制请求失败：" + (J.Text(document.RootElement, "error") ?? "响应未确认 success"));
        return document.RootElement.Clone();
    }

    public Task<JsonElement> StatusAsync(CancellationToken token) => SendAsync(new { command = "getInputControlStatus" }, token);
    public Task<JsonElement> PrepareRecordingEndAsync(CancellationToken token)
        => SendAsync(new { command = "prepareRecordingEnd" }, token);
    public async Task<JsonElement> SaveClipAsync(string expectedClipPath, CancellationToken token, long? triggerTicks = null,
        bool activateCsp = false)
    {
        string id = "viewport-" + Guid.NewGuid().ToString("N");
        var response = await SendAsync(new { command = "requestClipSave", expectedClipPath, requestId = id, triggerTicks, activateCsp }, token);
        if (J.Text(response, "requestId") != id || J.Get(response, "saveInputDispatched").ValueKind != JsonValueKind.True
            || J.Tick(response, "saveInputDispatchedTicks", -1) < 0)
            throw new InvalidDataException("Recognizer 未确认本次保存输入已派发");
        return response;
    }
}
