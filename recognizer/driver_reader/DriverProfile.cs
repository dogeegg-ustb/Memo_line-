using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DriverReader;

/// <summary>
/// 三次贝塞尔压感响应曲线求解器。
/// </summary>
public sealed class CubicBezierCurve
{
    public (double X, double Y) P0 { get; }
    public (double X, double Y) P1 { get; }
    public (double X, double Y) P2 { get; }
    public (double X, double Y) P3 { get; }

    public CubicBezierCurve((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
    }

    /// <summary>
    /// 给定输入压力 X，使用高精度二分法求解参数 t 并返回对应的输出压力 Y。
    /// </summary>
    public double EvaluateY(double x)
    {
        if (x <= P0.X) return P0.Y;
        if (x >= P3.X) return P3.Y;

        double low = 0.0;
        double high = 1.0;

        for (int i = 0; i < 20; i++)
        {
            double mid = (low + high) * 0.5;
            double bx = GetPoint(mid).X;
            if (bx < x)
                low = mid;
            else
                high = mid;
        }

        double t = (low + high) * 0.5;
        return GetPoint(t).Y;
    }

    public (double X, double Y) GetPoint(double t)
    {
        double u = 1.0 - t;
        double tt = t * t;
        double uu = u * u;
        double uuu = uu * u;
        double ttt = tt * t;

        double x = uuu * P0.X + 3.0 * uu * t * P1.X + 3.0 * u * tt * P2.X + ttt * P3.X;
        double y = uuu * P0.Y + 3.0 * uu * t * P1.Y + 3.0 * u * tt * P2.Y + ttt * P3.Y;
        return (x, y);
    }
}

/// <summary>
/// 数位板驱动配置中提取的硬件规格与压感响应特性模型。
/// </summary>
public sealed class DriverProfile
{
    public required string Vendor { get; init; }
    public required string DeviceName { get; init; }
    public required string ConfigPath { get; init; }
    public required string CurveSummary { get; init; }
    public double? RecommendedPressureMax { get; init; }
    public double? PhysicalWidth { get; init; }
    public double? PhysicalHeight { get; init; }
    public CubicBezierCurve? BezierCurve { get; init; }
    public double? GammaCurve { get; init; }
    public double ThresholdRatio { get; init; }

    /// <summary>
    /// 将输入的原始硬件压力值，根据驱动配置的压感曲线变换为校准后的实际输出压力。
    /// </summary>
    public double TransformPressure(double rawPressure, double pressureMax)
    {
        if (pressureMax <= 0 || rawPressure <= 0)
            return 0;

        double normInput = Math.Clamp(rawPressure / pressureMax, 0.0, 1.0);

        if (ThresholdRatio > 0)
        {
            if (normInput < ThresholdRatio)
                return 0;
            normInput = (normInput - ThresholdRatio) / (1.0 - ThresholdRatio);
        }

        double normOutput;
        if (BezierCurve != null)
        {
            double domainMin = Math.Min(BezierCurve.P0.X, BezierCurve.P3.X);
            double domainMax = Math.Max(BezierCurve.P0.X, BezierCurve.P3.X);
            double rangeMin = Math.Min(BezierCurve.P0.Y, BezierCurve.P3.Y);
            double rangeMax = Math.Max(BezierCurve.P0.Y, BezierCurve.P3.Y);

            double spanX = Math.Max(domainMax - domainMin, 1.0);
            double spanY = Math.Max(rangeMax - rangeMin, 1.0);

            double queryX = domainMin + normInput * spanX;
            double evalY = BezierCurve.EvaluateY(queryX);

            normOutput = Math.Clamp((evalY - rangeMin) / spanY, 0.0, 1.0);
        }
        else if (GammaCurve.HasValue && GammaCurve.Value > 0)
        {
            normOutput = Math.Clamp(Math.Pow(normInput, GammaCurve.Value), 0.0, 1.0);
        }
        else
        {
            normOutput = normInput;
        }

        return normOutput * pressureMax;
    }

