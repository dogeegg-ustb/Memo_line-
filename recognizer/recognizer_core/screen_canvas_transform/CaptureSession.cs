using System.Drawing;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Capture;

public enum RoiKind { WorkspaceUser, Navigator, OcrNumbers }

/// <summary>A single host-supplied frame and its local-pixel ROIs.</summary>
public sealed class CaptureSession : IDisposable
{
    public const int MinRoiSizePx = 32;

    public string CaptureId { get; }
    public DateTime CapturedAtUtc { get; } = DateTime.UtcNow;
    public IntRect VirtualScreenBoundsPhysicalPx { get; }
    public int OriginX => VirtualScreenBoundsPhysicalPx.Left;
    public int OriginY => VirtualScreenBoundsPhysicalPx.Top;
    public float DpiX { get; }
    public float DpiY { get; }
    public Bitmap FrozenCapture { get; }
    public IntRect? WorkspaceUserRoiCapturePx { get; private set; }
    public IntRect? NavigatorRoiCapturePx { get; private set; }
    public IntRect? OcrNumbersRoiCapturePx { get; private set; }
    public IntRect CaptureBounds => IntRect.FromXYWH(0, 0, FrozenCapture.Width, FrozenCapture.Height);

    public CaptureSession(string captureId, Bitmap ownedFrame, int originX, int originY, float dpiX, float dpiY)
    {
        CaptureId = captureId;
        FrozenCapture = ownedFrame ?? throw new ArgumentNullException(nameof(ownedFrame));
        VirtualScreenBoundsPhysicalPx = IntRect.FromXYWH(originX, originY, ownedFrame.Width, ownedFrame.Height);
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public bool TrySetRoi(RoiKind kind, IntRect roiCapturePx, out string error)
    {
        if (roiCapturePx.IsEmpty || roiCapturePx.Width < MinRoiSizePx || roiCapturePx.Height < MinRoiSizePx
            || roiCapturePx.Left < 0 || roiCapturePx.Top < 0
            || roiCapturePx.Right > FrozenCapture.Width || roiCapturePx.Bottom > FrozenCapture.Height)
        {
            error = "ROI must be at least 32×32 pixels and stay inside the supplied image.";
            return false;
        }

        switch (kind)
        {
            case RoiKind.WorkspaceUser: WorkspaceUserRoiCapturePx = roiCapturePx; break;
            case RoiKind.Navigator: NavigatorRoiCapturePx = roiCapturePx; break;
            case RoiKind.OcrNumbers: OcrNumbersRoiCapturePx = roiCapturePx; break;
            default: error = "Unknown ROI kind."; return false;
        }
        error = string.Empty;
        return true;
    }

    public IntRect CaptureToScreen(IntRect capturePx) => new(
        capturePx.Left + OriginX, capturePx.Top + OriginY,
        capturePx.Right + OriginX, capturePx.Bottom + OriginY);

    public IntRect ScreenToCapture(IntRect screenPx) => new(
        screenPx.Left - OriginX, screenPx.Top - OriginY,
        screenPx.Right - OriginX, screenPx.Bottom - OriginY);

    public void Dispose() => FrozenCapture.Dispose();
}
