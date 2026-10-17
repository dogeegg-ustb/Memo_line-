using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>One original layer that an edit replaced: its full-canvas pixels and its CSP layer properties.</summary>
internal sealed record RecycleEntry(string Id, string Directory, DateTimeOffset Created, string EditKind, string LayerUuid,
    string LayerName, int Width, int Height, JsonElement Properties, string Outcome)
{
    internal string ImagePath => Path.Combine(Directory, RecycleBin.ImageName);
    internal string KindDisplay => EditKind == nameof(Editing.EditKind.PreciseUndo) ? "精确撤销" : "事件编辑";
    internal string OutcomeDisplay => Outcome switch
    {
        "executed" => "已替换", "failed" => "编辑失败（原层可能仍在 CSP）", "cancelled" => "已取消", _ => "保存中"
    };
}

/// <summary>
/// Kept beside the project recording ("&lt;project&gt;.memoline.recycle"): an edit deletes the original layer in CSP,
/// so this is the only copy of it. Entries are written before the edit runs and are never changed afterwards
/// except for their outcome.
/// </summary>
internal static class RecycleBin
{
    internal const string ImageName = "layer.png", EntryName = "entry.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string RootFor(string? projectRecording, string workRoot)
        => projectRecording is { Length: > 0 } ? Path.GetFullPath(projectRecording) + ".recycle" : Path.Combine(workRoot, "recycle");

    /// <summary>Writes the entry description at once and the PNG in the returned task (≈0.5 s for a 4961×7016 canvas).</summary>
    internal static (RecycleEntry Entry, Task Image) Save(string root, EditKind kind, LayerKey layer, string name, ImageRgba image, JsonElement properties)
    {
        image.Validate();
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(root, id);
        System.IO.Directory.CreateDirectory(directory);
        var entry = new RecycleEntry(id, directory, DateTimeOffset.Now, kind.ToString(), layer.Uuid, name, image.Width, image.Height,
            properties.ValueKind == JsonValueKind.Object ? properties.Clone() : JsonSerializer.SerializeToElement(new { }), "pending");
        Write(entry);
        var pixels = image.Copy();
        var png = Task.Run(() =>
        {
            byte[] encoded = DirtyMatrixPaster.ClipboardImageCodec.EncodePng(pixels.Width, pixels.Height, pixels.Bytes);
            string temporary = entry.ImagePath + ".part";
            File.WriteAllBytes(temporary, encoded);
            File.Move(temporary, entry.ImagePath, overwrite: true);
        });
        return (entry, png);
    }

    internal static RecycleEntry SetOutcome(RecycleEntry entry, string outcome)
    {
        var updated = entry with { Outcome = outcome };
        Write(updated);
        return updated;
    }

    internal static void Delete(RecycleEntry entry)
    {
        try { System.IO.Directory.Delete(entry.Directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static IReadOnlyList<RecycleEntry> List(string root)
    {
        if (!System.IO.Directory.Exists(root)) return [];
        var entries = new List<RecycleEntry>();
        foreach (string directory in System.IO.Directory.EnumerateDirectories(root))
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, EntryName)));
                var r = document.RootElement;
                entries.Add(new(r.GetProperty("id").GetString()!, directory, r.GetProperty("created").GetDateTimeOffset(),
                    r.GetProperty("editKind").GetString()!, r.GetProperty("layerUuid").GetString()!, r.GetProperty("layerName").GetString()!,
                    r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), r.GetProperty("properties").Clone(),
                    r.GetProperty("outcome").GetString()!));
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        return entries.OrderByDescending(e => e.Created).ToArray();
    }

    /// <summary>Straight RGBA of the saved layer, for the clipboard (a full-canvas paste lands at the original position).</summary>
    internal static ImageRgba LoadImage(RecycleEntry entry)
    {
        using var stream = File.OpenRead(entry.ImagePath);
        using var bitmap = new Bitmap(stream);
        if (bitmap.Width != entry.Width || bitmap.Height != entry.Height) throw new InvalidDataException("回收箱图像尺寸与记录不一致。");
        var bytes = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int row = bitmap.Width * 4;
            for (int y = 0; y < bitmap.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * row, row);
        }
        finally { bitmap.UnlockBits(data); }
        for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        return new(bitmap.Width, bitmap.Height, bytes);
    }

    /// <summary>Readable CSP layer properties (Recognizer clipState), the known ones first.</summary>
    internal static IReadOnlyList<(string Name, string Value)> DescribeProperties(RecycleEntry entry)
    {
        var p = entry.Properties;
        string? Text(string key) => p.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(), JsonValueKind.True => "是", JsonValueKind.False => "否",
            JsonValueKind.Null => "—", _ => v.GetRawText()
        } : null;
        var known = new (string Key, string Label)[]
        {
            ("name", "图层名称"), ("opacity_percent", "不透明度 %"), ("blend_mode", "合成模式"), ("visible", "显示"),
            ("clipped", "用下一图层剪裁"), ("locked", "锁定"), ("draft", "草稿图层"), ("has_mask", "图层蒙版"),
            ("is_folder", "文件夹"), ("parent_id", "所属文件夹 ID"), ("depth", "层级深度"), ("offset_x", "偏移 X"), ("offset_y", "偏移 Y"),
        };
        var rows = new List<(string, string)> { ("图层 UUID", entry.LayerUuid), ("画布尺寸", $"{entry.Width}×{entry.Height}"),
            ("来源", entry.KindDisplay), ("时间", entry.Created.ToString("yyyy-MM-dd HH:mm:ss")), ("状态", entry.OutcomeDisplay) };
        foreach (var (key, label) in known) if (Text(key) is { } value) rows.Add((label, value));
        if (p.ValueKind == JsonValueKind.Object)
            foreach (var property in p.EnumerateObject().Where(o => known.All(k => k.Key != o.Name) && o.Name != "uuid"))
                rows.Add((property.Name, Text(property.Name) ?? ""));
        return rows;
    }

    private static void Write(RecycleEntry entry)
    {
        string path = Path.Combine(entry.Directory, EntryName), temporary = path + ".part";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            id = entry.Id, created = entry.Created, editKind = entry.EditKind, layerUuid = entry.LayerUuid, layerName = entry.LayerName,
            width = entry.Width, height = entry.Height, properties = entry.Properties, outcome = entry.Outcome
        }, Options), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
