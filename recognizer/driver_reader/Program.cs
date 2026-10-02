using System.Globalization;
using System.Text;
using DriverReader;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return args.Length == 0 ? 0 : 0;
}

var command = args[0].ToLowerInvariant();

try
{
    switch (command)
    {
        case "auto":
        {
            bool asJson = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            RunAutoDetect(asJson);
            return 0;
        }

        case "export":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("错误: export 命令需要指定输入配置文件路径。");
                Console.Error.WriteLine("用法: DriverReader export <input_config> [output.json]");
                return 1;
            }

            string inputPath = args[1];
            string? outputPath = args.Length >= 3 ? args[2] : null;
            return RunExport(inputPath, outputPath);
        }

        case "eval":
        {
            if (args.Length < 3 || !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double rawPressure))
            {
                Console.Error.WriteLine("错误: eval 命令需要指定配置文件与原始压力数值。");
                Console.Error.WriteLine("用法: DriverReader eval <input_config> <raw_pressure> [pressure_max]");
                return 1;
            }

            string inputPath = args[1];
            double? maxP = args.Length >= 4 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double pMax)
                ? pMax : null;

            return RunEval(inputPath, rawPressure, maxP);
        }

        case "table":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("错误: table 命令需要指定配置文件路径。");
                Console.Error.WriteLine("用法: DriverReader table <input_config> [steps]");
                return 1;
            }

            string inputPath = args[1];
            int steps = args.Length >= 3 && int.TryParse(args[2], out int s) ? Math.Max(s, 2) : 20;
            return RunTable(inputPath, steps);
        }

        case "inspect":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("错误: inspect 命令需要指定配置文件路径。");
                return 1;
            }
            return RunInspect(args[1]);
        }

        default:
        {
            // 如果第一个参数不是已知指令，但存在该文件或目录，默认作为 inspect 执行
            if (File.Exists(args[0]) || Directory.Exists(args[0]))
            {
                return RunInspect(args[0]);
            }

            Console.Error.WriteLine($"未知指令或找不到文件: {args[0]}");
            PrintUsage();
            return 1;
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"执行失败: {ex.Message}");
    return 1;
}

static int RunInspect(string path)
{
    var profile = DriverProfileParser.Parse(path);
    Console.WriteLine("==================================================");
    Console.WriteLine("          数位板驱动配置解析报告 (DriverReader)");
    Console.WriteLine("==================================================");
    Console.WriteLine($"配置文件路径: {Path.GetFullPath(path)}");
    Console.WriteLine($"数位板厂商  : {profile.Vendor}");
    Console.WriteLine($"识别设备名称: {profile.DeviceName}");
    Console.WriteLine($"推荐压力上限: {profile.RecommendedPressureMax ?? 16383}");
    if (profile.PhysicalWidth.HasValue && profile.PhysicalHeight.HasValue)
        Console.WriteLine($"物理有效区域: {profile.PhysicalWidth} x {profile.PhysicalHeight} (单位: LPI/计数)");
    Console.WriteLine($"压感响应类型: {(profile.BezierCurve != null ? "三次贝塞尔样条曲线 (Cubic Bézier)" : (profile.GammaCurve.HasValue ? "幂律/Gamma 曲线" : "线性"))}");
    Console.WriteLine($"压感曲线概述: {profile.CurveSummary}");
    if (profile.ThresholdRatio > 0)
        Console.WriteLine($"起笔接触死区: {profile.ThresholdRatio * 100:0.##}%");

    Console.WriteLine();
    Console.WriteLine("压感响应变换测试采样表:");
    Console.WriteLine("  百分比     原始硬件输入      驱动输出压感      输出百分比   响应偏向");
    Console.WriteLine("  -------------------------------------------------------------");

    double maxP = profile.RecommendedPressureMax ?? 16383;
    var table = profile.GenerateSampleTable(10);
    foreach (var pt in table)
    {
        string bias = pt.OutputPercent > pt.InputPercent + 5 ? "偏软 (轻触出重线)" :
                      pt.OutputPercent < pt.InputPercent - 5 ? "偏硬 (需要更用力)" : "居中线性";
        Console.WriteLine($"  {pt.InputPercent,5:0}%    {pt.InputPressure,10:0}      {pt.OutputPressure,10:0}        {pt.OutputPercent,6:0.0}%    {bias}");
    }
    Console.WriteLine("==================================================");
    return 0;
}

