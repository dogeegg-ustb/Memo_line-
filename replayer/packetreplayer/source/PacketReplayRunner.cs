using System.Text.Json;
using StrokeReplay;

namespace PacketReplay;

internal static class PacketReplayRunner
{
    internal static void Validate(PacketPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            if (step.Kind == "unsupported") throw new InvalidOperationException(step.Error);
            if (step.Kind == "brush" && step.Brush is null || step.Kind == "layer" && step.Layer is null || step.Kind == "view" && step.View is null
                || step.Kind == "color" && step.Color.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("包内存在未确认的历史状态：" + step.Description);
        }
        foreach (var drawing in plan.Strokes)
        {
            if (drawing.State.Brush is null || drawing.State.Layer is null)
                throw new InvalidOperationException($"操作 {drawing.Stroke.OperationId} 缺少已确认的历史笔刷或图层，不能可靠回放。");
            var begin = drawing.Stroke.Samples[0].EventId;
            var end = drawing.Stroke.Samples.LastOrDefault(s => s.EventId > 0)?.EventId ?? begin;
            if (plan.Steps.Any(s => s.Kind is "brush" or "layer" or "color" or "command" && s.Anchor >= begin && s.Anchor < end))
                throw new InvalidOperationException($"操作 {drawing.Stroke.OperationId} 接触过程中包含笔刷、图层、颜色或命令变更，无法按整笔回放。");
        }
        if (plan.Steps.Count == 0) throw new InvalidOperationException("这个聚集包没有可回放的笔画或状态事件。");
    }

