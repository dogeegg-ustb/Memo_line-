using PacketReplay;
using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>
/// Executed Memoline edits and the versions they make (user spec, 2026-10-20). Memoline's own events are not the
/// user's: the strokes a precise undo drew again so CSP's undo steps back through them are never selected again, and
/// once the user undoes them they never happened. An event edit makes a new version of the packets it rebuilt: its
/// Memoline-operation packet ("记艺线操作包"). A stroke it changed is that new version from then on — still the
/// user's, editable again with the change carried along; one it deleted is gone; one it replayed unchanged stays as
/// it was. Old-version images (every event in them changed by the edit) are kept for display only ("旧版本").
/// An edit the user undid in CSP (its original layer came back, see <see cref="LayerLineage"/>) is ignored.
/// </summary>
internal static class MemolineEdits
{
    internal sealed record Edit(string TransactionId, string Kind, int Order, long Ticks,
        IReadOnlyDictionary<EditableStrokeKey, EventChange> Changes, IReadOnlySet<EditableStrokeKey> Replayed,
        IReadOnlyList<(string Replaced, string Reproduction)> Replacements)
    {
        internal bool PreciseUndo => Kind == "preciseUndo";
    }

    /// <summary>The executed edits of the sessions, oldest first, without those the user undid.</summary>
    internal static IReadOnlyList<Edit> Read(IReadOnlyList<SessionSnapshot> sessions, LayerLineage lineage)
    {
        var edits = new List<Edit>();
        for (int order = 0; order < sessions.Count; order++)
        {
            var session = sessions[order];
            foreach (var frame in session.Frames.Where(f => J.Text(f, "kind") == "editingActionCompleted" &&
                         J.U64(f, "appendId") <= session.BoundaryAppendId).OrderBy(f => J.U64(f, "appendId")))
            {
                var data = J.Get(frame, "data");
                if (J.Text(data, "transactionId") is not { Length: > 0 } id || J.Text(data, "editKind") is not { Length: > 0 } kind) continue;
                var replacements = J.Array(J.Get(data, "layerReplacements"))
                    .Select(r => (Replaced: J.Text(r, "replaced") ?? "", Reproduction: J.Text(r, "reproduction") ?? ""))
                    .Where(r => r.Replaced.Length > 0 && r.Reproduction.Length > 0).ToArray();
                if (replacements.Any(r => lineage.Links.Any(l => l.Kind == LayerLineage.LinkKind.Replacement && l.Revoked &&
                        l.Predecessor == r.Replaced && l.Successor == r.Reproduction && l.Order == order))) continue;
                var changes = new Dictionary<EditableStrokeKey, EventChange>();
                foreach (var item in J.Array(J.Get(data, "changes")))
                    if (Key(item) is { } key) changes[key] = Change(item);
                var replayed = J.Array(J.Get(data, "replayed")).Select(Key).OfType<EditableStrokeKey>().ToHashSet();
                // Recordings before the list was kept: every kept change was replayed.
                if (replayed.Count == 0) replayed.UnionWith(changes.Where(c => !c.Value.Delete || kind == "preciseUndo").Select(c => c.Key));
                edits.Add(new(id, kind, order, J.I64(frame, "ticks"), changes, replayed, replacements));
            }
        }
        return edits;
    }

    private static EditableStrokeKey? Key(JsonElement item)
        => J.Text(item, "sessionId") is { Length: > 0 } session && J.U64(item, "beginAppendId") is > 0 and var append
            ? new EditableStrokeKey(session, append) : null;

