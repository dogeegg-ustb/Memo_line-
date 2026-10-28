using PacketReplay;
using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>
/// Which layer's history each layer continues. Only three records join layers: a Memoline edit replacing a layer
/// with its reproduction, a paste of Memoline's recycle bin copy of a replaced layer, and a merge into another
/// layer. A layer the user deleted has no successor; a layer the user created has no predecessor.
/// </summary>
internal sealed class LayerLineage
{
    internal enum LinkKind { Replacement, RecyclePaste, Merged }

    /// <param name="Order">Session order of the record; <paramref name="Ticks"/> is on that session's clock.</param>
    internal sealed record Link(LinkKind Kind, string Predecessor, string Successor, int Order, long Ticks)
    {
        /// <summary>The predecessor showed up again afterwards (the user undid the edit or the merge in CSP):
        /// it is itself again, and the successor is a layer of its own.</summary>
        internal bool Revoked { get; init; }
    }

    private readonly ILookup<string, Link> _heirs;
    internal IReadOnlyList<Link> Links { get; }
    /// <summary>Layer UUIDs present in CSP at the current editing boundary.</summary>
    internal IReadOnlySet<string> Alive { get; }
    /// <summary>Merges still in effect, oldest first.</summary>
    internal IReadOnlyList<Link> Merges => Links.Where(l => l.Kind == LinkKind.Merged && !l.Revoked).ToArray();

    private LayerLineage(IReadOnlyList<Link> links, IReadOnlySet<string> alive)
    {
        Links = links; Alive = alive;
        _heirs = links.Where(l => l.Kind != LinkKind.Merged && !l.Revoked)
            .OrderByDescending(l => l.Order).ThenByDescending(l => l.Ticks).ToLookup(l => l.Predecessor, StringComparer.Ordinal);
    }

    internal static LayerLineage Build(IReadOnlyList<SessionSnapshot> sessions)
    {
        var links = new List<Link>();
        var seen = new List<(int Order, long Ticks, string Uuid)>();
        for (int order = 0; order < sessions.Count; order++)
        {
            var session = sessions[order];
            foreach (var frame in session.Frames.Where(f => J.U64(f, "appendId") <= session.BoundaryAppendId).OrderBy(f => J.U64(f, "appendId")))
            {
                long ticks = J.I64(frame, "ticks");
                var data = J.Get(frame, "data");
                switch (J.Text(frame, "kind"))
                {
                    case "editingActionCompleted":
                        foreach (var item in J.Array(J.Get(data, "layerReplacements")))
                            if (J.Text(item, "replaced") is { Length: > 0 } old && J.Text(item, "reproduction") is { Length: > 0 } now && old != now)
                                links.Add(new(LinkKind.Replacement, old, now, order, ticks));
                        break;
                    case "layerLineage":
                        // The watcher records it after the save that showed the change; that save's trigger is the moment.
                        long at = J.I64(J.Get(data, "evidence"), "triggerTicks", ticks);
                        if (J.Text(data, "layer") is not { Length: > 0 } layer || J.Text(data, "target") is not { Length: > 0 } target || layer == target) break;
                        if (J.Text(data, "kind") == "recyclePaste") links.Add(new(LinkKind.RecyclePaste, layer, target, order, at));
                        else if (J.Text(data, "kind") == "merged") links.Add(new(LinkKind.Merged, layer, target, order, at));
                        break;
                    case "coreStateUpdated" when J.Text(data, "module") == "clipState" && J.Confirmed(data) && SaveBound(data):
                        // A save's parse is published after the save; the save itself is when the layers were there.
                        long saved = J.I64(J.Get(data, "evidence"), "triggerTicks", ticks);
                        foreach (var uuid in Uuids(data)) seen.Add((order, saved, uuid));
                        break;
                }
            }
            foreach (var packet in session.Packets) seen.Add((order, packet.TriggerTicks, packet.Layer.Uuid));
        }
        var alive = AliveAt(sessions[^1]);
        foreach (var uuid in alive) seen.Add((sessions.Count, 0, uuid));
        var resolved = links.Select(link => link with { Revoked = seen.Any(s => s.Uuid == link.Predecessor &&
            (s.Order, s.Ticks).CompareTo((link.Order, link.Ticks)) > 0) }).ToArray();
        return new(resolved, alive);
    }

