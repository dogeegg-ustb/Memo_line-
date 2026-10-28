using System.Diagnostics;

namespace MemolineDemo.Editing;

/// <summary>Stage timing for history loading; printed to stderr when MEMOLINE_PROFILE=1.</summary>
internal static class EditingProfiler
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("MEMOLINE_PROFILE") == "1";

    internal static IDisposable Stage(string name) => Enabled ? new Scope(name) : NoScope.Instance;

    private static readonly Dictionary<string, Stopwatch> Totals = [];
    /// <summary>Accumulates repeated work (one call per contact); see <see cref="Report"/>.</summary>
    internal static IDisposable Part(string name)
    {
        if (!Enabled) return NoScope.Instance;
        Stopwatch watch;
        lock (Totals) if (!Totals.TryGetValue(name, out watch!)) Totals.Add(name, watch = new());
        return new Accumulator(watch);
    }
    internal static void Report()
    {
        if (!Enabled) return;
        lock (Totals)
        {
            foreach (var (name, watch) in Totals) Console.Error.WriteLine($"[profile]   Σ {name}: {watch.Elapsed.TotalMilliseconds:0} ms");
            Totals.Clear();
        }
    }
    private sealed class Accumulator : IDisposable
    {
        private readonly Stopwatch _watch;
        internal Accumulator(Stopwatch watch) { _watch = watch; lock (watch) watch.Start(); }
        public void Dispose() { lock (_watch) _watch.Stop(); }
    }

    private sealed class Scope(string name) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly long _before = Process.GetCurrentProcess().PrivateMemorySize64;
        public void Dispose()
        {
            long after = Process.GetCurrentProcess().PrivateMemorySize64;
            Console.Error.WriteLine($"[profile] {name}: {_watch.Elapsed.TotalMilliseconds:0} ms, private {_before >> 20}→{after >> 20} MB, managed {GC.GetTotalMemory(false) >> 20} MB");
        }
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}
