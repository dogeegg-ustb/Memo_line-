namespace LayerStealer.Capture;

internal static class CopyTrace
{
    private static readonly string PathName = Path.Combine(AppContext.BaseDirectory, "layer-stealer.log");
    public static void Write(string message)
    {
        try
        {
            if (File.Exists(PathName) && new FileInfo(PathName).Length > 1_000_000) File.WriteAllText(PathName, "");
            File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
