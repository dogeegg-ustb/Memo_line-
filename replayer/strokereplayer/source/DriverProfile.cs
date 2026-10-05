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
/// 矩形区域坐标定义（支持物理区域或屏幕区域）。
/// </summary>
public sealed class RectArea
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }

    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);

    public RectArea() { }

    public RectArea(double left, double top, double right, double bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public override string ToString() => $"[{Left:0.##}, {Top:0.##}] → [{Right:0.##}, {Bottom:0.##}] (尺寸: {Width:0.##} x {Height:0.##})";
}

/// <summary>
/// 数位板物理坐标系与屏幕显示坐标系之间的映射配置。
/// </summary>
public sealed class CoordinateMapping
{
    /// <summary>
    /// 映射模式（"Absolute" 绝对坐标 / "Relative" 相对坐标）。
    /// </summary>
    public string MappingMode { get; set; } = "Absolute";

    /// <summary>
    /// 物理活动区域。
    /// </summary>
    public RectArea PhysicalArea { get; set; } = new();

    /// <summary>
    /// 物理单位（例如 "Counts" 原始计数、"mm" 毫米、"LPI" 线每英寸）。
    /// </summary>
    public string PhysicalUnit { get; set; } = "Counts";

    /// <summary>
    /// 目标屏幕显示区域（像素坐标）。
    /// </summary>
    public RectArea? ScreenArea { get; set; }

    /// <summary>
    /// 归一化屏幕映射比例（0.0 ~ 1.0）。
    /// </summary>
    public RectArea? ScreenMapRatio { get; set; }

    /// <summary>
    /// 目标显示器编号（0 通常为主屏或全屏虚拟桌面）。
    /// </summary>
    public int? ScreenIndex { get; set; }

    /// <summary>
    /// 旋转角度（0°, 90°, 180°, 270°）。
    /// </summary>
    public int RotationDegrees { get; set; } = 0;

    /// <summary>
    /// 是否锁定/保持宽高比。
    /// </summary>
    public bool LockAspectRatio { get; set; }

    /// <summary>
    /// 水平映射缩放系数（Screen_Width / Physical_Width）。
    /// </summary>
    public double? ScaleX => (ScreenArea != null && PhysicalArea.Width > 0)
        ? ScreenArea.Width / PhysicalArea.Width : null;

    /// <summary>
    /// 垂直映射缩放系数（Screen_Height / Physical_Height）。
    /// </summary>
    public double? ScaleY => (ScreenArea != null && PhysicalArea.Height > 0)
        ? ScreenArea.Height / PhysicalArea.Height : null;

    /// <summary>
    /// 将物理坐标映射到屏幕坐标。
    /// </summary>
    public (double ScreenX, double ScreenY) PhysicalToScreen(double px, double py, double defaultScreenWidth = 1920, double defaultScreenHeight = 1080)
    {
        double pWidth = PhysicalArea.Width > 0 ? PhysicalArea.Width : 1.0;
        double pHeight = PhysicalArea.Height > 0 ? PhysicalArea.Height : 1.0;

        double normX = Math.Clamp((px - PhysicalArea.Left) / pWidth, 0.0, 1.0);
        double normY = Math.Clamp((py - PhysicalArea.Top) / pHeight, 0.0, 1.0);

        if (RotationDegrees == 90)
        {
            double tmp = normX;
            normX = 1.0 - normY;
            normY = tmp;
        }
        else if (RotationDegrees == 180)
        {
            normX = 1.0 - normX;
            normY = 1.0 - normY;
        }
        else if (RotationDegrees == 270)
        {
            double tmp = normX;
            normX = normY;
            normY = 1.0 - tmp;
        }

        if (ScreenArea != null && ScreenArea.Width > 0 && ScreenArea.Height > 0)
        {
            double sx = ScreenArea.Left + normX * ScreenArea.Width;
            double sy = ScreenArea.Top + normY * ScreenArea.Height;
            return (sx, sy);
        }

        if (ScreenMapRatio != null)
        {
            double sLeft = ScreenMapRatio.Left * defaultScreenWidth;
            double sTop = ScreenMapRatio.Top * defaultScreenHeight;
            double sWidth = ScreenMapRatio.Width * defaultScreenWidth;
            double sHeight = ScreenMapRatio.Height * defaultScreenHeight;

            double sx = sLeft + normX * sWidth;
            double sy = sTop + normY * sHeight;
            return (sx, sy);
        }

        return (normX * defaultScreenWidth, normY * defaultScreenHeight);
    }

