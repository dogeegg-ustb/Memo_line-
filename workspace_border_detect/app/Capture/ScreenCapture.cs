using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WorkspaceBorderDetect.Models;

namespace WorkspaceBorderDetect.Capture;

/// <summary>
/// Captures the full Windows virtual desktop (all monitors), including negative origins.
/// </summary>
public static class ScreenCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint SrcCopy = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    public static IntRect GetVirtualScreenBoundsPhysical()
    {
        int x = GetSystemMetrics(SmXVirtualScreen);
        int y = GetSystemMetrics(SmYVirtualScreen);
        int w = GetSystemMetrics(SmCxVirtualScreen);
        int h = GetSystemMetrics(SmCyVirtualScreen);
        return IntRect.FromXYWH(x, y, w, h);
    }

    /// <summary>
    /// Freeze a full virtual-desktop screenshot in 32bpp ARGB (BGRA in memory).
    /// </summary>
    public static Bitmap CaptureVirtualScreen()
    {
        var bounds = GetVirtualScreenBoundsPhysical();
        int width = bounds.Width;
        int height = bounds.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("虚拟桌面尺寸无效。");

        // Prefer BitBlt from the desktop DC so negative virtual origins are covered.
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return CaptureVirtualScreenFallback(bounds);

        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr old = SelectObject(memDc, hBitmap);

        bool ok = BitBlt(memDc, 0, 0, width, height, screenDc, bounds.Left, bounds.Top, SrcCopy);

        SelectObject(memDc, old);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        if (!ok)
        {
            DeleteObject(hBitmap);
            return CaptureVirtualScreenFallback(bounds);
        }

        try
        {
            using var temp = Image.FromHbitmap(hBitmap);
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.DrawImageUnscaled(temp, 0, 0);
            }
            return result;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private static Bitmap CaptureVirtualScreenFallback(IntRect bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height),
            CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
