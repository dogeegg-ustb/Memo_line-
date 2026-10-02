using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StrokeReplay;

/// <summary>
/// 三次贝塞尔压感响应曲线。
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
    /// 给定输入压力 X，使用二分法高精度求解 t 并返回对应的输出压力 Y。
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
/// 提取自数位板驱动的硬件与压感特性配置。
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

        // 如果存在下笔起笔克重阈值
        if (ThresholdRatio > 0)
        {
            if (normInput < ThresholdRatio)
                return 0;
            normInput = (normInput - ThresholdRatio) / (1.0 - ThresholdRatio);
        }

        double normOutput;
        if (BezierCurve != null)
        {
            // 将 [0, 1] 映射到贝塞尔曲线域求值后再归一化
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
}

/// <summary>
/// 驱动配置文件解析器（支持高漫/绘王明文配置及 Wacom 路径1 备份文件/XML）。
/// </summary>
public static class DriverProfileParser
{
    public static DriverProfile Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("配置文件路径不能为空。", nameof(path));

        path = Path.GetFullPath(path);

        // 1. 如果是目录（可能是 Wacom .wacomprefs 包目录）
        if (Directory.Exists(path))
        {
            return ParseWacomDirectory(path);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到驱动配置文件：{path}", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();

        // 2. 根据文件后缀和内容探测解析器
        if (ext is ".dt" or ".json")
        {
            // 尝试作为高漫 / 绘王解析，如果失败再尝试 Wacom 或通用 JSON
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

        // 尝试自动探测格式
        try
        {
            return ParseGaomonHuion(path);
        }
        catch
        {
            try
            {
                return ParseWacomFile(path);
            }
            catch
            {
                throw new InvalidDataException(
                    "未能识别所选文件格式。支持高漫/绘王配置（*.dt, *.json）或 Wacom 备份配置（*.wacomprefs, *.wacomxs, *.xml, *.prefs）。");
            }
        }
    }

    /// <summary>
    /// 解析高漫 / 绘王驱动配置（如 EKeySetting.dt 或 Setting.json）。
    /// </summary>
    public static DriverProfile ParseGaomonHuion(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);

        // 高漫/绘王 .dt 文件末尾常带一个 '\0'，先去除尾部空白与空字符
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

        // 扫描包含 Ctr0X / Right / Bottom 的活动设备节点
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

        // 提取压感贝塞尔控制点
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
        // 检查文件头是否是 Zip 压缩包（部分 .wacomprefs 或 .wacomxs 导出的 bundle 实际上是 zip 封装）
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

        // 直接作为文本读取
        string content = File.ReadAllText(filePath, Encoding.UTF8);
        return ParseWacomXmlOrText(content, filePath);
    }

    /// <summary>
    /// 解析 Wacom Bundle 文件夹（macOS 或 Windows 展开后的 .wacomprefs 目录）。
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

        // 尝试使用 XML LINQ 解析
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

                // 查找设备名
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

                // 查找最大压感
                var maxPressElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("MaxPressure", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("MaxPressureLevels", StringComparison.OrdinalIgnoreCase));
                if (maxPressElem != null && double.TryParse(maxPressElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mp))
                {
                    maxPressure = mp;
                    matchedAnyElement = true;
                }

                // 查找活动区域
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

                // 查找起笔阈值 (ClickThreshold / PressureThreshold)
                var threshElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("ClickThreshold", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("PressureThreshold", StringComparison.OrdinalIgnoreCase));
                if (threshElem != null && double.TryParse(threshElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thVal))
                {
                    thresholdRatio = Math.Clamp(thVal / (maxPressure ?? 8192), 0.0, 0.3);
                    matchedAnyElement = true;
                }

                // 查找 TipFeel / SoftFirmIndex / PressureCurve
                var feelElem = root.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("TipFeel", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("SoftFirmIndex", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Equals("SensitivityLevel", StringComparison.OrdinalIgnoreCase));

                if (feelElem != null && int.TryParse(feelElem.Value, out int feelInt))
                {
                    matchedAnyElement = true;
                    // Wacom TipFeel 档位通常在 -3..+3 或 1..7 (4 为居中)
                    int feelShift = feelInt;
                    if (feelInt >= 1 && feelInt <= 7) feelShift = feelInt - 4; // 换算到 -3..+3

                    if (feelShift < 0) // 偏软 Soft
                    {
                        double factor = Math.Abs(feelShift) / 3.0;
                        gamma = 1.0 - factor * 0.65; // gamma < 1 (凸曲线，轻触出重线)
                        curveSummary = $"Wacom TipFeel 软压感 (档位 {feelShift}, Gamma={gamma:0.##})";
                    }
                    else if (feelShift > 0) // 偏硬 Firm
                    {
                        double factor = feelShift / 3.0;
                        gamma = 1.0 + factor * 1.5; // gamma > 1 (凹曲线，需要更大力)
                        curveSummary = $"Wacom TipFeel 硬压感 (档位 +{feelShift}, Gamma={gamma:0.##})";
                    }
                    else
                    {
                        curveSummary = "Wacom TipFeel 居中标准压感 (线性)";
                    }
                }

                // 检查是否有显式贝塞尔点或四个参数的 PressureCurve (如 Linux/Wacom 的 0 0 100 100)
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
            // 如果不是标准 XML，采用正则匹配
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

/// <summary>
/// 笔迹复现器持久化用户设置（保存在 %APPDATA%\StrokeReplay\settings.json）。
/// </summary>
public sealed class ReplaySettings
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrokeReplay");
    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");

    public string? DriverConfigPath { get; set; }
    public bool EnableDriverCurve { get; set; } = true;
    public double PressureMax { get; set; } = 16383;
    public double Speed { get; set; } = 1.0;

    public static ReplaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<ReplaySettings>(json);
                if (loaded != null)
                    return loaded;
            }
        }
        catch
        {
            // 忽略损坏的设置，使用默认
        }

        return new ReplaySettings();
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsDirectory))
                Directory.CreateDirectory(SettingsDirectory);

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsFile, json, Encoding.UTF8);
        }
        catch
        {
            // 避免保存失败导致整个程序崩溃
        }
    }
}
