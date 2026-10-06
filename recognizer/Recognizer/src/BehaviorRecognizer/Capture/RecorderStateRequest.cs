using System.Collections.Concurrent;
using System.Text.Json;

namespace BehaviorRecognizer.Capture;

public sealed record RecorderStateRequest(string RequestId, string SessionId, string[] Modules, string? SaveId = null);
public sealed record RecorderStateResponse(bool Success, string RequestId, string SessionId, long RequestedTicks,
    Dictionary<string, JsonElement> Results, string? Error = null);

/// <summary>Collects only results belonging to an explicitly requested capture package.</summary>
public sealed class RecorderStateRequests
{
    public static readonly string[] Modules = ["brushState", "subtoolState", "currentLayerState", "colorState", "canvasViewState",
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
