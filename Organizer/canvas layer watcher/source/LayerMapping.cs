using System.Text.Json;

namespace CanvasLayerWatcher;

internal sealed record LayerReference(long Id, string Name, string? Uuid);

internal static class LayerMapping
{
    public static string Compact(string name) => string.Concat(name.Where(c => !char.IsWhiteSpace(c)));
    public static bool SameName(string? a, string? b) => a is not null && b is not null && Compact(a) == Compact(b);
    private static string UuidKey(string value) => string.Concat(value.Where(c => c is not ('-' or '{' or '}') && !char.IsWhiteSpace(c))).ToLowerInvariant();

    public static LayerReference[] ReadLayers(JsonElement state)
    {
        var layers = J.Get(state, "layers");
        if (layers.ValueKind != JsonValueKind.Array) return [];
        return layers.EnumerateArray().Where(item => J.Tick(item, "id", 0) > 0 && J.Text(item, "name") is not null)
            .Select(item => new LayerReference(J.Tick(item, "id", 0), J.Text(item, "name")!, J.Text(item, "uuid"))).ToArray();
    }

    public static LayerReference? FromCore(IReadOnlyList<LayerReference> layers, string observedName)
    {
        var matches = layers.Where(layer => SameName(layer.Name, observedName)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"图层属性核心中「{observedName}」匹配多个图层，无法确定切换时的图层 ID");
        return matches.SingleOrDefault();
    }

    public static LayerReference FromFile(JsonElement inspection, string observedName, LayerReference? coreLayer)
    {
        if (string.IsNullOrWhiteSpace(observedName)) throw new InvalidDataException("接口未提供有效图层名称");
        var layers = ReadLayers(inspection);
        LayerReference[] matches;
        if (!string.IsNullOrWhiteSpace(coreLayer?.Uuid))
        {
            // UUID survives renames and can also identify a layer whose local
            // SQLite ID changed. Do not substitute a different same-name layer.
            matches = layers.Where(layer => !string.IsNullOrWhiteSpace(layer.Uuid) && UuidKey(layer.Uuid) == UuidKey(coreLayer.Uuid)).ToArray();
        }
        else if (coreLayer is not null)
            matches = layers.Where(layer => layer.Id == coreLayer.Id && SameName(layer.Name, coreLayer.Name)).ToArray();
        else
            matches = layers.Where(layer => SameName(layer.Name, observedName)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"图层「{observedName}」与保存文件匹配 {matches.Length} 个图层（核心 ID={coreLayer?.Id}），无法唯一对应");
        return matches[0];
    }
}
