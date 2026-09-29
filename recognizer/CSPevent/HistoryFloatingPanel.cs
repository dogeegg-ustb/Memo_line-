using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CSPevent;

internal sealed record ActionHistoryItem(
    DateTime Time,
    string Category,      // "icon", "text", "click"
    string Title,         // e.g. "撤销 [Edit.Undo]"
    string Detail,        // e.g. "2D高光确认 · 置信度: 0.68 · 耗时 1ms"
    Color BadgeBg,
    Color BadgeFg,
    Bitmap? CropImage = null,
    string? CropPath = null) : IDisposable
{
    public void Dispose()
    {
        CropImage?.Dispose();
    }
}

internal sealed class HistoryFloatingPanel : Form
{
    private readonly List<ActionHistoryItem> _items = new();
    private readonly object _lock = new();
    private readonly Panel _header;
    private readonly Label _titleLabel;
    private readonly Label _countLabel;
    private readonly Button _latestCropBtn;
    private readonly Button _cropsFolderBtn;
    private readonly Button _pinBtn;
    private readonly Button _clearBtn;
    private readonly Button _collapseBtn;
    private readonly Button _closeBtn;
    private readonly HistoryListControl _listControl;
    private readonly Panel _footer;
    private readonly Label _statusLabel;
    private readonly Panel _resizeGrip;

    private bool _isDragging;
    private Point _dragStart;
    private bool _isResizing;
    private Point _resizeStart;
    private Size _resizeStartSize;
    private bool _collapsed;
    private int _expandedHeight = 440;

    internal HistoryFloatingPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Width = 360;
        Height = _expandedHeight;
        MinimumSize = new Size(260, 140);
        BackColor = Color.FromArgb(24, 24, 32);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

        // Default screen position: top-right corner of primary working area
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(Math.Max(workArea.Left + 20, workArea.Right - Width - 32),
                             Math.Max(workArea.Top + 20, workArea.Top + 60));

        // 1. Header
        _header = new Panel {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = Color.FromArgb(32, 33, 44),
            Cursor = Cursors.SizeAll
        };
        _header.MouseDown += Header_MouseDown;
        _header.MouseMove += Header_MouseMove;
        _header.MouseUp += Header_MouseUp;

        _titleLabel = new Label {
            Text = "⚡ CSP 历史",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(240, 240, 255),
            AutoSize = true,
            Location = new Point(8, 12),
            Cursor = Cursors.SizeAll
        };
        _titleLabel.MouseDown += Header_MouseDown;
        _titleLabel.MouseMove += Header_MouseMove;
        _titleLabel.MouseUp += Header_MouseUp;

        _countLabel = new Label {
            Text = "(0)",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(140, 145, 170),
            AutoSize = true,
            Location = new Point(90, 14),
            Cursor = Cursors.SizeAll
        };
        _countLabel.MouseDown += Header_MouseDown;
        _countLabel.MouseMove += Header_MouseMove;
        _countLabel.MouseUp += Header_MouseUp;

        _latestCropBtn = CreateHeaderButton("🖼️", "查看最新匹配截屏切片 (latest_match.png)", 188);
        _latestCropBtn.Click += (_, _) => OpenLatestCrop();

        _cropsFolderBtn = CreateHeaderButton("📁", "打开截屏切片文件夹 (crops)", 216);
        _cropsFolderBtn.Click += (_, _) => OpenCropsFolder();

        _pinBtn = CreateHeaderButton("📌", "取消置顶 / 置顶", 244);
        _pinBtn.Click += (_, _) => ToggleTopMost();

        _clearBtn = CreateHeaderButton("🧹", "清空历史", 272);
        _clearBtn.Click += (_, _) => ClearHistory();

        _collapseBtn = CreateHeaderButton("─", "折叠 / 展开", 300);
        _collapseBtn.Click += (_, _) => ToggleCollapse();

        _closeBtn = CreateHeaderButton("✕", "隐藏到后台托盘", 328);
        _closeBtn.Click += (_, _) => Hide();

