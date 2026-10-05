namespace BehaviorRecognizer.Capture;

/// <summary>Observes the Windows input target used for CSP interaction scheduling.</summary>
public interface IInputTargetProbe
{
    bool TryGetCspCursor(out CspWindowProbe.ScreenPoint point);
    bool IsCspPoint(int x, int y);
    bool IsCspForeground();
}

internal sealed class CspInputTargetProbe : IInputTargetProbe
{
    public bool TryGetCspCursor(out CspWindowProbe.ScreenPoint point) => CspWindowProbe.TryGetCspCursor(out point);
    public bool IsCspPoint(int x, int y) => CspWindowProbe.IsCspPoint(x, y);
    public bool IsCspForeground() => CspWindowProbe.IsCspForeground();
}
