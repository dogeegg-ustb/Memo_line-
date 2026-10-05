namespace StrokeReplay;

internal enum ReplayMode { Concentrated, Discrete }

internal sealed record ReplayOptions(ReplayMode Mode = ReplayMode.Concentrated, double StrokeGapSeconds = .2)
{
    internal string DisplayName => Mode == ReplayMode.Discrete ? "离散模式" : "集中模式";

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("未知的回放模式。");
        if (!double.IsFinite(StrokeGapSeconds) || StrokeGapSeconds < 0 || StrokeGapSeconds > .2)
            throw new ArgumentException("离散笔间隔上限须在 0 到 0.2 秒之间。");
    }

    internal TimeSpan GapBetween(long endTicks, long? nextStartTicks, long frequency)
    {
        Validate();
        if (frequency <= 0) throw new ArgumentException("录制时钟频率必须是正数。");
        if (Mode != ReplayMode.Discrete || nextStartTicks is null || nextStartTicks <= endTicks)
            return TimeSpan.Zero;
        var seconds = ((double)nextStartTicks.Value - endTicks) / frequency;
        return TimeSpan.FromSeconds(Math.Min(seconds, StrokeGapSeconds));
    }

    internal static ReplayMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "concentrated" or "集中模式" => ReplayMode.Concentrated,
        "discrete" or "离散模式" => ReplayMode.Discrete,
        _ => throw new ArgumentException("回放模式须为 concentrated（集中模式）或 discrete（离散模式）。")
    };
}
