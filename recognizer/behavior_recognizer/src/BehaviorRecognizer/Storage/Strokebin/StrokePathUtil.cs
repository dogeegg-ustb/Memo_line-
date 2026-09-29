using BehaviorRecognizer.Abstractions.Stroke;

namespace BehaviorRecognizer.Storage.Strokebin;

/// <summary>输出路径与时间工具�?/summary>
public static class StrokePathUtil
{
    /// <summary>当前 UTC Unix 毫秒�?/summary>
    public static ulong NowUnixMs() =>
        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // UTC 毫秒

    /// <summary>确保并返�?&lt;root&gt;/stroke 目录�?/summary>
    public static string MakeStrokeDir(string outputRoot)
    {
        var dir = Path.Combine(outputRoot, "stroke"); // 拼接 stroke 子目�?
        Directory.CreateDirectory(dir); // 自动创建
        return dir; // 返回绝对/规范化路�?
    }

    /// <summary>�?UTC 时间生成下一可用 .strokebin 路径�?/summary>
    public static string NextStrokeBinPath(string strokeDir, ulong unixMs)
    {
        var baseName = FormatFileTimestamp(unixMs); // yyyyMMdd_HHmmss
        var candidate = Path.Combine(strokeDir, baseName + ".strokebin"); // 首选文件名
        if (!File.Exists(candidate)) // 无冲突直接用
            return candidate;

        for (var i = 1; i <= 999; i++) // 同秒冲突追加 _001.._999
        {
            candidate = Path.Combine(strokeDir, $"{baseName}_{i:D3}.strokebin"); // 编号后缀
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(strokeDir, baseName + "_overflow.strokebin"); // 仍冲突用 overflow
    }

    /// <summary>UTC 格式化为 yyyyMMdd_HHmmss�?/summary>
    public static string FormatFileTimestamp(ulong unixMs)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)unixMs); // �?DateTimeOffset
        return dto.UtcDateTime.ToString("yyyyMMdd_HHmmss"); // UTC 文件名时�?
    }

    /// <summary>UTC 毫秒�?ISO8601�?/summary>
    public static string ToIso8601(ulong unixMs)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)unixMs); // 转时�?
        return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z"; // ISO8601Z
    }
}
