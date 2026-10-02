using System.Diagnostics;
using System.Text;
using LayerStealer.Capture;

namespace LayerStealer.Windows;

internal sealed record CspTarget(nint Window, uint ProcessId, string Title)
{
    public static bool IsCsp(nint window)
    {
        if (window == 0 || !Native.IsWindow(window)) return false;
        Native.GetWindowThreadProcessId(window, out uint id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName.Equals("CLIPStudioPaint", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static CspTarget Find(nint preferred)
    {
        nint foreground = Native.GetForegroundWindow();
        if (IsCsp(foreground)) return FromWindow(foreground);
        if (IsCsp(preferred) && Native.IsWindowVisible(preferred)) return FromWindow(preferred);
        CspTarget? best = null;
        long bestArea = -1;
        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window) || !IsCsp(window)) return true;
            var candidate = FromWindow(window);
            // Prefer the document/main window over a floating palette, as the historical capture did.
            var title = candidate.Title;
            long area = 0;
            if (Native.GetClientRect(window, out var rect)) area = (long)rect.Bounds.Width * rect.Bounds.Height;
            if (title.Contains("CLIP STUDIO PAINT", StringComparison.OrdinalIgnoreCase)) area += 1L << 40;
            if (area > bestArea) { best = candidate; bestArea = area; }
            return true;
        }, 0);
        return best ?? throw new InvalidOperationException("未找到 CSP 窗口。请先打开 CLIP STUDIO PAINT 和画布。");
    }

    private static CspTarget FromWindow(nint window)
    {
        Native.GetWindowThreadProcessId(window, out uint id);
        var text = new StringBuilder(512);
        Native.GetWindowText(window, text, text.Capacity);
        return new(window, id, text.ToString());
    }

    public Rectangle ClientBounds()
    {
        if (!IsCsp(Window) || !Native.GetClientRect(Window, out var rect))
            throw new InvalidOperationException("CSP 窗口已关闭，请重新选择。");
        var origin = Point.Empty;
        if (!Native.ClientToScreen(Window, ref origin) || rect.Bounds.Width <= 0 || rect.Bounds.Height <= 0)
            throw new InvalidOperationException("无法读取 CSP 绘图窗口的位置。");
        return new(origin, rect.Bounds.Size);
    }

    public async Task ActivateAsync(CancellationToken token)
    {
        if (Native.IsIconic(Window)) Native.ShowWindowAsync(Window, 9);
        Native.SetForegroundWindow(Window);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            // CSP can redirect activation from its owner frame to an owned drawing window.
            if (!Native.IsIconic(Window) && IsForegroundInScope()) return;
            await Task.Delay(50, token);
        }
        throw new InvalidOperationException("无法激活 CSP。请切回 CSP 后按 Ctrl + Alt + F8。");
    }

    public void VerifyForeground()
    {
        if (!IsForegroundInScope())
            throw new InvalidOperationException("前台已离开目标 CSP，复制操作已停止。");
    }

    internal static bool IsInScope(nint targetWindow, uint targetProcess, bool targetExists,
        nint foregroundWindow, uint foregroundProcess)
        => targetWindow != 0 && targetExists && foregroundWindow != 0
            && targetProcess != 0 && foregroundProcess == targetProcess;

    private bool IsForegroundInScope()
    {
        nint foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(Window, out uint targetProcess);
        Native.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        // Compare process identity, not HWND. Ctrl+right-click creates a CSP popup window.
        // Keep the original PID bound so handle reuse or another CSP instance cannot pass.
        return targetProcess == ProcessId && IsInScope(Window, ProcessId, Native.IsWindow(Window), foreground, foregroundProcess);
    }

    public async Task WaitForForegroundAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (IsForegroundInScope()) return;
            // Allow a short activation transition without moving focus away from the popup.
            await Task.Delay(25, token);
        }
        CopyTrace.Write("foreground rejected: " + ForegroundDescription());
        VerifyForeground();
    }

    public static string ForegroundDescription()
    {
        nint window = Native.GetForegroundWindow();
        uint thread = Native.GetWindowThreadProcessId(window, out uint process);
        var className = new StringBuilder(128);
        Native.GetClassName(window, className, className.Capacity);
        return $"hwnd=0x{window:X} pid={process} tid={thread} class={className}";
    }

    public void Verify(Point point)
    {
        VerifyForeground();
        Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out uint pointProcess);
        if (!ClientBounds().Contains(point) || pointProcess != ProcessId)
            throw new InvalidOperationException("复制位置不在可见 CSP 窗口内，请校准绘图区中心。");
    }
}
