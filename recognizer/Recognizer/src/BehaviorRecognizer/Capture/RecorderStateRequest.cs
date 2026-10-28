using System.Collections.Concurrent;
using System.Text.Json;

namespace BehaviorRecognizer.Capture;

/// <param name="Background">Nobody waits for the result: the helper still captures at once, but analyzes one module at a
/// time and not while the pen is down, so the OCR burst does not slow CSP while the user draws.</param>
public sealed record RecorderStateRequest(string RequestId, string SessionId, string[] Modules, string? SaveId = null,
    int[]? BrushPanelRoi = null, bool Background = false)
{
    public bool HasValidPanelSelection() => BrushPanelRoi is null ||
        Modules.Contains("brushState") && BrushPanelRoi is [var x, var y, >= 8 and <= 8192, >= 8 and <= 8192] &&
        (long)x + BrushPanelRoi[2] <= int.MaxValue && (long)y + BrushPanelRoi[3] <= int.MaxValue;
}
public sealed record RecorderStateResponse(bool Success, string RequestId, string SessionId, long RequestedTicks,
    Dictionary<string, JsonElement> Results, string? Error = null);

/// <summary>Collects only results belonging to an explicitly requested capture package.</summary>
public sealed class RecorderStateRequests
{
    public static readonly string[] Modules = ["brushState", "subtoolState", "currentLayerState", "colorState", "colorInputState", "canvasViewState",
        "clipState", "shortcuts", "initializationConfiguration"];
    private sealed class Pending(RecorderStateRequest request, long ticks)
    {
        internal readonly RecorderStateRequest Request = request;
        internal readonly long Ticks = ticks;
        internal readonly Dictionary<string, JsonElement> Results = [];
        internal readonly TaskCompletionSource<RecorderStateResponse> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    public Task<RecorderStateResponse> Add(string packageId, RecorderStateRequest request, long ticks)
    {
        var pending = new Pending(request, ticks);
        if (!_pending.TryAdd(packageId, pending)) throw new InvalidOperationException("Duplicate request package.");
        return pending.Completion.Task;
    }
    public void Part(string packageId, string module, JsonElement data)
    {
        if (!_pending.TryGetValue(packageId, out var pending)) return;
        lock (pending)
        {
            if (!pending.Request.Modules.Contains(module) || pending.Results.ContainsKey(module)) return;
            pending.Results[module] = data.Clone();
            if (pending.Results.Count == pending.Request.Modules.Length)
                pending.Completion.TrySetResult(new(true, pending.Request.RequestId, pending.Request.SessionId, pending.Ticks, new(pending.Results)));
        }
    }
    public void Remove(string packageId) => _pending.TryRemove(packageId, out _);
    public void FailAll(string error)
    {
        foreach (var (package, pending) in _pending)
        {
            pending.Completion.TrySetException(new IOException(error));
            Remove(package);
        }
    }
}
