using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Ui;

/// <summary>
/// Click-through overlay: observed red-frame edges (solid=complete, dashed=partial)
/// with workspace-edge labels (左/右/上/下) placed on each edge midpoint along the outward
/// normal (perpendicular to the viewport border). Labels use system workspace_edge roles.
/// Endpoints are ScreenPhysicalPx from caller.
/// </summary>
public sealed class CompleteEdgeOverlayWindow : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int HwndTopMost = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoCopyBits = 0x0100;
    private const int StrokeThickness = 3;
    private const int Pad = 8;
    private const int LabelOffsetPx = 10;
    private const float LabelFontSizePx = 15f;

    private static readonly System.Drawing.Color PartialEdgeColor =
        System.Drawing.Color.FromArgb(200, 80, 170, 255);

    private static readonly System.Drawing.Color EdgeColor =
        System.Drawing.Color.FromArgb(230, 30, 120, 255);

    private static readonly System.Drawing.Color LabelFillColor =
        System.Drawing.Color.FromArgb(255, 255, 230, 80);

    private static readonly System.Drawing.Color LabelOutlineColor =
        System.Drawing.Color.FromArgb(220, 20, 20, 20);

    private static readonly IntPtr WindowClassAtom;
    private static readonly WndProcDelegate WndProcKeepAlive = StaticWndProc;

    private IntPtr _hwnd;
    private string? _boundCaptureId;
    private bool _disposed;

    static CompleteEdgeOverlayWindow()
    {
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcKeepAlive),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = "ScreenCanvasTransform.CompleteEdgeOverlay",
            hIconSm = IntPtr.Zero
        };
        WindowClassAtom = RegisterClassEx(ref wc);
    }

    public string? BoundCaptureId => _boundCaptureId;

    public readonly record struct LabeledScreenEdge(
        double X0, double Y0, double X1, double Y1, int WorkspaceEdge, bool IsComplete);

    public void Show(IReadOnlyList<LabeledScreenEdge> screenEdges, string captureId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(captureId))
            throw new ArgumentException("CaptureId 不能为空。", nameof(captureId));

        if (screenEdges is null || screenEdges.Count == 0)
        {
            Hide();
            return;
        }

        using var font = CreateLabelFont();
        float frameCx = 0f, frameCy = 0f;
        int midCount = 0;
        foreach (var e in screenEdges)
        {
            frameCx += (float)(e.X0 + e.X1) * 0.5f;
            frameCy += (float)(e.Y0 + e.Y1) * 0.5f;
            ++midCount;
        }
        if (midCount > 0)
        {
            frameCx /= midCount;
            frameCy /= midCount;
        }

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var e in screenEdges)
        {
            minX = Math.Min(minX, Math.Min(e.X0, e.X1));
            minY = Math.Min(minY, Math.Min(e.Y0, e.Y1));
            maxX = Math.Max(maxX, Math.Max(e.X0, e.X1));
            maxY = Math.Max(maxY, Math.Max(e.Y0, e.Y1));

            if (WorkspaceEdgeBits.ToLabel(e.WorkspaceEdge) is not null)
            {
                var labelBounds = LabelBoundsScreen(e, font, frameCx, frameCy);
                minX = Math.Min(minX, labelBounds.Left);
                minY = Math.Min(minY, labelBounds.Top);
                maxX = Math.Max(maxX, labelBounds.Right);
                maxY = Math.Max(maxY, labelBounds.Bottom);
            }
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY))
        {
            Hide();
            return;
        }

        int left = (int)Math.Floor(minX) - Pad;
        int top = (int)Math.Floor(minY) - Pad;
        int right = (int)Math.Ceiling(maxX) + Pad;
        int bottom = (int)Math.Ceiling(maxY) + Pad;
        int w = Math.Max(1, right - left);
        int h = Math.Max(1, bottom - top);

        EnsureWindow();
        _boundCaptureId = captureId;

        SetWindowPos(_hwnd, (IntPtr)HwndTopMost, left, top, w, h,
            SwpNoActivate | SwpShowWindow | SwpNoCopyBits);

        var local = new LabeledScreenEdge[screenEdges.Count];
        for (int i = 0; i < screenEdges.Count; i++)
        {
            var e = screenEdges[i];
            local[i] = new LabeledScreenEdge(
                e.X0 - left, e.Y0 - top, e.X1 - left, e.Y1 - top, e.WorkspaceEdge, e.IsComplete);
        }

        UpdateLayeredContent(w, h, local, font);
        ShowWindow(_hwnd, SwShowNoActivate);
    }

    public bool TryShowIfCaptureMatches(
        IReadOnlyList<LabeledScreenEdge> screenEdges,
        string expectedCaptureId,
        string resultCaptureId)
    {
        if (!string.Equals(expectedCaptureId, resultCaptureId, StringComparison.Ordinal))
        {
            Hide();
            return false;
        }

        Show(screenEdges, expectedCaptureId);
        return true;
    }

    public void Hide()
    {
        _boundCaptureId = null;
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SwHide);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Hide();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    private static Font CreateLabelFont()
    {
        try
        {
            return new Font("Microsoft YaHei UI", LabelFontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, LabelFontSizePx, FontStyle.Bold,
                GraphicsUnit.Pixel);
        }
    }

    private static RectangleF LabelBoundsScreen(
        LabeledScreenEdge edge, Font font, float frameCenterX, float frameCenterY)
    {
        string? text = WorkspaceEdgeBits.ToLabel(edge.WorkspaceEdge);
        if (text is null)
            return RectangleF.Empty;

        using var measureBmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var measureG = Graphics.FromImage(measureBmp);
        measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        SizeF size = measureG.MeasureString(text, font);
        PointF origin = LabelOriginOutsideEdge(
            (float)edge.X0, (float)edge.Y0, (float)edge.X1, (float)edge.Y1,
            frameCenterX, frameCenterY, size);
        return new RectangleF(origin.X, origin.Y, size.Width, size.Height);
    }

    /// <summary>
    /// 字不转：只把标签放到边中点外侧（沿边法向）。文字内容来自边的 workspace_edge。
    /// </summary>
    private static PointF LabelOriginOutsideEdge(
        float x0, float y0, float x1, float y1,
        float frameCenterX, float frameCenterY, SizeF textSize)
    {
        float mx = (x0 + x1) * 0.5f;
        float my = (y0 + y1) * 0.5f;
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        float nx = 0f, ny = -1f;
        if (len >= 1e-3f)
        {
            nx = -dy / len;
            ny = dx / len;
            if ((mx - frameCenterX) * nx + (my - frameCenterY) * ny < 0f)
            {
                nx = -nx;
                ny = -ny;
            }
        }
        float offset = LabelOffsetPx + Math.Max(textSize.Width, textSize.Height) * 0.25f;
        return new PointF(
            mx + nx * offset - textSize.Width * 0.5f,
            my + ny * offset - textSize.Height * 0.5f);
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        int exStyle = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopMost;
        _hwnd = CreateWindowEx(
            exStyle,
            WindowClassAtom,
            "ScreenCanvasCompleteEdgeOverlay",
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("无法创建完整边覆盖层窗口。");
    }

    private void UpdateLayeredContent(int width, int height, LabeledScreenEdge[] localEdges, Font font)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float frameCx = 0f, frameCy = 0f;
            foreach (var e in localEdges)
            {
                frameCx += (float)(e.X0 + e.X1) * 0.5f;
                frameCy += (float)(e.Y0 + e.Y1) * 0.5f;
            }
            if (localEdges.Length > 0)
            {
                frameCx /= localEdges.Length;
                frameCy /= localEdges.Length;
            }

            using var solidPen = new System.Drawing.Pen(EdgeColor, StrokeThickness);
            solidPen.StartCap = LineCap.Flat;
            solidPen.EndCap = LineCap.Flat;
            using var dashedPen = new System.Drawing.Pen(PartialEdgeColor, StrokeThickness);
            dashedPen.StartCap = LineCap.Flat;
            dashedPen.EndCap = LineCap.Flat;
            dashedPen.DashStyle = DashStyle.Dash;
            foreach (var e in localEdges)
            {
                var pen = e.IsComplete ? solidPen : dashedPen;
                g.DrawLine(pen, (float)e.X0, (float)e.Y0, (float)e.X1, (float)e.Y1);
            }

            using var fillBrush = new SolidBrush(LabelFillColor);
            using var outlineBrush = new SolidBrush(LabelOutlineColor);
            foreach (var e in localEdges)
            {
                string? text = WorkspaceEdgeBits.ToLabel(e.WorkspaceEdge);
                if (text is null)
                    continue;

                SizeF textSize = g.MeasureString(text, font);
                PointF origin = LabelOriginOutsideEdge(
                    (float)e.X0, (float)e.Y0, (float)e.X1, (float)e.Y1,
                    frameCx, frameCy, textSize);
                DrawOutlinedText(g, text, font, origin, outlineBrush, fillBrush);
            }
        }

        PremultiplyAlpha(bmp);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
        IntPtr old = SelectObject(memDc, hBitmap);

        var windowSize = new SizeStruct { Cx = width, Cy = height };
        var pointSource = new PointStruct { X = 0, Y = 0 };
        GetWindowRect(_hwnd, out var wr);
        var topLeft = new PointStruct { X = wr.Left, Y = wr.Top };
        var blend = new BlendFunction
        {
            BlendOp = AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AcSrcAlpha
        };

        UpdateLayeredWindow(_hwnd, screenDc, ref topLeft, ref windowSize, memDc, ref pointSource, 0, ref blend, UlwAlpha);

        SelectObject(memDc, old);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static void DrawOutlinedText(
        Graphics g,
        string text,
        Font font,
        PointF origin,
        Brush outline,
        Brush fill)
    {
        const float d = 1.25f;
        g.DrawString(text, font, outline, origin.X - d, origin.Y);
        g.DrawString(text, font, outline, origin.X + d, origin.Y);
        g.DrawString(text, font, outline, origin.X, origin.Y - d);
        g.DrawString(text, font, outline, origin.X, origin.Y + d);
        g.DrawString(text, font, fill, origin, StringFormat.GenericDefault);
    }

    private static unsafe void PremultiplyAlpha(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            for (int y = 0; y < data.Height; y++)
            {
                byte* row = scan0 + y * data.Stride;
                for (int x = 0; x < data.Width; x++)
                {
                    byte* p = row + x * 4;
                    byte a = p[3];
                    if (a == 0)
                    {
                        p[0] = p[1] = p[2] = 0;
                        continue;
                    }
                    if (a == 255)
                        continue;
                    p[0] = (byte)(p[0] * a / 255);
                    p[1] = (byte)(p[1] * a / 255);
                    p[2] = (byte)(p[2] * a / 255);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WmNchittest = 0x0084;
        const int HtTransparent = -1;
        if (msg == WmNchittest)
            return (IntPtr)HtTransparent;
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    #region Win32

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeStruct { public int Cx; public int Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, IntPtr lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectStruct lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref PointStruct pptDst, ref SizeStruct psize,
        IntPtr hdcSrc, ref PointStruct pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    #endregion
}
