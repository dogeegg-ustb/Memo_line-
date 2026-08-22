using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCanvasTransform.Models;

namespace ScreenCanvasTransform.Ui;

/// <summary>
/// Click-through, no-activate border overlay for a single ROI.
/// Hidden before capture so it never enters evidence.
/// </summary>
public sealed class RoiBorderOverlayWindow : IDisposable
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
    private const int BorderThickness = 2;

    private readonly string _className;
    private readonly string _windowTitle;
    private readonly System.Drawing.Color _borderColor;
    private readonly IntPtr _windowClassAtom;
    private readonly WndProcDelegate _wndProcKeepAlive;

    private IntPtr _hwnd;
    private string? _boundCaptureId;
    private bool _disposed;

    public RoiBorderOverlayWindow(string className, string windowTitle, System.Drawing.Color borderColor)
    {
        _className = className;
        _windowTitle = windowTitle;
        _borderColor = borderColor;
        _wndProcKeepAlive = StaticWndProc;

        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = _className,
            hIconSm = IntPtr.Zero
        };
        _windowClassAtom = RegisterClassEx(ref wc);
    }

    /// <summary>Corrected WorkspaceRoi — green.</summary>
    public static RoiBorderOverlayWindow CreateWorkspace()
        => new(
            "ScreenCanvasTransform.WorkspaceRoiBorder",
            "ScreenCanvasWorkspaceRoiBorder",
            System.Drawing.Color.FromArgb(230, 72, 199, 92));

    /// <summary>User NavigatorRoi (adopted directly) — cyan.</summary>
    public static RoiBorderOverlayWindow CreateNavigator()
        => new(
            "ScreenCanvasTransform.NavigatorRoiBorder",
            "ScreenCanvasNavigatorRoiBorder",
            System.Drawing.Color.FromArgb(230, 40, 180, 255));

    /// <summary>NavigatorThumbnailRoi from C-II — magenta.</summary>
    public static RoiBorderOverlayWindow CreateNavigatorThumbnail()
        => new(
            "ScreenCanvasTransform.NavigatorThumbnailRoiBorder",
            "ScreenCanvasNavigatorThumbnailRoiBorder",
            System.Drawing.Color.FromArgb(230, 220, 60, 200));

    public string? BoundCaptureId => _boundCaptureId;

    public void Show(IntRect screenPhysicalPx, string captureId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(captureId))
            throw new ArgumentException("CaptureId 不能为空。", nameof(captureId));

        if (screenPhysicalPx.Width < 1 || screenPhysicalPx.Height < 1)
        {
            Hide();
            return;
        }

        EnsureWindow();
        _boundCaptureId = captureId;

        int x = screenPhysicalPx.Left;
        int y = screenPhysicalPx.Top;
        int w = screenPhysicalPx.Width;
        int h = screenPhysicalPx.Height;

        SetWindowPos(_hwnd, (IntPtr)HwndTopMost, x, y, w, h,
            SwpNoActivate | SwpShowWindow | SwpNoCopyBits);

        UpdateLayeredContent(w, h);
        ShowWindow(_hwnd, SwShowNoActivate);
    }

    public bool TryShowIfCaptureMatches(IntRect screenPhysicalPx, string expectedCaptureId, string resultCaptureId)
    {
        if (!string.Equals(expectedCaptureId, resultCaptureId, StringComparison.Ordinal))
        {
            Hide();
            return false;
        }

        Show(screenPhysicalPx, expectedCaptureId);
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

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        int exStyle = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopMost;
        _hwnd = CreateWindowEx(
            exStyle,
            _windowClassAtom,
            _windowTitle,
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"无法创建覆盖层窗口：{_windowTitle}");
    }

    private void UpdateLayeredContent(int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            using var pen = new System.Drawing.Pen(_borderColor, BorderThickness);
            pen.Alignment = PenAlignment.Inset;
            float inset = BorderThickness / 2f;
            g.DrawRectangle(pen, inset, inset, Math.Max(0, width - BorderThickness), Math.Max(0, height - BorderThickness));
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
