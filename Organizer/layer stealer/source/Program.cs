using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using LayerStealer.Capture;
using LayerStealer.UI;

namespace LayerStealer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool smoke = args is ["--smoke", _];
        using var window = new MainWindow(smoke);
        if (smoke)
        {
            string directory = Path.GetFullPath(args[1]);
            window.Shown += (_, _) => window.BeginInvoke((Action)(() =>
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    window.SaveUiPreview(Path.Combine(directory, "ui-empty.png"));
                    var sample = new Bitmap(960, 600, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(sample))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        using var brush = new SolidBrush(Color.FromArgb(128, 42, 123, 213));
                        graphics.FillEllipse(brush, 90, 65, 500, 420);
                        using var pen = new Pen(Color.FromArgb(230, 35, 50, 85), 18);
                        graphics.DrawBezier(pen, 180, 420, 430, 50, 570, 540, 820, 180);
                    }
                    sample.Save(Path.Combine(directory, "sample-layer.png"), ImageFormat.Png);
                    window.Present(new(sample, "测试 PNG · 透明背景", 0, 0));
                    window.SaveUiPreview(Path.Combine(directory, "ui-layer.png"));
                    window.ClientSize = new(900, 560);
                    window.SaveUiPreview(Path.Combine(directory, "ui-small.png"));
                    File.WriteAllText(Path.Combine(directory, "ui-ok.txt"), "UI constructed; empty, alpha image and minimum layout rendered. No CSP input or clipboard writes.");
                }
                catch (Exception ex) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "ui-failure.txt"), ex.ToString()); Environment.ExitCode = 1; }
                finally { window.Close(); }
            }));
        }
        Application.Run(window);
    }
}
