using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>Resolves recorder catalog nodes by identity; repeated names never choose a route.</summary>
public sealed class ToolCatalog
{
    private readonly Dictionary<string, string?> _parents = new(StringComparer.OrdinalIgnoreCase);
    private ToolCatalog() { }
    public static ToolCatalog Empty() => new();
    public string? ParentGroupFor(string? child) => child is not null && _parents.TryGetValue(child, out var parent) ? parent : null;
    public bool TryResolve(string child, out string parent)
    { parent = ParentGroupFor(child)!; return parent is not null; }
    public static ToolCatalog FromJson(JsonElement root)
    {
        var result = new ToolCatalog();
        if (root.ValueKind != JsonValueKind.Object) return result;
        if (root.TryGetProperty("toolCatalog", out var catalog)) root = catalog;
        if (Text(root, "status") is "unavailable" or "unknown" or "ambiguous") return result;
        if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return result;
        var byId = nodes.EnumerateArray().ToDictionary(n => RequiredText(n, "id"), n => n, StringComparer.Ordinal);
        foreach (var node in byId.Values.Where(n => Text(n, "kind") == "subtool"))
        {
            string? name = Text(node, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            string? toolId = Text(node, "toolId"), parentId = Text(node, "parentId");
            string? group = null;
            var seen = new HashSet<string>(StringComparer.Ordinal) { RequiredText(node, "id") };
            while (parentId is not null && seen.Add(parentId) && byId.TryGetValue(parentId, out var parent))
            {
                if (Text(parent, "kind") == "tool")
                { if (toolId is null || toolId == parentId) group = Text(parent, "name"); break; }
                parentId = Text(parent, "parentId");
            }
            if (result._parents.ContainsKey(name)) result._parents[name] = null;
            else result._parents.Add(name, group);
        }
        return result;
    }
    public static ToolCatalog Load(string path)
    { using var document = JsonDocument.Parse(File.ReadAllText(path)); return FromJson(document.RootElement); }
    public static ToolCatalog TryLoad(string? path) => path is not null && File.Exists(path) ? Load(path) : Empty();
    private static string RequiredText(JsonElement node, string key) => Text(node, key) ?? throw new InvalidDataException("工具目录缺少 " + key);
    private static string? Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
