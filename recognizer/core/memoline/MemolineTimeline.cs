using System.Text.Json;

namespace BehaviorRecognizer.Storage.Memoline;

/// <summary>Resolve appended results into slots reserved after causal input IDs.</summary>
public sealed class MemolineTimeline
{
    private readonly SortedDictionary<ulong,JsonElement> _hardware = [];
    private readonly Dictionary<string,JsonElement> _reservations = [];
    private readonly Dictionary<string,JsonElement> _results = [];

    public void Add(JsonElement frame)
    {
        string kind=frame.GetProperty("kind").GetString()!;
        if (frame.TryGetProperty("path",out var path) && path.GetString()=="hardware")
            _hardware.Add(frame.GetProperty("eventId").GetUInt64(),frame.Clone());
        else if (kind is "statePackageReserved" or "statePackageResult")
        {
            var data=frame.GetProperty("data");
            string id=data.GetProperty("packageId").GetString()!;
            var target=kind=="statePackageReserved"?_reservations:_results;
            if (!target.TryAdd(id,frame.Clone())) throw new InvalidDataException($"Duplicate package record: {id}");
        }
    }

    public void Write(Utf8JsonWriter json)
    {
        foreach (string id in _results.Keys)
            if (!_reservations.ContainsKey(id)) throw new InvalidDataException($"Result without reservation: {id}");
        var slots=_reservations.Values.GroupBy(f=>f.GetProperty("data").GetProperty("afterEventId").GetUInt64())
            .ToDictionary(g=>g.Key,g=>g.OrderBy(f=>f.GetProperty("data").TryGetProperty("reservationOrder",out var order)
                ?order.GetUInt64():f.GetProperty("appendId").GetUInt64()).ToArray());
        json.WriteBoolean("timelineAvailable",_reservations.Count>0);
        json.WritePropertyName("timeline");
        json.WriteStartArray();
        ulong replayThrough=_hardware.Count==0?0:_hardware.Keys.Max();
        void Packages(ulong anchor)
        {
            if (!slots.TryGetValue(anchor,out var frames)) return;
            foreach (var reservation in frames)
            {
                var data=reservation.GetProperty("data");
                string id=data.GetProperty("packageId").GetString()!;
                bool resolved=_results.TryGetValue(id,out var resultFrame);
                var result=resolved?resultFrame.GetProperty("data").GetProperty("result"):default;
                string status=resolved?result.GetProperty("status").GetString()!:"pending";
                if (status=="unchanged") continue;
                bool blocked=status is "pending" or "unknown" or "ambiguous" or "error";
                if (blocked) replayThrough=Math.Min(replayThrough,anchor);
                json.WriteStartObject();
                json.WriteString("kind",anchor==0?"initialState":"statePackage");
                json.WriteString("packageId",id);
                json.WriteNumber("afterEventId",anchor);
                json.WriteNumber("ticks",data.GetProperty("occurredTicks").GetInt64());
                json.WriteString("status",status);
                json.WriteBoolean("resolved",resolved);
                json.WriteBoolean("replayBlocked",blocked);
                if (resolved) { json.WritePropertyName("result"); result.WriteTo(json); }
                json.WriteEndObject();
            }
        }
        Packages(0);
        foreach (var (id,frame) in _hardware) { frame.WriteTo(json); Packages(id); }
        if (slots.Keys.Any(id=>id!=0&&!_hardware.ContainsKey(id))) throw new InvalidDataException("Package anchor input does not exist.");
        json.WriteEndArray();
        json.WriteNumber("pendingStatePackages",_reservations.Keys.Count(id=>!_results.ContainsKey(id)));
        json.WriteNumber("replayableThroughEventId",replayThrough);
    }
}
