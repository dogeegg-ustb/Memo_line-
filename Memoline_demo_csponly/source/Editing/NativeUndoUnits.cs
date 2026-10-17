using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using PacketReplay;

namespace MemolineDemo.Editing;

// ViewState claims inputs that changed only what CSP keeps out of its history (layer visibility,
// the selected layer): nothing joins the stack, and the inputs no longer make the stack unknown.
internal enum NativeUndoUnitKind { Stroke, LayerProperty, Macro, ViewState }

/// <summary>One native undo unit made by a recorded document change other than a pen stroke. It claims
/// the hardware inputs from <see cref="FirstInputAppendId"/> to <see cref="LastInputAppendId"/>: the
/// timeline stacks the unit at the last of them instead of discarding history knowledge there.</summary>
internal sealed record NativeUndoUnitProof(NativeUndoUnitKind Kind, ulong FirstInputAppendId, ulong LastInputAppendId,
    LayerKey? Layer, string Description);

/// <summary>Finds recorded document changes besides pen strokes that CSP keeps as single undo units:
/// layer property changes now, macro operations (fill, transform, filters, layer creation …) later, as
/// further detectors in <see cref="NativeUndoUnitDetectors.Default"/>. A detector proves exactly one
/// unit or nothing; an unproven change keeps the timeline's barrier.</summary>
internal interface INativeUndoUnitDetector
{
    IEnumerable<NativeUndoUnitProof> Detect(SessionSnapshot session, IReadOnlyList<JsonElement> frames,
        IReadOnlyList<HistorySlot> history);
}

internal static class NativeUndoUnitDetectors
{
    // Macro operation detectors register here.
    internal static IReadOnlyList<INativeUndoUnitDetector> Default { get; } = [new LayerPropertyUndoDetector()];
}

/// <summary>A layer panel gesture (mouse buttons or pen contacts) between two saved documents. One changed
/// property of one layer, of a kind CSP records as one history step, is one unit. A change only in what CSP
/// keeps out of its history (showing or hiding layers is not recorded in History; Ctrl+Z leaves the
/// selected layer) is no unit. Another press between the two saves, a changed layer order, or any other
/// difference proves nothing.</summary>
internal sealed class LayerPropertyUndoDetector : INativeUndoUnitDetector
{
    // The first field describes the value.
    private static readonly (string Label, string[] Fields)[] SingleStepProperties =
    [
        ("不透明度", ["opacity_percent", "opacity_raw"]), ("合成模式", ["blend_mode", "blend_mode_id"]),
        ("用下一图层剪裁", ["clipped"]), ("锁定", ["locked"]), ("草稿图层", ["draft"]), ("图层名称", ["name"])
    ];
    // Changes CSP keeps out of its history; folder expansion may accompany them but proves nothing alone.
    private static readonly HashSet<string> HistoryFreeFields = ["visible", "effective_visible", "selected"];
    private static readonly HashSet<string> ViewFields = [.. HistoryFreeFields, "folder_collapsed"];

    private sealed record Input(ulong Append, ulong Event, string Kind, string? Region);
    private sealed record Gesture(ulong FirstAppend, ulong LastAppend, ulong FirstEvent, ulong LastEvent);

    public IEnumerable<NativeUndoUnitProof> Detect(SessionSnapshot session, IReadOnlyList<JsonElement> frames,
        IReadOnlyList<HistorySlot> history)
    {
        // Only a save-triggered reading describes the document at a known input; reading the file on
        // request may still show an earlier save.
        var saves = history.Where(slot => slot.Module == "clipState" && J.Confirmed(slot.Data) &&
            J.Text(slot.Data, "packageId") is null &&
            J.Get(J.Get(slot.Data, "evidence"), "triggerTicks").ValueKind == JsonValueKind.Number).ToArray();
        if (saves.Length < 2) yield break;
        var inputs = Inputs(frames);
        foreach (var gesture in Gestures(inputs))
        {
            var before = saves.LastOrDefault(save => save.Anchor < gesture.FirstEvent);
            var after = saves.FirstOrDefault(save => save.Anchor >= gesture.LastEvent);
            if (before is null || after is null) continue;
            // Canvas strokes cannot change layer properties; any other press between the saves could.
            if (inputs.Any(input => Press(input) && input.Region != "canvasViewport" &&
                input.Event > before.Anchor && input.Event <= after.Anchor &&
                (input.Event < gesture.FirstEvent || input.Event > gesture.LastEvent))) continue;
            if (Change(before.Data, after.Data) is not { } change) continue;
            var layers = session.Packets.Where(packet => packet.Layer.SessionId == session.SessionId && packet.Layer.Uuid == change.Uuid)
                .Select(packet => packet.Layer).Distinct().ToArray();
            yield return new(change.Kind, gesture.FirstAppend, gesture.LastAppend,
                change.Uuid is not null && layers.Length == 1 ? layers[0] : null, change.Description);
        }
    }

    private static List<Input> Inputs(IReadOnlyList<JsonElement> frames)
    {
        var inputs = new List<Input>();
        var regions = PanelRegionMap.Unavailable();
        foreach (var frame in frames.OrderBy(frame => J.U64(frame, "appendId")))
        {
            var data = J.Get(frame, "data");
            if (J.Text(frame, "kind") == "workspaceStatus")
                regions = PanelRegionMap.FromWorkspaceStatus(data, J.U64(frame, "appendId"), J.I64(frame, "ticks"));
            if (J.Text(frame, "path") != "hardware" || J.U64(frame, "eventId") == 0 || J.Text(frame, "kind") is not { } kind ||
                J.Text(data, "extraInfo")?.Equals("0x4D4C5243", StringComparison.OrdinalIgnoreCase) == true) continue;
            var location = PanelRegionMap.ReadLocation(data) ?? (regions.HasRegions &&
                J.Number(J.Get(data, "x")) is { } x && J.Number(J.Get(data, "y")) is { } y ? regions.Classify(x, y) : null);
            inputs.Add(new(J.U64(frame, "appendId"), J.U64(frame, "eventId"), kind,
                location is { Status: "matched" } ? location.Region : null));
        }
        return inputs;
    }

