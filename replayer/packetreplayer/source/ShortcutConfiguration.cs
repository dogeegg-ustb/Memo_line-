using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PacketReplay;

internal sealed record ToolNode(string Id, string Name, string Kind, string? ToolId, string? GroupId, bool Hidden,
    IReadOnlyList<string> PathIds);
internal sealed record ShortcutBinding(string Id, string Name, string? Command, string Shortcut,
    ToolNode? Tool = null, IReadOnlyList<ToolNode>? Subtools = null);
internal sealed record BrushRoute(ShortcutBinding Binding, string ToolId, string? SubtoolId, string? GroupId, int SubtoolCount);
internal sealed class ShortcutConfiguration(IReadOnlyList<ShortcutBinding> bindings, IReadOnlyList<ToolNode>? nodes = null)
{
    internal IReadOnlyList<ShortcutBinding> Bindings { get; } = bindings;
    internal IReadOnlyList<ToolNode> Nodes { get; } = nodes ?? [];
    internal JsonElement Raw { get; private init; }
    private static ToolNode Node(JsonElement n) => new(J.Text(n, "id") ?? "", J.Text(n, "name") ?? "",
        J.Text(n, "kind") ?? "", J.Text(n, "toolId"), J.Text(n, "groupId"), J.Get(n, "hidden").ValueKind == JsonValueKind.True,
        J.Array(J.Get(n, "pathIds")).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray());
    internal static ShortcutConfiguration Parse(JsonElement data)
    {
        var entries = J.Array(J.Get(data, "bindings")).Select(b => new ShortcutBinding(J.Text(b, "id") ?? "",
            J.Text(b, "action_name") ?? "", J.Text(b, "command"), J.Text(b, "shortcut") ?? "",
            J.Get(b, "tool").ValueKind == JsonValueKind.Object ? Node(J.Get(b, "tool")) : null,
            J.Array(J.Get(b, "subtools")).Select(Node).ToArray())).ToArray();
        if (entries.Length == 0) throw new InvalidDataException("Memoline 接口未返回已保存的 CSP 快捷键。");
        return new(entries, J.Array(J.Get(J.Get(data, "toolCatalog"), "nodes")).Select(Node).ToArray()) { Raw = data.Clone() };
    }
    internal BrushRoute RouteForBrush(string name)
    {
        var targets = Nodes.Concat(Bindings.SelectMany(b => b.Subtools ?? []))
            .Where(n => n.Kind == "subtool" && !n.Hidden && BrushSnapshot.SameName(n.Name, name)).DistinctBy(n => n.Id).ToArray();
        if (targets.Length > 1) throw new InvalidOperationException($"CSP 配置中存在重名子工具“{name}”，历史名称无法唯一匹配。");
        if (targets.Length == 0)
        {
            var direct = Pick(Bindings.Where(b => b.Id.StartsWith("tool_") && BrushSnapshot.SameName(b.Name, name)), $"笔刷 {name}");
            return new(direct, direct.Tool?.ToolId ?? direct.Id, direct.Tool?.Id, direct.Tool?.GroupId, 1);
        }
        var target = targets[0];
        var candidates = Bindings.Where(b => b.Id == target.Id || (b.Subtools ?? []).Any(n => n.Id == target.Id)).ToArray();
        var binding = Pick(candidates.Where(b => b.Tool?.Kind == "tool").Any()
            ? candidates.Where(b => b.Tool?.Kind == "tool") : candidates, $"笔刷 {name} 所属工具大类");
        return new(binding, target.ToolId ?? binding.Id, target.Id, target.GroupId,
            Math.Max(1, Nodes.Count(n => n.Kind == "subtool" && !n.Hidden && n.ToolId == target.ToolId)));
    }
    internal int ShortcutCycleCount(BrushRoute route) => Math.Max(1,
        Bindings.Where(b => b.Id.StartsWith("tool_") && b.Shortcut == route.Binding.Shortcut).Select(b => b.Id).Distinct().Count());
    internal ShortcutBinding ForCommand(string command) => Pick(Bindings.Where(b => b.Command == command), $"命令 {command}");
    private static ShortcutBinding Pick(IEnumerable<ShortcutBinding> candidates, string description)
    {
        foreach (var b in candidates.OrderBy(b => b.Shortcut.Length))
            try { Keys(b.Shortcut); return b; } catch (ArgumentException) { }
        throw new InvalidOperationException($"CSP 配置接口没有可发送的{description}快捷键；请在 CSP 中配置并重新加载。");
    }

