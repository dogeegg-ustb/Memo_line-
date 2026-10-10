using CanvasLayerWatcher;

namespace MemolineDemo;

internal sealed class DemoApplicationContext : ApplicationContext
{
    private readonly DemoEditingHost _editing;
    private readonly MainWindow _window;
    private readonly NotifyIcon _tray;
    private readonly GlobalHotkey _hotkey;
    private EditingPanel? _panel;
    private bool _closing;

    internal DemoApplicationContext(string[] args, DemoController controller)
    {
        _editing = new DemoEditingHost(controller);
        _window = new MainWindow(args, controller) { HideOnClose=true }; _editing.Attach(_window);
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Memoline", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示录制窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("打开编辑面板", null, (_, _) => ShowPanel());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _window.FormClosing += WindowClosing;
        _window.Show();
        _hotkey = new GlobalHotkey(_window);
        _hotkey.Pressed += (_, _) => ShowPanel();
    }

    private void ShowWindow() { _window.Show(); _window.WindowState = FormWindowState.Normal; _window.Activate(); }
    private async void ShowPanel()
    {
        _panel ??= new EditingPanel(_editing);
        _window.Hide();
        await _panel.OpenAsync();
    }
    private void WindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true; _window.Hide();
    }
    protected override async void ExitThreadCore()
    {
        if (_closing) return;
        _closing = true;
        _hotkey.Dispose(); _tray.Visible = false; _tray.Dispose();
        _panel?.Dispose();
        await _editing.WaitForIdleAsync();
        await _window.StopRecordingAsync();
        await _editing.DisposeAsync();
        _window.FormClosing -= WindowClosing;
        _window.HideOnClose=false;
        _window.Close();
        base.ExitThreadCore();
    }
}