    /// <summary>
    /// 生成压感映射采样阶梯表。
    /// </summary>
    public List<PressureSamplePoint> GenerateSampleTable(int steps = 10)
    {
        var list = new List<PressureSamplePoint>();
        double maxP = RecommendedPressureMax ?? 16383;
        for (int i = 0; i <= steps; i++)
        {
            double ratio = (double)i / steps;
            double raw = ratio * maxP;
            double calibrated = TransformPressure(raw, maxP);
            list.Add(new PressureSamplePoint
            {
                InputPressure = Math.Round(raw, 1),
                InputPercent = Math.Round(ratio * 100, 1),
                OutputPressure = Math.Round(calibrated, 1),
                OutputPercent = Math.Round(calibrated / maxP * 100, 1)
            });
        }
        return list;
    }

    /// <summary>
    /// 导出为结构化 JSON 字符串。
    /// </summary>
    public string ToJson(bool indented = true)
    {
        var export = new DriverProfileExport
        {
            Vendor = Vendor,
            DeviceName = DeviceName,
            ConfigPath = ConfigPath,
            CurveSummary = CurveSummary,
            CurveType = BezierCurve != null ? "CubicBezier" : (GammaCurve.HasValue ? "Gamma" : "Linear"),
            RecommendedPressureMax = RecommendedPressureMax,
            PhysicalWidth = PhysicalWidth,
            PhysicalHeight = PhysicalHeight,
            ThresholdRatio = ThresholdRatio,
            Gamma = GammaCurve,
            BezierPoints = BezierCurve != null ? new BezierPointsExport
            {
                P0 = new PointExport { X = BezierCurve.P0.X, Y = BezierCurve.P0.Y },
                P1 = new PointExport { X = BezierCurve.P1.X, Y = BezierCurve.P1.Y },
                P2 = new PointExport { X = BezierCurve.P2.X, Y = BezierCurve.P2.Y },
                P3 = new PointExport { X = BezierCurve.P3.X, Y = BezierCurve.P3.Y }
            } : null,
            SampleTable = GenerateSampleTable(10)
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return JsonSerializer.Serialize(export, options);
    }
}

public sealed class PressureSamplePoint
{
    public double InputPressure { get; set; }
    public double InputPercent { get; set; }
    public double OutputPressure { get; set; }
    public double OutputPercent { get; set; }
}

public sealed class PointExport
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class BezierPointsExport
{
    public PointExport P0 { get; set; } = new();
    public PointExport P1 { get; set; } = new();
    public PointExport P2 { get; set; } = new();
    public PointExport P3 { get; set; } = new();
}

public sealed class DriverProfileExport
{
    public string Vendor { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string CurveType { get; set; } = string.Empty;
    public string CurveSummary { get; set; } = string.Empty;
    public double? RecommendedPressureMax { get; set; }
    public double? PhysicalWidth { get; set; }
    public double? PhysicalHeight { get; set; }
    public double ThresholdRatio { get; set; }
    public double? Gamma { get; set; }
    public BezierPointsExport? BezierPoints { get; set; }
    public List<PressureSamplePoint> SampleTable { get; set; } = [];
}

/// <summary>
/// 驱动配置文件解析与自动发现器。
/// </summary>
public static class DriverProfileParser
{
    /// <summary>
    /// 自动扫描本地计算机常见数位板驱动配置文件存放路径。
    /// </summary>
    public static List<string> AutoDetectCandidatePaths()
    {
        var candidates = new List<string>();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 1. 高漫
        candidates.Add(Path.Combine(appData, "GAOMON", "data", "EKeySetting.dt"));
        candidates.Add(Path.Combine(appData, "GAOMON", "data", "Setting.json"));

        // 2. 绘王
        candidates.Add(Path.Combine(appData, "HUION", "data", "EKeySetting.dt"));
        candidates.Add(Path.Combine(appData, "HUION", "data", "Setting.json"));

        // 3. Wacom
        candidates.Add(Path.Combine(appData, "WTablet", "WacomTabletUserDefaults.xml"));
        candidates.Add(Path.Combine(appData, "WTablet", "Wacom_Tablet.dat"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WacomTabletDefaults.xml"));

        // 4. OpenTabletDriver
        candidates.Add(Path.Combine(localAppData, "OpenTabletDriver", "settings.json"));

        // 5. XP-Pen
        candidates.Add(Path.Combine(userProfile, ".PenTablet", "config.xml"));
        candidates.Add(Path.Combine(appData, "Pentablet", "config.xml"));

        return candidates.Where(p => File.Exists(p) || Directory.Exists(p)).Distinct().ToList();
    }

    /// <summary>
    /// 解析指定路径的数位板驱动配置文件。
    /// </summary>
    public static DriverProfile Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("配置文件路径不能为空。", nameof(path));

        path = Path.GetFullPath(path);

        if (Directory.Exists(path))
        {
            return ParseWacomDirectory(path);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到配置文件：{path}", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext is ".dt" or ".json")
        {
            try
            {
                return ParseGaomonHuion(path);
            }
            catch when (ext == ".json")
            {
                return ParseOpenTabletDriver(path);
            }
        }

        if (ext is ".wacomprefs" or ".wacomxs" or ".prefs" or ".xml")
        {
            return ParseWacomFile(path);
        }

        // 兜底自动嗅探
        try { return ParseGaomonHuion(path); }
        catch
        {
            try { return ParseWacomFile(path); }
            catch
            {
                throw new InvalidDataException(
                    "未能识别所选文件格式。支持高漫/绘王配置（*.dt, *.json）、Wacom 备份配置（*.wacomprefs, *.wacomxs, *.xml, *.prefs）或 OTD（settings.json）。");
            }
        }
    }

    /// <summary>
    /// 解析高漫 / 绘王驱动配置（如 EKeySetting.dt 或 Setting.json）。
    /// </summary>
    public static DriverProfile ParseGaomonHuion(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);

        // 去除尾部可能存在的 '\0' 及空白字符
        int end = bytes.Length;
        while (end > 0 && (bytes[end - 1] == 0 || char.IsWhiteSpace((char)bytes[end - 1])))
        {
            end--;
        }

        if (end <= 0)
            throw new InvalidDataException("高漫/绘王配置文件内容为空。");

        string jsonText = Encoding.UTF8.GetString(bytes, 0, end);
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("高漫/绘王配置根节点不是有效的 JSON 对象。");

        string? bestKey = null;
        JsonElement bestElement = default;
        int bestScore = -1;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            int score = 0;
            if (prop.Value.TryGetProperty("Ctr0X", out _)) score += 10;
            if (prop.Value.TryGetProperty("Right", out var r) && r.GetInt64() > 0) score += 5;
            if (prop.Value.TryGetProperty("WinInk", out _)) score += 2;

            if (score > bestScore)
            {
                bestScore = score;
                bestKey = prop.Name;
                bestElement = prop.Value;
            }
        }

        if (bestKey == null || bestScore <= 0)
            throw new InvalidDataException("在所选文件中未找到包含数位板参数的高漫/绘王设备条目。");

        double? right = bestElement.TryGetProperty("Right", out var rElem) && rElem.TryGetDouble(out var rv) ? rv : null;
        double? bottom = bestElement.TryGetProperty("Bottom", out var bElem) && bElem.TryGetDouble(out var bv) ? bv : null;

        CubicBezierCurve? bezier = null;
        string curveSummary;

        if (bestElement.TryGetProperty("Ctr0X", out var c0x) && c0x.TryGetDouble(out var c0xVal) &&
            bestElement.TryGetProperty("Ctr0Y", out var c0y) && c0y.TryGetDouble(out var c0yVal) &&
            bestElement.TryGetProperty("Ctr1X", out var c1x) && c1x.TryGetDouble(out var c1xVal) &&
            bestElement.TryGetProperty("Ctr1Y", out var c1y) && c1y.TryGetDouble(out var c1yVal) &&
            bestElement.TryGetProperty("Ctr2X", out var c2x) && c2x.TryGetDouble(out var c2xVal) &&
            bestElement.TryGetProperty("Ctr2Y", out var c2y) && c2y.TryGetDouble(out var c2yVal))
        {
            double maxP = 16384.0;
            bezier = new CubicBezierCurve(
                (c0xVal, c0yVal),
                (c1xVal, c1yVal),
                (c2xVal, c2yVal),
                (maxP, maxP));

            curveSummary = $"三次贝塞尔控制点: Ctr0({c0xVal:0},{c0yVal:0}) → Ctr1({c1xVal:0},{c1yVal:0}) → Ctr2({c2xVal:0},{c2yVal:0})";
        }
        else
        {
            curveSummary = "未指定压感控制点（默认线性）";
        }

        string deviceName = bestKey;
        if (bestKey.StartsWith("GM", StringComparison.OrdinalIgnoreCase))
            deviceName = $"高漫数位板 ({bestKey})";
        else if (bestKey.Contains("OEM", StringComparison.OrdinalIgnoreCase))
            deviceName = $"绘王/高漫 OEM ({bestKey})";

        return new DriverProfile
        {
            Vendor = "Gaomon / Huion",
            DeviceName = deviceName,
            ConfigPath = filePath,
            CurveSummary = curveSummary,
            RecommendedPressureMax = 16383,
            PhysicalWidth = right,
            PhysicalHeight = bottom,
            BezierCurve = bezier,
            ThresholdRatio = 0
        };
    }

