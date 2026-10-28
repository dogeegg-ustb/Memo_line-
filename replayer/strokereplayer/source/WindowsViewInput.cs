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
    internal const nuint RecorderInputTag = 0x4D4C5243;
    private readonly HashSet<ushort> _heldKeys = [];
    private bool _leftDown;

    internal WindowsViewInput()
    {
        var processes = Process.GetProcessesByName("CLIPStudioPaint");
        try
        {
            foreach (var process in processes)
                if ((_window = MainWindow((uint)process.Id)) != 0) { _processId = (uint)process.Id; break; }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        if (_window == 0) throw new InvalidOperationException("请先打开 CSP 画布。");
        EnsureTarget();
    }

    /// <summary>CSP's frame window. Process.MainWindowHandle is the first visible unowned window in Z order, and on .NET 10
    /// that includes a layered tool window above the frame (tested 2026-10-26), which is what the drop shadow of a CSP
    /// floating panel or menu is (class SysShadow, unowned). The shadow goes with its popup; every check after that read
    /// as "CSP closed" at once: an event edit failed 164 ms into its first stroke (2026-10-26 14:03:44).</summary>
    private static nint MainWindow(uint processId)
    {
        const int ExStyle = -20, ToolWindow = 0x80;
        const uint Owner = 4;
        nint found = 0;
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint pid);
            if (pid != processId || Native.GetWindow(window, Owner) != 0 || !Native.IsWindowVisible(window) ||
                (Native.GetWindowLongPtr(window, ExStyle) & ToolWindow) != 0) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    internal static void PrepareDpiAwareness() => Native.SetProcessDpiAwarenessContext(new nint(-4));

    /// <summary>Brings CSP to the front before every injected input: the user has usually just clicked Memoline, and a
    /// transient window (status panel, screenshot tool) can take the foreground between inputs; physical input aborts
    /// elsewhere. The switch is asynchronous (CSP activates when its UI thread next pumps messages), so poll for it.</summary>
    public void EnsureTarget()
    {
        if (IsTargetForeground()) return;
        if (Native.IsWindow(_window))
        {
            Activate();
            for (int i = 0; i < 25 && !IsTargetForeground(); i++) Thread.Sleep(30);
        }
        if (!IsTargetForeground())
            throw new InvalidOperationException("无法把 CSP 切到前台（CSP 已关闭或 Windows 拒绝切换），回放已停止。");
    }

    private void Activate()
    {
        if (Native.IsIconic(_window)) Native.ShowWindow(_window, 9);
        if (Native.SetForegroundWindow(_window)) return;
        // Windows lets only the process that sent the last input take the foreground. An empty mouse input (no move,
        // no button) makes it this one; it reaches no window, so it is not counted in SentInputs.
        Native.SendInput(1, [WithRecorderTag(new Input { Type = 0 })], Marshal.SizeOf<Input>());
        Native.SetForegroundWindow(_window);
    }

    private bool IsTargetForeground()
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out uint pid);
        return Native.IsWindow(_window) && pid == _processId;
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
        // What the point is over, as if the input shield were not there: injected input reaches it through a hole.
        var window = InputShield.WindowFromPoint(point);
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid != _processId) throw new InvalidOperationException("目标输入区域被其他窗口遮挡，回放已停止。");
    }

    internal async Task ClickAsync(Rectangle input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var point = PanelPoint(input);
        using var hole = InputShield.Active?.OpenHole(point);
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
        using var hole = InputShield.Active?.OpenHole(point);
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
        using var hole = InputShield.Active?.OpenHole(point);
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
        // The hole follows the cursor: the drag reaches CSP wherever it is, captured or not.
        using var hole = InputShield.Active?.OpenHole(from);
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
                hole?.MoveTo(point);
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
        await Task.Run(() => WaitForInputHandled(held.Count * 2), token);
    }

    /// <summary>
    /// Presses a shortcut with the cursor parked on <paramref name="target"/>, anywhere on the desktop, and then puts the
    /// cursor back. CSP's screen colour picker (applicationscreeneyecolorpicker) takes the pixel under the cursor when its
    /// shortcut is pressed. Memoline's swatch is on the virtual screen, which touches the main screen at one corner only:
    /// moving the mouse never gets there, absolute input does. Keys still go to CSP, the foreground window.
    /// </summary>
    internal async Task SendShortcutAtAsync(Rectangle target, IReadOnlyList<ushort> keys, TimeSpan dwell, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureTarget();
        if (target.Width <= 0 || target.Height <= 0) throw new InvalidOperationException("取色位置无效。");
        if (!Native.GetCursorPos(out var before)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取光标位置。");
        try
        {
            Move(new Point(target.Left + target.Width / 2, target.Top + target.Height / 2));
            var at = before;
            for (int i = 0; i < 10 && (!Native.GetCursorPos(out at) || !target.Contains(at.X, at.Y)); i++)
                await Task.Delay(10, token);
            if (!target.Contains(at.X, at.Y))
                throw new InvalidOperationException($"光标没能移到色块 {target}（停在 {at.X},{at.Y}），没有按下取色快捷键。");
            await SendShortcutAsync(keys, token);
            await Task.Delay(dwell, token);
        }
        finally { Move(new Point(before.X, before.Y)); }
    }

    /// <summary>
    /// Returns once CSP's UI thread has handled the input sent so far. A sent message is handled only when that thread
    /// asks for its next message, between the queued key messages, so a WM_NULL per key transition comes back after
    /// them. Right after a save CSP keeps its UI thread for up to a second: a layer panel read 140 ms after two
    /// select-layer presses still showed the first (2026-10-24) and the next press overshot the target.
    /// </summary>
    internal void WaitForInputHandled(int rounds)
    {
        const uint WmNull = 0, SmtoAbortIfHung = 2;
        for (int i = 0; i < rounds && Native.IsWindow(_window); i++)
            Native.SendMessageTimeout(_window, WmNull, 0, 0, SmtoAbortIfHung, 3000, out _);
    }

    private void Move(Point point)
    {
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!screen.Contains(point) || screen.Width <= 1 || screen.Height <= 1)
            throw new InvalidOperationException("目标输入点超出虚拟桌面。");
        Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput {
            X = (int)Math.Round((point.X - screen.Left) * 65535d / (screen.Width - 1)),
            Y = (int)Math.Round((point.Y - screen.Top) * 65535d / (screen.Height - 1)), Flags = 0x0001 | 0x8000 | 0x4000 } } });
    }
    private void Key(ushort key, bool down)
    {
        Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = down ? 0u : 2u } } });
        if (down) _heldKeys.Add(key); else _heldKeys.Remove(key);
    }
    private void Mouse(uint flags)
    {
        Send(new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } });
        if ((flags & 0x0002) != 0) _leftDown = true;
        if ((flags & 0x0004) != 0) _leftDown = false;
    }
    internal void ReleaseInputs()
    {
        Exception? failure = null;
        if (_leftDown) try { Mouse(0x0004); } catch (Exception ex) { failure = ex; }
        foreach (var key in _heldKeys.Reverse().ToArray())
            try { Key(key, false); } catch (Exception ex) { failure ??= ex; }
        if (failure is not null) throw failure;
    }
    internal static Input WithRecorderTag(Input input)
    {
        if (input.Type == 0) input.Data.Mouse.Extra = RecorderInputTag;
        else if (input.Type == 1) input.Data.Keyboard.Extra = RecorderInputTag;
        else throw new ArgumentException("仅支持鼠标和键盘输入。");
        return input;
    }
    private static long s_sentInputs;
    /// <summary>Every keyboard/mouse input this process sent to CSP. While it is unchanged (and no pen
    /// was injected) the document can only change through physical input, which aborts an edit.</summary>
    internal static long SentInputs => Interlocked.Read(ref s_sentInputs);
    private static void Send(Input input)
    {
        // Counted before sending: a failed call may still have reached CSP in part.
        Interlocked.Increment(ref s_sentInputs);
        if (Native.SendInput(1, [WithRecorderTag(input)], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法发送视图恢复输入。");
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
        internal delegate bool EnumWindowsProc(nint window, nint parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);
    }
}
