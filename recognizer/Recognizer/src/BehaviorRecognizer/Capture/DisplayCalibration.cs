namespace BehaviorRecognizer.Capture;

/// <summary>Decides which display a tablet drives from hover samples: the display on which the mapped raw
/// position matches the Windows cursor at <see cref="Matches"/> distinct cursor places in a row, while no other
/// display matches.</summary>
public sealed class DisplayCalibration
{
    public const int Matches = 5;
    public const double Tolerance = 3;
    private readonly Dictionary<int, int> _streaks = [];
    private (int X, int Y)? _lastCursor;

    public int? Observe(IEnumerable<(int Index, double X, double Y)> candidates, IEnumerable<int> displays, int cursorX, int cursorY)
    {
        // A cursor that did not move adds no evidence (it may be parked while the pen moves).
        if (_lastCursor == (cursorX, cursorY)) return null;
        _lastCursor = (cursorX, cursorY);
        var hits = candidates.Where(c => Math.Abs(c.X - cursorX) <= Tolerance && Math.Abs(c.Y - cursorY) <= Tolerance)
            .Select(c => c.Index).ToHashSet();
        foreach (int display in displays) _streaks[display] = hits.Contains(display) ? _streaks.GetValueOrDefault(display) + 1 : 0;
        var settled = _streaks.Where(s => s.Value >= Matches).Select(s => s.Key).ToArray();
        if (settled.Length != 1 || hits.Count != 1) return null;
        _streaks.Clear(); _lastCursor = null;
        return settled[0];
    }
}
