using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace StrokeReplay;

internal sealed class WindowsViewInput : IViewInput
{
    private readonly uint _processId;
    private readonly nint _window;
    private const ushort Space = 0x20, Control = 0x11, A = 0x41, Enter = 0x0D;

    internal WindowsViewInput()
    {
        using var process = Process.GetProcessesByName("CLIPStudioPaint")
            .FirstOrDefault(p => p.MainWindowHandle != 0)
            ?? throw new InvalidOperationException("请先打开 CSP 画布。");
        _processId = (uint)process.Id;
        _window = process.MainWindowHandle;
        if (Native.IsIconic(_window)) Native.ShowWindow(_window, 9);
        Native.SetForegroundWindow(_window);
        EnsureTarget();
    }

    internal static void PrepareDpiAwareness() => Native.SetProcessDpiAwarenessContext(new nint(-4));

    public void EnsureTarget()
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out uint pid);
        if (!Native.IsWindow(_window) || pid != _processId)
            throw new InvalidOperationException("CSP 已关闭或失去前台焦点，回放已停止。");
    }

    internal void EnsureCanvasPoint(double x, double y, Rectangle viewport)
    {
        EnsureTarget();
        var point = new Point(checked((int)Math.Round(x)), checked((int)Math.Round(y)));
        if (!viewport.Contains(point)) throw new InvalidOperationException($"笔点 ({point.X},{point.Y}) 不在当前画布视口内。");
        EnsurePoint(point);
    }

    private void EnsurePoint(Point point)
    {
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(point))
            throw new InvalidOperationException("Recognizer 提供的输入位置超出当前桌面。");
        var window = Native.WindowFromPoint(new NativePoint(point.X, point.Y));
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid != _processId) throw new InvalidOperationException("目标输入区域被其他窗口遮挡，回放已停止。");
    }

    internal async Task ClickAsync(Rectangle input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var point = PanelPoint(input);
        Move(point);
        try { Mouse(0x0002); }
        finally { Mouse(0x0004); }
        await Task.Delay(80, token);
        EnsureTarget();
    }

    internal async Task ScrollAsync(Rectangle panel, int delta, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var point = PanelPoint(panel);
        Move(point);
        Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0800, Data = unchecked((uint)delta) } } });
        await Task.Delay(100, token);
        EnsureTarget();
    }

    private Point PanelPoint(Rectangle input)
    {
        EnsureTarget();
        if (input.Width <= 0 || input.Height <= 0) throw new InvalidOperationException("面板输入区域无效。");
        var point = new Point(input.Left + input.Width / 2, input.Top + input.Height / 2);
        EnsurePoint(point);
        return point;
    }

    public async Task SetNumberAsync(Rectangle input, double value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureTarget();
        if (input.Width <= 0 || input.Height <= 0) throw new InvalidOperationException("数值输入区域无效。");
        var point = new Point(input.Left + input.Width / 2, input.Top + input.Height / 2);
        EnsurePoint(point);
        Move(point);
        Mouse(0x0002);
        Mouse(0x0004);
        await Task.Delay(80, token);
        EnsureTarget();
        Key(Control, true);
        try { Key(A, true); Key(A, false); }
        finally { Key(Control, false); }
        foreach (char character in value.ToString("0.################", CultureInfo.InvariantCulture))
        {
            token.ThrowIfCancellationRequested();
            EnsureTarget();
            Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } } });
            Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 | 0x0002 } } });
        }
        Key(Enter, true);
        Key(Enter, false);
        // Navigator mouse release requests a fresh view parse even when Enter
        // has no matching shortcut in the Recognizer catalog.
        EnsureTarget();
        EnsurePoint(point);
        Move(point);
        Mouse(0x0002);
        Mouse(0x0004);
        Key(0x1B, true);
        Key(0x1B, false);
    }

    public async Task PanAsync(Point from, Point to, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureTarget();
        EnsurePoint(from);
        EnsurePoint(to);
        Move(from);
        bool spaceDown = false, mouseDown = false;
        try
        {
            Key(Space, true);
            spaceDown = true;
            await Task.Delay(60, token);
            EnsureTarget();
            Mouse(0x0002);
            mouseDown = true;
            for (int step = 1; step <= 12; step++)
            {
                await Task.Delay(16, token);
                EnsureTarget();
                var point = new Point(from.X + (to.X - from.X) * step / 12, from.Y + (to.Y - from.Y) * step / 12);
                EnsurePoint(point);
                Move(point);
            }
        }
        finally
        {
            try { if (mouseDown) Mouse(0x0004); }
            finally { if (spaceDown) Key(Space, false); }
        }
    }

    internal async Task SendShortcutAsync(IReadOnlyList<ushort> keys, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureTarget();
        var held = new List<ushort>();
        try
        {
            foreach (var key in keys)
            {
                token.ThrowIfCancellationRequested();
                EnsureTarget();
                Key(key, true);
                held.Add(key);
            }
            await Task.Delay(40, token);
        }
        finally
        {
            // Attempt every release even if one SendInput call fails.
            Exception? failure = null;
            foreach (var key in held.AsEnumerable().Reverse())
                try { Key(key, false); } catch (Exception ex) { failure ??= ex; }
            if (failure is not null) throw failure;
        }
    }

    private static void Move(Point point)
    {
        if (!Native.SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void Key(ushort key, bool down) => Send(new Input { Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = down ? 0u : 2u } } });
    private static void Mouse(uint flags) => Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } });
    private static void Send(Input input)
    {
        if (Native.SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法发送视图恢复输入。");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    }
}
