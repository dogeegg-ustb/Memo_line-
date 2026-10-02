using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DirtyMatrix.Core;

namespace DirtyMatrix.Windows;

public enum PointerAction { Move, Down, Up, Reset }
public readonly record struct PointerSample(PointerAction Action, PointD Screen, nint Foreground, bool ModifierHeld);

/// <summary>Independent message pump; hook work stays short and never waits for the UI.</summary>
public sealed class ScreenInputMonitor : IDisposable
{
    private readonly ConcurrentQueue<PointerSample> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly Native.HookProc _callback;
    private nint _hook;
    private uint _threadId;
    private Exception? _startupError;
    private volatile bool _disposed;
    public ScreenInputMonitor()
    {
        _callback = OnHook;
        _thread = new Thread(Run) { IsBackground = true, Name = "dirty matrix input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null) throw _startupError;
    }
    public bool TryRead(out PointerSample sample) => _queue.TryDequeue(out sample);
    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        _hook = Native.SetWindowsHookExW(14, _callback, Native.GetModuleHandleW(null), 0);
        if (_hook == 0) _startupError = new Win32Exception(Marshal.GetLastWin32Error(), "无法启动屏幕输入监视。");
        _ready.Set();
        if (_hook == 0) return;
        try
        {
            while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
            { Native.TranslateMessage(ref message); Native.DispatchMessageW(ref message); }
        }
        finally { Native.UnhookWindowsHookEx(_hook); }
    }
    private nint OnHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_disposed && (int)wParam is 0x200 or 0x201 or 0x202)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.MouseData>(lParam);
                var action = (int)wParam switch { 0x201 => PointerAction.Down, 0x202 => PointerAction.Up, _ => PointerAction.Move };
                bool modifier = (Native.GetAsyncKeyState(0x20) & 0x8000) != 0 ||
                    (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
                if (_queue.Count > 65536)
                { _queue.Clear(); _queue.Enqueue(new(PointerAction.Reset, default, 0, false)); }
                _queue.Enqueue(new(action, new(data.Point.X, data.Point.Y), Native.GetForegroundWindow(), modifier));
            }
            catch { /* A hook must always return control to Windows, including during shutdown. */ }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.PostThreadMessageW(_threadId, 0x12, 0, 0);
        _thread.Join(2000);
        if (!_thread.IsAlive) _ready.Dispose();
    }
}
