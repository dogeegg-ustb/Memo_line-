using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ScreenCanvasTransform.Diagnostics;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Capture;

/// <summary>
/// Captures only visible top-level windows belonging to CLIP STUDIO PAINT's UI thread.
/// Result is composited into a virtual-desktop-sized bitmap so ScreenPhysicalPx mapping is unchanged.
/// </summary>
public static class ClipStudioCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const int DwmaCloaked = 14;

    private static readonly string[] TargetProcessNames =
    {
        "CLIPStudioPaint"
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

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
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private readonly struct WindowEntry
    {
        public WindowEntry(IntPtr hwnd, IntRect screenRect, int zOrder)
        {
            Hwnd = hwnd;
            ScreenRect = screenRect;
            ZOrder = zOrder;
        }

        public IntPtr Hwnd { get; }
        public IntRect ScreenRect { get; }
        public int ZOrder { get; }
    }

    /// <summary>
    /// Freeze a virtual-desktop-sized frame that contains only CLIP STUDIO PAINT UI-thread windows.
    /// </summary>
    public static Bitmap CaptureThreadWindows()
    {
        var desktop = ScreenCapture.GetVirtualScreenBoundsPhysical();
        if (desktop.Width <= 0 || desktop.Height <= 0)
            throw new InvalidOperationException("虚拟桌面尺寸无效。");

        if (!TryResolveTargetThread(out uint processId, out uint threadId, out string processName))
        {
            throw new InvalidOperationException(
                "未找到 CLIP STUDIO PAINT 窗口。请先打开 CSP，并确保其窗口可见。");
        }

        var windows = EnumerateThreadWindows(processId, threadId, desktop);
        if (windows.Count == 0)
        {
            throw new InvalidOperationException(
                $"已定位进程 {processName}(pid={processId})，但其 UI 线程(tid={threadId})没有可截取的可见窗口。");
        }

        LiveDebugLog.Write(
            $"[ClipStudioCapture] process={processName} pid={processId} tid={threadId} windows={windows.Count} " +
            $"desktop={desktop.Width}x{desktop.Height} origin=({desktop.Left},{desktop.Top})");

        var result = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.Clear(Color.Black);
            // EnumWindows is top→bottom; paint bottom→top so overlaps match on-screen stacking.
            foreach (var window in windows.OrderByDescending(w => w.ZOrder))
            {
                using var windowBmp = TryPrintWindow(window.Hwnd, window.ScreenRect.Width, window.ScreenRect.Height);
                if (windowBmp is null)
                {
                    LiveDebugLog.Write(
                        $"[ClipStudioCapture] PrintWindow failed hwnd=0x{window.Hwnd.ToInt64():X} rect={window.ScreenRect}");
                    continue;
                }

                int destX = window.ScreenRect.Left - desktop.Left;
                int destY = window.ScreenRect.Top - desktop.Top;
                g.DrawImageUnscaled(windowBmp, destX, destY);
                LiveDebugLog.Write(
                    $"[ClipStudioCapture] painted hwnd=0x{window.Hwnd.ToInt64():X} rect={window.ScreenRect} -> ({destX},{destY})");
            }
        }

        return result;
    }

    private static bool TryResolveTargetThread(out uint processId, out uint threadId, out string processName)
    {
        processId = 0;
        threadId = 0;
        processName = "";

        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero &&
            TryGetClipStudioIdentity(foreground, out processId, out threadId, out processName))
        {
            return true;
        }

        WindowEntry? best = null;
        uint bestPid = 0;
        uint bestTid = 0;
        string bestName = "";
        int z = 0;
        var desktop = ScreenCapture.GetVirtualScreenBoundsPhysical();

        EnumWindows((hWnd, _) =>
        {
            if (!IsCandidateVisibleWindow(hWnd, desktop))
            {
                z++;
                return true;
            }

            if (!TryGetClipStudioIdentity(hWnd, out uint pid, out uint tid, out string name))
            {
                z++;
                return true;
            }

            if (!GetWindowRect(hWnd, out NativeRect nr) || nr.Width <= 0 || nr.Height <= 0)
            {
                z++;
                return true;
            }

            var rect = new IntRect(nr.Left, nr.Top, nr.Right, nr.Bottom);
            int area = rect.Width * rect.Height;
            int bestArea = best is null ? 0 : best.Value.ScreenRect.Width * best.Value.ScreenRect.Height;
            bool preferTitle = WindowTitleLooksLikePaint(hWnd);
            bool bestPreferTitle = best is not null && WindowTitleLooksLikePaint(best.Value.Hwnd);

            if (best is null ||
                (preferTitle && !bestPreferTitle) ||
                (preferTitle == bestPreferTitle && area > bestArea))
            {
                best = new WindowEntry(hWnd, rect, z);
                bestPid = pid;
                bestTid = tid;
                bestName = name;
            }

            z++;
            return true;
        }, IntPtr.Zero);

        if (best is null)
            return false;

        processId = bestPid;
        threadId = bestTid;
        processName = bestName;
        return true;
    }

    private static List<WindowEntry> EnumerateThreadWindows(uint processId, uint threadId, IntRect desktop)
    {
        var list = new List<WindowEntry>();
        int z = 0;

        EnumWindows((hWnd, _) =>
        {
            int order = z++;
            if (!IsCandidateVisibleWindow(hWnd, desktop))
                return true;

            uint tid = GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != processId || tid != threadId)
                return true;

            if (!GetWindowRect(hWnd, out NativeRect nr) || nr.Width < 2 || nr.Height < 2)
                return true;

            var rect = new IntRect(nr.Left, nr.Top, nr.Right, nr.Bottom);
            var clipped = rect.ClampTo(desktop);
            if (clipped.Width < 2 || clipped.Height < 2)
                return true;

            // Keep full window rect for PrintWindow size; drawing is clipped by bitmap bounds.
            list.Add(new WindowEntry(hWnd, rect, order));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static bool IsCandidateVisibleWindow(IntPtr hWnd, IntRect desktop)
    {
        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            return false;

        if (IsCloaked(hWnd))
            return false;

        if (!GetWindowRect(hWnd, out NativeRect nr) || nr.Width < 2 || nr.Height < 2)
            return false;

        var rect = new IntRect(nr.Left, nr.Top, nr.Right, nr.Bottom);
        var clipped = rect.ClampTo(desktop);
        return clipped.Width >= 2 && clipped.Height >= 2;
    }

    private static bool TryGetClipStudioIdentity(
        IntPtr hWnd,
        out uint processId,
        out uint threadId,
        out string processName)
    {
        processId = 0;
        processName = "";
        threadId = GetWindowThreadProcessId(hWnd, out processId);
        if (processId == 0 || threadId == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return IsClipStudioProcessName(processName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClipStudioProcessName(string processName)
    {
        foreach (string target in TargetProcessNames)
        {
            if (string.Equals(processName, target, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool WindowTitleLooksLikePaint(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        string title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title))
            return false;

        return title.Contains("CLIP STUDIO PAINT", StringComparison.OrdinalIgnoreCase)
               || title.Contains("CLIP STUDIO", StringComparison.OrdinalIgnoreCase)
               || title.Contains("クリップスタジオ", StringComparison.Ordinal);
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DwmaCloaked, out int cloaked, sizeof(int)) != 0)
            return false;
        return cloaked != 0;
    }

    private static Bitmap? TryPrintWindow(IntPtr hwnd, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return null;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr old = SelectObject(memDc, hBitmap);

        bool ok = PrintWindow(hwnd, memDc, PwRenderFullContent);
        if (!ok)
            ok = PrintWindow(hwnd, memDc, 0);

        SelectObject(memDc, old);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        if (!ok)
        {
            DeleteObject(hBitmap);
            return null;
        }

        try
        {
            using var temp = Image.FromHbitmap(hBitmap);
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.Black);
                g.DrawImageUnscaled(temp, 0, 0);
            }
            return result;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
