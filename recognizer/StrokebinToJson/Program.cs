using BehaviorRecognizer.Storage.Strokebin;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

if (args.Length is < 1 or > 2)
{
    PrintUsage();
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"找不到输入文件: {inputPath}");
    return 1;
}

var outputPath = Path.GetFullPath(args.Length == 2
    ? args[1]
    : Path.ChangeExtension(inputPath, ".json"));

if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("输入文件和输出文件不能相同。");
    return 1;
}

try
{
    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
        Directory.CreateDirectory(outputDirectory);

    await StrokeJsonExporter.ExportFileAsync(inputPath, outputPath);
    Console.WriteLine($"已导出 JSON: {outputPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"转换失败: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("STRO 笔迹文件 JSON 译码器");
    Console.WriteLine();
    Console.WriteLine("用法:");
    Console.WriteLine("  StrokebinToJson <input.strokebin> [output.json]");
    Console.WriteLine();
    Console.WriteLine("省略输出路径时，会在输入文件旁生成同名 .json 文件。");
}