    /// <summary>
    /// 将屏幕坐标反向映射到数位板物理坐标。
    /// </summary>
    public (double PhysicalX, double PhysicalY) ScreenToPhysical(double sx, double sy, double defaultScreenWidth = 1920, double defaultScreenHeight = 1080)
    {
        double normX = 0;
        double normY = 0;

        if (ScreenArea != null && ScreenArea.Width > 0 && ScreenArea.Height > 0)
        {
            normX = Math.Clamp((sx - ScreenArea.Left) / ScreenArea.Width, 0.0, 1.0);
            normY = Math.Clamp((sy - ScreenArea.Top) / ScreenArea.Height, 0.0, 1.0);
        }
        else if (ScreenMapRatio != null)
        {
            double sLeft = ScreenMapRatio.Left * defaultScreenWidth;
            double sTop = ScreenMapRatio.Top * defaultScreenHeight;
            double sWidth = ScreenMapRatio.Width * defaultScreenWidth;
            double sHeight = ScreenMapRatio.Height * defaultScreenHeight;

            normX = Math.Clamp((sx - sLeft) / (sWidth > 0 ? sWidth : 1.0), 0.0, 1.0);
            normY = Math.Clamp((sy - sTop) / (sHeight > 0 ? sHeight : 1.0), 0.0, 1.0);
        }
        else
        {
            normX = Math.Clamp(sx / defaultScreenWidth, 0.0, 1.0);
            normY = Math.Clamp(sy / defaultScreenHeight, 0.0, 1.0);
        }

        if (RotationDegrees == 90)
        {
            double tmp = normX;
            normX = normY;
            normY = 1.0 - tmp;
        }
        else if (RotationDegrees == 180)
        {
            normX = 1.0 - normX;
            normY = 1.0 - normY;
        }
        else if (RotationDegrees == 270)
        {
            double tmp = normX;
            normX = 1.0 - normY;
            normY = tmp;
        }

        double px = PhysicalArea.Left + normX * PhysicalArea.Width;
        double py = PhysicalArea.Top + normY * PhysicalArea.Height;
        return (px, py);
    }

