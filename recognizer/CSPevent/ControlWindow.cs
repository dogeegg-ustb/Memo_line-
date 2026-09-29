using System.Drawing;
using System.Windows.Forms;

namespace CSPevent;

internal sealed class ControlWindow : Form
{
    internal ControlWindow(Action exit)
    {
        Text = "CSPevent";
        Icon = SystemIcons.Information;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(380, 180);
        Font = new Font("Microsoft YaHei UI", 10);
        var title = new Label {
            Text = "正在监听鼠标点击",
            Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 20)
        };
        var description = new Label {
            Text = "点击后会在鼠标附近显示 OCR 与词库匹配结果。\n关闭此窗口后，程序会继续在系统托盘运行。",
            AutoSize = true,
            Location = new Point(23, 64)
        };
        var hide = new Button { Text = "隐藏到托盘", Size = new Size(118, 34), Location = new Point(112, 128) };
        hide.Click += (_, _) => Hide();
        var quit = new Button { Text = "退出程序", Size = new Size(110, 34), Location = new Point(241, 128) };
        quit.Click += (_, _) => exit();
        Controls.AddRange(new Control[] { title, description, hide, quit });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