    /// <summary>
    /// 解析 Wacom 路径1 导出的备份文件（.wacomprefs / .wacomxs / .prefs / .xml）。
    /// </summary>
    public static DriverProfile ParseWacomFile(string filePath)
    {
        bool isZip = false;
        using (var fs = File.OpenRead(filePath))
        {
            byte[] header = new byte[4];
            if (fs.Read(header, 0, 4) == 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
            {
                isZip = true;
            }
        }

        if (isZip)
        {
            using var archive = ZipFile.OpenRead(filePath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".prefs", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains("wacom", StringComparison.OrdinalIgnoreCase));

            if (entry == null && archive.Entries.Count > 0)
                entry = archive.Entries[0];

            if (entry != null)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                return ParseWacomXmlOrText(text, filePath);
            }
        }

        string content = File.ReadAllText(filePath, Encoding.UTF8);
        return ParseWacomXmlOrText(content, filePath);
    }

    /// <summary>
    /// 解析 Wacom Bundle 文件夹（展开后的 .wacomprefs 目录）。
    /// </summary>
    public static DriverProfile ParseWacomDirectory(string dirPath)
    {
        var files = Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".prefs", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("wacom", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
            throw new InvalidDataException($"在 Wacom 目录中未找到 .prefs 或 .xml 配置文件：{dirPath}");

        var targetFile = files.FirstOrDefault(f => f.Contains("wacomtablet.prefs", StringComparison.OrdinalIgnoreCase)) ?? files[0];
        string content = File.ReadAllText(targetFile, Encoding.UTF8);
        return ParseWacomXmlOrText(content, targetFile);
    }

    private static DriverProfile ParseWacomXmlOrText(string content, string sourcePath)
    {
        bool hasWacomKeyword = content.Contains("Wacom", StringComparison.OrdinalIgnoreCase) ||
                               content.Contains("<Tablet", StringComparison.OrdinalIgnoreCase) ||
                               content.Contains("TipFeel", StringComparison.OrdinalIgnoreCase) ||
                               content.Contains("PressureCurve", StringComparison.OrdinalIgnoreCase);

        if (!hasWacomKeyword)
        {
            throw new InvalidDataException("所选文件不包含 Wacom 驱动配置特征。");
        }

        string deviceName = "Wacom Tablet";
        double? maxPressure = 8192;
        double? width = null;
        double? height = null;
        CubicBezierCurve? bezier = null;
        double? gamma = null;
        double thresholdRatio = 0.0;
        string curveSummary = "Wacom 默认线性压感";
        bool matchedAnyElement = false;

        try
        {
            var doc = XDocument.Parse(content);
            var root = doc.Root;

            if (root != null)
            {
                if (root.Name.LocalName.Contains("Wacom", StringComparison.OrdinalIgnoreCase) ||
                    root.Name.LocalName.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ||
                    root.Name.LocalName.Contains("Preference", StringComparison.OrdinalIgnoreCase))
                {
                    matchedAnyElement = true;
                }

                var nameElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("DeviceName", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("TabletName", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("TabletModel", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("Model", StringComparison.OrdinalIgnoreCase));
                if (nameElem != null && !string.IsNullOrWhiteSpace(nameElem.Value))
                {
                    deviceName = nameElem.Value.Trim();
                    matchedAnyElement = true;
                }

                var maxPressElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("MaxPressure", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("MaxPressureLevels", StringComparison.OrdinalIgnoreCase));
                if (maxPressElem != null && double.TryParse(maxPressElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mp))
                {
                    maxPressure = mp;
                    matchedAnyElement = true;
                }

                var rightElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("CoordMaxX", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("Right", StringComparison.OrdinalIgnoreCase));
                var bottomElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("CoordMaxY", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("Bottom", StringComparison.OrdinalIgnoreCase));
                if (rightElem != null && double.TryParse(rightElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                {
                    width = r;
                    matchedAnyElement = true;
                }
                if (bottomElem != null && double.TryParse(bottomElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                {
                    height = b;
                    matchedAnyElement = true;
                }

                var threshElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("ClickThreshold", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("PressureThreshold", StringComparison.OrdinalIgnoreCase));
                if (threshElem != null && double.TryParse(threshElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thVal))
                {
                    thresholdRatio = Math.Clamp(thVal / (maxPressure ?? 8192), 0.0, 0.3);
                    matchedAnyElement = true;
                }

                var feelElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("TipFeel", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("SoftFirmIndex", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("SensitivityLevel", StringComparison.OrdinalIgnoreCase));

                if (feelElem != null && int.TryParse(feelElem.Value, out int feelInt))
                {
                    matchedAnyElement = true;
                    int feelShift = feelInt;
                    if (feelInt >= 1 && feelInt <= 7) feelShift = feelInt - 4;

                    if (feelShift < 0)
                    {
                        double factor = Math.Abs(feelShift) / 3.0;
                        gamma = 1.0 - factor * 0.65;
                        curveSummary = $"Wacom TipFeel 软压感 (档位 {feelShift}, Gamma={gamma:0.##})";
                    }
                    else if (feelShift > 0)
                    {
                        double factor = feelShift / 3.0;
                        gamma = 1.0 + factor * 1.5;
                        curveSummary = $"Wacom TipFeel 硬压感 (档位 +{feelShift}, Gamma={gamma:0.##})";
                    }
                    else
                    {
                        curveSummary = "Wacom TipFeel 居中标准压感 (线性)";
                    }
                }

                var curveElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("PressureCurve", StringComparison.OrdinalIgnoreCase));
                if (curveElem != null && !string.IsNullOrWhiteSpace(curveElem.Value))
                {
                    var match = Regex.Match(curveElem.Value, @"(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)");
                    if (match.Success)
                    {
                        matchedAnyElement = true;
                        double x1 = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        double y1 = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                        double x2 = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                        double y2 = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

                        bezier = new CubicBezierCurve((0, 0), (x1, y1), (x2, y2), (100, 100));
                        curveSummary = $"Wacom 贝塞尔压感曲线: P1({x1},{y1}) → P2({x2},{y2})";
                        gamma = null;
                    }
                }
            }
        }
        catch
        {
            var nameMatch = Regex.Match(content, @"<DeviceName[^>]*>([^<]+)</DeviceName>", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                deviceName = nameMatch.Groups[1].Value.Trim();
                matchedAnyElement = true;
            }

            var feelMatch = Regex.Match(content, @"<(TipFeel|SoftFirmIndex)[^>]*>(-?\d+)</", RegexOptions.IgnoreCase);
            if (feelMatch.Success && int.TryParse(feelMatch.Groups[2].Value, out int feel))
            {
                matchedAnyElement = true;
                int shift = feel >= 1 && feel <= 7 ? feel - 4 : feel;
                gamma = shift < 0 ? (1.0 - Math.Abs(shift) / 3.0 * 0.6) : (1.0 + shift / 3.0 * 1.4);
                curveSummary = $"Wacom TipFeel (指数={gamma:0.##})";
            }
        }

        if (!matchedAnyElement)
        {
            throw new InvalidDataException("所选文件中未解析出任何有效的 Wacom 设备或压感参数。");
        }

        return new DriverProfile
        {
            Vendor = "Wacom",
            DeviceName = deviceName,
            ConfigPath = sourcePath,
            CurveSummary = curveSummary,
            RecommendedPressureMax = maxPressure ?? 8192,
            PhysicalWidth = width,
            PhysicalHeight = height,
            BezierCurve = bezier,
            GammaCurve = gamma,
            ThresholdRatio = thresholdRatio
        };
    }

    /// <summary>
    /// 解析 OpenTabletDriver 的 settings.json。
    /// </summary>
    public static DriverProfile ParseOpenTabletDriver(string filePath)
    {
        string text = File.ReadAllText(filePath, Encoding.UTF8);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("所选文件不是有效的 JSON 对象。");

        if (!root.TryGetProperty("Profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() == 0)
            throw new InvalidDataException("所选 JSON 不是有效的 OpenTabletDriver settings.json（未找到有效 Profiles 条目）。");

        string deviceName = "OpenTabletDriver 设备";
        double? width = null;
        double? height = null;
        double thresholdRatio = 0.0;
        string curveSummary = "OTD 线性压感配置";

        var firstProfile = profiles[0];
        if (firstProfile.ValueKind == JsonValueKind.Object)
        {
            if (firstProfile.TryGetProperty("Tablet", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
            {
                deviceName = $"OTD · {t.GetString()}";
            }

            if (firstProfile.TryGetProperty("AbsoluteModeSettings", out var absMode) && absMode.ValueKind == JsonValueKind.Object &&
                absMode.TryGetProperty("Tablet", out var tabArea) && tabArea.ValueKind == JsonValueKind.Object)
            {
                if (tabArea.TryGetProperty("Width", out var wElem) && wElem.TryGetDouble(out var w)) width = w;
                if (tabArea.TryGetProperty("Height", out var hElem) && hElem.TryGetDouble(out var h)) height = h;
            }

            if (firstProfile.TryGetProperty("Bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
            {
                if (bindings.TryGetProperty("TipActivationThreshold", out var thElem) && thElem.TryGetDouble(out var thVal) && thVal > 0)
                {
                    thresholdRatio = Math.Clamp(thVal / 100.0, 0.0, 0.5);
                }
            }

            if (firstProfile.TryGetProperty("Filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            {
                foreach (var filter in filters.EnumerateArray())
                {
                    if (filter.TryGetProperty("Path", out var filterPath) &&
                        filterPath.GetString()?.Contains("Pressure", StringComparison.OrdinalIgnoreCase) == true &&
                        filter.TryGetProperty("Enable", out var enabled) && enabled.GetBoolean())
                    {
                        curveSummary = $"OTD 激活滤镜: {Path.GetFileNameWithoutExtension(filterPath.GetString())}";
                        break;
                    }
                }
            }
        }

        return new DriverProfile
        {
            Vendor = "OpenTabletDriver",
            DeviceName = deviceName,
            ConfigPath = filePath,
            CurveSummary = curveSummary,
            RecommendedPressureMax = 16383,
            PhysicalWidth = width,
            PhysicalHeight = height,
            BezierCurve = null,
            ThresholdRatio = thresholdRatio
        };
    }
}
