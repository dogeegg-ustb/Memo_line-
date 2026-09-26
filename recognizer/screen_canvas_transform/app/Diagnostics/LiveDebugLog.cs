using System.IO;
using System.Text;

namespace ScreenCanvasTransform.Diagnostics;

/// <summary>Console + file log for live debug sessions.</summary>
public static class LiveDebugLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(),
        "sct_live_debug.log");

    public static string LogFilePath => LogPath;

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // no console attached
        }
    }
}