    internal static bool ColorMatches(JsonElement actual, JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object) return true;
        if (actual.ValueKind != JsonValueKind.Object) return false;
        if (J.Text(actual, "kind") != J.Text(target, "kind")) return false;
        var a = J.Array(J.Get(actual, "rgb")).Select(J.Number).ToArray();
        var b = J.Array(J.Get(target, "rgb")).Select(J.Number).ToArray();
        return a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);
    }

    internal static async Task InitializeStatesAsync(LivePacketFeed feed, bool prepareSession, bool requireColor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!prepareSession) { feed.EnsureConnected(); return; }
        await feed.View.WaitForConnectionAsync(token);
        string[] modules = ["initializationConfiguration", "shortcuts", "canvasViewState", "brushState", "currentLayerState"];
        await feed.RequestAsync(requireColor ? [.. modules, "colorState"] : modules, token);
    }

    internal static void ValidateCapabilities(PacketPlan plan, IPacketStateFeed feed)
    {
        var brushNames = plan.Strokes.Select(s => s.State.Brush!.Name).Concat(plan.Steps.Where(s => s.Brush is not null).Select(s => s.Brush!.Name))
            .Concat(plan.InitialState.Brush is { } initialBrush ? [initialBrush.Name] : [])
            .Select(J.ToolName).Distinct().ToArray();
        var currentBrush = J.ToolName(feed.Brush?.Name ?? "");
        foreach (var name in brushNames.Where(n => n != currentBrush))
            (feed.Shortcuts ?? throw new InvalidOperationException("尚未取得 CSP 快捷键配置。")).RouteForBrush(name);
        var targetLayers = plan.Strokes.Select(s => s.State.Layer!).Concat(plan.Steps.Where(s => s.Layer is not null).Select(s => s.Layer!))
            .Concat(plan.InitialState.Layer is { } initialLayer ? [initialLayer] : [])
            .Select(J.Name).Distinct().ToArray();
        var layers = J.Array(J.Get(feed.Layers, "layers")).ToArray();
        foreach (var name in targetLayers)
            if (layers.Count(l => J.Name(J.Text(l, "name") ?? "") == name) != 1)
                throw new InvalidOperationException($"layers 结构表不能唯一定位包内目标图层 {name}。");
        if (targetLayers.Any(n => n != J.Name(feed.Layer ?? "")))
        {
            var config = feed.Shortcuts ?? throw new InvalidOperationException("尚未取得 CSP 快捷键配置。");
            if (!config.Bindings.Any(b => b.Command is "layerselectupperlayer" or "layerselectlowerlayer"))
                throw new InvalidOperationException("CSP 配置缺少选择上／下图层快捷键。");
        }
        foreach (var command in plan.Steps.Where(s => s.Kind == "command"))
            (feed.Shortcuts ?? throw new InvalidOperationException("尚未取得 CSP 快捷键配置。")).ForCommand(command.Command!);
    }

    internal static async Task RunAsync(PacketDocument document, PacketPlan plan, LivePacketFeed feed, double speed,
        ReplayOptions options, Action<string> status, Action<int>? stepDone, CancellationToken token, int startStep = 0, int? endStep = null,
        bool prepareSession = true, Action? prepared = null)
    {
        Validate(plan);
        if (!double.IsFinite(speed) || speed <= 0) throw new ArgumentException("回放速度必须为正数。");
        options.Validate();
        status(prepareSession ? "建立本次复现的当前状态表…" : "沿用已确认状态表，继续执行包内事件…");
        if (prepareSession) await feed.View.WaitForConnectionAsync(token);
        else feed.EnsureConnected();
        var windows = new WindowsViewInput();
        bool requireColor = plan.InitialState.Color.ValueKind == JsonValueKind.Object || plan.Steps.Any(s => s.Kind == "color")
            || plan.Strokes.Any(s => s.State.Color.ValueKind == JsonValueKind.Object);
        await InitializeStatesAsync(feed, prepareSession, requireColor, token);
        if (prepareSession)
        {
            var clipPath = J.Text(feed.View.InitializationConfiguration, "clipPath")
                ?? throw new InvalidOperationException("当前 Memoline 初始化配置未提供 .clip 路径。");
            int pid = feed.RecorderProcessId ?? throw new InvalidOperationException("当前 Memoline 接口缺少匹配会话的控制进程信息。");
            windows.EnsureTarget();
            await PacketSessionPreparation.PrepareAsync(feed, new PacketRecorderControl(pid), clipPath,
                System.Diagnostics.Stopwatch.GetTimestamp() - feed.View.ClockOriginTicks!.Value, status, token);
            prepared?.Invoke();
        }
        ValidateCapabilities(plan, feed);
        var input = new PacketWindowsInput(windows);
        var states = new PacketReplayStateTable(feed, input, new PacketViewFeed(feed), windows, status, plan.StateBefore(startStep));
        if (prepareSession) await states.RestoreAsync(token);
        int completed = startStep;
        int until = Math.Min(endStep ?? plan.Steps.Count, plan.Steps.Count);
        foreach (var step in plan.Steps.Skip(startStep).Take(until - startStep))
        {
            token.ThrowIfCancellationRequested();
            feed.EnsureConnected();
            windows.EnsureTarget();
            status($"事件 {completed + 1}/{plan.Steps.Count}：{step.Description}");
            switch (step.Kind)
            {
                case "brush": case "layer": case "view": case "color":
                    await states.ApplyAsync(step, token); break;
                case "command":
                    var binding = (feed.Shortcuts ?? throw new InvalidOperationException("缺少 CSP 快捷键配置。")).ForCommand(step.Command!);
                    await input.ShortcutAsync(binding, token);
                    await feed.RequestAsync(requireColor ? ["currentLayerState", "brushState", "canvasViewState", "colorState"]
                        : ["currentLayerState", "brushState", "canvasViewState"], token);
                    break;
                case "stroke":
                    var drawing = step.Drawing!;
                    await states.PrepareStrokeAsync(drawing, token);
                    var stroke = drawing.Stroke;
                    foreach (var point in stroke.Samples.Where(p => p.InContact))
                        windows.EnsureCanvasPoint(point.X, point.Y, feed.View.Current!.Viewport ?? throw new InvalidOperationException("当前接口缺少画布视口。"));
                    int nextIndex = plan.Strokes.ToList().IndexOf(drawing) + 1;
                    var gap = options.GapBetween(stroke.EndTicks, nextIndex < plan.Strokes.Count ? plan.Strokes[nextIndex].Stroke.StartTicks : null, document.Mechanical.Frequency);
                    await PenReplayEngine.ReplayStrokeAsync(stroke, document.Mechanical.Frequency, speed, sample =>
                    {
                        token.ThrowIfCancellationRequested();
                        feed.EnsureConnected();
                        windows.EnsureTarget();
                        if (feed.Brush?.Matches(drawing.State.Brush!) != true || J.Name(feed.Layer ?? "") != J.Name(drawing.State.Layer!)
                            || !ColorMatches(feed.Color, drawing.State.Color)) throw new InvalidOperationException("回放期间笔刷、图层或颜色发生变化，已停止。");
                        var current = feed.View.Current ?? throw new InvalidOperationException("当前画布状态不可用。");
                        if (!ViewRestorer.Matches(current, stroke.View)) throw new InvalidOperationException("回放期间画布视图变化，已停止。");
                        if (sample.InContact) windows.EnsureCanvasPoint(sample.X, sample.Y, current.Viewport ?? throw new InvalidOperationException("当前接口缺少画布视口。"));
                        return Task.CompletedTask;
                    }, token, options, status, gap);
                    break;
            }
            stepDone?.Invoke(++completed);
        }
        status(until == plan.Steps.Count ? $"聚集包 {plan.Packet.Number} 回放完成：{plan.Strokes.Count} 笔，{plan.Steps.Count} 个事件。"
            : $"本笔完成：事件进度 {until}/{plan.Steps.Count}。");
    }
}
