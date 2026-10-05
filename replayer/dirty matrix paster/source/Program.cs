using System.Text.Json;

namespace DirtyMatrixPaster;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length > 0 && args[0] == "--inspect-clipboard") { Console.WriteLine(LayerClipboard.Inspect()); return 0; }
            if (args.Length >= 2 && args[0] == "--export")
            {
                using var result = Backend.ExportAsync(args[1], args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : null).GetAwaiter().GetResult();
                Console.WriteLine(result.Json); return 0;
            }
            if (args.Length >= 2 && args[0] is "--compose" or "--copy")
            {
                var inputs = new List<string>(); string? output = null;
                for (int i=1;i<args.Length;i++)
                {
                    if (args[i] == "--output" && i+1<args.Length) output=args[++i];
                    else if (args[i] == "--selection" && i+1<args.Length) inputs.AddRange(JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[++i])) ?? []);
                    else if (args[i] == "--layer-only") { }
                    else if (args[i].StartsWith("--")) throw new ArgumentException("不支持或缺少参数："+args[i]);
                    else inputs.Add(args[i]);
                }
                using var result = Backend.ComposeAsync(inputs,output).GetAwaiter().GetResult();
                if (args[0] == "--copy" && result.Ready)
                {
                    var formats = LayerClipboard.Write(result, !args.Contains("--layer-only"));
                    Console.WriteLine(JsonSerializer.Serialize(new { result = result.Root, clipboardFormats = formats }));
                }
                else Console.WriteLine(result.Json);
                return 0;
            }
            if (args.Length >= 3 && args[0] == "--smoke")
            {
                using var result = Backend.ComposeAsync([args[1]]).GetAwaiter().GetResult();
                using var window = new MainWindow(); window.ShowResult(result); window.RenderSmoke(Path.GetFullPath(args[2])); return 0;
            }
            if (args.Length >= 3 && args[0] == "--smoke-selection")
            {
                var inputs = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[1])) ?? [];
                using var result = Backend.ComposeAsync(inputs).GetAwaiter().GetResult();
                using var window = new MainWindow(inputs); window.ShowResult(result); window.RenderSmoke(Path.GetFullPath(args[2])); return 0;
            }
            if (args.Length > 0 && args[0].StartsWith("--")) throw new ArgumentException("用法: --compose/--copy <多个包> [--output 目录] [--layer-only] / --inspect-clipboard / --export <包> [PSD目录]");
            Application.Run(new MainWindow(args)); return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0 && args[0].StartsWith("--")) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "dirty matrix paster", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
