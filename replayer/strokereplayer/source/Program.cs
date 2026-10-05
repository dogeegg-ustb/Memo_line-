using System.Globalization;
using System.Runtime.InteropServices;

namespace StrokeReplay;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [STAThread]
    private static void Main(string[] args)
    {
        WindowsViewInput.PrepareDpiAwareness();
        if (args.Length > 0 && args[0] is "inspect" or "replay" or "listen" or "--help" or "-h" or "help")
        {
            AttachConsole(0xFFFFFFFF);
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
                RunCliAsync(args).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { Console.WriteLine("已停止。"); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ReplayWindow(args.FirstOrDefault()));
    }

    private static async Task RunCliAsync(string[] args)
    {
        if (args[0] is "--help" or "-h" or "help") { PrintHelp(); return; }
        string? endpoint = null;
        double speed = 1;
        bool inject = false;
        var mode = ReplayMode.Concentrated;
        double strokeGap = .2;
        string? path = args[0] == "listen" ? null : args.ElementAtOrDefault(1);
        for (int i = path is null ? 1 : 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--endpoint":
                case "--pipe": endpoint = Next(); break;
                case "--speed": speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--mode": mode = ReplayOptions.ParseMode(Next()); break;
                case "--stroke-gap": strokeGap = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--inject": inject = true; break;
                default: throw new ArgumentException($"未知参数：{args[i]}");
            }
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("参数缺少值。");
        }
        if (!double.IsFinite(speed) || speed <= 0) throw new ArgumentException("回放速度必须是正数。");
        var options = new ReplayOptions(mode, strokeGap);
        options.Validate();
        MemolineReplayDocument? document = null;
        if (args[0] != "listen")
        {
            if (path is null) throw new ArgumentException("请指定 .memoline 文件。");
            document = MemolineReplayReader.Read(path);
            Console.WriteLine(Describe(document));
            Console.WriteLine($"回放模式：{options.DisplayName}" + (mode == ReplayMode.Discrete ? $"，参考录制笔间隔，上限 {strokeGap * 1000:0.#} ms" : ""));
            if (args[0] == "inspect" || !inject) return;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        await using var monitor = new RecognizerViewMonitor(endpoint);
        monitor.StatusChanged += Console.WriteLine;
        var listening = monitor.RunAsync(stop.Token);
        try
        {
            if (document is null) await listening;
            else await ReplayDocumentAsync(document, monitor, speed, Console.WriteLine, stop.Token, options);
        }
        finally
        {
            stop.Cancel();
            try { await listening; } catch (OperationCanceledException) { }
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static string Describe(MemolineReplayDocument document)
    {
        var first = document.Strokes[0].StartTicks;
        var last = document.Strokes[^1].EndTicks;
        return string.Join(Environment.NewLine,
            $"文件：{document.FilePath}",
            $"会话：{document.SessionId}    时钟：{document.Frequency} ticks/秒",
            $"笔划：{document.Strokes.Count}    接触点：{document.Strokes.Sum(s => s.Samples.Count(p => p.InContact))}    录制跨度：{(last - first) / (double)document.Frequency:0.##} 秒",
            $"跳过视图导航：{document.SkippedNavigationOperations}    跳过画布外操作：{document.SkippedOutsideCanvasOperations}    跳过无效画布视图：{document.SkippedInvalidViewOperations}",
            "按每笔历史视图恢复缩放、旋转和画布位置，再回放数位板笔点。");
    }

    internal static async Task ReplayDocumentAsync(MemolineReplayDocument document, ICanvasViewFeed feed,
        double speed, Action<string> status, CancellationToken token, ReplayOptions? options = null)
    {
        options ??= new();
        options.Validate();
        if (!double.IsFinite(speed) || speed <= 0) throw new ArgumentException("回放速度必须是正数。");
        status("等待 Recognizer 当前画布视图…");
        await feed.WaitForViewAsync(-1, _ => true, TimeSpan.FromSeconds(20), token);
        var input = new WindowsViewInput();
        var restorer = new ViewRestorer(feed, input, status);
        int index = 0;
        foreach (var stroke in document.Strokes)
        {
            token.ThrowIfCancellationRequested();
            await restorer.RestoreAsync(stroke.View, token);
            var viewport = feed.Current?.Viewport ?? throw new InvalidOperationException("Recognizer 未提供画布视口。");
            foreach (var sample in stroke.Samples.Where(s => s.InContact))
                input.EnsureCanvasPoint(sample.X, sample.Y, viewport);
            var followingGap = options.GapBetween(stroke.EndTicks,
                index + 1 < document.Strokes.Count ? document.Strokes[index + 1].StartTicks : null, document.Frequency);
            status($"重放笔划 {++index}/{document.Strokes.Count}（操作 {stroke.OperationId}）…");
            int pointIndex = 0;
            long lastProgress = Environment.TickCount64;
            await PenReplayEngine.ReplayStrokeAsync(stroke, document.Frequency, speed, async sample =>
            {
                pointIndex++;
                if (Environment.TickCount64 - lastProgress >= 250 || pointIndex == stroke.Samples.Count)
                {
                    status($"重放笔划 {index}/{document.Strokes.Count}（操作 {stroke.OperationId}，笔点 {pointIndex}/{stroke.Samples.Count}）…");
                    lastProgress = Environment.TickCount64;
                }
                feed.EnsureConnected();
                input.EnsureTarget();
                long revision = feed.Revision;
                var current = feed.Current;
                if (current is null)
                {
                    status("Recognizer 视图未确认，暂停笔点并等待有效结果…");
                    current = await feed.WaitForViewAsync(revision, _ => true, TimeSpan.FromSeconds(20), token);
                    input.EnsureTarget();
                    status($"继续重放笔划 {index}/{document.Strokes.Count}（操作 {stroke.OperationId}）…");
                }
                if (!ViewRestorer.Matches(current, stroke.View))
                    throw new InvalidOperationException("回放期间已确认的画布视图发生变化，已停止。");
                if (sample.InContact) input.EnsureCanvasPoint(sample.X, sample.Y,
                    current.Viewport ?? throw new InvalidOperationException("Recognizer 未提供画布视口。"));
            }, token, options, status, followingGap);
        }
        status("回放完成。");
    }

    private static void PrintHelp() => Console.WriteLine("""
        StrokeReplay — Recognizer memoline 视图恢复与数位板重放

        StrokeReplay inspect <文件.memoline>
        StrokeReplay listen [--endpoint <当前会话.memoline.live.json> | --pipe <管道名>]
        StrokeReplay replay <文件.memoline> [--speed 1] [--mode concentrated|discrete] [--stroke-gap 0.2] [--endpoint <接口描述文件> | --pipe <管道名>] [--inject]

        不传 --inject 时只解析和检查文件。双击程序或传入 memoline 路径打开图形界面。
        接口留空时自动寻找仍在运行的 Recognizer 会话；持续订阅当前画布视图。
        回放需要已完成初始化的 Recognizer 和前台 CSP，画布尺寸须与录制一致。
        历史键盘、鼠标、笔刷、图层和色彩操作不重放；仅生成视图恢复所需的输入。
        空格按住的数位板导航操作和画布外数位板操作不作为笔迹重放。
        压力优先使用归一化压力，其次映射压力/设备压力上限或原始压力/设备压力上限，不重复套用驱动曲线。
        集中模式保持笔间立即继续；离散模式参考录制笔间隔，最多等待 200 ms。
        --stroke-gap 指定离散间隔上限（秒，0–0.2），最后一笔不额外等待。
        Ctrl+C 取消命令行监听或回放；图形界面可点击停止。
        """);
}
