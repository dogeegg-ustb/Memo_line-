using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WorkspaceBorderDetect.Capture;
using WorkspaceBorderDetect.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace WorkspaceBorderDetect.Ui;

/// <summary>
/// Fullscreen borderless ROI selector showing a FROZEN bitmap (not live desktop).
/// Esc cancels; Enter or mouse-release confirms when ROI >= 32px.
/// </summary>
public sealed class RoiSelectWindow : Window
{
    private readonly CaptureSession _session;
    private readonly System.Windows.Controls.Image _image;
    private readonly Canvas _canvas;
    private readonly Rectangle _selectionRect;
    private readonly TextBlock _hint;

    private bool _dragging;
    private Point _start;
    private Point _current;
    private IntRect? _confirmedRoi;

    public IntRect? ConfirmedRoiCapturePx => _confirmedRoi;

    public RoiSelectWindow(CaptureSession session)
    {
        _session = session;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Focusable = true;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };

        _canvas = new Canvas();
        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(220, 80, 200, 120)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 80, 200, 120)),
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_selectionRect);

        _hint = new TextBlock
        {
            Text = "拖拽框选工作区粗略范围 · Enter 确认 · Esc 取消",
            Foreground = Brushes.White,
            FontSize = 16,
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.8
            }
        };
        Canvas.SetLeft(_hint, 16);
        Canvas.SetTop(_hint, 16);
        _canvas.Children.Add(_hint);

        var root = new Grid();
        root.Children.Add(_image);
        root.Children.Add(_canvas);
        Content = root;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _image.Source = BitmapToImageSource(_session.FrozenCapture);
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _confirmedRoi = null;
            DialogResult = false;
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (TryConfirmFromCurrentSelection())
            {
                DialogResult = true;
                Close();
            }
            e.Handled = true;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(_canvas);
        _current = _start;
        CaptureMouse();
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _current = e.GetPosition(_canvas);
        UpdateSelectionVisual();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _current = e.GetPosition(_canvas);
        ReleaseMouseCapture();
        UpdateSelectionVisual();

        if (TryConfirmFromCurrentSelection())
        {
            DialogResult = true;
            Close();
        }
        e.Handled = true;
    }

    private bool TryConfirmFromCurrentSelection()
    {
        var roiDip = NormalizeDipRect(_start, _current);
        if (roiDip.Width < 1 || roiDip.Height < 1)
            return false;

        var captureRoi = MapDipRectToCapturePx(roiDip);
        if (!_session.TrySetUserRoi(captureRoi, out _))
            return false;

        _confirmedRoi = _session.UserRoiCapturePx;
        return true;
    }

    private void UpdateSelectionVisual()
    {
        var r = NormalizeDipRect(_start, _current);
        if (r.Width < 1 || r.Height < 1)
        {
            _selectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        _selectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selectionRect, r.X);
        Canvas.SetTop(_selectionRect, r.Y);
        _selectionRect.Width = r.Width;
        _selectionRect.Height = r.Height;
    }

    private static Rect NormalizeDipRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private IntRect MapDipRectToCapturePx(Rect dip)
    {
        double aw = Math.Max(_canvas.ActualWidth, 1);
        double ah = Math.Max(_canvas.ActualHeight, 1);
        int bw = _session.FrozenCapture.Width;
        int bh = _session.FrozenCapture.Height;

        int left = (int)Math.Floor(dip.X / aw * bw);
        int top = (int)Math.Floor(dip.Y / ah * bh);
        int right = (int)Math.Ceiling((dip.X + dip.Width) / aw * bw);
        int bottom = (int)Math.Ceiling((dip.Y + dip.Height) / ah * bh);

        return new IntRect(left, top, right, bottom).ClampTo(_session.CaptureBounds);
    }

    private static BitmapSource BitmapToImageSource(Bitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(
                data.Width,
                data.Height,
                bitmap.HorizontalResolution,
                bitmap.VerticalResolution,
                PixelFormats.Bgra32,
                null,
                data.Scan0,
                data.Stride * data.Height,
                data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
