using System.Text.Json;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class BoundaryLayerChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        JsonElement Structure(object? current, params (long Id, string Name, string Uuid)[] rows)
            => JsonSerializer.SerializeToElement(new { canvas = new { current_layer_id = current },
                layers = rows.Select(r => new { id = r.Id, name = r.Name, uuid = r.Uuid }).ToArray() });
        string? Uuid(JsonElement structure) => BoundaryLayers.Selected(structure) is { } row ? PacketReplay.J.Text(row, "uuid") : null;

        // Two rows share a name: the saved ID still names exactly one of them.
        Check(Uuid(Structure(8L, (3, "图层 1", "a"), (8, "图层 1", "b"))) == "b", "the saved selection resolves duplicate names");
        Check(Uuid(Structure(3L, (3, "图层 1", "a"), (8, "图层 1", "b"))) == "a", "the saved selection picks the lower duplicate too");
        Check(Uuid(Structure(null, (3, "图层 1", "a"))) is null, "a save without a selected layer falls back to the panel");
        Check(Uuid(Structure(9L, (3, "图层 1", "a"))) is null, "an ID with no row is not guessed");
        Check(Uuid(Structure(3L, (3, "图层 1", "a"), (3, "图层 2", "b"))) is null, "an ID on two rows is not guessed");
        return checks;
    }
}
