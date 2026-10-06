using System.Drawing;
using StrokeReplay;

namespace PacketReplay;

internal interface IPacketInput
{
    void EnsureTarget();
    Task ShortcutAsync(ShortcutBinding binding, CancellationToken token);
    Task NumberAsync(Rectangle input, double value, CancellationToken token);
    Task ClickAsync(Rectangle input, CancellationToken token);
    Task ScrollAsync(Rectangle panel, int delta, CancellationToken token);
}
internal sealed class PacketWindowsInput(WindowsViewInput input) : IPacketInput
{
    public void EnsureTarget() => input.EnsureTarget();
    public Task ShortcutAsync(ShortcutBinding binding, CancellationToken token) => input.SendShortcutAsync(ShortcutConfiguration.Keys(binding.Shortcut), token);
    public Task NumberAsync(Rectangle inputArea, double value, CancellationToken token) => input.SetNumberAsync(inputArea, value, token);
    public Task ClickAsync(Rectangle area, CancellationToken token) => input.ClickAsync(area, token);
    public Task ScrollAsync(Rectangle panel, int delta, CancellationToken token) => input.ScrollAsync(panel, delta, token);
}

internal sealed class PacketStateRestorer(IPacketStateFeed feed, IPacketInput input, Action<string> status)
{
    internal async Task RestoreBrushAsync(BrushSnapshot target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        feed.EnsureConnected();
        input.EnsureTarget();
        var current = feed.Brush ?? throw new InvalidOperationException("Memoline 尚未确认当前笔刷。");
        if (!BrushSnapshot.SameName(current.Name, target.Name))
        {
            var config = feed.Shortcuts ?? throw new InvalidOperationException("尚未取得 CSP 快捷键配置。");
            await SelectBrushAsync(config, config.RouteForBrush(target.Name), target.Name, token);
        }
        foreach (var number in target.Numbers)
        {
            token.ThrowIfCancellationRequested();
            feed.EnsureConnected();
            input.EnsureTarget();
            current = feed.Brush ?? throw new InvalidOperationException("当前笔刷状态不可用。");
            if (!BrushSnapshot.SameName(current.Name, target.Name)) throw new InvalidOperationException("笔刷在调整中发生变化。");
            var actual = current.Numbers.SingleOrDefault(n => n.Key == number.Key)
                ?? throw new InvalidOperationException($"当前面板未识别数字属性 {number.Key}。");
            if (Math.Abs(actual.Value - number.Value) <= .0001) continue;
            var area = actual.Input ?? throw new InvalidOperationException($"Memoline 未提供 {number.Key} 的唯一数字值屏幕位置；请更新并重新初始化。");
            status($"恢复笔刷属性：{number.Key} = {number.Value:0.###}");
            await input.NumberAsync(area, number.Value, token);
            await feed.RequestAsync(["brushState"], token);
            if (feed.Brush is not { } b || !BrushSnapshot.SameName(b.Name, target.Name)
                || !b.Numbers.Any(n => n.Key == number.Key && Math.Abs(n.Value - number.Value) <= .0001))
                throw new InvalidOperationException($"本次主动请求未确认 {number.Key} 已恢复为 {number.Value}。");
        }
        if (feed.Brush?.Matches(target) != true) throw new InvalidOperationException("笔刷数字属性未全部恢复。");
    }

