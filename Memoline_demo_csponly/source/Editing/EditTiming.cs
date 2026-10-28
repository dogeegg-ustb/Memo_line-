using System.Diagnostics;

namespace MemolineDemo.Editing;

/// <summary>Step durations of editing transactions, appended to logs/edit-timing.log: where an edit or precise undo
/// spends its time (CSP saves, CLIP reads, panel OCR, replay).</summary>
internal static class EditTiming
{
    private static readonly object Sync = new();
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "logs", "edit-timing.log");

    internal static Step Start(string? transaction, string name) => new(transaction, name, Stopwatch.GetTimestamp());

    internal readonly record struct Step(string? Transaction, string Name, long Started) : IDisposable
    {
        public void Dispose() => Write(Transaction, Name, Stopwatch.GetElapsedTime(Started).TotalMilliseconds);
    }

    /// <summary>A measurement that is not one step's duration, e.g. the pauses between replayed strokes.</summary>
    internal static void Note(string? transaction, string name, string value) => Append(transaction, name, value);

    private static void Write(string? transaction, string name, double milliseconds) => Append(transaction, name, $"{milliseconds:F0} ms");

    private static void Append(string? transaction, string name, string value)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                System.IO.File.AppendAllText(File,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{transaction ?? "-"}\t{name}\t{value}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