static void RunAutoDetect(bool asJson)
{
    var candidates = DriverProfileParser.AutoDetectCandidatePaths();
    var list = new List<DriverProfile>();

    foreach (var path in candidates)
    {
        try
        {
            var p = DriverProfileParser.Parse(path);
            list.Add(p);
        }
        catch
        {
            // 忽略不可解析的候选文件
        }
    }

    if (asJson)
    {
        var options = new System.Text.Json.JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var jsonArray = list.Select(p => System.Text.Json.JsonSerializer.Deserialize<object>(p.ToJson(false))).ToList();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonArray, options));
        return;
    }

    Console.WriteLine("==================================================");
    Console.WriteLine("      本机数位板驱动配置文件自动扫描结果");
    Console.WriteLine("==================================================");
    if (list.Count == 0)
    {
        Console.WriteLine("未在默认路径中找到已激活的常见数位板驱动配置文件。");
        Console.WriteLine("已扫描路径包括高漫、绘王、Wacom、OpenTabletDriver、XP-Pen 等。");
    }
    else
    {
        Console.WriteLine($"共找到 {list.Count} 个有效的数位板驱动配置：\n");
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            Console.WriteLine($"[{i + 1}] {p.DeviceName} ({p.Vendor})");
            Console.WriteLine($"    路径: {p.ConfigPath}");
            Console.WriteLine($"    特性: {p.CurveSummary}");
            Console.WriteLine($"    压力上限: {p.RecommendedPressureMax ?? 16383}");
            Console.WriteLine();
        }
    }
    Console.WriteLine("==================================================");
}

static int RunExport(string inputPath, string? outputPath)
{
    var profile = DriverProfileParser.Parse(inputPath);
    string json = profile.ToJson(indented: true);

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        Console.WriteLine(json);
    }
    else
    {
        string fullOut = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullOut);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullOut, json, Encoding.UTF8);
        Console.WriteLine($"成功导出驱动配置 JSON: {fullOut}");
    }
    return 0;
}

static int RunEval(string inputPath, double rawPressure, double? maxP)
{
    var profile = DriverProfileParser.Parse(inputPath);
    double effectiveMax = maxP ?? profile.RecommendedPressureMax ?? 16383;
    double calibrated = profile.TransformPressure(rawPressure, effectiveMax);

    Console.WriteLine($"输入压力: {rawPressure:0.##} / {effectiveMax:0.##} ({rawPressure / effectiveMax * 100:0.##}%)");
    Console.WriteLine($"输出压力: {calibrated:0.##} / {effectiveMax:0.##} ({calibrated / effectiveMax * 100:0.##}%)");
    return 0;
}

static int RunTable(string inputPath, int steps)
{
    var profile = DriverProfileParser.Parse(inputPath);
    var table = profile.GenerateSampleTable(steps);

    Console.WriteLine("input_percent\tinput_pressure\toutput_pressure\toutput_percent");
    foreach (var pt in table)
    {
        Console.WriteLine($"{pt.InputPercent:0.#}\t{pt.InputPressure:0.#}\t{pt.OutputPressure:0.#}\t{pt.OutputPercent:0.#}");
    }
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("""
    DriverReader — 数位板驱动配置文件解析器与压感特性提取工具

    用法:
      DriverReader auto [--json]
        自动扫描本机安装的高漫/绘王/Wacom/OTD/XP-Pen 驱动配置文件并显示报告。

      DriverReader <配置文件路径>
      DriverReader inspect <配置文件路径>
        详细解析指定驱动配置文件，输出设备型号、硬件物理范围、压感曲线及采样表。

      DriverReader export <配置文件路径> [output.json]
        将驱动配置解析为标准 JSON 格式。省略输出路径时输出到终端 stdout。

      DriverReader eval <配置文件路径> <raw_pressure> [pressure_max]
        输入一个原始硬件压力值，计算经过驱动曲线映射后的实际输出压力。

      DriverReader table <配置文件路径> [采样步数]
        输出制表符分隔的压感曲线响应阶梯表（TSV 格式），方便导入绘图或数据分析。

    支持格式:
      1. 高漫 / 绘王: EKeySetting.dt, Setting.json (*.dt, *.json)
      2. Wacom 路径1: .wacomprefs, .wacomxs, .xml, .prefs (包括解压目录和 zip 封装)
      3. OpenTabletDriver: settings.json
      4. XP-Pen / 友基: config.xml

    示例:
      DriverReader auto
      DriverReader "C:\Users\user\AppData\Roaming\GAOMON\data\EKeySetting.dt"
      DriverReader export "C:\Users\user\AppData\Roaming\GAOMON\data\EKeySetting.dt" profile.json
      DriverReader "C:\path\to\backup.wacomprefs"
    """);
}
