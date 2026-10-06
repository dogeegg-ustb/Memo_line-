using System.Text;
using System.Text.Json;
using CanvasLayerWatcher;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--shortcuts")
            {
                if (args.Length != 1 && !(args.Length == 3 && args[1] == "--config-dir"))
                    throw new ArgumentException("用法：--shortcuts [--config-dir CSP用户配置目录]");
                Console.OutputEncoding = Encoding.UTF8;
                var configuration = RecorderSetupPanel.QueryAsync(
                    Path.Combine(DemoController.ResolveRuntimeDirectory(), "Recognizer"), args, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--subscribe")
                return SubscribeAsync(args).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] is "--verify" or "--seal" or "--extract-mechanical" or "--upgrade-dimensions" or "--compact")
                return Command(args);
            ApplicationConfiguration.Initialize();
            var controller = new DemoController(renderOnly: args.Length == 2 && args[0] == "--smoke");
            try
            {
                using var window = new MainWindow(args, controller);
                if (args.Length == 2 && args[0] == "--smoke") window.RenderSmoke(Path.GetFullPath(args[1]));
                else Application.Run(window);
            }
            finally { Task.Run(() => controller.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal)) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "Memoline", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task<int> SubscribeAsync(string[] args)
    {
        if (args.Length != 4 || args[2] is not ("--pipe" or "--endpoint"))
            throw new ArgumentException("用法：--subscribe shortcuts|layers|layerstage|subtools|core.brushState（逗号分隔可同时订阅） --endpoint 会话.memoline.live.json（或 --pipe 管道名）");
        Console.OutputEncoding = Encoding.UTF8;
        string pipe = args[3];
        if (args[2] == "--endpoint")
        {
            using var endpoint = JsonDocument.Parse(await File.ReadAllTextAsync(args[3]));
            pipe = endpoint.RootElement.GetProperty("pipeName").GetString()
                ?? throw new InvalidDataException("接口描述缺少 pipeName。");
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await foreach (var message in RecorderRealtimeClient.SubscribeAsync(pipe,
                args[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                cancellationToken: cancellation.Token))
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message, MemolineWriter.Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int Command(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        object result;
        switch (args[0])
        {
            case "--verify" when args.Length == 2:
                result = BundleArchive.Verify(args[1]);
                break;
            case "--seal" when args.Length == 4:
                result = BundleArchive.Seal(args[1], args[2], args[3]);
                break;
            case "--compact" when args.Length == 3:
                long originalBytes = new FileInfo(args[1]).Length;
                var compact = BundleArchive.Compact(args[1], args[2]);
                long compactBytes = new FileInfo(compact.Path).Length;
                result = new { compact.Path, originalBytes, compactBytes, savedBytes = originalBytes - compactBytes,
                    savedPercent = originalBytes == 0 ? 0 : Math.Round(100.0 * (originalBytes - compactBytes) / originalBytes, 2),
                    compact.MechanicalSha256, compact.AggregationSha256, compact.FixedSha256 };
                break;
            case "--upgrade-dimensions" when args.Length == 5:
                result = BundleArchive.UpgradeDimensions(args[1], args[2], args[3], args[4]);
                break;
            case "--extract-mechanical" when args.Length == 3:
                BundleArchive.ExtractMechanical(args[1], args[2]);
                result = new { path = Path.GetFullPath(args[2]) };
                break;
            default:
                throw new ArgumentException("用法：--verify 文件 | --seal 原生文件 聚集JSONL 输出文件 | --compact 集成文件 输出文件 | --extract-mechanical 集成文件 输出原生文件 | --upgrade-dimensions 集成文件 维度JSONL 版本 输出文件");
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