    /// <summary>
    /// Layers listed by the editing boundary's own save when the session has one; else by the session's last confirmed
    /// save before its boundary; without one, the captured layers. Only parses of a save count: a parse of the file on
    /// disk without one (evidence.saveBound=false) shows CSP as last saved, not as now — on 2026-10-20 such a read after
    /// the user's Ctrl+Z still listed the reproduction and not the original it had brought back.
    /// </summary>
    internal static IReadOnlySet<string> AliveAt(SessionSnapshot session)
    {
        if (session.BoundaryLayers is { } boundary) return boundary;
        var last = session.Frames.Where(f => J.U64(f, "appendId") <= session.BoundaryAppendId && J.Text(f, "kind") == "coreStateUpdated" &&
                J.Text(J.Get(f, "data"), "module") == "clipState" && J.Confirmed(J.Get(f, "data")) && SaveBound(J.Get(f, "data")) &&
                Uuids(J.Get(f, "data")).Any())
            .OrderBy(f => J.U64(f, "appendId")).Select(f => J.Get(f, "data")).LastOrDefault();
        return (last.ValueKind == JsonValueKind.Object ? Uuids(last) : session.Packets.Select(p => p.Layer.Uuid))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The layer UUIDs a saved structure (a clipState's <c>state</c>) lists.</summary>
    internal static IReadOnlySet<string> Layers(JsonElement structure) =>
        J.Array(J.Get(structure, "layers")).Select(l => J.Text(l, "uuid")).OfType<string>().Where(u => u.Length > 0).ToHashSet(StringComparer.Ordinal);

    // Recordings before 2026-10-20 do not say; their parses are taken as they were.
    private static bool SaveBound(JsonElement clipState) => J.Get(J.Get(clipState, "evidence"), "saveBound").ValueKind != JsonValueKind.False;

    /// <summary>
    /// A reproduction the user undid in CSP: its edit was revoked (the original came back) and it is gone. Memoline made
    /// it, the user did not want it, so it never existed — it is no deleted layer of the user's.
    /// </summary>
    internal bool Undone(string uuid) => !Alive.Contains(uuid) &&
        Links.Any(l => l.Kind == LinkKind.Replacement && l.Revoked && l.Successor == uuid) &&
        !Links.Any(l => l.Kind == LinkKind.Replacement && !l.Revoked && l.Successor == uuid);

    private static IEnumerable<string> Uuids(JsonElement clipState) => Layers(J.Get(clipState, "state"));

    /// <summary>
    /// The layer whose history <paramref name="uuid"/> is part of: itself while it is in CSP, else the newest successor
    /// that leads to a layer in CSP, else (no living successor) the newest successor's owner, else itself.
    /// Merges are not followed: a merged layer's history is folded into its target separately.
    /// </summary>
    internal string Owner(string uuid) => Owner(uuid, new HashSet<string>(StringComparer.Ordinal));

    private string Owner(string uuid, HashSet<string> visiting)
    {
        if (Alive.Contains(uuid) || !visiting.Add(uuid)) return uuid;
        try
        {
            var heirs = _heirs[uuid].ToArray();
            foreach (var link in heirs)
                if (Owner(link.Successor, visiting) is var owner && Alive.Contains(owner)) return owner;
            return heirs.Length > 0 ? Owner(heirs[0].Successor, visiting) : uuid;
        }
        finally { visiting.Remove(uuid); }
    }

    /// <summary>True when the session holds a record that joins layers, or captured a layer it no longer has.</summary>
    internal static bool Joins(SessionSnapshot session)
    {
        if (session.Frames.Any(f => J.Text(f, "kind") is "editingActionCompleted" or "layerLineage" &&
                J.U64(f, "appendId") <= session.BoundaryAppendId)) return true;
        var alive = AliveAt(session);
        return session.Packets.Any(p => !alive.Contains(p.Layer.Uuid));
    }
}
