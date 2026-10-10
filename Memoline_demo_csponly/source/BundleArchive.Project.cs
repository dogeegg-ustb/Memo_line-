using System.IO.Compression;
using System.Security.Cryptography;
using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

public static partial class BundleArchive
{
    public const string ProjectSchema = "memoline-csponly/v4";

    public static string? ReadProjectPath(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadIndex(zip).ProjectPath;
    }

    /// <summary>The CLIP path recorded by the first initializationConfiguration frame.
    /// Identity lookup only: decodes a bounded prefix and does not verify the container.</summary>
    public static string? ReadRecordedClipPath(string path, long prefixBytes = 32L << 20)
    {
        using var zip = ZipFile.OpenRead(path);
        var index = ReadIndex(zip);
        if (index.ProjectPath is { } project) return project;
        string directory = Path.Combine(Path.GetTempPath(), "memoline-identity-" + Guid.NewGuid().ToString("N"));
        // A .part name makes the reader stop cleanly at the truncated tail of the prefix.
        string temporary = Path.Combine(directory, "recording.memoline.part");
        Directory.CreateDirectory(directory);
        try
        {
            using (var source = OpenEntry(zip, index.Mechanical))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long copied = 0; int read;
                while (copied < prefixBytes && (read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, prefixBytes - copied))) > 0)
                { target.Write(buffer, 0, read); copied += read; }
            }
            static string? Optional(System.Text.Json.JsonElement value, string key) =>
                value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty(key, out var child) &&
                child.ValueKind == System.Text.Json.JsonValueKind.String ? child.GetString() : null;
            foreach (var frame in MemolineReader.Read(temporary))
                if (Optional(frame, "kind") == "initializationConfiguration" &&
                    frame.TryGetProperty("data", out var data) && Optional(data, "clipPath") is { Length: > 0 } clip)
                    return clip;
            return null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); Directory.Delete(directory); }
    }

    // Each native session retains its original clock, append IDs, fixed packets
    // and journal. Historical containers are flat entries, never nested chains.
    private static void VerifyPreviousSessions(ZipArchive archive, ArchiveIndex index, CancellationToken token)
    {
        var sessions = new HashSet<string>(StringComparer.Ordinal) { index.SessionId };
        foreach (var entry in index.PreviousSessions)
        {
            token.ThrowIfCancellationRequested(); CheckEntry(archive, entry, token);
            string temp = Path.Combine(Path.GetTempPath(), "memoline-history-" + Guid.NewGuid().ToString("N") + ".memoline");
            try
            {
                using (var source = RequiredEntry(archive, entry.Entry).Open())
                using (var target = File.Create(temp)) Copy(source, target, token);
                using (var prior = ZipFile.OpenRead(temp))
                    if (ReadIndex(prior).PreviousSessions.Length != 0) throw new InvalidDataException("Historical sessions must not contain nested history.");
                var verified = Verify(temp, token);
                if (!sessions.Add(verified.SessionId)) throw new InvalidDataException("Duplicate native session in project recording.");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    /// <summary>Validated atomic publication to the project's existing path. Failure retains both inputs.</summary>
    public static void AppendProjectSession(string destination, string sessionBundle, string clipPath, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination); sessionBundle = ExistingPath(sessionBundle); clipPath = Path.GetFullPath(clipPath);
        if (destination.Equals(sessionBundle, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Session input must differ from project output.");
        Verify(sessionBundle, token);
        using var fresh = ZipFile.OpenRead(sessionBundle);
        var freshIndex = ReadIndex(fresh);
        if (freshIndex.PreviousSessions.Length != 0) throw new InvalidDataException("Append requires one native session.");
        string stage = destination + ".pending-" + Guid.NewGuid().ToString("N");
        string priorTemp = stage + ".prior";
        string? expectedHash = null;
        try
        {
            var previous = new List<EntryInfo>();
            ZipArchive? old = null;
            FileStream? lease = null;
            try
            {
                if (File.Exists(destination))
                {
                    // Hold a read-only lease that denies writers throughout staging.
                    lease = new(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                    expectedHash = Convert.ToHexString(SHA256.HashData(lease)); lease.Position = 0;
                    Verify(destination, token);
                    old = new(lease, ZipArchiveMode.Read, leaveOpen: true);
                    var oldIndex = ReadIndex(old);
                    if (oldIndex.ProjectPath is { } project && !Path.GetFullPath(project).Equals(clipPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Existing recording belongs to another CLIP project.");
                    if (oldIndex.SessionId == freshIndex.SessionId || oldIndex.PreviousSessions.Any(e => e.Entry == "sessions/"+freshIndex.Mechanical.Sha256.ToLowerInvariant()+".memoline"))
                        throw new InvalidDataException("Native session was already appended.");
                    // Strip history only from this private copy of the preceding active session.
                    var priorIndex = oldIndex with { Schema = oldIndex.Edits is null ? Schema : EditSchema, PreviousSessions = [] };
                    WriteArchive(priorTemp, priorIndex, target => CopyEntries(old, target, oldIndex.PreviousSessions.Select(e => e.Entry).ToHashSet()), token);
                    string id = "sessions/" + oldIndex.Mechanical.Sha256.ToLowerInvariant() + ".memoline";
                    previous.AddRange(oldIndex.PreviousSessions); previous.Add(FileEntry(priorTemp, id, token) with { Codec = null });
                }
                var combined = freshIndex with { Schema = ProjectSchema, ProjectPath = clipPath, PreviousSessions = previous.ToArray() };
                WriteArchive(stage, combined, target =>
                {
                    CopyEntries(fresh, target, new HashSet<string>());
                    if (old is not null)
                        foreach (var info in previous.Take(previous.Count-1))
                        { using var read = RequiredEntry(old, info.Entry).Open(); using var write = target.CreateEntry(info.Entry, CompressionLevel.NoCompression).Open(); Copy(read, write, token); }
                    if (previous.Count > 0)
                    { using var read = File.OpenRead(priorTemp); using var write = target.CreateEntry(previous[^1].Entry, CompressionLevel.NoCompression).Open(); Copy(read, write, token); }
                }, token);
            }
            finally { old?.Dispose(); lease?.Dispose(); }
            token.ThrowIfCancellationRequested();
            if (expectedHash is null) File.Move(stage, destination);
            else
            {
                using (var check = File.OpenRead(destination))
                    if (Convert.ToHexString(SHA256.HashData(check)) != expectedHash) throw new IOException("Project recording changed during append; retry sealing.");
                // File.Replace atomically publishes on the same volume.
                File.Replace(stage, destination, null);
            }
        }
        finally { if (File.Exists(stage)) File.Delete(stage); if (File.Exists(priorTemp)) File.Delete(priorTemp); }
    }

    private static void CopyEntries(ZipArchive source, ZipArchive target, IReadOnlySet<string> excluded)
    {
        foreach (var entry in source.Entries.Where(e => e.FullName != "index.json" && !excluded.Contains(e.FullName)))
        { using var read = entry.Open(); using var write = target.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open(); read.CopyTo(write); }
    }

    /// <summary>Like <see cref="ExtractProjectSessions"/>, but the newest session is read in place from the
    /// verified project file instead of being rewritten into a second archive (which is verified again).</summary>
    internal static IReadOnlyList<string> ProjectSessionSources(string path, string directory, CancellationToken token = default)
    {
        path = ExistingPath(path);
        Verify(path, token);
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var zip = ZipFile.OpenRead(path); var index = ReadIndex(zip); var result = new List<string>();
        foreach (var info in index.PreviousSessions)
        {
            token.ThrowIfCancellationRequested(); string targetPath = Path.Combine(directory, result.Count + ".memoline");
            using var source = RequiredEntry(zip, info.Entry).Open(); using var output = new FileStream(targetPath, FileMode.CreateNew);
            Copy(source, output, token); result.Add(targetPath);
        }
        result.Add(path); return result;
    }

    /// <summary>Extracts immutable session containers in chronological append order into a new owned directory.</summary>
    public static IReadOnlyList<string> ExtractProjectSessions(string path, string directory, CancellationToken token = default)
    {
        Verify(path, token);
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var zip = ZipFile.OpenRead(path); var index = ReadIndex(zip); var result = new List<string>();
        foreach (var info in index.PreviousSessions)
        {
            token.ThrowIfCancellationRequested(); string targetPath = Path.Combine(directory, result.Count + ".memoline");
            using var source = RequiredEntry(zip, info.Entry).Open(); using var output = new FileStream(targetPath, FileMode.CreateNew);
            Copy(source, output, token); result.Add(targetPath);
        }
        string current = Path.Combine(directory, result.Count + ".memoline");
        WriteArchive(current, index with { Schema = index.Edits is null ? Schema : EditSchema, PreviousSessions = [] },
            target => CopyEntries(zip, target, index.PreviousSessions.Select(e => e.Entry).ToHashSet()), token);
        result.Add(current); return result;
    }
}
