using System.Text.Json;

namespace MemolineDemo.Editing;

public sealed record BrushClassification(bool Eligible, bool Causal,
    IReadOnlyList<string> CausalTerms, bool ProbeGuaranteed, IReadOnlyList<string> Reasons,
    string Version = "csp-property-presence-v1")
{
    public string[] Terms => CausalTerms.ToArray();
    public bool ReadsUnknownSource { get; init; }
    public bool ReadsReferenceLayers { get; init; }
}

public sealed class BrushClassifier
{
    private static readonly HashSet<string> EligibleGroups = new(StringComparer.OrdinalIgnoreCase)
    { "笔", "筆", "鉛筆", "铅笔", "毛笔", "毛筆", "喷枪", "噴槍", "装饰", "裝飾", "橡皮擦", "色彩混合",
      "Pen", "Pencil", "Brush", "Airbrush", "Decoration", "Eraser", "Blend" };
    private readonly Dictionary<string, string> _causal = new(StringComparer.Ordinal);
    private readonly HashSet<string> _guaranteed = new(StringComparer.Ordinal);
    public string Version { get; }

    public BrushClassifier()
    {
        using var terms = Resource("causal-terms.v1.json");
        Version = terms.RootElement.GetProperty("version").GetString()!;
        foreach (var term in terms.RootElement.GetProperty("terms").EnumerateArray())
            _causal.Add(term.GetProperty("key").GetString()!, term.GetProperty("read").GetString()!);
        using var whitelist = Resource("probe-guaranteed.v1.json");
        foreach (var key in whitelist.RootElement.GetProperty("keys").EnumerateArray()) _guaranteed.Add(key.GetString()!);
    }

    public BrushClassification Classify(JsonElement brushState, ToolCatalog? catalog = null)
    {
        if (brushState.ValueKind != JsonValueKind.Object)
            return new(false, false, [], false, ["缺少已确认的 brushState。"], Version);
        string? status = Text(brushState, "status");
        bool envelope = brushState.TryGetProperty("state", out var state);
        if (envelope) brushState = state;
        string? name = Text(brushState, "name");
        string? group = catalog?.ParentGroupFor(name);
        var reasons = new List<string>();
        bool eligible = !string.IsNullOrWhiteSpace(name) && name is not ("unknown" or "ambiguous") &&
            group is not null && EligibleGroups.Contains(group);
        if (!eligible) reasons.Add(group is null ? "工具目录无法唯一解析子工具的父工具。" : $"工具组不是画布笔刷：{group}");
        if (envelope && status is not ("changed" or "unchanged") || status is "unknown" or "ambiguous")
        { eligible = false; reasons.Add("brushState 未确认。"); }
        var terms = new List<string>();
        bool guaranteed = true, unknown = false, reference = false;
        if (brushState.ValueKind == JsonValueKind.Object && brushState.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Array)
            foreach (var property in properties.EnumerateArray())
            {
                string? key = Text(property, "key");
                if (key is null || !_guaranteed.Contains(key)) guaranteed = false;
                if (key is not null && _causal.TryGetValue(key, out var read))
                { terms.Add(key); unknown |= read == "unknown"; reference |= read == "referenceLayers"; }
            }
        else guaranteed = false;
        return new(eligible, eligible && terms.Count > 0, terms.Distinct().ToArray(), guaranteed, reasons, Version)
        { ReadsUnknownSource = unknown, ReadsReferenceLayers = reference };
    }

    private static JsonDocument Resource(string filename)
    {
        var assembly = typeof(BrushClassifier).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + filename, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("缺少笔刷分类资源：" + filename);
        return JsonDocument.Parse(stream);
    }
    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
