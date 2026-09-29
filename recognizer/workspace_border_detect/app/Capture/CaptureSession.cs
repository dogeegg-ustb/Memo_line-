using System.Drawing;
using WorkspaceBorderDetect.Models;

namespace WorkspaceBorderDetect.Capture;

/// <summary>
/// One frozen virtual-desktop capture session bound by CaptureId.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    public const int MinRoiSizePx = 32;

    public string CaptureId { get; }
    public DateTime CapturedAtUtc { get; }

    /// <summary>Virtual desktop bounds in screen physical pixels (origin may be negative).</summary>
    public IntRect VirtualScreenBoundsPhysicalPx { get; }

    public int OriginX => VirtualScreenBoundsPhysicalPx.Left;
    public int OriginY => VirtualScreenBoundsPhysicalPx.Top;

    public float DpiX { get; }
    public float DpiY { get; }

    /// <summary>Frozen frame; top-left corresponds to (OriginX, OriginY) on the virtual desktop.</summary>
    public Bitmap FrozenCapture { get; }

    /// <summary>User ROI in capture pixel coordinates (half-open).</summary>
    public IntRect? UserRoiCapturePx { get; private set; }

    public CaptureSession(
        string captureId,
        Bitmap frozenCapture,
        IntRect virtualScreenBoundsPhysicalPx,
        float dpiX,
        float dpiY)
    {
        CaptureId = captureId;
        CapturedAtUtc = DateTime.UtcNow;
        FrozenCapture = frozenCapture ?? throw new ArgumentNullException(nameof(frozenCapture));
        VirtualScreenBoundsPhysicalPx = virtualScreenBoundsPhysicalPx;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public static CaptureSession CreateFromVirtualScreen()
    {
        var bounds = ScreenCapture.GetVirtualScreenBoundsPhysical();
        var bitmap = ScreenCapture.CaptureVirtualScreen();

        float dpiX = 96f;
        float dpiY = 96f;
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            dpiX = g.DpiX;
            dpiY = g.DpiY;
        }
        catch
        {
            // keep 96
        }

        return new CaptureSession(
            Guid.NewGuid().ToString("N"),
            bitmap,
            bounds,
            dpiX,
            dpiY);
    }

    public IntRect CaptureBounds => IntRect.FromXYWH(0, 0, FrozenCapture.Width, FrozenCapture.Height);

    public bool TrySetUserRoi(IntRect roiCapturePx, out string error)
    {
        var clamped = roiCapturePx.ClampTo(CaptureBounds);
        if (clamped.Width < MinRoiSizePx || clamped.Height < MinRoiSizePx)
        {
            error = $"ROI 过小（最小 {MinRoiSizePx}×{MinRoiSizePx} 像素）。";
            return false;
        }

        UserRoiCapturePx = clamped;
        error = string.Empty;
        return true;
    }

    public IntRect CaptureToScreen(IntRect capturePx)
        => new(
            capturePx.Left + OriginX,
            capturePx.Top + OriginY,
            capturePx.Right + OriginX,
            capturePx.Bottom + OriginY);

    public void Dispose()
    {
        FrozenCapture.Dispose();
    }
}
