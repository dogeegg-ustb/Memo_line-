using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace MemolineDemo;

/// <summary>
/// The panel stash (Recognizer/integration/panel_stash/panel_host.py) runs as long as Memoline does:
/// it moves CSP palettes dropped on a screen corner to the virtual screen, brings back the ones
/// stashed in an earlier run, and renders them on request (the recorder reads the History palette
/// through it after undo). It has no window of its own here, only the screen-edge drawer.
/// </summary>
internal sealed class PanelStashProcess : IAsyncDisposable
{
    internal const string PipeName = "MemoLine.PanelStash";
    private readonly Process _process;

    private PanelStashProcess(Process process) => _process = process;

    internal static PanelStashProcess? Start(string recorderRoot, Action<string> log)
    {
        string script = Path.Combine(recorderRoot, "integration", "panel_stash", "panel_host.py");
        if (!File.Exists(script)) { log("面板收纳未打包：" + script); return null; }
        string? configured = Environment.GetEnvironmentVariable("MEMOLINE_PYTHON");
        var start = new ProcessStartInfo(configured ?? "py")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)!
        };
        if (configured is null) start.ArgumentList.Add("-3");
        foreach (string argument in new[] { "-u", script, "--embedded", "--parent-pid", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1";
        try
        {
            var process = Process.Start(start) ?? throw new IOException("进程没有启动");
            log($"面板收纳已启动（pid {process.Id}）");
            return new PanelStashProcess(process);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log("面板收纳启动失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>Asks it to bring every palette back and detach the virtual screen, then waits for it.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                    await pipe.ConnectAsync(2000);
                    var bytes = Encoding.UTF8.GetBytes("{\"command\":\"shutdown\"}\n");
                    await pipe.WriteAsync(bytes);
                    await pipe.FlushAsync();
                    var reply = new byte[4096];
                    await pipe.ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await _process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally { _process.Dispose(); }
    }
}
