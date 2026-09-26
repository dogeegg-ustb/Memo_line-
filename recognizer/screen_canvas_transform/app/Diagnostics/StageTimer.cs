using System.Diagnostics;

namespace ScreenCanvasTransform.Diagnostics;

/// <summary>Wall time, including awaits; no image data is persisted.</summary>
internal sealed class StageTimer(string stage, string captureId = "") : IDisposable
{
    private readonly long _start = Stopwatch.GetTimestamp();
    public void Dispose() => LiveDebugLog.Write(
        $"[Perf] {stage}={Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F2}ms capture={captureId}");
}
