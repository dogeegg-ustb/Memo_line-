using PacketReplay;

namespace MemolineDemo.Editing;

/// <summary>Keeps the recorded dirty images after resolved undo/redo commands.
/// An undone contact stays attached to its original image and to the image that recorded its
/// reversal, so shared-contact closure replays (or composes) both together; replay skips it.</summary>
internal static class EffectiveUndoHistory
{
    internal static EditIndex Apply(SessionSnapshot session, EditIndex index, NativeUndoTimelineResult timeline)
    {
        var resolved = timeline.Commands.Where(c => c.State == NativeUndoCommandState.Resolved && c.Target is not null).ToArray();
        if (resolved.Length == 0) return index;
        var strokes = index.Strokes.ToDictionary();
        foreach (var key in strokes.Keys.ToArray())
            if (timeline.Pens.TryGetValue(key.BeginAppendId, out var pen) && pen.State == NativeUndoPenState.Undone)
                strokes[key] = strokes[key] with { IsUndone = true };
        var ticks = new Dictionary<ulong, long>();
        foreach (var frame in session.Frames)
            if (J.U64(frame, "appendId") is > 0 and var append) ticks.TryAdd(append, J.I64(frame, "ticks"));
        // image key -> extra events whose undo or redo changed that image's pixels
        var extra = new Dictionary<DirtyImageKey, HashSet<EditableStrokeKey>>();
        var handled = new HashSet<ulong>();
        foreach (var command in resolved)
        {
            var target = new EditableStrokeKey(session.SessionId, command.Target!.BeginAppendId);
            if (!strokes.TryGetValue(target, out var stroke) || !index.Layers.TryGetValue(stroke.Layer, out var layer)) continue;
            if (!ticks.TryGetValue(command.ResolutionAppendId, out long at) && !ticks.TryGetValue(command.InputAppendId, out at)) continue;
            var packet = layer.Packets.Where(p => !p.IsRecordingBaseline && p.TriggerTicks >= at).OrderBy(p => p.TriggerTicks).FirstOrDefault();
            if (packet is null) continue;
            handled.Add(command.InputAppendId);
            foreach (var image in packet.Images.Where(i => Intersects(i.Mask, stroke.WriteMask)))
            {
                if (!extra.TryGetValue(image.Key, out var set)) extra.Add(image.Key, set = []);
                set.Add(target);
            }
        }
        var images = index.Images.ToDictionary();
        foreach (var (key, added) in extra)
        {
            var image = images[key];
            var events = image.Events.Concat(added).Distinct().ToArray();
            var linked = events.Select(k => strokes[k]).ToArray();
            images[key] = image with
            {
                Events = events,
                IsCausal = linked.Any(s => s.IsCausal),
                // An image explained only by undo/redo now has the contacts that explain it.
                HasUnreplayableEvents = image.HasUnreplayableEvents && image.Events.Count > 0,
                W = Footprints.UnionBounded(linked.Select(s => s.WriteMask)),
                Probe = Footprints.UnionBounded(linked.Select(s => s.ProbeMask)),
                Impact = Footprints.UnionBounded(linked.Select(s => s.ImpactMask))
            };
        }
        foreach (var key in strokes.Keys.ToArray())
            strokes[key] = strokes[key] with { Images = images.Values.Where(i => i.Events.Contains(key)).Select(i => i.Key).ToArray() };
        var layers = index.Layers.ToDictionary(kv => kv.Key, kv => kv.Value with
        {
            Packets = kv.Value.Packets.Select(p => p with { Images = p.Images.Select(i => images[i.Key]).ToArray() }).ToArray(),
            Strokes = kv.Value.Strokes.Select(s => strokes[s.Key]).ToArray(),
            // Resolved commands are represented by IsUndone and image links; only unresolved ones still block.
            UndoRedo = kv.Value.UndoRedo.Where(c => c.InputAppendId == 0 || !handled.Contains(c.InputAppendId)).ToArray()
        });
        return new(layers, strokes, images, index.Issues);
    }

    private static bool Intersects(PixelMask a, PixelMask b)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        int x0 = Math.Max(a.Left, b.Left), y0 = Math.Max(a.Top, b.Top);
        int x1 = Math.Min(a.Left + a.Width, b.Left + b.Width), y1 = Math.Min(a.Top + a.Height, b.Top + b.Height);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (a.Contains(x, y) && b.Contains(x, y)) return true;
        return false;
    }
}
