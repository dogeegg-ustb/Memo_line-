using System.Drawing;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Capture;

public enum RoiKind
{
    WorkspaceUser,
    Navigator
}

/// <summary>
/// One frozen capture session bound by CaptureId.
/// Frame is virtual-desktop sized but only CLIP STUDIO PAINT UI-thread windows are painted.
/// WorkspaceUserRoi and NavigatorRoi are distinct objects (architecture §2/§5).
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

    /// <summary>User coarse workspace ROI in capture pixels (half-open). Sampling constraint only.</summary>
    public IntRect? WorkspaceUserRoiCapturePx { get; private set; }

    /// <summary>User navigator panel ROI in capture pixels — adopted directly, never border-corrected.</summary>
    public IntRect? NavigatorRoiCapturePx { get; private set; }

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

    /// <summary>
    /// Freeze only CLIP STUDIO PAINT UI-thread windows into a virtual-desktop-sized frame.
    /// </summary>
    public static CaptureSession CreateFromClipStudioWindows()
    {
        var bounds = ScreenCapture.GetVirtualScreenBoundsPhysical();
        var bitmap = ClipStudioCapture.CaptureThreadWindows();

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

    /// <summary>Backward-compatible alias — now captures CSP thread windows, not the full desktop.</summary>
    public static CaptureSession CreateFromVirtualScreen() => CreateFromClipStudioWindows();

    public IntRect CaptureBounds => IntRect.FromXYWH(0, 0, FrozenCapture.Width, FrozenCapture.Height);

    public bool TrySetRoi(RoiKind kind, IntRect roiCapturePx, out string error)
    {
        var clamped = roiCapturePx.ClampTo(CaptureBounds);
        if (clamped.Width < MinRoiSizePx || clamped.Height < MinRoiSizePx)
        {
            error = $"ROI 过小（最小 {MinRoiSizePx}×{MinRoiSizePx} 像素）。";
            return false;
        }

        switch (kind)
        {
            case RoiKind.WorkspaceUser:
                WorkspaceUserRoiCapturePx = clamped;
                break;
            case RoiKind.Navigator:
                NavigatorRoiCapturePx = clamped;
                break;
            default:
                error = "未知 ROI 类型。";
                return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Clear a user ROI so the same session can re-select that stage.</summary>
    public void ClearRoi(RoiKind kind)
    {
        switch (kind)
        {
            case RoiKind.WorkspaceUser:
                WorkspaceUserRoiCapturePx = null;
                break;
            case RoiKind.Navigator:
                NavigatorRoiCapturePx = null;
                break;
        }
    }

    public IntRect CaptureToScreen(IntRect capturePx)
        => new(
            capturePx.Left + OriginX,
            capturePx.Top + OriginY,
            capturePx.Right + OriginX,
            capturePx.Bottom + OriginY);

    public IntRect ScreenToCapture(IntRect screenPx)
        => new(
            screenPx.Left - OriginX,
            screenPx.Top - OriginY,
            screenPx.Right - OriginX,
            screenPx.Bottom - OriginY);

    public void Dispose()
    {
        FrozenCapture.Dispose();
    }
}
