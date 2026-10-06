using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CanvasLayerWatcher;

internal sealed record CaptureResult(string PngPath, string Metadata, string LayerName, long SaveDispatchedTicks, JsonElement ControlResponse);

internal static class Bridge
{
    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 clipfile-rs 读取组件");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("clipfile-rs：" + error.Trim());
        return output.Trim();
    }
}

internal static class SaveCapture
{
    internal readonly record struct Signature(long Length, long Modified);
    internal static Signature Stat(string path)
    { var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc.Ticks); }

    public static async Task<CaptureResult> CaptureAsync(string clip, string bridge, RecorderControlClient control, Size pixelSize, CaptureRequest request,
        Func<bool> stillValid, Func<bool> inContact, Action<string> status, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        // The request already owns its trigger-time layer and context. Do not
        // wait for another recognition result before dispatching the save.
        for (int attempt = 0; attempt < 100 && (!Native.InputIdle() || inContact()); attempt++) await Task.Delay(50, ct);
        if (!Native.InputIdle() || inContact()) throw new InvalidOperationException("用户仍在按键或笔接触中；本次保存已跳过");
        if (!stillValid()) throw new InvalidOperationException("会话或图层状态已改变；本次保存已跳过");
        Signature before = Stat(clip);
        bool finalCapture = request.TriggerKind == "recordingEnd";
        status(finalCapture ? "录制结束，调用 Recognizer 保存接口并解析最终图层…"
            : request.ViewPending ? "画布已存证，调用 Recognizer 保存接口，不等待视口解析…" : "画布视图已变化，调用 Recognizer 保存接口…");
        var controlResponse = await control.SaveClipAsync(clip, ct, request.TriggerTicks, activateCsp: finalCapture);
        string directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(directory);
        string unique = "trigger-" + request.TriggerTicks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        string snapshot = Path.Combine(directory, unique + ".clip"), png = Path.Combine(directory, unique + ".png");
        bool keepPng = false;
        try
        {
            await StableSnapshotAsync(clip, snapshot, before, ct, allowUnchanged: finalCapture);
            if (!stillValid()) throw new InvalidOperationException("监听会话已改变；旧结果已丢弃");
            string inspection = await Bridge.RunAsync(bridge, ["inspect", snapshot], token);
            using var inspected = JsonDocument.Parse(inspection);
            var storedLayer = LayerMapping.FromFile(inspected.RootElement, request.LayerName, request.Layer);
            string storedLayerName = storedLayer.Name;
            status($"文件已更新，读取图层「{storedLayerName}」（接口：{request.LayerName}，ID={storedLayer.Id}，UUID={storedLayer.Uuid}）…");
            // Freeze the trigger-time name; never substitute the saved document's later current layer.
            string metadata = await Bridge.RunAsync(bridge, ["export-id", snapshot, storedLayer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), png,
                pixelSize.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pixelSize.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)], token);
            if (!stillValid()) throw new InvalidOperationException("监听会话已改变；旧结果已丢弃");
            using var parsed = JsonDocument.Parse(metadata);
            if (J.Get(parsed.RootElement, "fullCanvas").ValueKind != JsonValueKind.True
                || J.Tick(parsed.RootElement, "width", 0) != pixelSize.Width || J.Tick(parsed.RootElement, "height", 0) != pixelSize.Height)
                throw new InvalidDataException("读取结果不是配置的完整画布像素尺寸");
            if (J.Tick(parsed.RootElement, "layerId", 0) != storedLayer.Id)
                throw new InvalidDataException("读取图层 ID 与文件对应结果不一致");
            keepPng = true;
            return new(png, metadata, storedLayerName, J.Tick(controlResponse, "saveInputDispatchedTicks", 0), controlResponse);
        }
        finally
        {
            TryDelete(snapshot);
            if (!keepPng) TryDelete(png);
        }
    }

    internal static string ResolveLayerName(JsonElement inspection, string observedName)
        => LayerMapping.FromFile(inspection, observedName, null).Name;

    internal static async Task StableSnapshotAsync(string source, string target, Signature before, CancellationToken token,
        bool allowUnchanged = false)
    {
        Signature? previous = null;
        var stable = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var now = Stat(source);
                if ((!allowUnchanged && now == before) || now != previous) { previous = now; stable.Restart(); }
                else if (stable.ElapsedMilliseconds >= (now == before ? 1500 : 700))
                {
                    // Refuse to copy while CSP has a writable handle; allow source replacement on next poll.
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > 4L * 1024 * 1024 * 1024) throw new InvalidDataException(".clip 超过 4 GB 快照上限");
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, token);
                    if (Stat(source) == now) return;
                    TryDelete(target); stable.Restart();
                }
            }
            catch (IOException) { stable.Restart(); }
            await Task.Delay(100, token);
        }
    }
    internal static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

internal static class Native
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    internal static void AllowRecorderForeground(int processId)
    { if (processId > 0) AllowSetForegroundWindow((uint)processId); }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    internal static bool InputIdle() => new[] { 1, 2, 4, 16, 17, 18, 32, 91, 92, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }
        .All(k => (GetAsyncKeyState(k) & 0x8000) == 0);
}
