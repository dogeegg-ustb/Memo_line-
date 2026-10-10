using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MemolineDemo;

public static partial class BundleArchive
{
    private const string EditSourcesPrefix = "edits/sources/";
    private sealed record ReplaySourceFile(string Path, EntryInfo Entry, MechanicalInfo Mechanical);

    private static bool IsEditSourceEntry(string entry) => Regex.IsMatch(entry,
        "^edits/sources/[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\.memoline$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<ReplaySourceFile> PrepareEditSources(IReadOnlyList<string>? paths,
        MechanicalInfo current, CancellationToken token)
    {
        var result = new List<ReplaySourceFile>();
        var sessions = new HashSet<string>(StringComparer.Ordinal) { current.SessionId };
        foreach (string input in paths ?? [])
        {
            token.ThrowIfCancellationRequested();
            string path = ExistingPath(input);
            var mechanical = ValidateMechanical(path, token);
            string entry = EditSourcesPrefix + mechanical.SessionId + ".memoline";
            if (!IsEditSourceEntry(entry) || !sessions.Add(mechanical.SessionId))
                throw new InvalidDataException("Unsafe, duplicate or current-session native edit source.");
            result.Add(new(path, FileEntry(path, entry, token), mechanical));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, MechanicalInfo> ReadEditSources(ZipArchive archive, ArchiveIndex index,
        MechanicalInfo current, CancellationToken token)
    {
        var sources = new Dictionary<string, MechanicalInfo>(StringComparer.Ordinal);
        foreach (var entry in index.EditSources)
        {
            token.ThrowIfCancellationRequested(); CheckEntry(archive, entry, token);
            string directory = Path.Combine(Path.GetTempPath(), "memoline-edit-source-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "recording.memoline");
            Directory.CreateDirectory(directory);
            try
            {
                using (var source = OpenEntry(archive, entry))
                using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    Copy(source, target, token);
                var mechanical = ValidateMechanical(path, token);
                if (entry.Entry != EditSourcesPrefix + mechanical.SessionId + ".memoline" ||
                    mechanical.SessionId == current.SessionId || !sources.TryAdd(mechanical.SessionId, mechanical))
                    throw new InvalidDataException("Native edit source metadata does not identify a unique historical recording.");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                Directory.Delete(directory);
            }
        }
        return sources;
    }
}
