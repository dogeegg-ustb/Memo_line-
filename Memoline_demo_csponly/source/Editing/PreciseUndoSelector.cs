using System.Globalization;

namespace MemolineDemo.Editing;

internal sealed record PreciseUndoClosureReason(EditableStrokeKey Stroke,
    EditableStrokeKey OverlappingEarlierStroke, string Message);

internal sealed record PreciseUndoSelection(LayerKey? CurrentLayer,
    IReadOnlyList<EditableStroke> Selected, int BaseCount, int CandidateCount,
    IReadOnlyList<DirtyImageKey> VisibleImages,
    IReadOnlyList<PreciseUndoClosureReason> Reasons, IReadOnlyList<PlanIssue> Issues)
{
    public int ClosureCount => Selected.Count - BaseCount;
    public bool IsValid => Issues.Count == 0 && Selected.Count > 0;
    public string CountDisplay => ClosureCount == 0
        ? BaseCount.ToString(CultureInfo.InvariantCulture)
        : $"{BaseCount.ToString(CultureInfo.InvariantCulture)} + {ClosureCount.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Selects visible-surface candidates and their forward write-overlap closure without CSP actions.</summary>
internal static class PreciseUndoSelector
{
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
        var covered = new PixelMask(selection.Left, selection.Top, selection.Width, selection.Height);
        var visible = new HashSet<DirtyImageKey>();
        for (int p = packets.Length - 1; p >= 0; p--)
        {
            var images = packets[p].Images.Where(i => !i.IsBaseline && i.Key.Layer == layer).ToArray();
            // Every image in a capture group has the same time: none covers another within the group.
            foreach (var image in images)
                if (SharedPixels(image.Mask, selection).Any(pixel => !covered.Contains(pixel.X, pixel.Y)))
                    visible.Add(image.Key);
            foreach (var image in images)
                foreach (var pixel in SharedPixels(image.Mask, selection)) covered.Set(pixel.X, pixel.Y);
        }

        var visibleImages = packets.SelectMany(p => p.Images).Where(i => visible.Contains(i.Key)).ToArray();
        var candidates = visibleImages.SelectMany(i => i.Events).Distinct()
            .Select(key => index.Strokes.TryGetValue(key, out var stroke) ? stroke : null)
            .Where(s => s is not null && s.Layer == layer && s.Eligible && !s.IsUndone && Intersects(s.WriteMask, selection))
            .Select(s => s!).OrderByDescending(s => s.SessionOrder).ThenByDescending(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray();
        var visibleKeys = visibleImages.Select(i => i.Key).Distinct().ToArray();
        if (candidates.Length == 0)
            return new(layer, [], 0, 0, visibleKeys, [],
                [new(PlanIssueCode.NoSelection, "选区内没有可见表层图片对应的合格笔画写入范围。")]);

        var selected = candidates.Take(50).OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToList();
        int baseCount = selected.Count;
        var selectedKeys = selected.Select(s => s.Key).ToHashSet();
        var reasons = new List<PreciseUndoClosureReason>();
        var issues = new List<PlanIssue>();
        // Overlap dependencies only point to later eventIds. This ascending pass reaches the fixed
        // point: any non-base predecessor that could add a stroke has already been processed.
        foreach (var stroke in timeline.Strokes.Where(s => s.Layer == layer && !s.IsUndone).DistinctBy(s => s.Key)
                     .OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId))
        {
            if (selectedKeys.Contains(stroke.Key)) continue;
            // Only strokes drawn directly over a base selection are moved; chaining through added
            // strokes spreads across a dense drawing and stops being a regional undo.
            var predecessor = selected.Take(baseCount).FirstOrDefault(s => (s.SessionOrder, s.BeginEventId).CompareTo((stroke.SessionOrder, stroke.BeginEventId)) < 0 &&
                Intersects(s.WriteMask, stroke.WriteMask));
            if (predecessor is null) continue;
            selected.Add(stroke);
            selectedKeys.Add(stroke.Key);
            reasons.Add(new(stroke.Key, predecessor.Key,
                $"笔画 {stroke.BeginEventId} 比已选笔画 {predecessor.BeginEventId} 晚，且写入范围重叠；为保持叠放顺序加入。"));
            if (!stroke.Eligible)
                issues.Add(new(PlanIssueCode.UnreplayableEventInMatrix,
                    "叠放闭包包含无法离散重放的笔画：" + (stroke.IneligibleReason ?? "事件不合格。"), Stroke: stroke.Key));
        }
        return new(layer, selected.OrderBy(s => s.SessionOrder).ThenBy(s => s.BeginEventId).ThenBy(s => s.Key.BeginAppendId).ToArray(),
            baseCount, candidates.Length, visibleKeys, reasons.ToArray(), issues);
    }

    private static PreciseUndoSelection Failure(LayerKey? layer, PlanIssueCode code, string message)
        => new(layer, [], 0, 0, [], [], [new(code, message)]);

    private static bool Intersects(PixelMask left, PixelMask right)
        => !left.IsEmpty && !right.IsEmpty && SharedPixels(left, right).Any();

    private static IEnumerable<(int X, int Y)> SharedPixels(PixelMask left, PixelMask right)
    {
        int x0 = Math.Max(left.Left, right.Left), y0 = Math.Max(left.Top, right.Top);
        long x1 = Math.Min((long)left.Left + left.Width, (long)right.Left + right.Width);
        long y1 = Math.Min((long)left.Top + left.Height, (long)right.Top + right.Height);
        for (long y = y0; y < y1; y++)
            for (long x = x0; x < x1; x++)
                if (left.Contains((int)x, (int)y) && right.Contains((int)x, (int)y))
                    yield return ((int)x, (int)y);
    }
}
