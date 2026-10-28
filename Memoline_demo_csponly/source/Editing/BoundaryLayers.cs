using System.Text.Json;

namespace MemolineDemo.Editing;

internal static class BoundaryLayers
{
    /// <summary>The structure row of the layer a saved document names as selected (`canvas.current_layer_id`),
    /// or null when the save names none or no single row has that ID.</summary>
    internal static JsonElement? Selected(JsonElement structure)
    {
        if (PacketReplay.J.Get(PacketReplay.J.Get(structure, "canvas"), "current_layer_id") is not { ValueKind: JsonValueKind.Number } id
            || !id.TryGetInt64(out long selected)) return null;
        var rows = PacketReplay.J.Array(PacketReplay.J.Get(structure, "layers"))
            .Where(row => PacketReplay.J.Get(row, "id") is { ValueKind: JsonValueKind.Number } rowId
                          && rowId.TryGetInt64(out long value) && value == selected).ToArray();
        return rows.Length == 1 ? rows[0] : null;
    }
}