    private static bool Press(Input input) => input.Kind is "mouseDown" or "mouseWheel" or "penBegin" or "keyInput" or "keyDown";

    // A gesture starts with a press in the layer panel and ends when every button and contact it opened is
    // released; a pen tap and the mouse click Windows derives from it are one gesture. Any key input or
    // a press elsewhere abandons it, so every hardware input in its range belongs to it.
    private static IEnumerable<Gesture> Gestures(List<Input> inputs)
    {
        Input? first = null;
        int open = 0;
        foreach (var input in inputs)
        {
            bool down = input.Kind is "mouseDown" or "penBegin", up = input.Kind is "mouseUp" or "penEnd";
            if (first is null)
            {
                if (down && input.Region == "layers") { first = input; open = 1; }
                continue;
            }
            if (down && input.Region == "layers") open++;
            else if (up) open--;
            else if (input.Kind is not ("mouseDrag" or "mouseMove" or "penSample")) { first = null; continue; }
            if (open == 0)
            {
                yield return new(first.Append, input.Append, first.Event, input.Event);
                first = null;
            }
        }
    }

    private static (NativeUndoUnitKind Kind, string? Uuid, string Description)? Change(JsonElement before, JsonElement after)
    {
        JsonElement oldCanvas = J.Get(J.Get(before, "state"), "canvas"), newCanvas = J.Get(J.Get(after, "state"), "canvas");
        if (!SameDocument(oldCanvas, newCanvas) || Layers(before) is not { } oldOrder || Layers(after) is not { } newOrder ||
            !oldOrder.Select(layer => layer.Uuid).SequenceEqual(newOrder.Select(layer => layer.Uuid))) return null;
        var old = oldOrder.ToDictionary(layer => layer.Uuid, layer => layer.Fields);
        var now = newOrder.ToDictionary(layer => layer.Uuid, layer => layer.Fields);
        var differences = now.SelectMany(layer => layer.Value.Keys.Union(old[layer.Key].Keys)
            .Where(field => layer.Value.GetValueOrDefault(field) != old[layer.Key].GetValueOrDefault(field))
            .Select(field => (Uuid: layer.Key, Field: field))).ToArray();
        var changed = differences.Where(c => !ViewFields.Contains(c.Field)).ToArray();
        if (changed.Length == 0)
        {
            bool reselected = Raw(J.Get(oldCanvas, "current_layer_id")) != Raw(J.Get(newCanvas, "current_layer_id")) ||
                differences.Any(c => c.Field == "selected");
            if (!reselected && !differences.Any(c => HistoryFreeFields.Contains(c.Field))) return null;
            var shown = differences.Where(c => c.Field == "visible")
                .Select(c => Name(now[c.Uuid]) + (now[c.Uuid]["visible"] == "true" ? " 显示" : " 隐藏"));
            var selected = reselected ? newOrder.Where(layer => layer.Fields.GetValueOrDefault("selected") == "true")
                .Select(layer => "选择 " + Name(layer.Fields)) : [];
            string description = string.Join("，", shown.Concat(selected));
            return (NativeUndoUnitKind.ViewState, null, description.Length > 0 ? description : "图层显示或选择");
        }
        if (changed.Any(c => c.Uuid != changed[0].Uuid)) return null;
        var property = SingleStepProperties.FirstOrDefault(p => changed.All(c => p.Fields.Contains(c.Field)));
        if (property.Label is null) return null;
        string uuid = changed[0].Uuid, field = property.Fields[0];
        return (NativeUndoUnitKind.LayerProperty, uuid,
            $"{Name(now[uuid])}：{property.Label} {old[uuid].GetValueOrDefault(field)} → {now[uuid].GetValueOrDefault(field)}");
    }

    private static string Name(Dictionary<string, string> layer)
        => JsonSerializer.Deserialize<string>(layer.GetValueOrDefault("name") ?? "\"\"") ?? "";
    private static string Raw(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "" : value.GetRawText();

    private static List<(string Uuid, Dictionary<string, string> Fields)>? Layers(JsonElement clip)
    {
        var result = new List<(string Uuid, Dictionary<string, string> Fields)>();
        foreach (var layer in J.Array(J.Get(J.Get(clip, "state"), "layers")))
        {
            if (layer.ValueKind != JsonValueKind.Object || J.Text(layer, "uuid") is not { } uuid || result.Any(l => l.Uuid == uuid)) return null;
            result.Add((uuid, layer.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText())));
        }
        return result.Count > 0 ? result : null;
    }

    // The selected layer is view state; size, resolution or units changing is not a layer property step.
    private static bool SameDocument(JsonElement before, JsonElement after)
        => before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object &&
            before.EnumerateObject().Where(p => p.Name != "current_layer_id").Select(p => (p.Name, p.Value.GetRawText()))
                .ToHashSet().SetEquals(after.EnumerateObject().Where(p => p.Name != "current_layer_id").Select(p => (p.Name, p.Value.GetRawText())));
}
