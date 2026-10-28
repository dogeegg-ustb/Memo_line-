using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PacketReplay;

/// <summary>A colour shown on the virtual screen for CSP's screen colour picker to take.</summary>
internal interface IColorSwatch
{
    /// <summary>Shows <paramref name="rgb"/> and returns the swatch's screen rectangle (physical pixels).</summary>
    Task<Rectangle> ShowAsync(IReadOnlyList<int> rgb, CancellationToken token);
    Task HideAsync(CancellationToken token);
}

/// <summary>
/// The panel stash's showSwatch / hideSwatch (recognizer/virtual_panel_probe/README.md): it attaches the virtual screen
/// when nothing is stashed there yet and checks that the screen shows the exact colour before it answers.
/// </summary>
internal sealed class PanelStashSwatch(string pipeName = PanelStashSwatch.PipeName) : IColorSwatch
{
    internal const string PipeName = "MemoLine.PanelStash";
    // Attaching the virtual screen takes 1–2 s; the panel stash gives up on it after 15 s.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<Rectangle> ShowAsync(IReadOnlyList<int> rgb, CancellationToken token)
    {
        var reply = await RequestAsync(new { command = "showSwatch", rgb }, token);
        var rect = J.Array(J.Get(reply, "rect"))
            .Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : (int?)null).ToArray();
        if (rect.Length != 4 || rect.Any(v => v is null) || rect[2] <= rect[0] || rect[3] <= rect[1])
            throw new InvalidOperationException("面板收纳返回的色块位置无效。");
        return Rectangle.FromLTRB(rect[0]!.Value, rect[1]!.Value, rect[2]!.Value, rect[3]!.Value);
    }

    public Task HideAsync(CancellationToken token) => RequestAsync(new { command = "hideSwatch" }, token);

    /// <summary>Throws (InvalidOperationException) when the panel stash cannot be reached, before anything needs a swatch.</summary>
    internal Task CheckAsync(CancellationToken token) => RequestAsync(new { command = "getStatus" }, token);

    private async Task<JsonElement> RequestAsync(object request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(2000, deadline.Token); }
            catch (TimeoutException ex)
            { throw new InvalidOperationException("记艺线的面板收纳没有运行，无法在虚拟屏上显示色块。", ex); }
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            string line = await reader.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("面板收纳没有回复。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (J.Get(root, "success").ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("面板收纳：" + (J.Text(root, "error") ?? "请求失败"));
            return root.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException($"面板收纳 {Timeout.TotalSeconds:0} 秒内没有回复。"); }
        catch (IOException ex) { throw new InvalidOperationException("与面板收纳的连接中断：" + ex.Message, ex); }
    }
}