    internal static IReadOnlyList<ushort> Keys(string shortcut)
    {
        var parts = Regex.Split(shortcut.Trim(), @"\s+\+\s+");
        var keys = new List<ushort>();
        foreach (var part in parts.Take(parts.Length - 1))
            keys.Add(part.ToUpperInvariant() switch { "CTRL" => 0x11, "SHIFT" => 0x10, "ALT" => 0x12,
                _ => throw new ArgumentException($"无法匹配快捷键：{shortcut}") });
        string key = parts[^1].ToUpperInvariant();
        var named = new Dictionary<string, ushort> { ["SPACE"] = 32, ["TAB"] = 9, ["ENTER"] = 13, ["RETURN"] = 13,
            ["ESC"] = 27, ["ESCAPE"] = 27, ["DELETE"] = 46, ["BACKSPACE"] = 8, ["INSERT"] = 45, ["HOME"] = 36,
            ["END"] = 35, ["PAGEUP"] = 33, ["PAGEDOWN"] = 34, ["LEFT"] = 37, ["UP"] = 38, ["RIGHT"] = 39,
            ["DOWN"] = 40, ["NUM+"] = 107, ["NUM-"] = 109, ["NUM*"] = 106, ["NUM/"] = 111, ["NUM."] = 110 };
        ushort vk;
        if (named.TryGetValue(key, out var n)) vk = n;
        else if (Regex.IsMatch(key, @"^F([1-9]|1[0-9]|2[0-4])$")) vk = (ushort)(111 + int.Parse(key[1..]));
        else if (Regex.IsMatch(key, @"^NUM[0-9]$")) vk = (ushort)(96 + key[^1] - '0');
        else if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) vk = key[0];
        else if (key.Length == 1)
        {
            short code = VkKeyScanEx(key[0], GetKeyboardLayout(0));
            if (code == -1) throw new ArgumentException($"当前键盘布局无法发送 {shortcut}");
            if ((code & 0x100) != 0) keys.Add(0x10);
            if ((code & 0x200) != 0) keys.Add(0x11);
            if ((code & 0x400) != 0) keys.Add(0x12);
            vk = (ushort)(code & 255);
        }
        else throw new ArgumentException($"无法匹配快捷键：{shortcut}");
        return keys.Distinct().Append(vk).ToArray();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "VkKeyScanExW")] private static extern short VkKeyScanEx(char c, nint layout);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);

    internal static string LocateMemoline(string? specified = null)
    {
        if (!string.IsNullOrWhiteSpace(specified)) return Path.GetFullPath(specified);
        foreach (var starting in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (DirectoryInfo? directory = new(starting); directory is not null; directory = directory.Parent)
                foreach (var path in new[] { Path.Combine(directory.FullName, "Memoline.exe"), Path.Combine(directory.FullName, "Memoline_demo_csponly", "Memoline.exe"),
                    Path.Combine(directory.FullName, "Memoline_demo_csponly", "publish", "win-x64", "Memoline.exe") })
                    if (File.Exists(path)) return path;
        throw new FileNotFoundException("找不到 Memoline.exe；请指定程序路径，或连接已提供 shortcuts 的会话。");
    }

    internal static async Task<ShortcutConfiguration> QueryAsync(string? executable, string? configRoot, CancellationToken token)
    {
        var path = LocateMemoline(executable);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(path)! };
        start.ArgumentList.Add("--shortcuts");
        if (!string.IsNullOrWhiteSpace(configRoot)) { start.ArgumentList.Add("--config-dir"); start.ArgumentList.Add(configRoot); }
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Memoline 快捷键查询。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("Memoline 快捷键查询失败：" + error.Trim());
        using var json = JsonDocument.Parse(output);
        return Parse(json.RootElement);
    }
}
