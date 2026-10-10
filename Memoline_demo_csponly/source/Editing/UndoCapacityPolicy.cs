namespace MemolineDemo.Editing;

internal sealed record UndoCapacityDecision(bool Allowed,int Required,string Message);

internal static class UndoCapacityPolicy
{
    internal static int Required(int selectedCount) => checked(selectedCount+5);

    internal static UndoCapacityDecision Check(int selectedCount,int? verifiedCapacity,bool explicitlyConfirmed)
    {
        if(selectedCount<=0)return new(false,5,"没有可离散重放的所选笔画。");
        int required=Required(selectedCount);
        if(verifiedCapacity is { } capacity)
            return new(capacity>=required,required,capacity>=required
                ? $"已确认撤销容量 {capacity}，需要至少 {required}。"
                : $"CSP 撤销容量为 {capacity}，所选 {selectedCount} 笔需要至少 {required}；请先提高容量。");
        return new(explicitlyConfirmed,required,$"无法自动读取 CSP 撤销容量。请确认容量至少为 {required}（所选 {selectedCount} 笔 + 5）。");
    }
}