        _header.Controls.AddRange(new Control[] {
            _titleLabel, _countLabel, _latestCropBtn, _cropsFolderBtn, _pinBtn, _clearBtn, _collapseBtn, _closeBtn
        });

        // 2. Footer
        _footer = new Panel {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Color.FromArgb(28, 29, 38)
        };
        _statusLabel = new Label {
            Text = "● 监听中 (CLIPStudioPaint.exe)",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(64, 210, 130),
            AutoSize = true,
            Location = new Point(8, 5)
        };
        _resizeGrip = new Panel {
            Size = new Size(16, 16),
            Dock = DockStyle.Right,
            Cursor = Cursors.SizeNWSE,
            BackColor = Color.Transparent
        };
        _resizeGrip.Paint += ResizeGrip_Paint;
        _resizeGrip.MouseDown += ResizeGrip_MouseDown;
        _resizeGrip.MouseMove += ResizeGrip_MouseMove;
        _resizeGrip.MouseUp += ResizeGrip_MouseUp;

        _footer.Controls.Add(_statusLabel);
        _footer.Controls.Add(_resizeGrip);

        // 3. Scrollable List Control
        _listControl = new HistoryListControl {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(20, 20, 26)
        };

        Controls.Add(_listControl);
        Controls.Add(_footer);
        Controls.Add(_header);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // WS_EX_NOACTIVATE ensures clicks on this window never steal keyboard/focus from CSP
            cp.ExStyle |= Native.WsExNoActivate;
            return cp;
        }
    }

    private Button CreateHeaderButton(string text, string tooltip, int x)
    {
        var btn = new Button {
            Text = text,
            Size = new Size(24, 24),
            Location = new Point(x, 9),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(200, 205, 225),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            TabStop = false
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 65, 85);
        new ToolTip().SetToolTip(btn, tooltip);
        return btn;
    }

    private void ToggleTopMost()
    {
        TopMost = !TopMost;
        _pinBtn.ForeColor = TopMost ? Color.FromArgb(0, 200, 255) : Color.FromArgb(120, 120, 140);
    }

    private static void OpenLatestCrop()
    {
        string latest = Path.Combine(AppContext.BaseDirectory, "events", "latest_match.png");
        if (File.Exists(latest))
        {
            try { Process.Start(new ProcessStartInfo(latest) { UseShellExecute = true }); } catch { }
        }
        else
        {
            OpenCropsFolder();
        }
    }

    private static void OpenCropsFolder()
    {
        string crops = Path.Combine(AppContext.BaseDirectory, "events", "crops");
        try
        {
            Directory.CreateDirectory(crops);
            Process.Start(new ProcessStartInfo("explorer.exe", crops) { UseShellExecute = true });
        }
        catch { }
    }

    private void ToggleCollapse()
    {
        if (_collapsed)
        {
            _collapsed = false;
            Height = _expandedHeight;
            _listControl.Visible = true;
            _footer.Visible = true;
            _collapseBtn.Text = "─";
        }
        else
        {
            _expandedHeight = Height;
            _collapsed = true;
            _listControl.Visible = false;
            _footer.Visible = false;
            Height = _header.Height;
            _collapseBtn.Text = "□";
        }
    }

    internal void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var it in _items) it.Dispose();
            _items.Clear();
            _listControl.SetItems(_items);
            _countLabel.Text = "(0)";
            _statusLabel.Text = "● 监听中 (CLIPStudioPaint.exe)";
        }
    }

    internal void AddAction(string category, string title, string detail,
        Bitmap? cropImage = null, string? cropPath = null,
        Color? badgeBg = null, Color? badgeFg = null)
    {
        var item = new ActionHistoryItem(
            DateTime.Now,
            category,
            title,
            detail,
            badgeBg ?? GetDefaultBadgeBg(category),
            badgeFg ?? GetDefaultBadgeFg(category),
            cropImage,
            cropPath);

        lock (_lock)
        {
            // Insert newest at the beginning (top of list)
            _items.Insert(0, item);
            while (_items.Count > 300)
            {
                _items[^1].Dispose();
                _items.RemoveAt(_items.Count - 1);
            }
            _listControl.SetItems(_items);
            _countLabel.Text = $"({_items.Count})";
            if (!string.IsNullOrEmpty(cropPath))
            {
                _statusLabel.Text = $"● 截屏已生成: {Path.GetFileName(cropPath)}";
            }
        }
    }

    private static Color GetDefaultBadgeBg(string category) => category switch
    {
        "icon" => Color.FromArgb(12, 70, 115),      // Dark Azure
        "text" => Color.FromArgb(18, 80, 45),       // Dark Forest Green
        "state" => Color.FromArgb(110, 65, 15),     // Dark Amber
        _ => Color.FromArgb(48, 50, 62)             // Dark Slate
    };

    private static Color GetDefaultBadgeFg(string category) => category switch
    {
        "icon" => Color.FromArgb(90, 200, 255),
        "text" => Color.FromArgb(100, 240, 150),
        "state" => Color.FromArgb(255, 195, 80),
        _ => Color.FromArgb(200, 205, 220)
    };

    // Dragging
    private void Header_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _dragStart = e.Location;
        }
    }

    private void Header_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            Point current = PointToScreen(e.Location);
            Location = new Point(current.X - _dragStart.X, current.Y - _dragStart.Y);
        }
    }

    private void Header_MouseUp(object? sender, MouseEventArgs e)
    {
        _isDragging = false;
    }

    // Resizing
    private void ResizeGrip_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isResizing = true;
            _resizeStart = Cursor.Position;
            _resizeStartSize = Size;
        }
    }

    private void ResizeGrip_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_isResizing)
        {
            Point current = Cursor.Position;
            int dx = current.X - _resizeStart.X;
            int dy = current.Y - _resizeStart.Y;
            Size = new Size(Math.Max(MinimumSize.Width, _resizeStartSize.Width + dx),
                            Math.Max(MinimumSize.Height, _resizeStartSize.Height + dy));
        }
    }

    private void ResizeGrip_MouseUp(object? sender, MouseEventArgs e)
    {
        _isResizing = false;
    }

    private void ResizeGrip_Paint(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(90, 95, 120), 1.5f);
        int w = _resizeGrip.Width;
        int h = _resizeGrip.Height;
        e.Graphics.DrawLine(pen, w - 4, h - 12, w - 12, h - 4);
        e.Graphics.DrawLine(pen, w - 4, h - 8, w - 8, h - 4);
        e.Graphics.DrawLine(pen, w - 4, h - 4, w - 4, h - 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                foreach (var item in _items) item.Dispose();
                _items.Clear();
            }
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Clean subtle border
        using var borderPen = new Pen(Color.FromArgb(50, 52, 70), 1f);
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// Custom owner-draw, double-buffered, flicker-free list control for action history cards.
/// </summary>
internal sealed class HistoryListControl : UserControl
{
    private readonly VScrollBar _vScrollBar;
    private readonly ToolTip _toolTip = new() { InitialDelay = 250, ReshowDelay = 150 };
    private List<ActionHistoryItem> _items = new();
    private int _hoveredIndex = -1;
    private int _lastToolTipIndex = -1;
    private const int ItemHeight = 56;

