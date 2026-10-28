using System.Diagnostics;

namespace CanvasLayerWatcher;

/// <summary>Step durations of the watcher's own work (dirty-matrix packets, baselines), appended to
/// logs/edit-timing.log next to Memoline's editing steps.</summary>
internal static class StepTiming
{
    private static readonly object Sync = new();
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "logs", "edit-timing.log");

    internal static Step Start(string scope, string name) => new(scope, name, Stopwatch.GetTimestamp());

    internal readonly record struct Step(string Scope, string Name, long Started) : IDisposable
    {
        public void Dispose()
        {
            double milliseconds = Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                    System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{Scope}\t{Name}\t{milliseconds:F0} ms{Environment.NewLine}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
