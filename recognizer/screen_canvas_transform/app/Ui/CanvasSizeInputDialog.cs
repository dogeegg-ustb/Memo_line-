using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ScreenCanvasTransform.Ui;

public sealed class CanvasSizeInputDialog : Window
{
    private readonly TextBox _widthBox;
    private readonly TextBox _heightBox;

    public int CanvasPixelWidth { get; private set; }
    public int CanvasPixelHeight { get; private set; }

    public CanvasSizeInputDialog()
    {
        Title = "画布像素尺寸";
        Width = 380;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = "请输入完整画布的像素宽度和高度（必须在截图与 ROI 之前）。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock { Text = "CanvasPixelWidth", Margin = new Thickness(0, 0, 0, 4) });
        _widthBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(_widthBox);
        root.Children.Add(new TextBlock { Text = "CanvasPixelHeight", Margin = new Thickness(0, 0, 0, 4) });
        _heightBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(_heightBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var ok = new Button { Content = "确认", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        ok.Click += (_, _) => OnConfirm();
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
    }

    private void OnConfirm()
    {
        if (!int.TryParse(_widthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
            !int.TryParse(_heightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
            w <= 0 || h <= 0 || w > 65536 || h > 65536)
        {
            MessageBox.Show(this, "请输入有效的正整数画布尺寸（≤65536）。", "输入无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CanvasPixelWidth = w;
        CanvasPixelHeight = h;
        DialogResult = true;
        Close();
    }
}
