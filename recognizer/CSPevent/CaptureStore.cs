using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace CSPevent;

internal sealed record CapturedClick(
    string Id,
    DateTimeOffset CapturedAt,
    int X,
    int Y,
    string Button,
    string Language,
    long WindowHandle,
    int Left,
    int Top,
    int Width,
    int Height,
    string BeforePath,
    string? AfterPath);

internal sealed class CaptureStore
{
    private readonly string _root;
    private readonly string _pending;
    private readonly string _screenshots;
    private readonly string _crops;
    private readonly string _log;
    private long _nextId;

    internal CaptureStore(string directory)
    {
        _root = directory;
        _pending = Path.Combine(_root, "pending");
        _screenshots = Path.Combine(_root, "screenshots");
        _crops = Path.Combine(_root, "crops");
        _log = Path.Combine(_root, "events.jsonl");
        Directory.CreateDirectory(_pending);
        Directory.CreateDirectory(_screenshots);
        Directory.CreateDirectory(_crops);
    }

    internal CapturedClick SaveBefore(Bitmap bitmap, Point click, Rectangle bounds,
        string button, string language, IntPtr window)
    {
        string id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Interlocked.Increment(ref _nextId):D5}";
        string before = Path.Combine(_screenshots, id + "-before.png");
        bitmap.Save(before, ImageFormat.Png);
        var entry = new CapturedClick(id, DateTimeOffset.Now,
            click.X, click.Y, button, language, window.ToInt64(), bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, before, null);
        WritePending(entry);
        return entry;
    }

    internal CapturedClick SaveAfter(CapturedClick entry, Bitmap bitmap)
    {
        string after = Path.Combine(_screenshots, entry.Id + "-after.png");
        bitmap.Save(after, ImageFormat.Png);
        entry = entry with { AfterPath = after };
        WritePending(entry);
        return entry;
    }

    private void WritePending(CapturedClick entry) =>
        File.WriteAllText(PendingPath(entry.Id), JsonSerializer.Serialize(entry));

    private string PendingPath(string id) => Path.Combine(_pending, id + ".json");

    internal IReadOnlyList<CapturedClick> LoadPending()
    {
        var result = new List<CapturedClick>();
        foreach (string file in Directory.EnumerateFiles(_pending, "*.json").OrderBy(path => path))
        {
            try
            {
                CapturedClick? entry = JsonSerializer.Deserialize<CapturedClick>(File.ReadAllText(file));
                if (entry is not null && File.Exists(entry.BeforePath)) result.Add(entry);
            }
            catch { /* Preserve damaged metadata for inspection; other events still load. */ }
        }
        return result;
    }

    internal (string CropPath, string LatestCropPath) SaveCrop(string id, Bitmap bitmap)
    {
        string cropPath = Path.Combine(_crops, $"{id}-crop.png");
        bitmap.Save(cropPath, ImageFormat.Png);
        string latest = Path.Combine(_root, "latest_match.png");
        try
        {
            File.Copy(cropPath, latest, overwrite: true);
        }
        catch { /* Ignore lock if file is open in external image viewer */ }
        return (cropPath, latest);
    }

    internal void Complete(CapturedClick entry, string rawOcr, string selectedText,
        MatchResult match, HighlightEvidence highlight, long ocrMilliseconds,
        string? error = null, IconMatchResult? iconMatch = null, string? cropPath = null,
        TextPresence? textPresence = null)
    {
        var record = new {
            entry.Id, entry.CapturedAt, entry.X, entry.Y, entry.Button, entry.Language,
            entry.WindowHandle, entry.Left, entry.Top, entry.Width, entry.Height,
            entry.BeforePath, entry.AfterPath,
            CropPath = cropPath,
            OcrText = rawOcr, SelectedText = selectedText,
            TextDetected = textPresence?.HasText, TextDetectionScore = textPresence?.Score,
            TextBounds = textPresence?.Bounds is not null ? new[] {
                textPresence.Bounds.Value.X, textPresence.Bounds.Value.Y,
                textPresence.Bounds.Value.Width, textPresence.Bounds.Value.Height
            } : null,
            MatchedLabel = match.Label, match.CandidateCount, match.State,
            Highlight = highlight.State, highlight.ChangedFraction,
            HighlightLeft = highlight.Left, HighlightRight = highlight.Right,
            IconId = iconMatch?.IconId,
            IconName = iconMatch?.Label,
            IconCommand = iconMatch?.CommandHint,
            IconScore = iconMatch?.Score,
            IconState = iconMatch?.State,
            IconButtonRect = iconMatch?.ButtonRect is not null ? new[] {
                iconMatch.ButtonRect.Value.X,
                iconMatch.ButtonRect.Value.Y,
                iconMatch.ButtonRect.Value.Width,
                iconMatch.ButtonRect.Value.Height
            } : null,
            OcrMilliseconds = ocrMilliseconds, Error = error
        };
        File.AppendAllText(_log, JsonSerializer.Serialize(record) + Environment.NewLine);
        File.Delete(PendingPath(entry.Id));
    }
}
