using System.Globalization;

namespace MemolineDemo.Editing;

internal sealed record PreciseUndoClosureReason(EditableStrokeKey Stroke,
    EditableStrokeKey OverlappingEarlierStroke, string Message);

internal sealed record PreciseUndoSelection(LayerKey? CurrentLayer,
    IReadOnlyList<EditableStroke> Selected, int BaseCount, int CandidateCount,
    IReadOnlyList<DirtyImageKey> Images,
    IReadOnlyList<PreciseUndoClosureReason> Reasons, IReadOnlyList<PlanIssue> Issues)
{
    public int ClosureCount => Selected.Count - BaseCount;
    public bool IsValid => Issues.Count == 0 && Selected.Count > 0;
    public string CountDisplay => ClosureCount == 0
        ? BaseCount.ToString(CultureInfo.InvariantCulture)
        : $"{BaseCount.ToString(CultureInfo.InvariantCulture)} + {ClosureCount.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Selects by undo depth: the latest strokes whose write footprint meets the region, plus the strokes after
/// the earliest of them that touch their ink. Their pixels are what precise undo rewinds; nothing else is.
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

        var packets = timeline.Packets.Where(p => !p.IsRecordingBaseline && p.Layer == layer)
            .OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
        var packetOf = new Dictionary<DirtyImageKey, int>();
        for (int p = 0; p < packets.Length; p++)
            foreach (var image in packets[p].Images.Where(i => !i.IsBaseline && i.Key.Layer == layer)) packetOf[image.Key] = p;
        var strokes = timeline.Strokes.Where(s => s.Layer == layer && !s.IsUndone).DistinctBy(s => s.Key)
            .OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
        var candidates = strokes.Where(s => s.Eligible && s.Images.Any(packetOf.ContainsKey) && Intersects(s.WriteMask, selection)).Reverse().ToArray();
        if (candidates.Length == 0)
            return new(layer, [], 0, 0, [], [],
                [new(PlanIssueCode.NoSelection, "选区内没有可撤销的合格笔画写入范围。")]);

        var selected = candidates.Take(Depth).Reverse().ToList();
        int baseCount = selected.Count;
        var seed = (selected[0].SessionOrder, selected[0].BeginEventId);
        var selectedKeys = selected.Select(s => s.Key).ToHashSet();
        var reasons = new List<PreciseUndoClosureReason>();
        var issues = new List<PlanIssue>();
        // Rewinding a selected stroke's pixels rewinds every later stroke drawn on them, so those are replayed too.
        // Not transitive: chaining through added strokes spreads across a dense drawing.
        foreach (var stroke in strokes)
        {
            if (selectedKeys.Contains(stroke.Key) || (stroke.SessionOrder, stroke.BeginEventId).CompareTo(seed) <= 0) continue;
            var overlapped = selected.Take(baseCount).FirstOrDefault(s => Intersects(s.WriteMask, stroke.WriteMask));
            if (overlapped is null) continue;
            bool later = (stroke.SessionOrder, stroke.BeginEventId).CompareTo((overlapped.SessionOrder, overlapped.BeginEventId)) > 0;
            // An earlier stroke that cannot be replayed keeps its own ink wherever it is not under the selection.
            if (!stroke.Eligible && !later) continue;
            selected.Add(stroke);
            selectedKeys.Add(stroke.Key);
            reasons.Add(new(stroke.Key, overlapped.Key, later
                ? $"笔画 {stroke.BeginEventId} 画在已选笔画 {overlapped.BeginEventId} 之上；倒流会抹掉它，所以一起重放。"
                : $"笔画 {stroke.BeginEventId} 在撤销深度内且与已选笔画 {overlapped.BeginEventId} 重叠；倒流会抹掉重叠处，所以一起重放。"));
            if (!stroke.Eligible)
                issues.Add(new(PlanIssueCode.UnreplayableEventInMatrix,
                    "倒流范围包含无法离散重放的笔画：" + (stroke.IneligibleReason ?? "事件不合格。"), Stroke: stroke.Key));
        }
        var ordered = selected.OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
        var images = ordered.SelectMany(s => s.Images).Where(packetOf.ContainsKey).Distinct().ToArray();
        return new(layer, ordered, baseCount, candidates.Length, images, reasons.ToArray(), issues);
    }

    private static PreciseUndoSelection Failure(LayerKey? layer, PlanIssueCode code, string message)
        => new(layer, [], 0, 0, [], [], [new(code, message)]);

    internal static bool Intersects(PixelMask left, PixelMask right)
    {
        if (left.IsEmpty || right.IsEmpty) return false;
        int x0 = Math.Max(left.Left, right.Left), y0 = Math.Max(left.Top, right.Top);
        long x1 = Math.Min((long)left.Left + left.Width, (long)right.Left + right.Width);
        long y1 = Math.Min((long)left.Top + left.Height, (long)right.Top + right.Height);
        for (long y = y0; y < y1; y++)
            for (long x = x0; x < x1; x++)
                if (left.Contains((int)x, (int)y) && right.Contains((int)x, (int)y)) return true;
        return false;
    }
}
