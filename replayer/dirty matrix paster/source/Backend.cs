using System.Diagnostics;
using System.Text.Json;

namespace DirtyMatrixPaster;

internal sealed class ExportResult : IDisposable
{
    public JsonDocument Document { get; }
    public string Json { get; }
    public JsonElement Root => Document.RootElement;
    public bool Ready => String("status") == "ready";
    public string String(string name) => Root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    public int Number(string name) => Root.GetProperty(name).GetInt32();
    public ExportResult(string json) { Json = json; Document = JsonDocument.Parse(json); }
    public void Dispose() => Document.Dispose();
}

internal static class Backend
{
    internal static string Root => AppContext.BaseDirectory;
    internal static string DefaultPacketsRoot => Path.GetFullPath(Path.Combine(Root, "../../../../Organizer/canvas layer watcher/publish/win-x64/layer-diffs/packets"));
    internal static readonly string[] ExampleIds = [
        "trigger-00000000000666281525-7f616539", "trigger-00000000000784015010-837b22a9",
        "trigger-00000000000877612019-68287a8c", "trigger-00000000000917275037-35d50518",
        "trigger-00000000000955782253-c0ef3076", "trigger-00000000001726040625-3bc00438",
        "trigger-00000000002075039984-4a873da8"
    ];
    internal static async Task<ExportResult> ExportAsync(string input, string? output = null)
        => await RunAsync([Path.GetFullPath(input),OutputDirectory(output)]);
    private static string OutputDirectory(string? output) => Path.GetFullPath(output ?? Path.Combine(Root, "artifacts", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
    internal static async Task<ExportResult> ComposeAsync(IReadOnlyList<string> inputs, string? output = null)
    {
        if (inputs.Count == 0) throw new ArgumentException("请选择至少一个脏矩阵包");
        string directory=OutputDirectory(output); Directory.CreateDirectory(directory);
        string request=Path.Combine(directory,"composition-request.json");
        await File.WriteAllTextAsync(request,JsonSerializer.Serialize(new { inputs=inputs.Select(Path.GetFullPath).ToArray(), outputDirectory=directory }));
        return await RunAsync(["--compose",request]);
    }
    private static async Task<ExportResult> RunAsync(string[] arguments)
    {
        var engine = Path.Combine(Root, "backend", "engine.mjs");
        if (!File.Exists(engine)) throw new FileNotFoundException("找不到合成后端，请运行 Build.ps1", engine);
        var node = Path.Combine(Root, "runtime", "node.exe");
        var info = new ProcessStartInfo(File.Exists(node) ? node : "node.exe")
        { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
          StandardErrorEncoding = System.Text.Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--max-old-space-size=2048"); info.ArgumentList.Add(engine);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动合成后端");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("合成后端超过 3 分钟未完成"); }
        var json = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "合成后端失败" : error.Trim());
        return new ExportResult(json);
    }
}
