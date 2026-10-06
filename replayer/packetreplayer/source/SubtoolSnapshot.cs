using System.Drawing;
using System.Text.Json;
using StrokeReplay;

namespace PacketReplay;

internal sealed record SubtoolMatch(string Id, string? ToolId, string? GroupId);
internal sealed record SubtoolEntry(string Name, string Kind, Rectangle? Input, IReadOnlyList<SubtoolMatch> Matches);
internal sealed record SubtoolSnapshot(Rectangle Panel, long CapturedTicks, string CaptureId,
    IReadOnlyList<SubtoolEntry> Entries, IReadOnlyList<SubtoolEntry> Groups)
{
    internal static SubtoolSnapshot? Parse(JsonElement data)
    {
        var evidence = J.Get(data, "evidence");
        var ticks = J.Get(evidence, "capturedTicks");
        var panel = CanvasViewSnapshot.ParseRectangle(J.Get(evidence, "panelRoi"));
        if (panel is null || ticks.ValueKind != JsonValueKind.Number || !ticks.TryGetInt64(out long captured) || captured < 0
            || J.Text(evidence, "captureId") is not { Length: > 0 } capture) return null;
        // Empty/unknown OCR is still a fresh panel observation. It may be scrolled,
        // but never borrow old rows or click positions from lastConfirmedState.
        SubtoolEntry Entry(JsonElement e, string kind)
        {
            var box = BrushSnapshot.ScreenBox(e, data);
            if (box is { } b && !panel.Value.Contains(b)) box = null;
            return new(J.Text(e, "name") ?? J.Text(e, "text") ?? "", J.Text(e, "kind") ?? kind, box,
                J.Array(J.Get(e, "matches")).Select(m => new SubtoolMatch(J.Text(m, "id") ?? "", J.Text(m, "toolId"), J.Text(m, "groupId"))).ToArray());
        }
        var entries = J.Array(J.Get(data, "ocrEntries")).Select(e => Entry(e, "subtool")).ToArray();
        var groups = J.Array(J.Get(data, "groupEntries")).Select(e => Entry(e, "group"))
            .Concat(entries.Where(e => e.Kind == "group")).Distinct().ToArray();
        return new(panel.Value, captured, capture, entries.Where(e => e.Kind != "group").ToArray(), groups);
    }

    internal bool BelongsTo(string toolId) => Entries.Concat(Groups).Any(e => e.Matches.Count > 0)
        && Entries.Concat(Groups).Where(e => e.Matches.Count > 0).All(e => e.Matches.Any(m => m.ToolId == toolId));
    internal bool ShowsGroup(string groupId) => Entries.Count > 0 && Entries.All(e => e.Matches.Any(m => m.GroupId == groupId));
    internal Rectangle? Find(BrushRoute route, string name)
    {
        var candidates = Entries.Where(e => BrushSnapshot.SameName(e.Name, name)
            && (route.SubtoolId is null ? e.Matches.Count == 1 : e.Matches.Any(m => m.Id == route.SubtoolId))
            && e.Matches.Where(m => m.ToolId == route.ToolId && (route.GroupId is null || m.GroupId == route.GroupId)).Count() == 1).ToArray();
        if (candidates.Length > 1) throw new InvalidOperationException($"子工具 OCR 出现多个“{name}”位置，无法唯一选择。");
        return candidates.Length == 1 ? candidates[0].Input : null;
    }
    internal Rectangle? FindGroup(string id)
    {
        var found = Groups.Where(e => e.Matches.Any(m => m.Id == id)).ToArray();
        return found.Length == 1 ? found[0].Input : null;
    }
    internal string VisibleSignature => string.Join("|", Entries.Select(e => $"{J.ToolName(e.Name)}:{e.Input}").Order());
}
