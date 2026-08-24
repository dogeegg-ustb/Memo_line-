using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCanvasTransform.Interop;

namespace ScreenCanvasTransform.Ui;

/// <summary>
/// Transparent orange L-marker at CanvasTopLeft, click-through, no-activate.
/// Arm endpoints come from native marker geometry (canvas ε projected to screen).
/// </summary>
public sealed class MarkerOverlayWindow : IDisposable
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
    private const int MinArmScreenPx = 28;
    private const int MinMarkerWindowPx = 64;
    private const int Pad = 14;

    private static readonly IntPtr WindowClassAtom;
    private static readonly WndProcDelegate WndProcKeepAlive = StaticWndProc;
    private static readonly System.Drawing.Color MarkerColor =
        System.Drawing.Color.FromArgb(220, 255, 140, 0);

    private IntPtr _hwnd;
    private string? _boundCaptureId;
    private ulong _boundGeneration;
    private bool _disposed;

    static MarkerOverlayWindow()
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
            lpszClassName = "ScreenCanvasTransform.MarkerOverlay",
            hIconSm = IntPtr.Zero
        };
        WindowClassAtom = RegisterClassEx(ref wc);
    }

    public string? BoundCaptureId => _boundCaptureId;
    public ulong BoundGeneration => _boundGeneration;

    public void Show(NativeSct.SctMarkerGeometry marker, string captureId, ulong generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(captureId))
            throw new ArgumentException("CaptureId 不能为空。", nameof(captureId));

        double ax = marker.AnchorScreen.X;
        double ay = marker.AnchorScreen.Y;
        double xx = marker.XArmEndScreen.X;
        double xy = marker.XArmEndScreen.Y;
        double yx = marker.YArmEndScreen.X;
        double yy = marker.YArmEndScreen.Y;

        EnforceMinArm(ref xx, ref xy, ax, ay);
        EnforceMinArm(ref yx, ref yy, ax, ay);

        int left = (int)Math.Floor(Math.Min(ax, Math.Min(xx, yx))) - Pad;
        int top = (int)Math.Floor(Math.Min(ay, Math.Min(xy, yy))) - Pad;
        int right = (int)Math.Ceiling(Math.Max(ax, Math.Max(xx, yx))) + Pad;
        int bottom = (int)Math.Ceiling(Math.Max(ay, Math.Max(xy, yy))) + Pad;
        int w = Math.Max(MinMarkerWindowPx, right - left);
        int h = Math.Max(MinMarkerWindowPx, bottom - top);

        // Keep the projected anchor inside the enlarged bitmap when a very small
        // or off-screen transform produces a window smaller than the marker arms.
        if (ax - left < Pad || ay - top < Pad || xx - left < Pad || yy - top < Pad)
        {
            left = (int)Math.Floor(Math.Min(ax, Math.Min(xx, yx))) - Pad;
            top = (int)Math.Floor(Math.Min(ay, Math.Min(xy, yy))) - Pad;
            right = Math.Max(right, left + MinMarkerWindowPx);
            bottom = Math.Max(bottom, top + MinMarkerWindowPx);
            w = Math.Max(MinMarkerWindowPx, right - left);
            h = Math.Max(MinMarkerWindowPx, bottom - top);
        }

        EnsureWindow();
        _boundCaptureId = captureId;
        _boundGeneration = generation;

        SetWindowPos(_hwnd, (IntPtr)HwndTopMost, left, top, w, h,
            SwpNoActivate | SwpShowWindow | SwpNoCopyBits);

        float oX = (float)(ax - left);
        float oY = (float)(ay - top);
        float xEndX = (float)(xx - left);
        float xEndY = (float)(xy - top);
        float yEndX = (float)(yx - left);
        float yEndY = (float)(yy - top);

        UpdateLayeredContent(w, h, oX, oY, xEndX, xEndY, yEndX, yEndY,
            marker.TargetStrokeDisplayPx > 0 ? marker.TargetStrokeDisplayPx : 3f);
        ShowWindow(_hwnd, SwShowNoActivate);
    }

    public bool TryShowIfGenerationMatches(
        NativeSct.SctMarkerGeometry marker,
        string captureId,
        ulong generation,
        string expectedCaptureId,
        ulong expectedGeneration)
    {
        if (!string.Equals(captureId, expectedCaptureId, StringComparison.Ordinal) ||
            generation != expectedGeneration)
        {
            return false;
        }

        Show(marker, captureId, generation);
        return true;
    }

    public void Hide()
    {
        _boundCaptureId = null;
        _boundGeneration = 0;
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

    private static void EnforceMinArm(ref double endX, ref double endY, double ax, double ay)
    {
        double dx = endX - ax;
        double dy = endY - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len >= MinArmScreenPx || len < 1e-6)
            return;
        double s = MinArmScreenPx / len;
        endX = ax + dx * s;
        endY = ay + dy * s;
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        int exStyle = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopMost;
        _hwnd = CreateWindowEx(
            exStyle,
            WindowClassAtom,
            "ScreenCanvasMarkerOverlay",
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("无法创建标记覆盖层窗口。");
    }

    private void UpdateLayeredContent(
        int width, int height,
        float oX, float oY,
        float xEndX, float xEndY,
        float yEndX, float yEndY,
        float strokePx)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float visibleStroke = Math.Max(5f, strokePx);
            using var halo = new System.Drawing.Pen(System.Drawing.Color.FromArgb(230, 90, 35, 0), visibleStroke + 6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var pen = new System.Drawing.Pen(MarkerColor, visibleStroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(halo, oX, oY, xEndX, xEndY);
            g.DrawLine(halo, oX, oY, yEndX, yEndY);
            g.DrawLine(pen, oX, oY, xEndX, xEndY);
            g.DrawLine(pen, oX, oY, yEndX, yEndY);
        }

        PremultiplyAlpha(bmp);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
        IntPtr old = SelectObject(memDc, hBitmap);

        var size = new SizeStruct { Cx = width, Cy = height };
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

        UpdateLayeredWindow(_hwnd, screenDc, ref topLeft, ref size, memDc, ref pointSource, 0, ref blend, UlwAlpha);

        SelectObject(memDc, old);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
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
