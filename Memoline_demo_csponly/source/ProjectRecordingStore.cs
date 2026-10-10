using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace MemolineDemo;

internal static class ProjectRecordingStore
{
    internal static string Resolve(string directory, string clipPath)
    {
        directory = Path.GetFullPath(directory); clipPath = Path.GetFullPath(clipPath);
        Directory.CreateDirectory(directory);
        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.memoline"))
        {
            string? project;
            try
            {
                // Adopt prior v1-v3 recordings only with their recorded CLIP identity. This runs for every
                // recording at start-up, so it reads only the stream prefix; the chosen file is verified when used.
                project = BundleArchive.ReadRecordedClipPath(file);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException) { continue; }
            if (project is not null && Path.GetFullPath(project).Equals(clipPath,StringComparison.OrdinalIgnoreCase)) matches.Add(file);
        }
        if (matches.Count > 0)
            return matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
        string name = Path.GetFileNameWithoutExtension(clipPath);
        string target = Path.Combine(directory,name+".memoline");
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        // Different projects may share a filename. Derive a stable second path
        // from the full CLIP path, never a new counter for each recording.
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clipPath.ToUpperInvariant())))[..12].ToLowerInvariant();
        target = Path.Combine(directory,name+"-"+identity+".memoline");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("工程记录已存在但身份无法确认，请先检查文件："+target);
        return target;
    }
    private static string? Text(JsonElement value,string key) => value.ValueKind==JsonValueKind.Object && value.TryGetProperty(key,out var child) && child.ValueKind==JsonValueKind.String ? child.GetString() : null;
}
