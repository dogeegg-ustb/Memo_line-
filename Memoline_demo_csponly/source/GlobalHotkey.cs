using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MemolineDemo;

internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001, ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;
    private readonly int _id;
    private bool _registered;
    internal event EventHandler? Pressed;

    internal GlobalHotkey(Control owner, int id = 0x4D01)
    {
        _id = id; AssignHandle(owner.Handle);
        _registered = RegisterHotKey(Handle, _id, ModAlt | ModNoRepeat, VkM);
        if (!_registered) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册全局 Alt+M 热键。");
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == _id) Pressed?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        if (_registered) { UnregisterHotKey(Handle, _id); _registered = false; }
        ReleaseHandle(); GC.SuppressFinalize(this);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hWnd, int id);
}
