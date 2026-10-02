namespace LayerStealer.UI;

// Adapted from Organizer/dirty matrix/source/UI/ScreenPicker.cs; uses physical screen pixels.
internal sealed class CenterPicker : Form
{
    public Point SelectedPoint { get; private set; }
    public CenterPicker()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true; ShowInTaskbar = false; KeyPreview = true;
        BackColor = Color.FromArgb(18, 24, 30); Opacity = .45;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        SelectedPoint = PointToScreen(e.Location);
        DialogResult = DialogResult.OK;
        Close();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        using var font = new Font("Microsoft YaHei UI", 17);
        e.Graphics.DrawString("点击 CSP 绘图区中心，设为右键复制位置 · Esc 取消", font, Brushes.White,
            screen.Left - Left + 28, screen.Top - Top + 28);
    }
}
