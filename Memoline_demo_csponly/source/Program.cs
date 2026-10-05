using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] is "--verify" or "--seal" or "--extract-mechanical" or "--upgrade-dimensions")
                return Command(args);
            ApplicationConfiguration.Initialize();
            var controller = new DemoController();
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
            case "--upgrade-dimensions" when args.Length == 5:
                result = BundleArchive.UpgradeDimensions(args[1], args[2], args[3], args[4]);
                break;
            case "--extract-mechanical" when args.Length == 3:
                BundleArchive.Verify(args[1]);
                using (var zip = ZipFile.OpenRead(args[1]))
                using (var source = (zip.GetEntry("mechanical/recording.memoline") ?? throw new InvalidDataException("原生记录 entry 缺失。")).Open())
                using (var target = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write)) source.CopyTo(target);
                result = new { path = Path.GetFullPath(args[2]) };
                break;
            default:
                throw new ArgumentException("用法：--verify 文件 | --seal 原生文件 聚集JSONL 输出文件 | --extract-mechanical 集成文件 输出原生文件 | --upgrade-dimensions 集成文件 维度JSONL 版本 输出文件");
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
