namespace CanvasLayerWatcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length is 2 or 3 && args[0] == "--smoke")
        { using var window = new MainWindow([]); window.RenderSmoke(Path.GetFullPath(args[1]), args.Length == 3 ? Path.GetFullPath(args[2]) : null); return; }
        Application.Run(new MainWindow(args));
    }
}
