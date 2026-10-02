namespace DirtyMatrix.UI;

public sealed class ScreenPicker : Form
{
    private readonly bool _rectangle;
    private readonly string _instruction;
    private Point? _start;
    private Point _current;
    public Rectangle Selection { get; private set; }
    public Point SelectedPoint { get; private set; }
    public ScreenPicker(bool rectangle, string instruction)
    {
        _rectangle = rectangle; _instruction = instruction;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen; TopMost = true; ShowInTaskbar = false;
        BackColor = Color.FromArgb(20, 25, 22); Opacity = 0.4;
        Cursor = Cursors.Cross; KeyPreview = true; DoubleBuffered = true;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _start = e.Location; _current = e.Location; Capture = true;
        if (!_rectangle)
        { SelectedPoint = PointToScreen(e.Location); DialogResult = DialogResult.OK; Close(); }
    }
    protected override void OnMouseMove(MouseEventArgs e) { _current = e.Location; if (_start is not null) Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_rectangle || e.Button != MouseButtons.Left || _start is not Point start) return;
        _current = e.Location;
        var rect = Rectangle.FromLTRB(Math.Min(start.X, _current.X), Math.Min(start.Y, _current.Y),
            Math.Max(start.X, _current.X), Math.Max(start.Y, _current.Y));
        Capture = false;
        if (rect.Width < 4 || rect.Height < 4) { _start = null; return; }
        Selection = new(PointToScreen(rect.Location), rect.Size); DialogResult = DialogResult.OK; Close();
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_start is Point start)
        {
            var rect = Rectangle.FromLTRB(Math.Min(start.X, _current.X), Math.Min(start.Y, _current.Y),
                Math.Max(start.X, _current.X), Math.Max(start.Y, _current.Y));
            using var fill = new SolidBrush(Color.Lime); e.Graphics.FillRectangle(fill, rect);
        }
        var monitor = Screen.FromPoint(Cursor.Position).Bounds;
        using var font = new Font("Microsoft YaHei UI", 15);
        e.Graphics.DrawString(_instruction + "  ·  Esc 取消", font, Brushes.White,
            monitor.Left - Left + 30, monitor.Top - Top + 30);
    }
}
