using System.Diagnostics;
using System.Text;

namespace MemolineDemo.Editing;

/// <summary>
/// Runs precise undo and event editing against a sealed project without CSP.
/// Replay is simulated by re-applying each replayed stroke's recorded image pixels inside its write
/// footprint, so the run checks dirty-matrix orchestration rather than CSP replay fidelity.
/// </summary>
internal static class OfflineEditProbe
{
    internal static int Run(string[] args)
    {
        // --offline-edit 工程.memoline 工作目录 [--layer uuid前缀] [--rect x,y,w,h]
        if (args.Length < 3) throw new ArgumentException("用法：--offline-edit 工程.memoline 空工作目录 [--layer UUID前缀] [--rect x,y,w,h] | --offline-edit --resolve 录制目录 CLIP路径");
        Console.OutputEncoding = Encoding.UTF8;
        if (args[1] == "--frames" && args.Length >= 3)
        {
            // --frames 机械流 [kind过滤正则]：按时间列出帧（相对首帧秒数）。
            var filter = args.Length > 3 ? new System.Text.RegularExpressions.Regex(args[3]) : null;
            long? origin = null, frequency = null;
            foreach (var frame in BehaviorRecognizer.Storage.Memoline.MemolineReader.Read(Path.GetFullPath(args[2])))
            {
                string kind = PacketReplay.J.Text(frame, "kind") ?? "";
                if (kind == "header") { frequency = PacketReplay.J.I64(frame, "frequency"); continue; }
                var data = PacketReplay.J.Get(frame, "data");
                string module = PacketReplay.J.Text(data, "module") ?? PacketReplay.J.Text(data, "command") ?? "";
                string line = $"{kind} {module} {PacketReplay.J.Text(data, "status")} {PacketReplay.J.Text(data, "transactionId")} {PacketReplay.J.Text(data, "reason")}".Trim();
                if (filter is not null && !filter.IsMatch(line)) continue;
                long ticks = PacketReplay.J.I64(frame, "ticks");
                origin ??= ticks;
                Console.WriteLine($"{(ticks - origin.Value) / (double)(frequency ?? 10_000_000),9:0.000}s  #{PacketReplay.J.U64(frame, "appendId")}  {line}");
            }
            return 0;
        }
        if (args[1] == "--frame-json" && args.Length >= 5)
        {
            // --frame-json 机械流 起始秒 结束秒 [kind过滤正则]：输出时间段内帧的原始 JSON（秒数相对首帧）。
            double from = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            double to = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            var filter = args.Length > 5 ? new System.Text.RegularExpressions.Regex(args[5]) : null;
            long? origin = null, frequency = null;
            foreach (var frame in BehaviorRecognizer.Storage.Memoline.MemolineReader.Read(Path.GetFullPath(args[2])))
            {
                string kind = PacketReplay.J.Text(frame, "kind") ?? "";
                if (kind == "header") { frequency = PacketReplay.J.I64(frame, "frequency"); continue; }
                if (kind == "screenshotBlob") continue;
                long ticks = PacketReplay.J.I64(frame, "ticks"); origin ??= ticks;
                double second = (ticks - origin.Value) / (double)(frequency ?? 10_000_000);
                if (second < from || second > to || filter is not null && !filter.IsMatch(kind)) continue;
                Console.WriteLine($"{second,9:0.000}s {frame.GetRawText()}");
            }
            return 0;
        }
        if (args[1] == "--stroke-views" && args.Length >= 3)
        {
            // --stroke-views 机械流：每笔重放时要恢复的画布视图（落笔秒数相对首帧）。
            var document = StrokeReplay.MemolineReplayReader.Read(Path.GetFullPath(args[2]));
            long origin = document.Strokes.Count > 0 ? document.Strokes.Min(s => s.StartTicks) : 0;
            foreach (var stroke in document.Strokes)
                Console.WriteLine($"{(stroke.StartTicks - origin) / (double)document.Frequency,9:0.000}s op={stroke.OperationId} " +
                    $"origin=({stroke.View.OriginX:0.#},{stroke.View.OriginY:0.#}) scale={stroke.View.ScalePercent} rot={stroke.View.RotationDegrees}");
            return 0;
        }
        if (args[1] == "--views" && args.Length >= 3)
        {
            // --views 机械流：画布视图状态（捕获时间、原点、缩放）与落笔时间对照，秒数相对首帧。
            long? origin = null, frequency = null;
            double Sec(long ticks) => (ticks - origin!.Value) / (double)(frequency ?? 10_000_000);
            foreach (var frame in BehaviorRecognizer.Storage.Memoline.MemolineReader.Read(Path.GetFullPath(args[2])))
            {
                string kind = PacketReplay.J.Text(frame, "kind") ?? "";
                if (kind == "header") { frequency = PacketReplay.J.I64(frame, "frequency"); continue; }
                long ticks = PacketReplay.J.I64(frame, "ticks"); origin ??= ticks;
                var data = PacketReplay.J.Get(frame, "data");
                IEnumerable<System.Text.Json.JsonElement> updates = kind == "statePackageResult"
                    ? PacketReplay.J.Array(PacketReplay.J.Get(PacketReplay.J.Get(data, "result"), "updates"))
                    : kind == "coreStateUpdated" ? [data] : [];
                foreach (var update in updates.Where(u => PacketReplay.J.Text(u, "module") == "canvasViewState"))
                {
                    var state = PacketReplay.J.Get(update, "state"); var o = PacketReplay.J.Get(state, "canvasOriginScreenPx");
                    long captured = PacketReplay.J.I64(PacketReplay.J.Get(update, "evidence"), "capturedTicks");
                    Console.WriteLine($"{Sec(ticks),9:0.000}s view {kind,-18} pkg={PacketReplay.J.Text(data, "packageId")?[..8]} after={PacketReplay.J.U64(data, "afterEventId")} " +
                        $"captured={(captured > 0 ? Sec(captured).ToString("0.000") : "?")} status={PacketReplay.J.Text(update, "status")} " +
                        $"origin=({PacketReplay.J.Get(o, "x")},{PacketReplay.J.Get(o, "y")}) scale={PacketReplay.J.Get(state, "ocrScalePercent")} rot={PacketReplay.J.Get(state, "ocrRotationDegrees")}");
                }
                if (kind is "penBegin" || kind == "keyInput" && PacketReplay.J.Text(data, "action") == "down" ||
                    kind is "mouseDown" or "mouseWheel")
                    Console.WriteLine($"{Sec(ticks),9:0.000}s {kind} event={PacketReplay.J.U64(frame, "eventId")} " +
                        $"({PacketReplay.J.Get(data, "x")},{PacketReplay.J.Get(data, "y")}) vk={PacketReplay.J.Get(data, "vk")}");
            }
            return 0;
        }
        if (args[1] == "--resolve" && args.Length == 4)
        {
            var watch = Stopwatch.StartNew();
            string resolved = ProjectRecordingStore.Resolve(args[2], args[3]);
            Console.WriteLine($"工程记录：{resolved}（{watch.Elapsed.TotalMilliseconds:0} ms）");
            return 0;
        }
        string project = Path.GetFullPath(args[1]), work = Path.GetFullPath(args[2]);
        string? layerPrefix = Option(args, "--layer"), rect = Option(args, "--rect");
        var clock = Stopwatch.StartNew();
        void Log(string text) => Console.WriteLine(
            $"[{clock.Elapsed.TotalSeconds,7:0.0}s {Process.GetCurrentProcess().PrivateMemorySize64 >> 20,5} MB 堆 {GC.GetTotalMemory(Environment.GetEnvironmentVariable("MEMOLINE_PROBE_LIVE") == "1") >> 20,5} MB] {text}");

        Log("读取工程：" + project);
        // --use-cache：与 Alt+M 相同，读写工程旁的 .memoline.cache（忽略工作目录）。
        var sessions = args.Contains("--use-cache") ? ProjectSessionReader.ReadProjectCached(project) : ProjectSessionReader.ReadProject(project, work);
        Log($"会话数 {sessions.Count}：" + string.Join("，", sessions.Select(s =>
            $"{s.SessionId[..8]}（帧 {s.Frames.Count}，包 {s.Packets.Count}，笔画 {s.Replay.Strokes.Count}）")));
        if (sessions.Count == 0) throw new InvalidDataException("工程中没有会话。");
        var current = sessions[^1];
        var prior = sessions.Take(sessions.Count - 1).ToArray();
        // --live 机械流.memoline.part layer-diffs目录：与 Alt+M 相同，工程全部会话作为历史，接上正在录制的会话。
        if (Option(args, "--live") is { } live)
        {
            int at = Array.IndexOf(args, "--live");
            // --boundary appendId：只读到该编辑边界为止，与当时 Alt+M 看到的一样。
            current = LiveSessionReader.Read(live, args[at + 2], Option(args, "--boundary") is { } boundary ? ulong.Parse(boundary) : 0);
            prior = sessions.ToArray();
            Log($"实时会话 {current.SessionId[..8]}：帧 {current.Frames.Count}，包 {current.Packets.Count}，笔画 {current.Replay.Strokes.Count}");
        }
        // History is indexed as the recorder does when recording starts.
        var index = prior.Length == 0 && !ProjectEditIndex.HasReplacements(current) ? EditIndexBuilder.Build(current)
            : ProjectEditIndex.Build(prior, current, ProjectEditIndex.BuildHistory(prior));
        EditingMemory.Park(index); EditingMemory.Compact();
        Log($"索引：图层 {index.Layers.Count}，合格/全部笔画 {index.Strokes.Values.Count(s => s.Eligible)}/{index.Strokes.Count}，图片 {index.Images.Count}，问题 {index.Issues.Count}");
        foreach (var issue in index.Issues.Take(20)) Log($"  索引问题 {issue.Code}：{issue.Message}");
        foreach (var reason in index.Strokes.Values.Where(s => !s.Eligible).GroupBy(s => s.IneligibleReason ?? "?").Take(10))
            Log($"  不合格笔画 ×{reason.Count()}：{reason.Key}");
        foreach (var layer in index.Layers.Values)
            Log($"  图层 {layer.Layer.Uuid} 会话 {layer.Layer.SessionId[..8]}：包 {layer.Packets.Count}，图片 {layer.Packets.Sum(p => p.Images.Count)}，" +
                $"笔画 {layer.Strokes.Count}（因果 {layer.Strokes.Count(s => s.IsCausal)}），链 {(layer.ChainValid ? "有效" : "无效")}，" +
                $"当前捕获 {(layer.Current is null ? "无" : layer.Current.Width + "×" + layer.Current.Height)}");

        foreach (var stroke in current.Replay.Strokes)
            Log($"  实时笔画 op {stroke.OperationId}：样本 {stroke.Samples.Count}，" +
                (index.Strokes.Values.FirstOrDefault(s => s.Key.SessionId == current.SessionId && s.OperationId == stroke.OperationId) is { } indexed
                    ? $"已索引，图层 {indexed.Layer.Uuid[..8]}，合格 {indexed.Eligible}，已撤销 {indexed.IsUndone}，图片 {indexed.Images.Count}"
                    : "未进入索引"));
        foreach (var layer in index.Layers.Values)
            foreach (var command in layer.UndoRedo)
                Log($"  未解析的 {command.Command}：会话序号 {command.SessionOrder}，输入 appendId {command.InputAppendId}，歧义 {command.Ambiguous}");
        var target = index.Layers.Values.Where(l => layerPrefix is null || l.Layer.Uuid.StartsWith(layerPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Strokes.Count(s => s.Eligible)).FirstOrDefault()
            ?? throw new InvalidDataException("没有可用于测试的图层。");
        var canvas = target.Current ?? target.Packets.Select(p => p.Current).LastOrDefault(c => c is not null)
            ?? throw new InvalidDataException("目标图层没有整层捕获。");
        var selection = new PixelMask(canvas.Width, canvas.Height);
        if (rect is not null)
        {
            var v = rect.Split(',').Select(int.Parse).ToArray();
            selection = new PixelMask(v[0], v[1], v[2], v[3]);
            for (int y = v[1]; y < v[1] + v[3]; y++) for (int x = v[0]; x < v[0] + v[2]; x++) selection.Set(x, y);
        }
        else for (int y = 0; y < canvas.Height; y++) for (int x = 0; x < canvas.Width; x++) selection.Set(x, y);
        Log($"目标图层 {target.Layer.Uuid}，选区 {selection.Left},{selection.Top} {selection.Width}×{selection.Height}");

        bool ok = true;
        if (args.Contains("--sweep"))
        {
            // 每张脏矩阵的第一笔：删除一次、改笔刷尺寸一次；汇总所有失败原因。
            var failures = new Dictionary<string, int>(); int runs = 0, passed = 0;
            void Quiet(string _) { }
            foreach (var swept in index.Images.Values.Where(i => i.Events.Any(k => index.Strokes.TryGetValue(k, out var s) && s.Eligible && !s.IsUndone)))
            {
                var key = swept.Events.First(k => index.Strokes[k].Eligible && !index.Strokes[k].IsUndone);
                var stroke = index.Strokes[key];
                var changes = new List<EventChange> { new(Delete: true) };
                if (stroke.Numbers.TryGetValue("brush_size", out double sweptSize))
                    changes.Add(new(Numbers: stroke.Numbers.ToDictionary(n => n.Key, n => n.Key == "brush_size" ? sweptSize * 2 : n.Value)));
                foreach (var change in changes)
                {
                    runs++;
                    var request = EditRequest.Event(stroke.Layer, selection, new Dictionary<EditableStrokeKey, EventChange> { [key] = change });
                    var plan = ReconstructionPlanner.Build(index, request);
                    string? reason = plan.IsValid ? null : plan.Issues[0].Code + "：" + plan.Issues[0].Message;
                    if (reason is null)
                    {
                        var result = ReconstructionEngine.RunAsync(plan, new SimulatedExecutor(index, request, Quiet), CancellationToken.None).GetAwaiter().GetResult();
                        if (result.Status is not (ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified))
                            reason = "执行 " + result.Status + "：" + (result.Issues.FirstOrDefault()?.Message ?? "");
                    }
                    if (reason is null) passed++;
                    else failures[reason] = failures.GetValueOrDefault(reason) + 1;
                }
            }
            Log($"扫描：{passed}/{runs} 次编辑可执行");
            foreach (var (reason, count) in failures.OrderByDescending(f => f.Value)) Log($"  ×{count} {reason}");
            return passed == runs ? 0 : 2;
        }
        // 1. 精确撤销
        var undo = PreciseUndoSelector.Select(index, target.Layer, selection);
        Log($"精确撤销选择：{undo.CountDisplay} 笔（候选 {undo.CandidateCount}，涉及图片 {undo.Images.Count}），问题 {undo.Issues.Count}");
        foreach (var issue in undo.Issues) Log($"  {issue.Code}：{issue.Message}");
        if (undo.IsValid)
            ok &= Execute("精确撤销", index, new EditRequest(EditKind.PreciseUndo, target.Layer, selection,
                undo.Selected.ToDictionary(s => s.Key, _ => new EventChange(Delete: true))), Log);
        else ok = false;

        // 2. 事件编辑：在含多笔的最早图片中删除一笔；再在同一图片中修改另一笔的笔刷尺寸
        var image = target.Packets.Where(p => !p.IsRecordingBaseline).SelectMany(p => p.Images)
            .Where(i => i.Events.Count(k => index.Strokes.TryGetValue(k, out var s) && s.Eligible) >= 2)
            .FirstOrDefault() ?? target.Packets.SelectMany(p => p.Images).FirstOrDefault(i => i.Events.Any());
        if (image is null) { Log("没有含事件的脏矩阵，跳过事件编辑。"); return ok ? 0 : 2; }
        var events = image.Events.Where(k => index.Strokes.TryGetValue(k, out var s) && s.Eligible).ToArray();
        Log($"事件编辑目标图片 {image.Key.PacketId}/{image.Key.ImageId}：事件 {image.Events.Count}（合格 {events.Length}），因果 {image.IsCausal}");
        ok &= Execute("事件编辑-删除", index, EditRequest.Event(target.Layer, selection,
            new Dictionary<EditableStrokeKey, EventChange> { [events[0]] = new(Delete: true) }), Log);
        var resize = events.Length > 1 ? events[1] : events[0];
        if (index.Strokes[resize].Numbers.TryGetValue("brush_size", out double size))
            ok &= Execute("事件编辑-改笔刷尺寸", index, EditRequest.Event(target.Layer, selection,
                new Dictionary<EditableStrokeKey, EventChange> { [resize] = new(Numbers: new Dictionary<string, double> { ["brush_size"] = size * 2 }) }), Log);
        else Log("目标笔画没有 brush_size 数值，跳过改参数测试。");
        Log(ok ? "离线测试完成。" : "离线测试有失败项。");
        return ok ? 0 : 2;
    }

    private static bool Execute(string name, EditIndex index, EditRequest request, Action<string> log)
    {
        log($"== {name}：修改 {request.Changes.Count} 笔 ==");
        var plan = ReconstructionPlanner.Build(index, request);
        log($"计划：{(plan.IsValid ? "有效" : "无效")}，问题 {plan.Issues.Count}");
        foreach (var issue in plan.Issues.Take(20)) log($"  {issue.Code}：{issue.Message}");
        foreach (var layer in plan.Layers)
        {
            log($"  图层 {layer.Layer.Uuid}：种子包 {layer.SeedPacketId}，组 {layer.Groups.Count}，不可信图片 {layer.Untrusted.Count}");
            foreach (var segment in layer.Segments)
                log($"    {segment.Kind}：图片 {segment.Images.Count}，事件 {segment.Events.Count}");
            foreach (var (key, reason) in layer.Reasons)
                log($"    不可信 {key.PacketId[^17..]}/{key.ImageId}（事件 {index.Images[key].Events.Count}，" +
                    $"已撤销 {index.Images[key].Events.Count(k => index.Strokes[k].IsUndone)}）：{reason.Rule} " +
                    string.Join(" ", reason.Pixels.Take(3).Select(p => $"({p.X},{p.Y})")));
        }
        if (!plan.IsValid) return false;
        var executor = new SimulatedExecutor(index, request, log);
        var result = ReconstructionEngine.RunAsync(plan, executor, CancellationToken.None).GetAwaiter().GetResult();
        log($"执行结果：{result.Status}，Flush {executor.Flushes} 次，重放事件 {executor.Replayed}，在线升级图片 {result.Promoted.Count}，问题 {result.Issues.Count}");
        foreach (var issue in result.Issues) log($"  {issue.Code}：{issue.Message}");
        foreach (var (key, layer) in result.Layers)
        {
            var original = index.Layers[key].Current;
            long changed = original is null ? -1 : ImageCompositor.Difference(layer, original).Count;
            log($"  复现层 {key.Uuid}：{layer.Width}×{layer.Height}，与编辑前当前图层相差 {changed} 像素");
        }
        // As the recorder does after an edit.
        foreach (var layer in result.Layers.Values) layer.Park();
        EditingMemory.Park(index); EditingMemory.Compact();
        log("释放编辑内存后");
        return result.Status is ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified;
    }

    private sealed class SimulatedExecutor(EditIndex index, EditRequest request, Action<string> log) : ICspReconstructionExecutor
    {
        internal int Flushes, Replayed;
        public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token)
        {
            Flushes++; Replayed += events.Count;
            for (int i = 1; i < events.Count; i++)
                if (events[i - 1].Stroke.BeginEventId >= events[i].Stroke.BeginEventId &&
                    events[i - 1].Stroke.SessionOrder == events[i].Stroke.SessionOrder)
                    throw new InvalidOperationException("重放清单未按原生 eventId 严格递增。");
            if (events.Select(e => e.Stroke.Key).Distinct().Count() != events.Count)
                throw new InvalidOperationException("重放清单含重复事件。");
            log($"    Flush#{Flushes} 图层 {layer.Uuid[..8]}：{events.Count} 笔 " +
                string.Join(",", events.Take(12).Select(e => e.Stroke.BeginEventId + (e.Change is null ? "" : "*"))) + (events.Count > 12 ? ",…" : ""));
            foreach (var e in events)
                foreach (var key in e.Stroke.Images)
                {
                    if (!index.Images.TryGetValue(key, out var image)) continue;
                    var m = image.Mask; var f = e.Stroke.WriteMask;
                    int x0 = m.Left, y0 = m.Top, x1 = m.Left + m.Width, y1 = m.Top + m.Height;
                    if (!f.IsEmpty)
                    { x0 = Math.Max(x0, f.Left); y0 = Math.Max(y0, f.Top); x1 = Math.Min(x1, f.Left + f.Width); y1 = Math.Min(y1, f.Top + f.Height); }
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            if (!m.Contains(x, y) || !f.IsEmpty && !f.Contains(x, y)) continue;
                            int source = ((y - m.Top) * m.Width + x - m.Left) * 4;
                            image.Now.Bytes.AsSpan(source, 4).CopyTo(seed.Bytes.AsSpan((y * seed.Width + x) * 4, 4));
                        }
                }
            return Task.FromResult(seed);
        }

        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
            => InstallReproductionLayerAsync(layer, expected, [], token);

        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected,
            IReadOnlyList<ReplayEvent> finalReplay, CancellationToken token)
        {
            int undoCount = request.Kind == EditKind.PreciseUndo ? request.Changes.Count : 0;
            log($"    安装复现层 {layer.Uuid[..8]}" + (undoCount + finalReplay.Count > 0
                ? $"，随后离散重放 {undoCount} 笔撤销笔画 + {finalReplay.Count} 笔推迟笔画" : ""));
            if (undoCount + finalReplay.Count == 0) return Task.FromResult(expected);
            // Same order as the CSP transaction: reverted contacts first, the undone strokes last.
            var undone = request.Changes.Keys.Where(_ => undoCount > 0).Select(k => new ReplayEvent(index.Strokes[k], null)).ToArray();
            var restored = finalReplay.Where(e => !request.Changes.ContainsKey(e.Stroke.Key)).DistinctBy(e => e.Stroke.Key).ToArray();
            int flushes = Flushes, replayed = Replayed;
            var result = expected.Copy();
            foreach (var batch in new[] { restored, undone }.Where(b => b.Length > 0))
                result = FlushAsync(layer, result, batch.OrderBy(e => e.Stroke.SessionOrder).ThenBy(e => e.Stroke.BeginEventId).ToArray(),
                    new Dictionary<LayerKey, ImageRgba>(), token).GetAwaiter().GetResult();
            Flushes = flushes; Replayed = replayed;
            return Task.FromResult(result);
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
