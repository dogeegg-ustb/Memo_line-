using System.Drawing;
using System.Text.Json;
using StrokeReplay;

namespace PacketReplay;

internal static class J
{
    internal static JsonElement Get(JsonElement e, string key) => CanvasViewSnapshot.Property(e, key);
    internal static string? Text(JsonElement e, string key) => CanvasViewSnapshot.Text(e, key);
    internal static IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    internal static ulong U64(JsonElement e, string key) => Get(e, key).TryUnsigned();
    internal static ulong TryUnsigned(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out var n) ? n : 0;
    internal static long I64(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : 0;
    internal static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    internal static bool Confirmed(JsonElement e) => Text(e, "status") is "changed" or "unchanged";
    internal static string Name(string s) => string.Concat(s.Normalize().Where(c => !char.IsWhiteSpace(c))).Replace('筆', '笔').Replace('圖', '图').Replace('層', '层');
    internal static string ToolName(string s)
    {
        const string from = "筆圓擬澀麥極細號簽線圖層選擇範圍畫氣噴裝飾體較強軟濃";
        const string to = "笔圆拟涩麦极细号签线图层选择范围画气喷装饰体较强软浓";
        return string.Concat(s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').Select(c => from.IndexOf(c) is var i && i >= 0 ? to[i] : c));
    }
}

internal sealed record BrushNumber(string Key, double Value, Rectangle? Input = null);
internal sealed record BrushSnapshot(string Name, IReadOnlyList<BrushNumber> Numbers, int IgnoredProperties)
{
    internal static BrushSnapshot? Parse(JsonElement data, bool live = false)
    {
        if (!J.Confirmed(data)) return null;
        var state = J.Get(data, "state");
        var name = J.Text(state, "name");
        if (string.IsNullOrWhiteSpace(name) || name is "unknown" or "ambiguous") return null;
        var properties = J.Array(J.Get(state, "properties")).ToArray();
        var regions = J.Array(J.Get(data, "valueRegions")).ToArray();
        var numbers = new List<BrushNumber>();
        foreach (var p in properties)
        {
            // An option index such as antialiasing=2 is never a numeric editor.
            if (J.Text(p, "type") != "number" || J.Text(p, "value_category") is "icon" or "text"
                || J.Text(p, "status") != "ok" || J.Text(p, "enabled") == "disabled") continue;
            if (J.Text(p, "key") is not { } key || J.Number(J.Get(p, "value")) is not { } value) continue;
            Rectangle? input = null;
            if (live)
            {
                var matching = regions.Where(r => J.Text(r, "propertyKey") == key && J.Text(r, "category") == "number"
                    && J.Text(r, "status") == "ok" && J.Number(J.Get(r, "value")) == value).ToArray();
                var boxes = matching.Select(r => ScreenBox(r, data)).Where(r => r is not null).Distinct().ToArray();
                if (boxes.Length == 1) input = boxes[0];
            }
            if (numbers.Any(n => n.Key == key)) throw new InvalidDataException($"笔刷属性 {key} 重复，无法唯一匹配。");
            numbers.Add(new(key, value, input));
        }
        return new(name, numbers, properties.Length - numbers.Count);
    }

    internal static Rectangle? ScreenBox(JsonElement region, JsonElement data)
    {
        var screen = CanvasViewSnapshot.ParseRectangle(J.Get(region, "screenBbox"));
        if (screen is not null) return screen;
        if (J.Text(region, "coordinateSpace") != "panel") return null;
        var local = CanvasViewSnapshot.ParseRectangle(J.Get(region, "bbox"));
        var panel = CanvasViewSnapshot.ParseRectangle(J.Get(J.Get(data, "evidence"), "panelRoi"));
        if (local is not { } l || panel is not { } p || l.Left < 0 || l.Top < 0 || l.Right > p.Width || l.Bottom > p.Height) return null;
        return new Rectangle(checked(p.Left + l.Left), checked(p.Top + l.Top), l.Width, l.Height);
    }

    internal static bool SameName(string a, string b) => J.ToolName(a) == J.ToolName(b);
    internal bool Matches(BrushSnapshot target) => SameName(Name, target.Name)
        && target.Numbers.All(t => Numbers.Any(n => n.Key == t.Key && Math.Abs(n.Value - t.Value) <= 0.0001));
}

internal sealed record HistoricalState(BrushSnapshot? Brush, string? Layer, JsonElement Color, CanvasViewSnapshot? View = null)
{
    // Each event changes only its own columns; drawings carry the resolved table
    // at their causal position, including state inherited from earlier packets.
    internal HistoricalState Apply(PacketStep step) => step.Kind switch
    {
        "brush" => this with { Brush = step.Brush },
        "layer" => this with { Layer = step.Layer },
        "color" => this with { Color = step.Color },
        "view" => this with { View = step.View },
        "stroke" => step.Drawing!.State with { View = step.Drawing.Stroke.View },
        _ => this
    };
}
internal sealed record HistorySlot(ulong Anchor, ulong Order, string Module, JsonElement Data);

internal static class StateHistory
{
    internal static IReadOnlyList<HistorySlot> Resolve(IReadOnlyList<JsonElement> frames)
    {
        var reservations = frames.Where(f => J.Text(f, "kind") == "statePackageReserved")
            .ToDictionary(f => J.Text(J.Get(f, "data"), "packageId")!, f => J.Get(f, "data"));
        var slots = new List<HistorySlot>();
        var cores = frames.Where(f => J.Text(f, "kind") == "coreStateUpdated")
            .GroupBy(f => (J.Text(J.Get(f, "data"), "packageId"), J.Text(J.Get(f, "data"), "module")))
            .ToDictionary(g => g.Key, g => g.OrderBy(f => J.U64(f, "appendId")).Last());
        var covered = new HashSet<(string?, string?)>();
        foreach (var result in frames.Where(f => J.Text(f, "kind") == "statePackageResult"))
        {
            var d = J.Get(result, "data");
            var id = J.Text(d, "packageId");
            if (id is null || !reservations.TryGetValue(id, out var reserved)) throw new InvalidDataException("状态包结果缺少因果预留。");
            var anchor = J.U64(reserved, "afterEventId");
            if (anchor != J.U64(d, "afterEventId")) throw new InvalidDataException("状态包结果与因果预留不一致。");
            foreach (var update in J.Array(J.Get(J.Get(d, "result"), "updates")))
            {
                var module = J.Text(update, "module");
                if (module is null) continue;
                slots.Add(new(anchor, J.U64(reserved, "reservationOrder"), module, update));
                covered.Add((id, module));
            }
        }
        foreach (var (key, frame) in cores)
        {
            if (covered.Contains(key)) continue;
            var d = J.Get(frame, "data");
            ulong anchor = J.U64(J.Get(d, "evidence"), "observedAfterEventId");
            ulong order = J.U64(frame, "appendId");
            if (key.Item1 is { } id && reservations.TryGetValue(id, out var reserved))
            { anchor = J.U64(reserved, "afterEventId"); order = J.U64(reserved, "reservationOrder"); }
            slots.Add(new(anchor, order, key.Item2!, d));
        }
        return slots.OrderBy(s => s.Anchor).ThenBy(s => s.Order).ToArray();
    }

    internal static HistoricalState At(IReadOnlyList<HistorySlot> slots, ulong eventId)
    {
        BrushSnapshot? brush = null;
        string? layer = null;
        JsonElement color = default;
        CanvasViewSnapshot? view = null;
        foreach (var slot in slots.Where(s => s.Anchor < eventId))
        {
            bool valid = J.Confirmed(slot.Data) && J.Get(J.Get(slot.Data, "evidence"), "causalAmbiguous").ValueKind != JsonValueKind.True;
            if (slot.Module == "brushState") brush = valid ? BrushSnapshot.Parse(slot.Data) : null;
            if (slot.Module == "currentLayerState") layer = valid && J.Get(slot.Data, "state").ValueKind == JsonValueKind.String ? J.Get(slot.Data, "state").GetString() : null;
            if (slot.Module == "colorState") color = valid ? J.Get(slot.Data, "state") : default;
            if (slot.Module == "canvasViewState") view = valid ? CanvasViewSnapshot.ParseState(J.Get(slot.Data, "state"), J.Get(slot.Data, "rawResult")) : null;
        }
        return new(brush, layer, color, view);
    }
}
