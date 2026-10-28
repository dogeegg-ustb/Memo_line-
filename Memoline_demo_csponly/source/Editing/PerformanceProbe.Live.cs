using System.Diagnostics;
using CanvasLayerWatcher;

namespace MemolineDemo.Editing;

internal static partial class PerformanceProbe
{
    /// <summary>
    /// --live-transaction-test CLIP 录制.memoline 笔数 左 上：在 CSP 当前打开的副本上跑一次真实的精确撤销事务
    /// （CspEditTransaction：先删后贴、重放、捕获），录制器管道与面板 OCR 用最小替身。先粘贴一张整画布图建立
    /// 已知的目标层，再把它替换为挖掉一块的合成图，并按生产回放设置离散重放录制笔画。
    /// </summary>
    private static int LiveTransactionTest(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string clip = Path.GetFullPath(args[1]);
        var document = StrokeReplay.MemolineReplayReader.Read(Path.GetFullPath(args[2]));
        int count = int.Parse(args[3]), left = int.Parse(args[4]), top = int.Parse(args[5]);
        var recorded = document.Strokes.Where(s => s.Samples.Count >= 2).Take(count).ToArray();
        var contacts = recorded.SelectMany(s => s.Samples.Where(p => p.InContact)).ToArray();
        double dx = left - contacts.Min(p => p.X), dy = top - contacts.Min(p => p.Y);
        recorded = recorded.Select(s => s with { Samples = s.Samples.Select(p => p with { X = p.X + dx, Y = p.Y + dy }).ToArray() }).ToArray();
        var csp = LiveCsp.Find();
        LiveCsp.Activate(csp);
        string bridge = @"D:\Memo_Line\Memo_Line\Memoline_demo_csponly\clip-layer-bridge.exe";
        var actions = new LiveTransactionActions(csp, clip, bridge, document.Frequency, recorded);
        var (width, height) = (4961, 7016);
        // A known target: a full-canvas layer pasted from a recorded capture, selected by the paste.
        var seed = FastPng.TryDecodeFile(@"D:\Memo_Line\Memo_Line\Memoline_demo_csponly\publish\win-x64\.work\e4636ee074d647579cd0f628a6b1a621\layer-diffs\snapshots\trigger-00000000001048998449-c38640ea.png",
            false, out _, out _)!;
        actions.SetClipboardImageAsync(new ImageRgba(width, height, seed), CancellationToken.None).GetAwaiter().GetResult();
        actions.PasteAsync(CancellationToken.None).GetAwaiter().GetResult();
        var start = actions.SaveAndInspectAsync(CancellationToken.None, expectChange: true).GetAwaiter().GetResult();
        var target = actions.ReadCurrentLayerAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"目标层「{target.Name}」{target.Uuid}；结构：{string.Join(" / ", start.Layers.Select(l => l.Name))}");
        actions.Log.Clear();
        var layer = new LayerKey("live", 1, target.Uuid!);
        // The composite precise undo would paste: the target with a block rewound to transparent.
        var expected = seed.ToArray();
        for (int y = 2900; y < 3500; y++) expected.AsSpan((y * width + 2000) * 4, 900 * 4).Clear();
        var events = recorded.Select((s, i) => new ReplayEvent(new EditableStroke(new("live", (ulong)(i + 1)), s.OperationId, s.StartTicks, s.EndTicks,
            s.Samples, "pen", layer, false) { BeginEventId = (ulong)(i + 1) }, null)).ToArray();
        using var journal = EditJournal.Open(Path.Combine(Path.GetDirectoryName(clip)!, "journal-" + Guid.NewGuid().ToString("N")[..8]));
        var transaction = new CspEditTransaction(actions, journal, "live-tx", new Dictionary<LayerKey, CanvasSize> { [layer] = new(width, height) },
            System.Text.Json.JsonSerializer.SerializeToElement(new { kind = "live-test" }),
            new Dictionary<LayerKey, IReadOnlyList<ReplayEvent>> { [layer] = events });
        var total = Stopwatch.StartNew();
        actions.Mark("（开始）");
        transaction.BeginAsync(CancellationToken.None).GetAwaiter().GetResult();
        actions.Mark("begin");
        var final = transaction.InstallReproductionLayerAsync(layer, new ImageRgba(width, height, expected), CancellationToken.None).GetAwaiter().GetResult();
        actions.Mark("install");
        transaction.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"事务合计 {total.Elapsed.TotalMilliseconds:0} ms");
        foreach (var line in actions.Log) Console.WriteLine("  " + line);
        var end = actions.SaveAndInspectAsync(CancellationToken.None, refresh: true).GetAwaiter().GetResult();
        long added = 0, outside = 0;
        for (int i = 0; i < expected.Length; i += 4)
            if (!final.Bytes.AsSpan(i, 4).SequenceEqual(expected.AsSpan(i, 4)))
            {
                added++; int p = i / 4, x = p % width, y = p / width;
                if (x < 2000 || x >= 2900 || y < 2900 || y >= 3500) outside++;
            }
        Console.WriteLine($"结束结构：{string.Join(" / ", end.Layers.Select(l => l.Name))}；原层仍在：{end.Layers.Any(l => l.Uuid == target.Uuid)}；" +
            $"复现层 {actions.Replaced.LastOrDefault().Reproduction}；最终层与粘贴图不同的像素 {added}（其中挖空区外 {outside}）");
        return 0;
    }

    /// <summary>
    /// --live-shield-test 录制.memoline 笔数 左 上 [noshield]：把录制笔画按精确撤销的离散设置重放到 CSP 当前画布（必须是
    /// 可以随意画的临时画布，平移到屏幕 左,上 处），默认在输入屏蔽窗口下，每个采样像事务一样检查落点在 CSP 上。
    /// 同时让另一支笔悬停，看有没有射线、Ctrl+Z 是否一次只撤一笔。
    /// </summary>
    private static int LiveShieldTest(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var document = StrokeReplay.MemolineReplayReader.Read(Path.GetFullPath(args[1]));
        int count = int.Parse(args[2]), left = int.Parse(args[3]), top = int.Parse(args[4]);
        bool shielded = !args.Skip(5).Contains("noshield");
        var strokes = document.Strokes.Where(s => s.Samples.Count >= 2).Take(count).ToArray();
        var contacts = strokes.SelectMany(s => s.Samples.Where(p => p.InContact)).ToArray();
        double dx = left - contacts.Min(p => p.X), dy = top - contacts.Min(p => p.Y);
        strokes = strokes.Select(s => s with { Samples = s.Samples.Select(p => p with { X = p.X + dx, Y = p.Y + dy }).ToArray() }).ToArray();
        if (args.Skip(5).Contains("dry"))
        {
            Console.WriteLine($"{strokes.Length} 笔，范围 {left},{top} – {contacts.Max(p => p.X) + dx:0},{contacts.Max(p => p.Y) + dy:0}");
            return 0;
        }
        var input = new StrokeReplay.WindowsViewInput();
        var desktop = SystemInformation.VirtualScreen;
        var options = WindowsCspEditingActions.ReplayTiming.Options(StrokeReplay.ReplayMode.Discrete);
        var watch = Stopwatch.StartNew();
        using (shielded ? StrokeReplay.InputShield.Raise() : null)
            for (int i = 0; i < strokes.Length; i++)
            {
                var gap = options.GapBetween(strokes[i].EndTicks, i + 1 < strokes.Length ? strokes[i + 1].StartTicks : null, document.Frequency);
                StrokeReplay.PenReplayEngine.ReplayStrokeCoreAsync(strokes[i], document.Frequency, 1,
                    sample => { input.EnsureCanvasPoint(sample.X, sample.Y, desktop); return Task.CompletedTask; }, CancellationToken.None,
                    options, StrokeReplay.PenReplayEngine.CreateSyntheticInjector, StrokeReplay.PenReplayEngine.PreciseDelayAsync, followingGap: gap)
                    .GetAwaiter().GetResult();
            }
        Console.WriteLine($"{strokes.Length} 笔（{(shielded ? "有" : "无")}输入屏蔽）重放 {watch.Elapsed.TotalMilliseconds:0} ms，" +
            $"范围 {left},{top} – {contacts.Max(p => p.X) + dx:0},{contacts.Max(p => p.Y) + dy:0}");
        return 0;
    }

    /// <summary>
    /// --bench-diff AFTER.png NOW.png 参考差异包目录：用当前代码重新生成脏矩阵图片，与旧代码当时生成的差异包比较
    /// （图片编号、外框、变化像素数，以及四种补丁逐像素），并计时比较与缩略图。
    /// </summary>
    private static int BenchDiff(string[] args)
    {
        string after = Path.GetFullPath(args[1]), now = Path.GetFullPath(args[2]), reference = Path.GetFullPath(args[3]);
        string output = Path.Combine(Path.GetTempPath(), "memoline-bench-diff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(output);
        var evidence = new DirtyEvidence(new EvidenceWindow("bench", 10_000_000, 0, 1, true, [], [], []), [], []);
        var watch = Stopwatch.StartNew();
        var diff = LayerDiff.Compare(after, now, evidence, output, CancellationToken.None);
        Console.WriteLine($"LayerDiff.Compare（含补丁 PNG）{watch.Elapsed.TotalMilliseconds:0} ms，图片 {diff.Images.Length}，变化像素 {diff.ChangedPixels}");
        FastPng.TryReadSize(File.ReadAllBytes(now), out int width, out int height);
        watch.Restart();
        CanvasThumbnail.Save(now, Path.Combine(output, "canvas-preview.png"), new(width, height), CancellationToken.None);
        Console.WriteLine($"缩略图 {watch.Elapsed.TotalMilliseconds:0} ms（推进差异包时与比较并行）");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(reference, "manifest.json")));
        var old = manifest.RootElement.GetProperty("images").EnumerateArray().ToArray();
        bool ok = old.Length == diff.Images.Length;
        Console.WriteLine($"图片数：旧 {old.Length}，新 {diff.Images.Length}");
        for (int i = 0; i < Math.Min(old.Length, diff.Images.Length); i++)
        {
            var o = old[i]; var n = diff.Images[i]; var b = o.GetProperty("bounds");
            bool same = o.GetProperty("id").GetString() == n.Id && b.GetProperty("left").GetInt32() == n.Bounds.Left && b.GetProperty("top").GetInt32() == n.Bounds.Top &&
                b.GetProperty("right").GetInt32() == n.Bounds.Right && b.GetProperty("bottom").GetInt32() == n.Bounds.Bottom &&
                o.GetProperty("changedPixels").GetInt64() == n.ChangedPixels;
            foreach (var (kind, file) in new[] { ("after", n.AfterImage), ("now", n.NowImage), ("mask", n.MaskImage), ("difference", n.DifferenceImage) })
            {
                var mine = DecodeGdi(File.ReadAllBytes(Path.Combine(output, file)), out _, out _);
                var theirs = DecodeGdi(File.ReadAllBytes(Path.Combine(reference, file)), out _, out _);
                if (!mine.AsSpan().SequenceEqual(theirs)) { same = false; Console.WriteLine($"  {n.Id} {kind} 像素不同"); }
            }
            Console.WriteLine($"  {n.Id} {n.Bounds.Left},{n.Bounds.Top}–{n.Bounds.Right},{n.Bounds.Bottom} 变化 {n.ChangedPixels}：{(same ? "一致" : "不一致")}");
            ok &= same;
        }
        var thumbMine = DecodeGdi(File.ReadAllBytes(Path.Combine(output, "canvas-preview.png")), out _, out _);
        var thumbTheirs = DecodeGdi(File.ReadAllBytes(Path.Combine(reference, "canvas-preview.png")), out _, out _);
        Console.WriteLine($"缩略图像素一致：{thumbMine.AsSpan().SequenceEqual(thumbTheirs)}");
        ok &= thumbMine.AsSpan().SequenceEqual(thumbTheirs);
        Console.WriteLine(ok ? "脏矩阵与旧结果完全一致。" : "存在差异！");
        return ok ? 0 : 2;
    }

    /// <summary>Drives CSP directly for <see cref="LiveTransactionTest"/>: saves with Ctrl+S, reads the copy with the
    /// bridge and clip_layers, replays the recorded strokes with the production replay settings.</summary>
    private sealed class LiveTransactionActions(IntPtr csp, string clip, string bridge, long frequency,
        IReadOnlyList<StrokeReplay.MemolineReplayStroke> strokes) : ICspEditingActions
    {
        internal readonly List<string> Log = [];
        internal readonly List<(string Replaced, string Reproduction)> Replaced = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _last;
        private bool _input = true;
        private CspLayerInspection? _inspection;
        private string? _copy;
        private long _current = -1;
        private Dictionary<string, long> _ids = [];
        internal void Mark(string what) { double now = _clock.Elapsed.TotalMilliseconds; Log.Add($"{what,-24} +{now - _last,6:0} ms"); _last = now; }
        public event Action<PhysicalInputEvidence>? PhysicalInput { add { } remove { } }
        public bool CaptureRequestsPaused { get; set; }
        public Task<ulong> BeginNativeTransactionAsync(string id, CancellationToken token) => Task.FromResult(1UL);
        public Task EndNativeTransactionAsync(string id, string outcome, CancellationToken token) => Task.CompletedTask;
        public Task<IAsyncDisposable> AcquireCaptureCoordinatorAsync(CancellationToken token) => Task.FromResult<IAsyncDisposable>(new Nothing());
        private sealed class Nothing : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
        public Task<bool> VerifyShortcutsAsync(CancellationToken token) => Task.FromResult(true);
        public bool SelectionConfirmed => true;
        public async Task<CspLayerInspection> SaveAndInspectAsync(CancellationToken token, bool refresh = false, bool expectChange = false)
        {
            if (!refresh && !_input && _inspection is { } known) return known;
            Mark("（保存前）");
            var before = SaveCapture.Stat(clip);
            LiveCsp.Activate(csp); LiveCsp.Combo(csp, Keys.ControlKey, Keys.S);
            _copy = Path.Combine(Path.GetDirectoryName(clip)!, "tx-" + Guid.NewGuid().ToString("N")[..8] + ".clip");
            await SaveCapture.StableSnapshotAsync(clip, _copy, before, token, allowUnchanged: true, expectChange,
                expectChange ? _ => { LiveCsp.Combo(csp, Keys.ControlKey, Keys.S); return Task.CompletedTask; } : null);
            Mark(expectChange ? "保存（已修改）" : "保存");
            using var inspected = System.Text.Json.JsonDocument.Parse(LiveCsp.Run(bridge, "inspect", _copy));
            var rows = inspected.RootElement.GetProperty("layers").EnumerateArray()
                .Where(l => !(l.TryGetProperty("isRoot", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True))
                .OrderBy(l => l.TryGetProperty("stackOrder", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetInt64() : long.MaxValue).ToArray();
            _ids = rows.ToDictionary(r => r.GetProperty("uuid").GetString()!, r => r.GetProperty("id").GetInt64());
            using var parsed = System.Text.Json.JsonDocument.Parse(LiveCsp.Run("py", "-3", @"D:\Memo_Line\Memo_Line\recognizer\clip_layers\clip_layers.py", _copy));
            _current = parsed.RootElement.GetProperty("canvas").GetProperty("current_layer_id").GetInt64();
            _input = false;
            Mark("读取结构");
            string revision;
            using (var file = File.OpenRead(_copy)) revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
            return _inspection = new("live", 1, true, rows.Select(r => new CspLayerEvidence(r.GetProperty("uuid").GetString()!, r.GetProperty("name").GetString() ?? "",
                r.GetProperty("isRaster").GetBoolean(), r.GetProperty("folder").GetBoolean(), r.GetProperty("visible").GetBoolean(),
                r.TryGetProperty("parentUuid", out var parent) ? parent.GetString() : null)).ToArray(), revision);
        }
        public Task NavigateToLayerAsync(string name, CancellationToken token) => throw new NotSupportedException("实机测试不做面板导航。");
        public Task SelectAdjacentLayerAsync(bool upper, CancellationToken token)
        { _input = true; LiveCsp.Combo(csp, Keys.Menu, upper ? Keys.OemCloseBrackets : Keys.OemOpenBrackets); return Task.CompletedTask; }
        public Task<CspCurrentLayerEvidence> ReadCurrentLayerAsync(CancellationToken token)
        {
            var row = _inspection?.Layers.FirstOrDefault(l => _ids.TryGetValue(l.Uuid, out long id) && id == _current);
            return Task.FromResult(row is null ? new CspCurrentLayerEvidence("?", false) : new CspCurrentLayerEvidence(row.Name, true, row.Uuid));
        }
        public Task<CspHideEvidence> HideLayerAsync(CspLayerEvidence layer, CancellationToken token) => throw new NotSupportedException();
        public Task SetClipboardImageAsync(ImageRgba image, CancellationToken token)
        {
            var png = FastPng.Encode(image.Width, image.Height, image.Bytes, bgra: false);
            var thread = new Thread(() => DirtyMatrixPaster.ClipboardImageCodec.Publish(png, null));
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            Mark("剪贴板");
            return Task.CompletedTask;
        }
        public Task PasteAsync(CancellationToken token)
        {
            _input = true; LiveCsp.Activate(csp); LiveCsp.Combo(csp, Keys.ControlKey, Keys.D); LiveCsp.Combo(csp, Keys.ControlKey, Keys.V);
            Mark("粘贴按键"); return Task.CompletedTask;
        }
        public Task<LayerExportEvidence> ExportLayerAsync(LayerKey layer, CancellationToken token)
        {
            string png = Path.ChangeExtension(_copy!, null) + "-" + _ids[layer.Uuid] + ".png";
            LiveCsp.Run(bridge, "export-id", _copy!, _ids[layer.Uuid].ToString(), png, "4961", "7016");
            Mark("导出");
            return Task.FromResult(new LayerExportEvidence(layer, 4961, 7016, true, File.ReadAllBytes(png)));
        }
        public Task VerifyReferenceSceneAsync(LayerKey layer, IReadOnlyList<ReplayEvent> events, StrokeReplay.ReplayMode mode,
            IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token) => Task.CompletedTask;
        public async Task ReplayAsync(CspLayerEvidence work, IReadOnlyList<ReplayEvent> events, StrokeReplay.ReplayMode mode,
            IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token)
        {
            _input = true;
            var options = WindowsCspEditingActions.ReplayTiming.Options(mode);
            await Task.Run(async () =>
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var stroke = strokes[(int)events[i].Stroke.Key.BeginAppendId - 1];
                    var gap = options.GapBetween(stroke.EndTicks, i + 1 < events.Count ? stroke.EndTicks : null, frequency);
                    await StrokeReplay.PenReplayEngine.ReplayStrokeAsync(stroke, frequency, 1, _ =>
                        LiveCsp.Foreground(csp) ? Task.CompletedTask : throw new InvalidOperationException("CSP 不在前台。"), token, options, followingGap: gap);
                }
            }, token);
            Mark($"重放 {events.Count} 笔 {mode}");
        }
        public Task DeleteLayerAsync(CancellationToken token)
        { _input = true; LiveCsp.Activate(csp); LiveCsp.Combo(csp, Keys.ControlKey, Keys.Menu, Keys.A); Mark("删除按键"); return Task.CompletedTask; }
        public Task RegisterBaselineAsync(LayerExportEvidence capture, CancellationToken token) => Task.CompletedTask;
        public void ReleaseInputs() { }
        public void Status(string text) => Log.Add("状态：" + text);
        public void LayerReplaced(string replacedUuid, string reproductionUuid) => Replaced.Add((replacedUuid, reproductionUuid));
    }
}