    private static EventChange Change(JsonElement item)
    {
        var numbers = J.Get(item, "numbers").ValueKind == JsonValueKind.Object
            ? J.Get(item, "numbers").EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal)
            : new Dictionary<string, double>();
        var state = J.Get(item, "replacementBrushState");
        var rgb = J.Array(J.Get(item, "rgb")).Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n is >= 0 and <= 255 ? n : -1).ToArray();
        return new(J.Get(item, "delete").ValueKind == JsonValueKind.True, J.Text(item, "brushName"), numbers)
        { ReplacementBrushState = state.ValueKind == JsonValueKind.Object ? state.Clone() : default,
            Rgb = rgb.Length == 3 && rgb.All(v => v >= 0) ? rgb : null };
    }

    /// <summary>The change a stroke is replayed with when nothing new is asked of it: its current version's.</summary>
    internal static EventChange? Current(EditableStroke stroke)
        => !stroke.MemolineInjected && stroke.Version is { Change: { Delete: false } change } ? change : null;

    /// <summary>A change asked of a stroke's current version, as a change of its recording (what replay needs).</summary>
    internal static EventChange Effective(EditableStroke stroke, EventChange requested)
    {
        if (requested.Delete || Current(stroke) is not { } prior) return requested;
        // The colour is independent of the subtool: a version drawn in another colour keeps it unless asked again.
        var rgb = requested.Rgb ?? prior.Rgb;
        // Another subtool than the current version's: the new snapshot carries its own numbers.
        if (requested.BrushName is { } name && !BrushSnapshot.SameName(name, stroke.BrushName ?? "")) return requested with { Rgb = rgb };
        var numbers = prior.NumberValues.ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal);
        foreach (var (key, value) in requested.NumberValues) numbers[key] = value;
        return new(false, prior.BrushName, numbers)
        { ReplacementBrushState = requested.ReplacementBrushState.ValueKind == JsonValueKind.Object ? requested.ReplacementBrushState : prior.ReplacementBrushState,
            Rgb = rgb };
    }

    /// <summary>A stroke as an edit changed it: the subtool and numbers it is drawn with now, and where it writes.</summary>
    private static EditableStroke Fold(EditableStroke stroke, EventChange change, int width, int height)
    {
        bool newTool = ReconstructionPlanner.ChangesSubtool(stroke, change);
        var numbers = (newTool && BrushSnapshot.Parse(change.ReplacementBrushState) is { } target
                ? target.Numbers.ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal)
                : stroke.Numbers.ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal));
        foreach (var (key, value) in change.NumberValues) numbers[key] = value;
        // A new size has a recorded recipe to raster; a new subtool has none: its footprint is joined with the
        // operation image's pixels once those are known (see Apply).
        var write = stroke.WriteMask;
        if (!newTool && change.NumberValues.ContainsKey("brush_size") && width > 0 && height > 0)
            try
            {
                var resized = ReconstructionPlanner.OverrideWriteMask(stroke, change, new PixelMask(0, 0, width, height));
                if (!resized.IsEmpty) write = resized;
            }
            catch (InvalidDataException) { } // No frozen recipe: the recorded footprint stays, joined with the operation image below.
        return stroke with
        {
            BrushName = change.BrushName ?? stroke.BrushName, Numbers = numbers,
            BrushState = newTool && change.ReplacementBrushState.ValueKind == JsonValueKind.Object ? change.ReplacementBrushState : stroke.BrushState,
            // The recorded ink no longer matches a resized brush; the write footprint stands in for it.
            WriteMask = write, InkMask = ReferenceEquals(write, stroke.WriteMask) ? stroke.InkMask : new(0, 0, 0, 0),
        };
    }

    /// <summary>Applies the edits to a joined index (see the class summary).</summary>
    internal static EditIndex Apply(EditIndex index, IReadOnlyList<Edit> edits)
    {
        if (edits.Count == 0) return index;
        var strokes = index.Strokes.ToDictionary();
        var images = index.Images.ToDictionary();
        var operationPackets = index.Layers.Values.SelectMany(l => l.Packets).Where(p => p.Operation is not null).ToArray();
        foreach (var edit in edits.OrderBy(e => e.Order).ThenBy(e => e.Ticks))
        {
            var changed = edit.Changes.Keys.Where(strokes.ContainsKey).ToHashSet();
            foreach (var key in changed)
            {
                var stroke = strokes[key]; var change = edit.Changes[key];
                var version = new StrokeVersion(edit.TransactionId, edit.Kind, change);
                var (width, height) = Canvas(index, stroke);
                strokes[key] = edit.PreciseUndo
                    ? stroke with { Version = version, MemolineInjected = true, Eligible = false,
                        IneligibleReason = "记艺线精确撤销注入的事件：不是用户操作，不再参与选择。" }
                    : change.Delete ? stroke with { Version = version, IsUndone = true }
                    : Fold(stroke, change, width, height) with { Version = version };
            }
            // The operation packet's images show what the edit changed: their events are what it drew again.
            var drawn = edit.Replayed.Concat(edit.PreciseUndo ? changed : []).Where(strokes.ContainsKey).ToHashSet();
            foreach (var packet in operationPackets.Where(p => p.Operation!.TransactionId == edit.TransactionId))
                foreach (var image in packet.Images.Where(i => images.ContainsKey(i.Key)))
                {
                    var events = drawn.Where(k => strokes[k].Layer == image.Key.Layer)
                        .OrderBy(k => strokes[k].SessionOrder).ThenBy(k => strokes[k].BeginEventId).ToArray();
                    images[image.Key] = images[image.Key] with
                    {
                        Events = events, Operation = packet.Operation, IsCausal = true, UnknownHistoryInterval = false,
                        HasUnreplayableEvents = events.Length == 0 || events.Any(k => !strokes[k].Eligible || strokes[k].MemolineInjected),
                        W = Bounds(image.Mask), Probe = Bounds(image.Mask), Impact = image.Mask.Clone(),
                    };
                    foreach (var key in events)
                    {
                        var stroke = strokes[key];
                        // A changed stroke whose new footprint is not known from a recipe also writes where the edit changed pixels.
                        bool unknownFootprint = stroke.Version?.TransactionId == edit.TransactionId && !stroke.MemolineInjected &&
                            stroke.Version.Change is { Delete: false } c && (c.BrushName is not null ||
                                c.NumberValues.ContainsKey("brush_size") && stroke.FrozenCoverageSettings is null && stroke.CoverageLabels.All(l => l.CoverageSettings is null));
                        var write = unknownFootprint ? Union(stroke.WriteMask, image.Mask) : stroke.WriteMask;
                        strokes[key] = stroke with { Images = stroke.Images.Append(image.Key).Distinct().ToArray(), WriteMask = write,
                            InkMask = unknownFootprint ? new(0, 0, 0, 0) : stroke.InkMask };
                    }
                }
            // Old versions: earlier images holding nothing but strokes this edit changed. Only an edit with an operation
            // packet has a new version to show instead (recordings before 2026-10-20 have none).
            var own = operationPackets.Where(p => p.Operation!.TransactionId == edit.TransactionId).ToArray();
            if (own.Length == 0) continue;
            var at = own.Select(p => (p.SessionOrder, p.TriggerTicks)).Min();
            foreach (var layer in index.Layers.Values)
                foreach (var packet in layer.Packets.Where(p => p.Operation is null && (p.SessionOrder, p.TriggerTicks).CompareTo(at) < 0))
                    foreach (var image in packet.Images)
                        if (images.TryGetValue(image.Key, out var current) && current.SupersededBy is null &&
                            current.Events.Count > 0 && current.Events.All(changed.Contains))
                            images[image.Key] = current with { SupersededBy = edit.TransactionId };
        }
        var layers = index.Layers.ToDictionary(kv => kv.Key, kv => kv.Value with
        {
            Packets = kv.Value.Packets.Select(p => p with { Images = p.Images.Select(i => images.GetValueOrDefault(i.Key) ?? i).ToArray() }).ToArray(),
            Strokes = kv.Value.Strokes.Select(s => strokes.GetValueOrDefault(s.Key) ?? s).ToArray(),
        });
        return index with { Layers = layers, Strokes = strokes, Images = images };
    }

    private static (int Width, int Height) Canvas(EditIndex index, EditableStroke stroke)
        => index.Layers.TryGetValue(stroke.Layer, out var layer) && (layer.Current ?? layer.Packets.LastOrDefault()?.Current) is { } current
            ? (current.Width, current.Height)
            : index.Layers.TryGetValue(stroke.Layer, out var other) && other.Packets.Count > 0 ? (other.Packets[0].CanvasWidth, other.Packets[0].CanvasHeight) : (0, 0);

    private static PixelMask Bounds(PixelMask mask) => PixelMask.Filled(mask.Left, mask.Top, mask.Width, mask.Height);

    private static PixelMask Union(PixelMask a, PixelMask b) => Footprints.UnionBounded([a, b]);
}
