using BehaviorRecognizer.Bootstrap;
using BehaviorRecognizer.Session;
using BehaviorRecognizer.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorRecognizer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("BehaviorRecognizer — 个人数位板持续采集");
        Console.WriteLine("内嵌 OpenTabletDriver 采集核心，无需单独安装 OTD。");
        Console.WriteLine();

        if (args.Length > 0 && args[0] is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--export")
        {
            var exporter = new JsonEventExporter();
            // .strokebin 默认导出为 .json；旧 .brlog 仍可为 .jsonl
            var output = args.Length >= 3
                ? args[2]
                : Path.ChangeExtension(args[1], args[1].EndsWith(".strokebin", StringComparison.OrdinalIgnoreCase) ? ".json" : ".jsonl");
            await exporter.ExportJsonAsync(args[1], output);
            Console.WriteLine($"已导出: {output}");
            return 0;
        }

        if (args.Length >= 1 && args[0] == "--recover")
        {
            var layout = ApplicationPaths.EnsureLayout();
            var dir = args.Length >= 2 ? args[1] : Path.Combine(layout.StrokeRoot, "stroke");
            var leftover = await new RecoveryReader().RecoverPartFilesAsync(dir);
            Console.WriteLine($"扫描完成: 发现 {leftover} 个未完整 .part（已保留，未改名）");
            return 0;
        }

        var paths = ApplicationPaths.EnsureLayout();
        var services = new ServiceCollection();
        services.AddBehaviorRecognizer(paths);
        await using var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<CapabilityOrchestrator>();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await orchestrator.StartAsync(cts.Token);

            if (orchestrator.LastEnvironment?.VMulti is
                Abstractions.Environment.VMultiStatus.NotInstalled or
                Abstractions.Environment.VMultiStatus.InstalledButInactive)
            {
                Console.WriteLine("提示: 输入 V 然后 Enter 可打开 vMulti 安装引导（不阻塞采集）。");
            }

            await WaitForExitAsync(orchestrator, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败: {ex}");
            PauseIfInteractive();
            return 1;
        }
        finally
        {
            try
            {
                await orchestrator.StopAsync();
                Console.WriteLine("采集已停止，会话已落盘。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"停止时出错: {ex.Message}");
            }
        }

        return 0;
    }

    private static async Task WaitForExitAsync(CapabilityOrchestrator orchestrator, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var readTask = Task.Run(() => Console.ReadLine(), token);
            var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, token));
            if (completed != readTask)
                break;

            var line = await readTask;
            if (string.Equals(line, "V", StringComparison.OrdinalIgnoreCase))
            {
                orchestrator.OpenVMultiInstallGuide();
                continue;
            }

            break;
        }
    }

    private static void PauseIfInteractive()
    {
        try
        {
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("按任意键退出…");
                Console.ReadKey(intercept: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            用法:
              BehaviorRecognizer                      启动持续采集
              BehaviorRecognizer --export <strokebin> [json]
              BehaviorRecognizer --recover [strokeDir]
              BehaviorRecognizer --help

            说明:
              - 用户无需安装 OpenTabletDriver 主程序
              - 启动时自动加载默认笔配置、检测 Windows Ink / vMulti
              - vMulti 缺失只提示引导，不阻塞基础采集
              - 笔迹写入 程序目录\procedure\stroke\yyyyMMdd_HHmmss.strokebin
            """);
    }
}
