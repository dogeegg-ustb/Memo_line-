namespace StrokeReplay;

internal enum ReplayMode { Concentrated, Discrete }

/// <param name="FixedGap">Discrete strokes always wait exactly <paramref name="StrokeGapSeconds"/> between contacts,
/// whatever the recorded pause; otherwise the recorded pause is capped at it.</param>
/// <param name="StrokeSeconds">When set, every contact is replayed in this time (its samples keep their relative
/// timing, compressed); null replays the recorded timing. A contact recorded faster than this keeps its own speed.</param>
/// <param name="MinSampleSeconds">The shortest time between two samples of a compressed contact. CSP draws only the
/// pointer updates it reads: injected faster than it reads them, Windows merges them and a stroke collapses into a
/// few straight segments (measured: 20 ms per contact kept 23% of the ink).</param>
/// <param name="SimplifyPixels">When positive, a contact is reduced to the samples that keep its path and pressure
/// within this many screen pixels (Ramer–Douglas–Peucker; pressure counts <see cref="PressurePixels"/> pixels per unit),
/// so a compressed contact needs fewer pointer updates.</param>
/// <param name="DownSettleSeconds">How long a compressed contact waits after its pen-down before the next sample:
/// CSP is busy starting the stroke then, and updates arriving meanwhile are merged into one.</param>
/// <param name="SyncRounds">After each injected sample, how many WM_NULL messages are sent (and waited for) to the window
/// under the pen: each returns only once CSP's UI thread is back in its message loop, so the next sample is not
/// injected while CSP is still handling the previous one.</param>
internal sealed record ReplayOptions(ReplayMode Mode = ReplayMode.Concentrated, double StrokeGapSeconds = .2, bool FixedGap = false,
    double? StrokeSeconds = null, double MinSampleSeconds = 0, double SimplifyPixels = 0, double DownSettleSeconds = 0,
    int SyncRounds = 0)
{
    internal const double PressurePixels = 40;
    internal string DisplayName => Mode == ReplayMode.Discrete ? "离散模式" : "集中模式";

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("未知的回放模式。");
        // Memoline's precise undo lets the user set its discrete pause up to 1 s; the replayer windows still cap theirs at 0.2 s.
        if (!double.IsFinite(StrokeGapSeconds) || StrokeGapSeconds < 0 || StrokeGapSeconds > 1)
            throw new ArgumentException("离散笔间隔上限须在 0 到 1 秒之间。");
        if (StrokeSeconds is { } stroke && (!double.IsFinite(stroke) || stroke <= 0 || stroke > 10))
            throw new ArgumentException("每笔回放时长须在 0 到 10 秒之间。");
        if (!double.IsFinite(MinSampleSeconds) || MinSampleSeconds < 0 || MinSampleSeconds > .1)
            throw new ArgumentException("采样最小间隔须在 0 到 0.1 秒之间。");
        if (!double.IsFinite(SimplifyPixels) || SimplifyPixels < 0 || SimplifyPixels > 20)
            throw new ArgumentException("笔迹简化容差须在 0 到 20 像素之间。");
        if (!double.IsFinite(DownSettleSeconds) || DownSettleSeconds < 0 || DownSettleSeconds > .5)
            throw new ArgumentException("落笔等待须在 0 到 0.5 秒之间。");
    }

    /// <summary>The contact with only the samples <see cref="SimplifyPixels"/> needs: the first and last contact
    /// samples, every sample that is not in contact (the pen-up), and the interior points RDP keeps.</summary>
    internal MemolineReplayStroke Simplify(MemolineReplayStroke stroke)
    {
        if (SimplifyPixels <= 0) return stroke;
        var samples = stroke.Samples;
        var contact = Enumerable.Range(0, samples.Count).Where(i => samples[i].InContact).ToArray();
        if (contact.Length <= 2) return stroke;
        var keep = new bool[samples.Count];
        for (int i = 0; i < samples.Count; i++) keep[i] = !samples[i].InContact;
        keep[contact[0]] = keep[contact[^1]] = true;
        var pending = new Stack<(int From, int To)>();
        pending.Push((0, contact.Length - 1));
        while (pending.TryPop(out var span))
        {
            if (span.To - span.From < 2) continue;
            var a = samples[contact[span.From]]; var b = samples[contact[span.To]];
            int farthest = -1; double distance = SimplifyPixels;
            for (int k = span.From + 1; k < span.To; k++)
            {
                double d = Deviation(samples[contact[k]], a, b);
                if (d > distance) { distance = d; farthest = k; }
            }
            if (farthest < 0) continue;
            keep[contact[farthest]] = true;
            pending.Push((span.From, farthest)); pending.Push((farthest, span.To));
        }
        return stroke with { Samples = samples.Where((_, i) => keep[i]).ToArray() };
    }

    // Distance of p from segment a–b in (x, y, pressure·PressurePixels) space.
    private static double Deviation(MemolineReplaySample p, MemolineReplaySample a, MemolineReplaySample b)
    {
        double ax = a.X, ay = a.Y, az = a.Pressure * PressurePixels, bx = b.X - ax, by = b.Y - ay, bz = b.Pressure * PressurePixels - az;
        double px = p.X - ax, py = p.Y - ay, pz = p.Pressure * PressurePixels - az;
        double length = bx * bx + by * by + bz * bz;
        double t = length <= 0 ? 0 : Math.Clamp((px * bx + py * by + pz * bz) / length, 0, 1);
        double dx = px - t * bx, dy = py - t * by, dz = pz - t * bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The playback speed of one contact: the recorded speed, raised so the contact takes at most
    /// <see cref="StrokeSeconds"/>.</summary>
    internal double SpeedFor(long startTicks, long endTicks, long frequency, double speed)
    {
        if (StrokeSeconds is not { } target || frequency <= 0 || endTicks <= startTicks) return speed;
        return Math.Max(speed, (endTicks - startTicks) / (double)frequency / target);
    }

    internal TimeSpan GapBetween(long endTicks, long? nextStartTicks, long frequency)
    {
        Validate();
        if (frequency <= 0) throw new ArgumentException("录制时钟频率必须是正数。");
        if (Mode == ReplayMode.Discrete && FixedGap) return TimeSpan.FromSeconds(StrokeGapSeconds);
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
