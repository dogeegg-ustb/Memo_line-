using System.Diagnostics;
using System.Runtime.InteropServices;
using LayerStealer.Windows;

namespace LayerStealer.Capture;

internal enum ScriptKey { Control, RightButton, C }
internal readonly record struct ScriptStep(ScriptKey Key, bool Down, int DelayAfterMs);

internal static class CopyScript
{
    // User-confirmed sequence: keep Ctrl held during the right-click and C keystroke.
    internal static ScriptStep[] Steps(int delayMs) =>
    [
        new(ScriptKey.Control, true, 40),
        new(ScriptKey.RightButton, true, 40),
        new(ScriptKey.RightButton, false, delayMs),
        new(ScriptKey.C, true, 40),
        new(ScriptKey.C, false, 0),
        new(ScriptKey.Control, false, 0)
    ];

    public static async Task<uint> RunAsync(CspTarget target, Point point, int delayMs, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        // Global-hotkey modifiers must be physically released before injecting our own chord.
        while (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 1, 2, 4 }.Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0))
        {
            if (watch.ElapsedMilliseconds > 3000) throw new InvalidOperationException("请松开快捷键和鼠标按钮后重试。");
            await Task.Delay(30, token);
        }
        await target.ActivateAsync(token);
        target.Verify(point);
        Native.GetCursorPos(out var oldCursor);
        if (!Native.SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("无法移动到绘图区中心。");
        uint sequence = Native.GetClipboardSequenceNumber();
        CopyTrace.Write($"script start target=0x{target.Window:X} pid={target.ProcessId} point=({point.X},{point.Y}) sequence={sequence}");
        try
        {
            await ExecuteAsync(Steps(delayMs), step =>
            {
                CopyTrace.Write($"send {step.Key} {(step.Down ? "down" : "up")} {CspTarget.ForegroundDescription()}");
                Send(step.Key, step.Down);
            }, token, beforeStep: _ => target.WaitForForegroundAsync(token));
            CopyTrace.Write("script complete; all injected keys released");
        }
        finally
        {
            // Do not override a physical mouse move made while the script was running.
            if (Native.GetCursorPos(out var cursor) && cursor == point) Native.SetCursorPos(oldCursor.X, oldCursor.Y);
        }
        return sequence;
    }

    internal static async Task ExecuteAsync(ScriptStep[] steps, Action<ScriptStep> send, CancellationToken token,
        Func<int, CancellationToken, Task>? delay = null, Action<ScriptKey>? release = null,
        Func<ScriptStep, Task>? beforeStep = null)
    {
        var held = new HashSet<ScriptKey>();
        delay ??= Task.Delay;
        release ??= key => Send(key, false);
        try
        {
            foreach (var step in steps)
            {
                token.ThrowIfCancellationRequested();
                if (beforeStep is not null) await beforeStep(step);
                token.ThrowIfCancellationRequested();
                // Remember a down before sending so even partial injection is released on failure.
                if (step.Down) held.Add(step.Key);
                send(step);
                if (!step.Down) held.Remove(step.Key);
                if (step.DelayAfterMs > 0) await delay(step.DelayAfterMs, token);
            }
        }
        finally
        {
            foreach (var key in new[] { ScriptKey.C, ScriptKey.RightButton, ScriptKey.Control })
                if (held.Contains(key))
                    try { release(key); } catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static void Send(ScriptKey key, bool down)
    {
        var input = key == ScriptKey.RightButton
            ? new Native.Input { Type = 0, Union = new() { Mouse = new() { Flags = down ? 0x0008u : 0x0010u } } }
            : new Native.Input { Type = 1, Union = new() { Keyboard = new() { Vk = key == ScriptKey.Control ? (ushort)0x11 : (ushort)0x43, Flags = down ? 0u : 2u } } };
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法发送复制操作。CSP 和本程序需以相同权限运行。");
    }
}
