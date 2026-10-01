using BehaviorRecognizer.Bootstrap;
using BehaviorRecognizer.Session;
using BehaviorRecognizer.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorRecognizer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Capture.CspWindowProbe.EnableDpiAwareness();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("BehaviorRecognizer — CSP 输入采集");
        Console.WriteLine("默认沿用 OTD 笔采集；可选被动 Windows 笔模式。");
        Console.WriteLine();

        if (args.Length > 0 && args[0] is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--export")
        {
            var exporter = new JsonEventExporter();
            // .memoline 和旧容器均可导出供检查。
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
        services.AddBehaviorRecognizer(paths, !args.Contains("--passive-pen", StringComparer.OrdinalIgnoreCase));
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
              BehaviorRecognizer --passive-pen        使用 Windows 被动笔事件
              BehaviorRecognizer --export <memoline> [jsonl]
              BehaviorRecognizer --recover [strokeDir]
              BehaviorRecognizer --help

            说明:
              - 用户无需安装 OpenTabletDriver 主程序
              - 默认保留原有 OTD 笔报告采集和压力数据
              - --passive-pen 不打开数位板 HID，但压力可能不可用
              - 启动时自动加载默认笔配置、检测 Windows Ink / vMulti
              - vMulti 缺失只提示引导，不阻塞基础采集
              - 有效键盘、鼠标和笔输入写入 程序目录\procedure\stroke\*.memoline
            """);
    }
}
