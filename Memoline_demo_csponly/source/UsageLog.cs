using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemolineDemo;

/// <summary>
/// While a recorder runs, appends to logs/usage.log every 2 s: CPU (percent of one core) and working set of
/// Memoline, the recorder, its helpers, CSP and dwm, and how long CSP's UI thread took to answer a WM_NULL — a
/// stalled CSP shows there as a long round trip. Kept so drawing lag can be traced to a process afterwards. The last
/// column splits Memoline's own memory: GC heap in use / committed, and private bytes.
/// </summary>
internal static class UsageLog
{
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "logs", "usage.log");
    private static readonly string[] Names = ["MemolineDemo", "Memoline", "BehaviorRecognizer", "TransformHost",
        "python", "pythonw", "py", "CLIPStudioPaint", "dwm"];

    internal static async Task RunAsync(Process recorder)
    {
        var previous = new Dictionary<int, TimeSpan>();
        var clock = Stopwatch.StartNew();
        double last = 0;
        Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t--- recorder {recorder.Id} started, {Environment.ProcessorCount} logical processors");
        try
        {
            while (!recorder.HasExited)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                double now = clock.Elapsed.TotalSeconds, elapsed = now - last; last = now;
                var parts = new List<string>();
                IntPtr csp = IntPtr.Zero;
                foreach (var process in Process.GetProcesses().OrderBy(p => p.ProcessName))
                    using (process)
                        try
                        {
                            if (!Names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                            var cpu = process.TotalProcessorTime;
                            string share = previous.TryGetValue(process.Id, out var before) ? $"{(cpu - before).TotalSeconds / elapsed * 100:F0}%" : "-";
                            previous[process.Id] = cpu;
                            parts.Add($"{process.ProcessName}#{process.Id} {share} {process.WorkingSet64 >> 20}MB");
                            if (process.ProcessName == "CLIPStudioPaint" && process.MainWindowHandle != IntPtr.Zero) csp = process.MainWindowHandle;
                        }
                        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                string ui = "-";
                if (csp != IntPtr.Zero)
                {
                    var answer = Stopwatch.StartNew();
                    ui = SendMessageTimeout(csp, 0, IntPtr.Zero, IntPtr.Zero, 2, 2000, out _) == IntPtr.Zero ? "timeout" : $"{answer.Elapsed.TotalMilliseconds:F0}ms";
                }
                // This process's working set split into the managed heap (live objects / committed by the GC) and the rest.
                long committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
                using (var self = Process.GetCurrentProcess())
                    parts.Add($"self heap {GC.GetTotalMemory(false) >> 20}/{committed >> 20}MB private {self.PrivateMemorySize64 >> 20}MB");
                Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\tcspUi {ui}\t{string.Join("\t", parts)}");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException) { }
    }

    private static void Append(string line)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(File)!); System.IO.File.AppendAllText(File, line + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
}