    public string GetFormulaDescription()
    {
        if (ScreenArea != null && ScaleX.HasValue && ScaleY.HasValue)
        {
            return $"Screen_X = (Physical_X - {PhysicalArea.Left:0.##}) * {ScaleX.Value:0.####} + {ScreenArea.Left:0.##}\n" +
                   $"Screen_Y = (Physical_Y - {PhysicalArea.Top:0.##}) * {ScaleY.Value:0.####} + {ScreenArea.Top:0.##}";
        }

        if (ScreenMapRatio != null)
        {
            return $"Screen_X = [(Physical_X - {PhysicalArea.Left:0.##}) / {PhysicalArea.Width:0.##} * {ScreenMapRatio.Width:0.##} + {ScreenMapRatio.Left:0.##}] * Screen_Width\n" +
                   $"Screen_Y = [(Physical_Y - {PhysicalArea.Top:0.##}) / {PhysicalArea.Height:0.##} * {ScreenMapRatio.Height:0.##} + {ScreenMapRatio.Top:0.##}] * Screen_Height";
        }

        return $"Screen_X = (Physical_X - {PhysicalArea.Left:0.##}) / {PhysicalArea.Width:0.##} * Screen_Width\n" +
               $"Screen_Y = (Physical_Y - {PhysicalArea.Top:0.##}) / {PhysicalArea.Height:0.##} * Screen_Height";
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
    public CoordinateMapping? CoordinateMapping { get; init; }

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
    /// <summary>
    /// 自动扫描本地计算机常见数位板驱动配置文件存放路径及用户常用目录中的备份。
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
        candidates.Add(Path.Combine(appData, "GaomonTablet", "data", "EKeySetting.dt"));

        // 2. 绘王
        candidates.Add(Path.Combine(appData, "HUION", "data", "EKeySetting.dt"));
        candidates.Add(Path.Combine(appData, "HUION", "data", "Setting.json"));
        candidates.Add(Path.Combine(appData, "HuionTablet", "data", "EKeySetting.dt"));

        // 3. Wacom
        candidates.Add(Path.Combine(appData, "WTablet", "WacomTabletUserDefaults.xml"));
        candidates.Add(Path.Combine(appData, "WTablet", "Wacom_Tablet.dat"));
        candidates.Add(Path.Combine(localAppData, "Wacom", "WacomTabletUserDefaults.xml"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WacomTabletDefaults.xml"));

        // 4. OpenTabletDriver
        candidates.Add(Path.Combine(localAppData, "OpenTabletDriver", "settings.json"));
        candidates.Add(Path.Combine(appData, "OpenTabletDriver", "settings.json"));

        // 5. XP-Pen / 友基 / 绘客 / 绘王兼容
        candidates.Add(Path.Combine(userProfile, ".PenTablet", "config.xml"));
        candidates.Add(Path.Combine(appData, "Pentablet", "config.xml"));
        candidates.Add(Path.Combine(appData, "XP-Pen", "config.xml"));
        candidates.Add(Path.Combine(appData, "Ugee", "config.xml"));
        candidates.Add(Path.Combine(appData, "VKTablet", "config.xml"));

        // 6. 扫描用户常用目录（桌面、文档、下载）中的 Wacom 备份文件及 EKeySetting
        var userDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(userProfile, "Downloads")
        };

        foreach (var dir in userDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".wacomprefs" or ".wacomxs" or ".prefs" ||
                        file.EndsWith("EKeySetting.dt", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(file);
                    }
                }
            }
            catch
            {
                // 忽略没有权限访问的目录
            }
        }