    internal HistoryListControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint, true);

        _vScrollBar = new VScrollBar {
            Dock = DockStyle.Right,
            Width = 14,
            Visible = false
        };
        _vScrollBar.Scroll += (_, _) => Invalidate();
        Controls.Add(_vScrollBar);

        MouseWheel += (_, e) => {
            if (!_vScrollBar.Visible) return;
            int newValue = _vScrollBar.Value - (e.Delta / 120) * 3;
            _vScrollBar.Value = Math.Clamp(newValue, 0, Math.Max(0, _vScrollBar.Maximum - _vScrollBar.LargeChange + 1));
            Invalidate();
        };

        MouseMove += (s, e) => {
            int idx = (e.Y + _vScrollBar.Value * ItemHeight) / ItemHeight;
            if (idx >= 0 && idx < _items.Count)
            {
                Cursor = Cursors.Hand;
                if (idx != _hoveredIndex)
                {
                    _hoveredIndex = idx;
                    Invalidate();
                }
                if (idx != _lastToolTipIndex)
                {
                    _lastToolTipIndex = idx;
                    var it = _items[idx];
                    string tip = string.IsNullOrEmpty(it.CropPath)
                        ? $"{it.Title}\n{it.Detail}"
                        : $"{it.Title}\n{it.Detail}\n🖼️ 截屏切片: {Path.GetFileName(it.CropPath)} (点击切片或双击卡片打开)";
                    _toolTip.SetToolTip(this, tip);
                }
            }
            else
            {
                Cursor = Cursors.Default;
                if (_hoveredIndex != -1)
                {
                    _hoveredIndex = -1;
                    _lastToolTipIndex = -1;
                    _toolTip.SetToolTip(this, null);
                    Invalidate();
                }
            }
        };

        MouseLeave += (_, _) => {
            _hoveredIndex = -1;
            _lastToolTipIndex = -1;
            _toolTip.SetToolTip(this, null);
            Cursor = Cursors.Default;
            Invalidate();
        };

        MouseClick += (s, e) => {
            if (e.Button == MouseButtons.Left)
            {
                int idx = (e.Y + _vScrollBar.Value * ItemHeight) / ItemHeight;
                if (idx >= 0 && idx < _items.Count)
                {
                    int cardY = (idx - (_vScrollBar.Visible ? _vScrollBar.Value : 0)) * ItemHeight + 3;
                    var thumbBox = new Rectangle(12, cardY + 6, 40, 40);
                    if (thumbBox.Contains(e.Location))
                    {
                        OpenCropImage(_items[idx].CropPath);
                    }
                }
            }
        };

        MouseDoubleClick += (s, e) => {
            if (e.Button == MouseButtons.Left)
            {
                int idx = (e.Y + _vScrollBar.Value * ItemHeight) / ItemHeight;
                if (idx >= 0 && idx < _items.Count)
                {
                    OpenCropImage(_items[idx].CropPath);
                }
            }
        };
    }

    private static void OpenCropImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch { }
    }

    internal void SetItems(List<ActionHistoryItem> items)
    {
        _items = new List<ActionHistoryItem>(items);
        UpdateScrollbar();
        Invalidate();
    }

    private void UpdateScrollbar()
    {
        int totalHeight = _items.Count * ItemHeight;
        if (totalHeight > Height)
        {
            _vScrollBar.Visible = true;
            _vScrollBar.Maximum = Math.Max(0, _items.Count - (Height / ItemHeight));
            _vScrollBar.LargeChange = Math.Max(1, Height / ItemHeight);
        }
        else
        {
            _vScrollBar.Visible = false;
            _vScrollBar.Value = 0;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollbar();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_items.Count == 0)
        {
            using var font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Italic);
            using var brush = new SolidBrush(Color.FromArgb(90, 95, 120));
            TextRenderer.DrawText(g, "等待画师操作中...\n点击 CSP 任意工具栏或菜单即可实时记录", font,
                new Rectangle(16, 30, Width - 32, 100), Color.FromArgb(100, 105, 130),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }

        int startIndex = _vScrollBar.Visible ? _vScrollBar.Value : 0;
        int maxVisible = (Height / ItemHeight) + 2;
        int endIndex = Math.Min(_items.Count, startIndex + maxVisible);

        int renderWidth = _vScrollBar.Visible ? Width - _vScrollBar.Width - 4 : Width - 4;

        using var fontTime = new Font("Segoe UI", 8.5f);
        using var fontBadge = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold);
        using var fontTitle = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        using var fontDetail = new Font("Microsoft YaHei UI", 8.5f);

        for (int i = startIndex; i < endIndex; i++)
        {
            ActionHistoryItem item = _items[i];
            int y = (i - startIndex) * ItemHeight + 3;
            var cardRect = new Rectangle(4, y, renderWidth, ItemHeight - 4);

            // Card background
            bool isHovered = i == _hoveredIndex;
            Color bgColor = isHovered ? Color.FromArgb(36, 38, 50) : Color.FromArgb(28, 29, 38);
            using (var cardBrush = new SolidBrush(bgColor))
            {
                g.FillRectangle(cardBrush, cardRect);
            }

            // Left accent bar
            using (var accentBrush = new SolidBrush(item.BadgeFg))
            {
                g.FillRectangle(accentBrush, cardRect.Left, cardRect.Top, 3, cardRect.Height);
            }

            // Thumbnail box (40x40)
            var thumbBox = new Rectangle(cardRect.Left + 8, cardRect.Top + 6, 40, 40);
            using (var boxBrush = new SolidBrush(Color.FromArgb(16, 17, 22)))
            {
                g.FillRectangle(boxBrush, thumbBox);
            }
            using (var boxPen = new Pen(isHovered ? Color.FromArgb(90, 100, 135) : Color.FromArgb(45, 47, 62), 1f))
            {
                g.DrawRectangle(boxPen, thumbBox);
            }

            if (item.CropImage != null)
            {
                int maxDim = 36;
                float scale = Math.Min((float)maxDim / item.CropImage.Width, (float)maxDim / item.CropImage.Height);
                int drawW = Math.Max(1, (int)Math.Round(item.CropImage.Width * scale));
                int drawH = Math.Max(1, (int)Math.Round(item.CropImage.Height * scale));
                int drawX = thumbBox.Left + (thumbBox.Width - drawW) / 2;
                int drawY = thumbBox.Top + (thumbBox.Height - drawH) / 2;

                var oldInterpolation = g.InterpolationMode;
                var oldPixelOffset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(item.CropImage, new Rectangle(drawX, drawY, drawW, drawH));
                g.InterpolationMode = oldInterpolation;
                g.PixelOffsetMode = oldPixelOffset;
            }
            else
            {
                using var fontPlaceholder = new Font("Segoe UI", 11f);
                TextRenderer.DrawText(g, "🔍", fontPlaceholder, thumbBox,
                    Color.FromArgb(80, 85, 105),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int textLeft = thumbBox.Right + 8;
            int availableTextWidth = Math.Max(10, cardRect.Right - textLeft - 6);

            // 1. Time stamp
            string timeStr = item.Time.ToString("HH:mm:ss");
            TextRenderer.DrawText(g, timeStr, fontTime,
                new Point(textLeft, cardRect.Top + 5),
                Color.FromArgb(130, 135, 160));

            // 2. Category Badge Pill
            string badgeText = item.Category switch {
                "icon" => "图标",
                "text" => "菜单",
                "state" => "图层",
                _ => "点击"
            };
            int badgeX = textLeft + 54;
            int badgeY = cardRect.Top + 4;
            var badgeRect = new Rectangle(badgeX, badgeY, 34, 16);
            using (var badgeBgBrush = new SolidBrush(item.BadgeBg))
            {
                g.FillRectangle(badgeBgBrush, badgeRect);
            }
            TextRenderer.DrawText(g, badgeText, fontBadge, badgeRect, item.BadgeFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 3. Title (Operation name)
            int titleX = badgeRect.Right + 6;
            int titleY = cardRect.Top + 3;
            int titleWidth = Math.Max(10, cardRect.Right - titleX - 6);
            TextRenderer.DrawText(g, item.Title, fontTitle,
                new Rectangle(titleX, titleY, titleWidth, 20),
                Color.FromArgb(245, 245, 255),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 4. Detail / Evidence
            int detailX = textLeft;
            int detailY = cardRect.Top + 27;
            int detailWidth = availableTextWidth;
            TextRenderer.DrawText(g, item.Detail, fontDetail,
                new Rectangle(detailX, detailY, detailWidth, 18),
                Color.FromArgb(145, 150, 175),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }
}
