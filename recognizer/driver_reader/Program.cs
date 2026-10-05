using System.Globalization;
using System.Text;
using DriverReader;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    RunAutoDetect(asJson: false, interactive: true);
    return 0;
}

if (args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLowerInvariant();

try
{
    switch (command)
    {
        case "auto":
        case "find":
        case "search":
        {
            bool asJson = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            RunAutoDetect(asJson, interactive: !asJson);
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

        case "map":
        {
            if (args.Length < 4 ||
                !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double px) ||
                !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
            {
                Console.Error.WriteLine("错误: map 命令需要指定配置文件与物理坐标 X Y。");
                Console.Error.WriteLine("用法: DriverReader map <input_config> <phys_x> <phys_y> [screen_w] [screen_h]");
                return 1;
            }

            string inputPath = args[1];
            double sw = args.Length >= 5 && double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double swV) ? swV : 1920;
            double sh = args.Length >= 6 && double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double shV) ? shV : 1080;

            return RunMap(inputPath, px, py, sw, sh);
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

    if (profile.CoordinateMapping != null)
    {
        var map = profile.CoordinateMapping;
        Console.WriteLine();
        Console.WriteLine("【物理坐标系 ↔ 屏幕坐标系映射关系】");
        Console.WriteLine($"  映射模式    : {(map.MappingMode == "Absolute" ? "绝对映射 (Absolute)" : "相对映射 (Relative)")}");
        Console.WriteLine($"  物理有效范围: {map.PhysicalArea} ({map.PhysicalUnit})");
        if (map.ScreenArea != null)
            Console.WriteLine($"  屏幕像素范围: {map.ScreenArea} (px)");
        if (map.ScreenMapRatio != null)
            Console.WriteLine($"  屏幕映射比例: 水平 [{map.ScreenMapRatio.Left * 100:0.##}% ~ {map.ScreenMapRatio.Right * 100:0.##}%], 垂直 [{map.ScreenMapRatio.Top * 100:0.##}% ~ {map.ScreenMapRatio.Bottom * 100:0.##}%]");
        if (map.ScreenIndex.HasValue)
            Console.WriteLine($"  目标显示器  : 显示器 #{map.ScreenIndex.Value}");
        Console.WriteLine($"  旋转角度    : {map.RotationDegrees}°");
        Console.WriteLine($"  保持宽高比  : {(map.LockAspectRatio ? "是 (保持原始比例)" : "否 (全屏拉伸)")}");
        if (map.ScaleX.HasValue && map.ScaleY.HasValue)
        {
            Console.WriteLine($"  横向缩放系数: {map.ScaleX.Value:0.####} px/{map.PhysicalUnit}");
            Console.WriteLine($"  纵向缩放系数: {map.ScaleY.Value:0.####} px/{map.PhysicalUnit}");
        }
        Console.WriteLine("  映射变换公式:");
        foreach (var line in map.GetFormulaDescription().Split('\n'))
        {
            Console.WriteLine($"    {line}");
        }
    }

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

static void RunAutoDetect(bool asJson, bool interactive = false)
{
    var list = DriverProfileParser.FindAllInstalledProfiles();

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
        Console.WriteLine("已扫描路径包括高漫、绘王、Wacom、OpenTabletDriver、XP-Pen 等默认配置及桌面/文档备份。");
        Console.WriteLine("您可以通过命令行指定路径进行解析: DriverReader <配置文件路径>");
    }
    else
    {
        var best = DriverProfileParser.FindBestProfile();
        Console.WriteLine($"共找到 {list.Count} 个有效的数位板驱动配置：\n");
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            bool isBest = best != null && p.ConfigPath.Equals(best.ConfigPath, StringComparison.OrdinalIgnoreCase);
            string star = isBest ? " ★ [推荐]" : "";
            Console.WriteLine($"[{i + 1}] {p.DeviceName} ({p.Vendor}){star}");
            Console.WriteLine($"    路径: {p.ConfigPath}");
            Console.WriteLine($"    特性: {p.CurveSummary}");
            Console.WriteLine($"    压力上限: {p.RecommendedPressureMax ?? 16383}");
            Console.WriteLine();
        }

        if (best != null)
        {
            Console.WriteLine($"★ 默认推荐关联: {best.DeviceName}");
        }
    }
    Console.WriteLine("==================================================");

    if (interactive && !Console.IsInputRedirected && list.Count > 0)
    {
        Console.WriteLine();
        Console.Write($"请输入配置序号 [1~{list.Count}] 查看详细采样报告与贝塞尔曲线（按 Enter 退出）: ");
        var input = Console.ReadLine();
        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= list.Count)
        {
            Console.WriteLine();
            RunInspect(list[idx - 1].ConfigPath);
        }
    }
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

static int RunMap(string inputPath, double px, double py, double screenW, double screenH)
{
    var profile = DriverProfileParser.Parse(inputPath);
    if (profile.CoordinateMapping == null)
    {
        Console.Error.WriteLine("该配置文件中未包含坐标系映射参数。");
        return 1;
    }

    var map = profile.CoordinateMapping;
    var (sx, sy) = map.PhysicalToScreen(px, py, screenW, screenH);
    var (rpx, rpy) = map.ScreenToPhysical(sx, sy, screenW, screenH);

    Console.WriteLine("==================================================");
    Console.WriteLine("        坐标映射计算 (Physical -> Screen)");
    Console.WriteLine("==================================================");
    Console.WriteLine($"配置文件    : {Path.GetFullPath(inputPath)}");
    Console.WriteLine($"设备名称    : {profile.DeviceName} ({profile.Vendor})");
    Console.WriteLine($"物理有效区  : {map.PhysicalArea} ({map.PhysicalUnit})");
    Console.WriteLine($"参考屏幕尺寸: {screenW} x {screenH} px");
    Console.WriteLine($"旋转角度    : {map.RotationDegrees}°");
    Console.WriteLine($"保持宽高比  : {(map.LockAspectRatio ? "是" : "否")}");
    Console.WriteLine($"输入物理坐标: ({px:0.##}, {py:0.##})");
    Console.WriteLine($"映射屏幕坐标: ({sx:0.##}, {sy:0.##})");
    Console.WriteLine($"反向还原物理: ({rpx:0.##}, {rpy:0.##})");
    Console.WriteLine("--------------------------------------------------");
    Console.WriteLine("映射公式:");
    foreach (var line in map.GetFormulaDescription().Split('\n'))
    {
        Console.WriteLine($"  {line}");
    }
    Console.WriteLine("==================================================");
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("""
    DriverReader — 数位板驱动配置文件解析器与压感特性提取工具

    用法:
      DriverReader
      DriverReader auto [--json]
      DriverReader find [--json]
        自动扫描本机安装的高漫/绘王/Wacom/OTD/XP-Pen 驱动配置文件并显示报告或交互查看。

      DriverReader <配置文件路径>
      DriverReader inspect <配置文件路径>
        详细解析指定驱动配置文件，输出设备型号、物理/屏幕坐标系映射、压感曲线及采样表。

      DriverReader map <配置文件路径> <phys_x> <phys_y> [screen_w] [screen_h]
        输入物理坐标，计算驱动配置在目标屏幕分辨率下的屏幕映射坐标及反向还原。

      DriverReader export <配置文件路径> [output.json]
        将驱动配置（含物理与屏幕坐标映射）解析为标准 JSON 格式。省略输出路径时输出到终端 stdout。

      DriverReader eval <配置文件路径> <raw_pressure> [pressure_max]
        输入一个原始硬件压力值，计算经过驱动曲线映射后的实际输出压力。

      DriverReader table <配置文件路径> [采样步数]
        输出制表符分隔的压感曲线响应阶梯表（TSV 格式），方便导入绘图或数据分析。

    支持格式:
      1. 高漫 / 绘王: EKeySetting.dt, Setting.json (*.dt, *.json)
      2. Wacom 路径1: .wacomprefs, .wacomxs, .xml, .prefs (包括解压目录和 zip 封装)
      3. OpenTabletDriver: settings.json
      4. XP-Pen / 友基 / 绘客: config.xml

    示例:
      DriverReader
      DriverReader find
      DriverReader auto --json
      DriverReader "C:\Users\user\AppData\Roaming\GAOMON\data\EKeySetting.dt"
      DriverReader map "C:\Users\user\AppData\Roaming\GAOMON\data\EKeySetting.dt" 25400 15875 1920 1080
      DriverReader export "C:\Users\user\AppData\Roaming\GAOMON\data\EKeySetting.dt" profile.json
      DriverReader "C:\path\to\backup.wacomprefs"
    """);
}