        return candidates.Where(p => File.Exists(p) || Directory.Exists(p)).Distinct().ToList();
    }

    /// <summary>
    /// 自动扫描并解析本机安装的所有有效数位板驱动配置。
    /// </summary>
    public static List<DriverProfile> FindAllInstalledProfiles()
    {
        var candidates = AutoDetectCandidatePaths();
        var list = new List<DriverProfile>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidates)
        {
            if (!visited.Add(path)) continue;
            try
            {
                var p = Parse(path);
                list.Add(p);
            }
            catch
            {
                // 忽略非数位板配置或损坏的文件
            }
        }

        return list;
    }

    /// <summary>
    /// 自动寻找并返回最推荐的数位板驱动配置。
    /// 优先级：具有三次贝塞尔曲线 > 具有 Gamma/TipFeel > 其它有效配置。
    /// </summary>
    public static DriverProfile? FindBestProfile()
    {
        var profiles = FindAllInstalledProfiles();
        if (profiles.Count == 0) return null;

        // 优先具有非线性贝塞尔响应曲线的驱动
        var bezier = profiles.FirstOrDefault(p => p.BezierCurve != null);
        if (bezier != null) return bezier;

        // 其次选择具有 Gamma / TipFeel 设置的驱动
        var gamma = profiles.FirstOrDefault(p => p.GammaCurve.HasValue);
        if (gamma != null) return gamma;

        return profiles[0];
    }

    /// <summary>
    /// 解析指定路径的数位板驱动配置文件。
    /// </summary>
    public static DriverProfile Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("配置文件路径不能为空。", nameof(path));

        path = Path.GetFullPath(path);

        // 1. 如果是目录，尝试作为 Wacom 解压目录解析
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

        double left = bestElement.TryGetProperty("Left", out var lElem) && lElem.TryGetDouble(out var lv) ? lv : 0.0;
        double top = bestElement.TryGetProperty("Top", out var tElem) && tElem.TryGetDouble(out var tv) ? tv : 0.0;
        double? right = bestElement.TryGetProperty("Right", out var rElem) && rElem.TryGetDouble(out var rv) ? rv : null;
        double? bottom = bestElement.TryGetProperty("Bottom", out var bElem) && bElem.TryGetDouble(out var bv) ? bv : null;

        int rotateAngle = bestElement.TryGetProperty("RotateAngle", out var rotElem) && rotElem.TryGetInt32(out var rot) ? rot : 0;
        bool isAbsolute = !bestElement.TryGetProperty("AbsoluteMode", out var absElem) || absElem.GetBoolean();
        int screenNum = bestElement.TryGetProperty("ScreenNum", out var snElem) && snElem.TryGetInt32(out var sn) ? sn : 0;

        double smal = bestElement.TryGetProperty("ScreenMapAreaLeft", out var smalElem) && smalElem.TryGetDouble(out var smalV) ? smalV : 0.0;
        double smat = bestElement.TryGetProperty("ScreenMapAreaTop", out var smatElem) && smatElem.TryGetDouble(out var smatV) ? smatV : 0.0;
        double smar = bestElement.TryGetProperty("ScreenMapAreaRight", out var smarElem) && smarElem.TryGetDouble(out var smarV) ? smarV : 1.0;
        double smab = bestElement.TryGetProperty("ScreenMapAreaBottom", out var smabElem) && smabElem.TryGetDouble(out var smabV) ? smabV : 1.0;

        CoordinateMapping? coordMapping = null;
        if (right.HasValue && bottom.HasValue)
        {
            coordMapping = new CoordinateMapping
            {
                MappingMode = isAbsolute ? "Absolute" : "Relative",
                PhysicalArea = new RectArea(left, top, right.Value, bottom.Value),
                PhysicalUnit = "Counts",
                ScreenMapRatio = new RectArea(smal, smat, smar, smab),
                ScreenIndex = screenNum,
                RotationDegrees = rotateAngle,
                LockAspectRatio = false
            };
        }

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
            ThresholdRatio = 0,
            CoordinateMapping = coordMapping
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

        double tabLeft = 0.0, tabTop = 0.0;
        double? dispLeft = null, dispTop = null, dispRight = null, dispBottom = null;
        int orientation = 0;
        bool forceProportions = false;
        bool isAbsolute = true;

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

                var tabLeftElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("TabletAreaLeft", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("TabletX", StringComparison.OrdinalIgnoreCase));
                var tabTopElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("TabletAreaTop", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("TabletY", StringComparison.OrdinalIgnoreCase));
                if (tabLeftElem != null && double.TryParse(tabLeftElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tl)) tabLeft = tl;
                if (tabTopElem != null && double.TryParse(tabTopElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tt)) tabTop = tt;

                var dLeftElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("DisplayAreaLeft", StringComparison.OrdinalIgnoreCase));
                var dTopElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("DisplayAreaTop", StringComparison.OrdinalIgnoreCase));
                var dRightElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("DisplayAreaRight", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("DisplayWidth", StringComparison.OrdinalIgnoreCase));
                var dBottomElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("DisplayAreaBottom", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("DisplayHeight", StringComparison.OrdinalIgnoreCase));
                if (dLeftElem != null && double.TryParse(dLeftElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dl)) dispLeft = dl;
                if (dTopElem != null && double.TryParse(dTopElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dt)) dispTop = dt;
                if (dRightElem != null && double.TryParse(dRightElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dr)) dispRight = dr;
                if (dBottomElem != null && double.TryParse(dBottomElem.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)) dispBottom = db;

                var orientElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Orientation", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("Rotation", StringComparison.OrdinalIgnoreCase));
                if (orientElem != null && int.TryParse(orientElem.Value, out var ov))
                {
                    orientation = ov == 1 ? 90 : ov == 2 ? 180 : ov == 3 ? 270 : ov;
                }

                var propElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("ForceProportions", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("ProportionsLocked", StringComparison.OrdinalIgnoreCase));
                if (propElem != null && bool.TryParse(propElem.Value, out var fp)) forceProportions = fp;

                var modeElem = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("TrackingMode", StringComparison.OrdinalIgnoreCase));
                if (modeElem != null && int.TryParse(modeElem.Value, out var tm)) isAbsolute = (tm == 0);

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
            // 回退到简单正则表达式匹配
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

        CoordinateMapping? coordMapping = null;
        if (width.HasValue && height.HasValue)
        {
            RectArea? screenArea = null;
            if (dispRight.HasValue && dispBottom.HasValue)
            {
                screenArea = new RectArea(dispLeft ?? 0, dispTop ?? 0, dispRight.Value, dispBottom.Value);
            }

            coordMapping = new CoordinateMapping
            {
                MappingMode = isAbsolute ? "Absolute" : "Relative",
                PhysicalArea = new RectArea(tabLeft, tabTop, width.Value, height.Value),
                PhysicalUnit = "Counts",
                ScreenArea = screenArea,
                RotationDegrees = orientation,
                LockAspectRatio = forceProportions
            };
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
            ThresholdRatio = thresholdRatio,
            CoordinateMapping = coordMapping
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
        CoordinateMapping? coordMapping = null;

        var firstProfile = profiles[0];
        if (firstProfile.ValueKind == JsonValueKind.Object)
        {
            if (firstProfile.TryGetProperty("Tablet", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
            {
                deviceName = $"OTD · {t.GetString()}";
            }

            if (firstProfile.TryGetProperty("AbsoluteModeSettings", out var absMode) && absMode.ValueKind == JsonValueKind.Object)
            {
                RectArea? physArea = null;
                RectArea? dispArea = null;
                int rotDeg = 0;
                bool lockAspect = false;

                if (absMode.TryGetProperty("Tablet", out var tabArea) && tabArea.ValueKind == JsonValueKind.Object)
                {
                    double tw = tabArea.TryGetProperty("Width", out var twElem) && twElem.TryGetDouble(out var twV) ? twV : 0;
                    double th = tabArea.TryGetProperty("Height", out var thElem) && thElem.TryGetDouble(out var thV) ? thV : 0;
                    double tx = tabArea.TryGetProperty("X", out var txElem) && txElem.TryGetDouble(out var txV) ? txV : tw / 2.0;
                    double ty = tabArea.TryGetProperty("Y", out var tyElem) && tyElem.TryGetDouble(out var tyV) ? tyV : th / 2.0;
                    double trot = tabArea.TryGetProperty("Rotation", out var trotElem) && trotElem.TryGetDouble(out var trV) ? trV : 0;
                    rotDeg = (int)Math.Round(trot);

                    physArea = new RectArea(Math.Round(tx - tw / 2.0, 2), Math.Round(ty - th / 2.0, 2), Math.Round(tx + tw / 2.0, 2), Math.Round(ty + th / 2.0, 2));
                    width = tw;
                    height = th;
                }

                if (absMode.TryGetProperty("Display", out var dispAreaObj) && dispAreaObj.ValueKind == JsonValueKind.Object)
                {
                    double dw = dispAreaObj.TryGetProperty("Width", out var dwElem) && dwElem.TryGetDouble(out var dwV) ? dwV : 0;
                    double dh = dispAreaObj.TryGetProperty("Height", out var dhElem) && dhElem.TryGetDouble(out var dhV) ? dhV : 0;
                    double dx = dispAreaObj.TryGetProperty("X", out var dxElem) && dxElem.TryGetDouble(out var dxV) ? dxV : dw / 2.0;
                    double dy = dispAreaObj.TryGetProperty("Y", out var dyElem) && dyElem.TryGetDouble(out var dyV) ? dyV : dh / 2.0;

                    dispArea = new RectArea(Math.Round(dx - dw / 2.0, 2), Math.Round(dy - dh / 2.0, 2), Math.Round(dx + dw / 2.0, 2), Math.Round(dy + dh / 2.0, 2));
                }

                if (absMode.TryGetProperty("LockAspectRatio", out var laElem))
                    lockAspect = laElem.GetBoolean();

                if (physArea != null)
                {
                    coordMapping = new CoordinateMapping
                    {
                        MappingMode = "Absolute",
                        PhysicalArea = physArea,
                        PhysicalUnit = "mm",
                        ScreenArea = dispArea,
                        RotationDegrees = rotDeg,
                        LockAspectRatio = lockAspect
                    };
                }
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
            ThresholdRatio = thresholdRatio,
            CoordinateMapping = coordMapping
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
