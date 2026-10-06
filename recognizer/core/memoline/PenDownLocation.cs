using System.Text.Json;

namespace BehaviorRecognizer.Storage.Memoline;

public sealed record PanelBounds(double Left, double Top, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= Left && y >= Top && x < Left + Width && y < Top + Height;
}

/// <summary>Frozen at penBegin; movement and layout changes do not change the starting region.</summary>
public sealed record PenDownLocation(string Region, string Name, double X, double Y,
    string CoordinateSource, string Status, string? Panel = null, PanelBounds? Bounds = null,
    ulong LayoutAppendId = 0, long? LayoutTicks = null);

/// <summary>Uses the live workspace's absolute screen ROIs, without driver coordinate remapping.</summary>
public sealed class PanelRegionMap
{
    private static readonly JsonSerializerOptions LocationJson = new(MemolineWriter.Json) { PropertyNameCaseInsensitive = true };
    private sealed record Region(string Code, string Name, string Panel, PanelBounds Bounds);
    private readonly Region[] _regions;
    private readonly ulong _appendId;
    private readonly long? _ticks;
    private static readonly (string Code, string Name, string[] Aliases)[] Categories =
    [
        ("brushProperties", "笔刷属性面板", ["笔刷属性", "笔刷属性面板", "brushProperties"]),
        ("brushSelection", "笔刷选择面板", ["工具组", "笔刷选择", "笔刷选择面板", "brushSelection"]),
        ("toolbar", "工具栏面板", ["工具栏", "工具栏面板", "toolbar"]),
        ("navigator", "导航器面板", ["导航器", "导航器面板", "navigator"]),
        ("layers", "图层面板", ["图层", "图层面板", "layers"]),
        ("canvasViewport", "画布视口面板", ["画布视口", "画布视口面板", "canvasViewport"])
    ];

    private PanelRegionMap(Region[] regions, ulong appendId, long? ticks)
    { _regions = regions; _appendId = appendId; _ticks = ticks; }
    public bool HasRegions => _regions.Length > 0;

    public static PanelRegionMap Unavailable(ulong appendId = 0, long? ticks = null) => new([], appendId, ticks);

    public static PanelRegionMap FromWorkspaceStatus(JsonElement data, ulong appendId = 0, long? ticks = null)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String || status.GetString() != "ready" || !data.TryGetProperty("layout", out var layout)
            || layout.ValueKind != JsonValueKind.Object || !layout.TryGetProperty("regions", out var regions))
            return Unavailable(appendId, ticks);
        return FromRegions(regions, appendId, ticks);
    }

    public static PanelRegionMap FromRegions(JsonElement regions, ulong appendId = 0, long? ticks = null)
    {
        var result = new List<Region>();
        if (regions.ValueKind == JsonValueKind.Object)
            foreach (var category in Categories)
                foreach (string alias in category.Aliases)
                    if (regions.TryGetProperty(alias, out var roi) && roi.ValueKind == JsonValueKind.Array
                        && roi.GetArrayLength() == 4)
                    {
                        var values = roi.EnumerateArray().ToArray();
                        if (values.Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double n) || !double.IsFinite(n))) continue;
                        double x = values[0].GetDouble(), y = values[1].GetDouble(), w = values[2].GetDouble(), h = values[3].GetDouble();
                        if (w <= 0 || h <= 0 || !double.IsFinite(x + w) || !double.IsFinite(y + h)) continue;
                        result.Add(new(category.Code, category.Name, alias, new(x, y, w, h)));
                        break;
                    }
        return new(result.ToArray(), appendId, ticks);
    }

    public PenDownLocation Classify(double x, double y)
    {
        // Specific panels take priority over a surrounding canvas ROI. Edges
        // are half-open, so a shared boundary belongs to exactly one panel.
        var match = _regions.FirstOrDefault(r => r.Bounds.Contains(x, y));
        return match is null
            ? new("other", "其他", x, y, "windowsCursor", _regions.Length == 0 ? "layoutUnavailable" : "outsideRegions",
                LayoutAppendId: _appendId, LayoutTicks: _ticks)
            : new(match.Code, match.Name, x, y, "windowsCursor", "matched", match.Panel, match.Bounds, _appendId, _ticks);
    }

    public static PenDownLocation? ReadLocation(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("penDownLocation", out var value)
            || value.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var result = value.Deserialize<PenDownLocation>(LocationJson);
            return result is not null && (result.Region == "other" || Categories.Any(c => c.Code == result.Region)) ? result : null;
        }
        catch (JsonException) { return null; }
    }
}