    private bool IsBrush(string name) => feed.Brush is { } b && BrushSnapshot.SameName(b.Name, name);
    private async Task SelectBrushAsync(ShortcutConfiguration config, BrushRoute route, string name, CancellationToken token)
    {
        bool foundClass = false;
        for (int attempt = 0; attempt < config.ShortcutCycleCount(route); attempt++)
        {
            token.ThrowIfCancellationRequested(); feed.EnsureConnected(); input.EnsureTarget();
            status($"切换工具大类：{route.Binding.Name}（{route.Binding.Shortcut}），目标子工具：{name}");
            await input.ShortcutAsync(route.Binding, token);
            await feed.RequestAsync(["brushState"], token);
            if (IsBrush(name)) return;
            await feed.RequestAsync(["subtoolState"], token);
            var currentTools = config.Nodes.Where(n => n.Kind == "subtool" && !n.Hidden && BrushSnapshot.SameName(n.Name, feed.Brush!.Name))
                .Select(n => n.ToolId).Distinct().ToArray();
            foundClass = feed.Subtools!.BelongsTo(route.ToolId) || currentTools.Length == 1 && currentTools[0] == route.ToolId;
            if (foundClass) break;
        }
        if (!foundClass) throw new InvalidOperationException($"快捷键循环后未确认目标工具大类 {route.Binding.Name}。");
        var panel = feed.Subtools!;
        if (route.GroupId is { } group && !panel.ShowsGroup(group) && panel.FindGroup(group) is { } groupArea)
        {
            status($"选择子工具组：{config.Nodes.FirstOrDefault(n => n.Id == group)?.Name ?? group}");
            await input.ClickAsync(groupArea, token);
            await feed.RequestAsync(["subtoolState", "brushState"], token);
            if (IsBrush(name)) return;
        }

        // Search downward from the current position, then upward to the other end.
        // Two identical fresh observations mark a boundary. Every wheel actively
        // requests another captured frame; the catalog bounds the search budget.
        int limit = Math.Clamp(route.SubtoolCount * 2 + 8, 16, 512);
        foreach (int delta in new[] { -120, 120 })
        {
            int unchanged = 0;
            for (int attempt = 0; attempt < limit; attempt++)
            {
                token.ThrowIfCancellationRequested(); feed.EnsureConnected(); input.EnsureTarget();
                panel = feed.Subtools ?? throw new InvalidOperationException("当前子工具面板 OCR 不可用。");
                if (panel.Find(route, name) is { } area)
                {
                    status($"OCR 选择子工具：{name}");
                    await input.ClickAsync(area, token);
                    await feed.RequestAsync(["brushState"], token);
                    if (!IsBrush(name)) throw new InvalidOperationException($"本次主动请求未确认选中的子工具为“{name}”。");
                    return;
                }
                if (unchanged >= 2) break;
                var signature = panel.VisibleSignature;
                status($"在子工具面板{(delta < 0 ? "向下" : "向上")}滚动查找：{name}");
                await input.ScrollAsync(panel.Panel, delta, token);
                await feed.RequestAsync(["subtoolState"], token);
                unchanged = panel.Entries.Count > 0 && feed.Subtools!.Entries.Count > 0 && feed.Subtools.VisibleSignature == signature ? unchanged + 1 : 0;
            }
        }
        throw new InvalidOperationException($"已滚动查找子工具面板，仍未找到“{name}”的唯一点击位置；请确认目标子工具组可见、名称可被 OCR 识别。");
    }

    internal async Task RestoreLayerAsync(string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        feed.EnsureConnected();
        input.EnsureTarget();
        var layers = J.Array(J.Get(feed.Layers, "layers")).ToArray();
        int Matches(string name) => layers.Count(l => J.Text(l, "name") is { } n && J.Name(n) == J.Name(name));
        if (Matches(target) != 1) throw new InvalidOperationException($"layers 结构表中的目标图层“{target}”缺失或重名，无法唯一选择。");
        var current = feed.Layer ?? throw new InvalidOperationException("layerstage 尚未确认当前图层。");
        if (Matches(current) != 1) throw new InvalidOperationException("当前图层名称无法在 layers 结构表唯一定位。");
        if (J.Name(current) == J.Name(target)) return;
        var config = feed.Shortcuts ?? throw new InvalidOperationException("尚未取得 CSP 快捷键配置。");
        int Index(string name) => Array.FindIndex(layers, l => J.Name(J.Text(l, "name") ?? "") == J.Name(name));
        // Memoline lists layers in CLIP tree order (lower siblings first).
        // Prefer the direction suggested by that structure, confirming each step.
        var preferred = Index(target) > Index(current) ? "layerselectupperlayer" : "layerselectlowerlayer";
        var routes = new[] { preferred, preferred == "layerselectupperlayer" ? "layerselectlowerlayer" : "layerselectupperlayer" }
            .Where(c => config.Bindings.Any(b => b.Command == c)).Select(config.ForCommand).ToArray();
        if (routes.Length == 0) throw new InvalidOperationException("CSP 配置没有选择上／下图层的快捷键。");
        foreach (var route in routes)
        {
            var visited = new HashSet<string>();
            for (int attempt = 0; attempt <= layers.Length; attempt++)
            {
                token.ThrowIfCancellationRequested();
                feed.EnsureConnected();
                input.EnsureTarget();
                current = feed.Layer ?? throw new InvalidOperationException("当前图层状态不可用。");
                if (J.Name(current) == J.Name(target)) return;
                if (Matches(current) != 1) throw new InvalidOperationException("图层切换结果无法唯一定位。");
                if (!visited.Add(J.Name(current))) break;
                status($"切换图层：{current} → {target}（{route.Shortcut}）");
                await input.ShortcutAsync(route, token);
                await feed.RequestAsync(["currentLayerState"], token);
            }
        }
        if (J.Name(feed.Layer ?? "") != J.Name(target)) throw new InvalidOperationException("无法通过已配置的图层快捷键到达目标图层；请展开图层文件夹并更新结构表。");
    }
}
