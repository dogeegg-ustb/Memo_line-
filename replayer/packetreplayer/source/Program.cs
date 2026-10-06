using System.Globalization;
using System.Runtime.InteropServices;
using StrokeReplay;

namespace PacketReplay;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint id);
    [STAThread] private static void Main(string[] args)
    {
        WindowsViewInput.PrepareDpiAwareness();
        if (args.FirstOrDefault() is "inspect" or "replay" or "shortcuts" or "--help" or "-h")
        {
            AttachConsole(0xFFFFFFFF);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            try { RunCliAsync(args).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Console.WriteLine("已停止。"); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PacketReplayWindow(args.FirstOrDefault()));
    }

    private static async Task RunCliAsync(string[] args)
    {
        if (args[0] is "--help" or "-h")
        {
            Console.WriteLine("""
                PacketReplay — 单个 Memoline 聚集包的事件和逐笔回放
                PacketReplay inspect <文件.memoline> [--packet 1]
                PacketReplay replay <文件.memoline> --packet 1 [--speed 1] [--mode concentrated|discrete]
                    [--stroke-gap 0.2] [--endpoint <live.json或管道>] [--memoline <Memoline.exe>]
                    [--config-dir <CSP配置目录>] [--inject]
                PacketReplay shortcuts [--memoline <Memoline.exe>] [--config-dir <CSP配置目录>]
                包序号从 1 开始，按时间窗排序。不传 --inject 只检查和预览，不向 CSP 发送输入。
                双击程序可选包，连续回放或每次回放下一笔。
                注入回放开始时通过 Memoline 自动保存当前 CSP 文档并刷新图层结构。
                状态判断及操作后确认使用 requestStates 主动请求，需要更新后的 Memoline 会话。
                """);
            return;
        }
        string? path = args[0] == "shortcuts" ? null : args.ElementAtOrDefault(1);
        string? endpoint = null, executable = null, configRoot = null;
        int? packetNumber = null;
        double speed = 1, gap = .2;
        ReplayMode mode = ReplayMode.Concentrated;
        bool inject = false;
        for (int i = path is null ? 1 : 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--packet": packetNumber = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--endpoint": case "--pipe": endpoint = Next(); break;
                case "--memoline": executable = Next(); break;
                case "--config-dir": configRoot = Next(); break;
                case "--speed": speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--stroke-gap": gap = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--mode": mode = ReplayOptions.ParseMode(Next()); break;
                case "--inject": inject = true; break;
                default: throw new ArgumentException("未知参数：" + args[i]);
            }
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("参数缺少值。");
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args[0] == "shortcuts")
            {
                var config = await ShortcutConfiguration.QueryAsync(executable, configRoot, stop.Token);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(config.Raw, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (path is null) throw new ArgumentException("请指定集成 .memoline 文件。");
            var document = PacketArchiveReader.Read(path, stop.Token);
            Console.WriteLine(Describe(document));
            if (packetNumber is null && args[0] == "inspect") return;
            if (packetNumber is null) throw new ArgumentException("回放须用 --packet 明确选择单个聚集包。");
            var packet = document.Packets.SingleOrDefault(p => p.Number == packetNumber) ?? throw new ArgumentException("聚集包序号不存在。");
            var plan = PacketArchiveReader.Plan(document, packet);
            Console.WriteLine(DescribePlan(plan));
            if (!inject || args[0] == "inspect") return;
            await using var feed = new LivePacketFeed(endpoint);
            feed.View.StatusChanged += Console.WriteLine;
            var listening = feed.View.RunAsync(stop.Token);
            try
            {
                try { feed.SetShortcuts(await ShortcutConfiguration.QueryAsync(executable, configRoot, stop.Token)); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
                { Console.WriteLine("快捷键查询：" + ex.Message + "；等待 shortcuts 订阅。"); }
                await PacketReplayRunner.RunAsync(document, plan, feed, speed, new(mode, gap), Console.WriteLine, null, stop.Token);
            }
            finally { stop.Cancel(); await listening; }
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static string Describe(PacketDocument document)
    {
        var lines = new List<string> { $"文件：{document.Path}", $"会话：{document.Verification.SessionId} · {document.Packets.Count} 个聚集包 · {document.Mechanical.Strokes.Count} 个可回放笔操作",
            $"跳过：导航 {document.Mechanical.SkippedNavigationOperations}，画布外 {document.Mechanical.SkippedOutsideCanvasOperations}，视图未确认 {document.Mechanical.SkippedInvalidViewOperations}" };
        foreach (var packet in document.Packets)
        {
            var plan = PacketArchiveReader.Plan(document, packet);
            lines.Add($"包 {packet.Number}: {packet.Id} · {packet.FromTicks / (double)document.Mechanical.Frequency:0.###}–{packet.ToTicks / (double)document.Mechanical.Frequency:0.###} 秒 · {plan.Strokes.Count} 笔 / {plan.Steps.Count} 事件 · {packet.Status}");
        }
        return string.Join(Environment.NewLine, lines);
    }
    internal static string DescribePlan(PacketPlan plan) => string.Join(Environment.NewLine,
        new[] { $"选择包 {plan.Packet.Number}：{plan.Packet.Id}" }.Concat(plan.Steps.Select((s, i) => $"{i + 1}. {s.Description}")).Concat(plan.Notes));
}
