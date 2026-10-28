using System.Globalization;

namespace MemolineDemo.Editing;

/// <summary>Why a stroke the region did not select is replayed too: it inked <see cref="Image"/>, a dirty image the
/// precise undo replays whole.</summary>
internal sealed record PreciseUndoClosureReason(EditableStrokeKey Stroke, DirtyImageKey Image, string Message);

internal sealed record PreciseUndoSelection(LayerKey? CurrentLayer,
    IReadOnlyList<EditableStroke> Selected, int BaseCount, int CandidateCount,
    IReadOnlyList<DirtyImageKey> Images,
    IReadOnlyList<PreciseUndoClosureReason> Reasons, IReadOnlyList<PlanIssue> Issues)
{
    public int ClosureCount => Selected.Count - BaseCount;
    public bool IsValid => Issues.Count == 0 && Selected.Count > 0;
    /// <summary>The strokes the region selected: what CSP's undo is to step back through.</summary>
    public IReadOnlyList<EditableStroke> Base => Selected.Where(s => Reasons.All(r => r.Stroke != s.Key)).ToArray();
    /// <summary>The other strokes of the replayed images; replayed unchanged, before the selection.</summary>
    public IReadOnlyList<EditableStroke> Closure => Selected.Where(s => Reasons.Any(r => r.Stroke == s.Key)).ToArray();
    /// <summary>Deletes the selection; the closure rides along as <see cref="EditRequest.Closure"/>.</summary>
    public EditRequest Request(PixelMask region) => new(EditKind.PreciseUndo, CurrentLayer, region,
        Base.ToDictionary(s => s.Key, _ => new EventChange(Delete: true))) { Closure = Closure.Select(s => s.Key).ToHashSet() };
    public string CountDisplay => ClosureCount == 0
        ? BaseCount.ToString(CultureInfo.InvariantCulture)
        : $"{BaseCount.ToString(CultureInfo.InvariantCulture)} + {ClosureCount.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The dirty images a precise undo replays and the strokes it replays in them (see <see cref="PreciseUndoSelector.Involve"/>).</summary>
internal sealed record PreciseUndoInvolvement(IReadOnlySet<DirtyImageKey> Images, IReadOnlyList<EditableStroke> Closure,
    IReadOnlyList<PreciseUndoClosureReason> Reasons, IReadOnlyList<PlanIssue> Issues);

/// <summary>
/// Selects by undo depth: the latest strokes whose ink meets the region. Precise undo works on whole dirty images, like
/// the dirty matrix and its snapshot (user, 2026-10-26: "只把嘴巴和那个点直接复制合成出一个图片，然后再事件重放眼镜与头发"):
/// an image the selection inked is rebuilt by replaying every stroke in it, the selection last; every other image is
/// pasted as recorded.
/// </summary>
internal static class PreciseUndoSelector
{
    internal const int Depth = 50;

    /// <param name="confirmedCurrentLayer">The current UUID already uniquely resolved from confirmed CSP state.</param>
    internal static PreciseUndoSelection Select(EditIndex index, LayerKey? confirmedCurrentLayer, PixelMask selection)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(selection);
        if (confirmedCurrentLayer is not { } layer || string.IsNullOrWhiteSpace(layer.SessionId) ||
            string.IsNullOrWhiteSpace(layer.Uuid) || !index.Layers.TryGetValue(layer, out var timeline))
            return Failure(confirmedCurrentLayer, PlanIssueCode.LayerIdentityUnconfirmed,
                "CSP 当前图层尚未唯一确认，或该 session、generation、UUID 没有对应的编辑图层。");
        if (selection.IsEmpty)
            return Failure(layer, PlanIssueCode.NoSelection, "选区内没有画布像素。");

        var map = new InkMap(timeline);
        // The region selects by ink: the write footprint's padding reaches strokes the region never touched.
        var candidates = map.Strokes.Where(s => s.Eligible && !s.MemolineInjected && map.InkImages(s).Count > 0 &&
            Intersects(s.Ink, selection)).Reverse().ToArray();
        if (candidates.Length == 0)
            return new(layer, [], 0, 0, [], [],
                [new(PlanIssueCode.NoSelection, "选区内没有可撤销的合格笔画写入范围。")]);
        var selected = candidates.Take(Depth).Reverse().ToArray();
        var involvement = map.Involve(selected);
        var ordered = selected.Concat(involvement.Closure)
            .OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
        var images = map.Packets.SelectMany(p => p.Images).Select(i => i.Key).Where(involvement.Images.Contains).ToArray();
        return new(layer, ordered, selected.Length, candidates.Length, images, involvement.Reasons, involvement.Issues);
    }

    /// <summary>The replayed images and their other strokes for a selection (<paramref name="selected"/>).</summary>
    internal static PreciseUndoInvolvement Involve(LayerIndex layer, IReadOnlyCollection<EditableStroke> selected)
        => new InkMap(layer).Involve(selected);

    /// <summary>
    /// Searches the region on every layer of the live document and undoes on one: the current layer when the
    /// region holds undoable strokes there, otherwise the nearest layer in the stack that does (on a tie, the
    /// layer above, whose ink is what shows). Only that layer's strokes are selected.
    /// </summary>
    /// <param name="stackOrder">Live layer UUIDs from bottom to top, as CSP's saved structure lists them.</param>
    internal static PreciseUndoSelection SelectNearest(EditIndex index, LayerKey? confirmedCurrentLayer,
        IReadOnlyList<string> stackOrder, PixelMask selection)
    {
        var own = Select(index, confirmedCurrentLayer, selection);
        if (own.CandidateCount > 0 || confirmedCurrentLayer is not { } current) return own;
        int Position(string uuid) { for (int i = 0; i < stackOrder.Count; i++) if (stackOrder[i] == uuid) return i; return -1; }
        int at = Position(current.Uuid);
        if (at < 0) return own;
        var others = index.Layers.Keys
            .Where(k => k != current && k.SessionId == current.SessionId && k.Generation == current.Generation && Position(k.Uuid) >= 0)
            .OrderBy(k => Math.Abs(Position(k.Uuid) - at)).ThenByDescending(k => Position(k.Uuid));
        foreach (var layer in others)
            if (Select(index, layer, selection) is { CandidateCount: > 0 } nearest) return nearest;
        return own;
    }

    private static PreciseUndoSelection Failure(LayerKey? layer, PlanIssueCode code, string message)
        => new(layer, [], 0, 0, [], [], [new(code, message)]);

    internal static bool Intersects(PixelMask left, PixelMask right) => left.Intersects(right);

    /// <summary>One layer's dirty images and which of them each stroke really inked.</summary>
    private sealed class InkMap
    {
        internal readonly LayerPacket[] Packets;
        internal readonly EditableStroke[] Strokes;
        private readonly Dictionary<DirtyImageKey, (DirtyImage Image, int Packet)> _images = [];
        private readonly Dictionary<EditableStrokeKey, IReadOnlyList<DirtyImageKey>> _inked = [];
        private readonly Dictionary<DirtyImageKey, List<EditableStroke>> _inkedBy = [];

        internal InkMap(LayerIndex layer)
        {
            Packets = layer.Packets.Where(p => !p.IsRecordingBaseline && p.Layer == layer.Layer)
                .OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
            for (int p = 0; p < Packets.Length; p++)
                foreach (var image in Packets[p].Images.Where(i => !i.IsBaseline && i.Key.Layer == layer.Layer)) _images[image.Key] = (image, p);
            Strokes = layer.Strokes.Where(s => s.Layer == layer.Layer && !s.IsUndone).DistinctBy(s => s.Key)
                .OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
            foreach (var stroke in Strokes)
            {
                var linked = stroke.Images.Where(_images.ContainsKey).Distinct().ToArray();
                // The watcher links a contact to every image its padded footprint meets (the nose dot listed 9 contacts,
                // 3 drew it). One continuous contact inks one image; a path that misses every linked image keeps the links.
                var inked = linked.Where(k => _images[k].Image.Mask.Intersects(stroke.Ink)).ToArray();
                _inked[stroke.Key] = inked.Length > 0 ? inked : linked;
                foreach (var key in _inked[stroke.Key]) (_inkedBy.TryGetValue(key, out var list) ? list : _inkedBy[key] = []).Add(stroke);
            }
        }

        internal IReadOnlyList<DirtyImageKey> InkImages(EditableStroke stroke) => _inked.GetValueOrDefault(stroke.Key) ?? [];

        /// <summary>
        /// The images to replay: those the selection inked; those a replayed stroke also inked (one contact is replayed
        /// whole); and every later image over one of them, whose recorded pixels hold the ink being rebuilt beneath.
        /// Their other strokes are the closure. A stroke in them that cannot be replayed keeps its recorded pixels where
        /// no replayed stroke can write; where one can, it would be lost, so that stops the undo.
        /// </summary>
        internal PreciseUndoInvolvement Involve(IReadOnlyCollection<EditableStroke> selected)
        {
            var images = new HashSet<DirtyImageKey>();
            var why = new Dictionary<DirtyImageKey, string>();
            var pending = new Queue<(DirtyImageKey Image, string Reason)>();
            foreach (var stroke in selected)
                foreach (var key in InkImages(stroke)) pending.Enqueue((key, $"已选笔画 {stroke.BeginEventId} 画在这张脏矩阵图片里"));
            while (pending.TryDequeue(out var next))
            {
                if (!images.Add(next.Image)) continue;
                why[next.Image] = next.Reason;
                var (image, packet) = _images[next.Image];
                foreach (var stroke in _inkedBy.GetValueOrDefault(next.Image) ?? [])
                    foreach (var other in InkImages(stroke).Where(k => !images.Contains(k)))
                        pending.Enqueue((other, $"笔画 {stroke.BeginEventId} 同时画在这张图片和要重放的图片里"));
                foreach (var (key, (later, at)) in _images)
                    if (at > packet && !images.Contains(key) && later.Mask.Intersects(image.Mask))
                        pending.Enqueue((key, $"它画在要重放的图片 {image.Key.ImageId} 之上"));
            }
            var chosen = selected.Select(s => s.Key).ToHashSet();
            var inImages = Strokes.Where(s => InkImages(s).Any(images.Contains)).ToArray();
            var replayable = inImages.Where(s => s.Eligible && !s.MemolineInjected).ToArray();
            var closure = replayable.Where(s => !chosen.Contains(s.Key)).ToArray();
            var reasons = closure.Select(s =>
            {
                var image = InkImages(s).First(images.Contains);
                return new PreciseUndoClosureReason(s.Key, image,
                    $"笔画 {s.BeginEventId} 在要整张重放的图片 {image.ImageId} 里（{why[image]}），所以在所选笔画之前原样重放。");
            }).ToArray();
            var issues = new List<PlanIssue>();
            PixelMask? reach = null;
            foreach (var stroke in inImages.Where(s => !s.Eligible || s.MemolineInjected))
            {
                reach ??= Footprints.UnionBounded(replayable.Select(s => s.WriteMask));
                if (!InkImages(stroke).Where(images.Contains).Any(k => _images[k].Image.Mask.Intersects(stroke.Ink) &&
                        Within(stroke.Ink, _images[k].Image.Mask, reach))) continue;
                issues.Add(new(PlanIssueCode.UnreplayableEventInMatrix, stroke.MemolineInjected
                    ? "要重放的图片里有上次精确撤销重画、还没有用 Ctrl+Z 撤掉的笔画，它与本次重放的笔画重叠。"
                    : "要重放的图片里有无法离散重放的笔画，且与重放的笔画重叠：" + (stroke.IneligibleReason ?? "事件不合格。"), Stroke: stroke.Key));
            }
            return new(images, closure, reasons, issues);
        }

        // Whether ink inside the image lies where a replayed stroke can write (and so would not survive as recorded pixels).
        private static bool Within(PixelMask ink, PixelMask image, PixelMask reach)
        {
            foreach (var (x, y) in ink.Pixels())
                if (image.Contains(x, y) && reach.Contains(x, y)) return true;
            return false;
        }
    }
}

internal static class EditSessionPreciseUndo
{
    internal static PreciseUndoSelection SelectPreciseUndo(this EditSession session, PixelMask region)
        => PreciseUndoSelector.SelectNearest(session.Index, session.CurrentLayer, session.LayerOrder.Select(l => l.Uuid).ToArray(), region);

    internal static string LayerName(this EditSession session, LayerKey layer)
        => session.LayerOrder.FirstOrDefault(l => l.Uuid == layer.Uuid).Name ?? layer.Uuid;
}
