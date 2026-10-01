using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BehaviorRecognizer.Capture;

/// <summary>Dedicated message-pump thread for low-level Windows keyboard and mouse notifications.</summary>
public sealed class WindowsInputHooks : IDisposable
{
    // Shared with the recorder AHK worker and ordered replay.
    internal const nuint RecorderInputTag = 0x4D4C5243;
    private readonly UnifiedInputCapture _capture;
    private readonly bool _passivePen;
    private readonly LayerSaveGuard? _guard;
    private readonly HookProc _keyboardProc, _mouseProc;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _mouseButtonsDown;
    private bool _passivePenDown;
    private uint _threadId;
    private nint _keyboardHook, _mouseHook;

    public WindowsInputHooks(UnifiedInputCapture capture, bool passivePen, LayerSaveGuard? guard = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _capture = capture;
        _passivePen = passivePen;
        _guard = guard;
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "CSP input monitor" };
        _thread.Start();
        try { _started.Task.GetAwaiter().GetResult(); }
        catch { _thread.Join(); throw; }
    }

    private void Run()
    {
        try
        {
            // Create this thread's message queue before exposing its ID to Dispose.
            Native.PeekMessage(out _, 0, 0, 0, 0);
            _threadId = Native.GetCurrentThreadId();
            _keyboardHook = Native.SetWindowsHookEx(13, _keyboardProc, 0, 0);
            _mouseHook = Native.SetWindowsHookEx(14, _mouseProc, 0, 0);
            if (_keyboardHook == 0 || _mouseHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to monitor Windows input.");
            _started.SetResult();
            int result;
            while ((result = Native.GetMessage(out var message, 0, 0, 0)) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Input message loop failed.");
        }
        catch (Exception ex) { _started.TrySetException(ex); }
        finally
        {
            if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        }
    }

    private nint KeyboardCallback(int code, nint message, nint data)
    {
        bool chainDecided = false;
        nint chainResult = 0;
        try
        {
            if (code >= 0)
            {
                var input = Marshal.PtrToStructure<KbdData>(data);
                if (input.extraInfo != RecorderInputTag) // Accept keyboard input translated by device drivers.
                {
                    int msg = (int)message;
                    if (msg is 0x0100 or 0x0104 or 0x0101 or 0x0105)
                    {
                        bool intercepted = _guard?.Keyboard((int)input.vkCode, input.scanCode,
                            (input.flags & 1) != 0, msg is 0x0100 or 0x0104) == true;
                        // Forward ordinary input before enqueueing its observation. Keep the
                        // downstream result so diagnostics can distinguish another hook's block.
                        chainResult = intercepted ? 1 : Native.CallNextHookEx(0, code, message, data);
                        chainDecided = true;
                        _capture.PostKey((int)input.vkCode, input.scanCode, (input.flags & 1) != 0,
                            msg is 0x0100 or 0x0104, injected: (input.flags & 0x10) != 0,
                            extraInfo: input.extraInfo, guardIntercepted: intercepted, nextHookResult: chainResult);
                        return chainResult;
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Keyboard] {ex.Message}"); }
        return chainDecided ? chainResult : Native.CallNextHookEx(0, code, message, data);
    }

    private nint MouseCallback(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0)
            {
                var input = Marshal.PtrToStructure<MouseData>(data);
                // Windows marks synthetic mouse messages from pen/touch with MI_WP_SIGNATURE.
                bool penOrTouch = (input.extraInfo & (nuint)0xFFFFFF00) == (nuint)0xFF515700;
                bool touch = (input.extraInfo & (nuint)0x80) != 0;
                if (penOrTouch && !touch && _passivePen)
                {
                    int msg = (int)message;
                    if (msg == 0x0201) _passivePenDown = true;
                    if (msg != 0x0200 || _passivePenDown)
                        _capture.PostMouse(msg, input.point.X, input.point.Y, (int)input.mouseData, passivePen: true);
                    if (msg == 0x0202) _passivePenDown = false;
                    if (_guard?.Mouse(msg, input.point.X, input.point.Y, input.mouseData) == true) return 1;
                }
                else if (penOrTouch && !touch)
                {
                    if (_guard?.Mouse((int)message, input.point.X, input.point.Y, input.mouseData) == true) return 1;
                }
                else if (!penOrTouch && ((input.flags & 1) == 0 ||
                    ((int)message is 0x020A or 0x020E && input.extraInfo != RecorderInputTag)))
                {
                    int msg = (int)message;
                    int button = msg switch
                    {
                        0x0201 or 0x0202 => 1, 0x0204 or 0x0205 => 2,
                        0x0207 or 0x0208 => 4,
                        0x020B or 0x020C => ((input.mouseData >> 16) & 0xffff) == 1 ? 8 : 16,
                        _ => 0
                    };
                    if (msg is 0x0201 or 0x0204 or 0x0207 or 0x020B) _mouseButtonsDown |= button;
                    if (msg != 0x0200 || _mouseButtonsDown != 0)
                        _capture.PostMouse(msg, input.point.X, input.point.Y, (int)input.mouseData);
                    if (msg is 0x0202 or 0x0205 or 0x0208 or 0x020C) _mouseButtonsDown &= ~button;
                    if (_guard?.Mouse(msg, input.point.X, input.point.Y, input.mouseData) == true) return 1;
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Mouse] {ex.Message}"); }
        return Native.CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (_thread.IsAlive && _threadId != 0)
            Native.PostThreadMessage(_threadId, 0x0012, 0, 0); // WM_QUIT
        _thread.Join(TimeSpan.FromSeconds(2));
        GC.KeepAlive(_keyboardProc);
        GC.KeepAlive(_mouseProc);
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KbdData
    { public uint vkCode, scanCode, flags, time; public nuint extraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { public CspWindowProbe.ScreenPoint point; public uint mouseData, flags, time; public nuint extraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg
    { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public CspWindowProbe.ScreenPoint point; public uint privateData; }
    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);
        [DllImport("user32.dll", SetLastError = true)] public static extern int GetMessage(out Msg message, nint window, uint min, uint max);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg message);
        [DllImport("user32.dll")] public static extern nint DispatchMessage(ref Msg message);
    }
}
