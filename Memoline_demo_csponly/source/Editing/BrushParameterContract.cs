using PacketReplay;

namespace MemolineDemo.Editing;

internal sealed record NumericEditOption(string Key, double RecordedValue, double LiveValue,
    bool RecordedValuesDiffer);

internal sealed record BrushParameterAvailability(IReadOnlyList<NumericEditOption> Numbers,
    bool TargetRouteAvailable, bool RequiresActiveBrushConfirmation, string? TargetBrushName, string Reason);

/// <summary>
/// Decides which historical numeric properties have a writable editor in the
/// actively confirmed target subtool. Positions come from packetreplayer's
/// BrushSnapshot parser; this contract never infers editors from numeric values.
/// </summary>
internal static class BrushParameterContract
{
    internal static BrushParameterAvailability Evaluate(IReadOnlyList<BrushSnapshot?> historical,
        BrushSnapshot? live, string? replacementName, ShortcutConfiguration? shortcuts)
    {
        if (historical.Count == 0) return new([], false, false, null, "请先在画布上扫选整笔。");
        if (historical.Any(brush => brush is null || !ValidNumbers(brush)))
            return new([], false, false, null, "所选笔画缺少完整、唯一的历史数字属性。");
        var recorded = historical.OfType<BrushSnapshot>().ToArray();
        bool replacing = replacementName is not null;
        if (replacing && string.IsNullOrWhiteSpace(replacementName))
            return new([], false, false, null, "请选择有效的子工具名。");
        string target = replacing ? replacementName! : recorded[0].Name;
        if (!replacing && recorded.Any(brush => !BrushSnapshot.SameName(brush.Name, target)))
            return new([], false, true, null, "所选笔画使用不同子工具；先选择统一的新子工具并确认其数字输入框。");
        try
        {
            if (shortcuts is null) return new([], false, false, target, "尚未取得 CSP 子工具快捷键路由。");
            shortcuts.RouteForBrush(target);
        }
        catch (InvalidOperationException error)
        { return new([], false, false, target, error.Message); }
        if (live is null || !BrushSnapshot.SameName(live.Name, target))
            return new([], true, true, target, $"请主动确认子工具「{target}」的数字输入框后再调整参数。");
        if (!ValidNumbers(live))
            return new([], true, true, target, "实时子工具的数字属性不唯一，需重新确认参数面板。");
        bool changingTool = recorded.Any(brush => !BrushSnapshot.SameName(brush.Name, target));
        var numbers = live.Numbers.Where(number => number.Input is { Width: > 0, Height: > 0 } &&
                recorded.Where(brush=>!changingTool||BrushSnapshot.SameName(brush.Name,target))
                    .All(brush => brush.Numbers.Any(value => value.Key == number.Key)))
            .OrderBy(number => number.Key, StringComparer.Ordinal)
            .Select(number =>
            {
                double original = changingTool ? number.Value : recorded[0].Numbers.Single(value => value.Key == number.Key).Value;
                return new NumericEditOption(number.Key, original, number.Value,
                    !changingTool && recorded.Any(brush => brush.Numbers.Single(value => value.Key == number.Key).Value != original));
            }).ToArray();
        return new(numbers, true, false, target, numbers.Length == 0
            ? "目标子工具没有已确认、可用于此次修改的唯一数字输入框。"
            : "数字参数已匹配当前子工具的唯一输入框；重放写入后还会读回校验。");
    }

    private static bool ValidNumbers(BrushSnapshot brush) => !string.IsNullOrWhiteSpace(brush.Name) &&
        brush.Numbers.All(number => !string.IsNullOrWhiteSpace(number.Key) && double.IsFinite(number.Value)) &&
        brush.Numbers.Select(number => number.Key).Distinct(StringComparer.Ordinal).Count() == brush.Numbers.Count;
}
