using DirtyMatrix.UI;

namespace DirtyMatrix;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var window = new MainWindow();
        // A deterministic UI construction check without interacting with CSP.
        if (args is ["--preview", var path])
        {
            window.Shown += (_, _) =>
            {
                window.BeginInvoke((Action)(() => { window.SavePreview(Path.GetFullPath(path)); window.Close(); }));
            };
        }
        if (args is ["--smoke", var directory])
        {
            window.Shown += (_, _) => window.BeginInvoke((Action)(() =>
            {
                try { window.RunSmokeChecks(Path.GetFullPath(directory)); }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory,"ui-failure.txt"),ex.ToString());
                    Environment.ExitCode = 1;
                }
                finally { window.Close(); }
            }));
        }
        Application.Run(window);
    }
}
