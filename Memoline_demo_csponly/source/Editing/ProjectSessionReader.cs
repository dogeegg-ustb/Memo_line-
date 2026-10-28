using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemolineDemo.Editing;

/// <summary>Restores immutable native sessions and their embedded image assets into an owned cache.</summary>
internal static class ProjectSessionReader
{
    private const string CacheSchema = "memoline-project-cache/v1";
    private const string CacheManifest = "cache.json";

    /// <summary>
    /// The verified, extracted history of a project recording, kept beside it in "&lt;project&gt;.memoline.cache".
    /// It is keyed by the project file's size and write time: sealing a new session rewrites the file and
    /// the next read rebuilds the cache once. Only a complete build (cache.json written last) is reused.
    /// </summary>
    internal static IReadOnlyList<SessionSnapshot> ReadProjectCached(string projectPath, CancellationToken token = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        string root = CacheRoot(projectPath);
        string directory = Path.Combine(root, CacheStamp(projectPath));
        if (TryReadCache(directory, token) is { } cached) return cached;
        // Stale stamps and interrupted builds are never read again.
        if (Directory.Exists(root))
            foreach (string old in Directory.EnumerateDirectories(root))
                try { Directory.Delete(old, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return ReadProject(projectPath, directory, token);
    }

    internal static string CacheRoot(string projectPath) => Path.GetFullPath(projectPath) + ".cache";
    internal static string CacheStamp(string projectPath)
    {
        var info = new FileInfo(projectPath);
        return info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
            info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<SessionSnapshot>? TryReadCache(string directory, CancellationToken token)
    {
        string manifest = Path.Combine(directory, CacheManifest);
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (Text(document.RootElement, "schema") != CacheSchema) return null;
            using (EditingProfiler.Stage("read cached project"))
                return Array(document.RootElement, "sessions").Select(session => LoadSession(
                    PackagePath(directory, RequiredText(session, "directory")),
                    Array(session, "terminal").Select(t => t.GetString() ?? "").ToHashSet(StringComparer.Ordinal), token)).ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or KeyNotFoundException)
        { return null; }
    }

    internal static Task<IReadOnlyList<SessionSnapshot>> ReadProjectAsync(string projectPath, string ownedDirectory,
        CancellationToken token = default)
        => Task.Run(() => ReadProject(projectPath, ownedDirectory, token), token);

    internal static IReadOnlyList<SessionSnapshot> ReadProject(string projectPath, string ownedDirectory,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.GetFullPath(ownedDirectory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("工程历史缓存必须使用新的空目录。");
        Directory.CreateDirectory(directory);
        // This verifies every sealed session, native frame, packet pointer and asset hash
        // before creating the private per-session containers. Their append order is retained.
        IReadOnlyList<string> bundles;
        using (EditingProfiler.Stage("verify + split project"))
            bundles = BundleArchive.ProjectSessionSources(projectPath, Path.Combine(directory, "sessions"), token);
        var result = new List<SessionSnapshot>(bundles.Count);
        var entries = new List<object>(bundles.Count);
        foreach (string bundle in bundles)
        {
            token.ThrowIfCancellationRequested();
            string name = "session-" + result.Count;
            var (snapshot, terminal) = ReadVerifiedSession(bundle, Path.Combine(directory, name), token);
            result.Add(snapshot);
            entries.Add(new { directory = name, terminal = terminal.Order(StringComparer.Ordinal).ToArray() });
        }
        // The split containers are only needed for verification; the extracted sessions are the cache.
        try { Directory.Delete(Path.Combine(directory, "sessions"), recursive: true); } catch (IOException) { }
        File.WriteAllText(Path.Combine(directory, CacheManifest),
            JsonSerializer.Serialize(new { schema = CacheSchema, sessions = entries }), new UTF8Encoding(false));
        return result;
    }

    private static SessionSnapshot LoadSession(string directory, IReadOnlySet<string> terminalPackets, CancellationToken token)
    {
        SessionSnapshot snapshot;
        using (EditingProfiler.Stage("read session"))
            snapshot = LiveSessionReader.Read(Path.Combine(directory, "recording.memoline"), Path.Combine(directory, "diff"), 0, token);
        var packets = snapshot.Packets.Select(packet => packet with
        {
            // A validated successful recording-end capture is as usable as an edit
            // capture. Empty recording-end rows never turn an older snapshot into one.
            IsBoundary = packet.IsBoundary || terminalPackets.Contains(packet.PacketId)
        }).ToList();
        using (EditingProfiler.Stage("restore baselines")) RestoreBaselines(packets, token);
        // Sessions are read one after another; only the one being read stays decoded.
        var loaded = snapshot with { Packets = packets };
        EditingMemory.Park(loaded);
        EditingMemory.Collect();
        return loaded;
    }

    private static (SessionSnapshot Snapshot, HashSet<string> Terminal) ReadVerifiedSession(string bundle, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var zip = ZipFile.OpenRead(bundle);
        using var indexStream = RequiredEntry(zip, "index.json").Open();
        using var index = JsonDocument.Parse(indexStream);
        string session = RequiredText(index.RootElement, "sessionId");
        long frequency = RequiredLong(index.RootElement, "frequency");
        string mechanical = Path.Combine(directory, "recording.memoline");
        string aggregation = Path.Combine(directory, "events.jsonl");
        using (EditingProfiler.Stage("extract streams"))
        {
            ExtractEntry(zip, index.RootElement.GetProperty("mechanical"), mechanical, token);
            ExtractEntry(zip, index.RootElement.GetProperty("aggregation"), aggregation, token);
        }
        string diffRoot = Path.Combine(directory, "diff");
        HashSet<string> terminalPackets;
        using (EditingProfiler.Stage("restore packet assets"))
            terminalPackets = RestorePackets(aggregation, diffRoot, session, frequency, token);
        return (LoadSession(directory, terminalPackets, token), terminalPackets);
    }

    private sealed class Asset(string path, int count, string? totalHash, long? length) : IDisposable
    {
        internal readonly string Path = path;
        internal readonly int Count = count;
        internal readonly string? TotalHash = totalHash;
        internal readonly long? Length = length;
        internal int Next;
        internal readonly FileStream Output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        internal readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Dispose() { Output.Dispose(); Hash.Dispose(); }
    }

    private static HashSet<string> RestorePackets(string aggregation, string diffRoot, string session,
        long frequency, CancellationToken token)
    {
        string assetRoot = Path.Combine(diffRoot, "assets");
        Directory.CreateDirectory(assetRoot);
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var packets = new List<JsonElement>();
        var terminal = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var input = new StreamReader(aggregation, new UTF8Encoding(false, true));
            while (input.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var row = JsonDocument.Parse(line);
                var value = row.RootElement;
                if (Text(value, "sessionId") is { } rowSession && rowSession != session)
                    throw new InvalidDataException("封盘聚集流的 sessionId 与机械流不同。");
                switch (Text(value, "kind"))
                {
                    case "asset": RestoreAsset(value, assets, assetRoot); break;
                    case "packet" when Text(value, "status") is "captured" or "baseline": packets.Add(value.Clone()); break;
                }
            }
            foreach (var asset in assets.Values)
            {
                token.ThrowIfCancellationRequested();
                if (asset.Next != asset.Count) throw new InvalidDataException("封盘 PNG 资产分块不完整。");
                if (asset.Length is { } length && asset.Output.Length != length)
                    throw new InvalidDataException("封盘 PNG 资产总长度不一致。");
                string hash = Convert.ToHexString(asset.Hash.GetHashAndReset());
                if (asset.TotalHash is { } total && !HashEqual(hash, total))
                    throw new InvalidDataException("封盘 PNG 资产总 SHA256 不一致。");
                asset.Output.Dispose();
            }
            var manifests = new HashSet<string>(StringComparer.Ordinal);
            foreach (var packet in packets)
            {
                token.ThrowIfCancellationRequested();
                var manifest = Get(packet, "manifest");
                if (manifest.ValueKind != JsonValueKind.Object) throw new InvalidDataException("成功的封盘捕获缺少 manifest。");
                var recognizer = Get(manifest, "recognizer");
                if (Text(recognizer, "sessionId") != session || RequiredLong(recognizer, "frequency") != frequency ||
                    RequiredLong(recognizer, "toTicks") != RequiredLong(packet, "triggerTicks"))
                    throw new InvalidDataException("封盘 manifest 与原生会话时钟不一致。");
                string manifestId = RequiredText(manifest, "id");
                if (!manifests.Add(manifestId)) throw new InvalidDataException("封盘差异包 manifest.id 重复。");
                string packetRoot = PackagePath(Path.Combine(diffRoot, "packets"), manifestId);
                Directory.CreateDirectory(packetRoot);
                var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mapping in Array(packet, "imageAssets"))
                {
                    string relative = RequiredText(mapping, "path"), id = RequiredText(mapping, "assetId");
                    string target = PackagePath(packetRoot, relative);
                    if (!Path.GetExtension(target).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        !mappings.TryAdd(target, id)) throw new InvalidDataException("封盘差异包的 PNG 路径重复或无效。");
                    CopyAsset(assets, id, target, token);
                }
                if (Text(packet, "fullCanvasAssetId") is { } canvasAsset)
                {
                    var descriptor = Get(manifest, "now");
                    if (descriptor.ValueKind != JsonValueKind.Object) descriptor = Get(manifest, "after");
                    string snapshotId = RequiredText(descriptor, "id");
                    string target = PackagePath(Path.Combine(diffRoot, "snapshots"), snapshotId + ".png");
                    CopyAsset(assets, canvasAsset, target, token);
                }
                File.WriteAllText(Path.Combine(packetRoot, "manifest.json"), manifest.GetRawText(), new UTF8Encoding(false));
                if (Text(packet, "boundaryKind") is "recordingEnd" or "recordingEndLayer") terminal.Add(manifestId);
            }
            return terminal;
        }
        finally { foreach (var asset in assets.Values) asset.Dispose(); }
    }

    private static void RestoreAsset(JsonElement row, Dictionary<string, Asset> assets, string root)
    {
        string id = RequiredText(row, "assetId");
        int index = checked((int)OptionalLong(row, "chunkIndex", 0)), count = checked((int)OptionalLong(row, "chunkCount", 1));
        if (index < 0 || count <= 0 || index >= count) throw new InvalidDataException("封盘资产分块序号无效。");
        string? total = Text(row, "totalSha256");
        long? length = Get(row, "byteLength") is { ValueKind: JsonValueKind.Number } size ? size.GetInt64() : null;
        if (Text(row, "encoding") is { } encoding && encoding != "png") throw new InvalidDataException("只支持封盘 PNG 资产。");
        if (!assets.TryGetValue(id, out var asset))
        {
            string filename = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".png";
            assets.Add(id, asset = new Asset(Path.Combine(root, filename), count, total, length));
        }
        if (asset.Next != index || asset.Count != count || asset.TotalHash != total || asset.Length != length)
            throw new InvalidDataException("封盘 PNG 资产分块不连续或声明发生改变。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(RequiredText(row, "base64")); }
        catch (FormatException error) { throw new InvalidDataException("封盘 PNG 资产 base64 无效。", error); }
        if (!HashEqual(Convert.ToHexString(SHA256.HashData(bytes)), RequiredText(row, "sha256")))
            throw new InvalidDataException("封盘 PNG 资产分块 SHA256 不一致。");
        asset.Output.Write(bytes); asset.Hash.AppendData(bytes); asset.Next++;
    }

    private static void CopyAsset(Dictionary<string, Asset> assets, string id, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!assets.TryGetValue(id, out var asset) || asset.Next != asset.Count)
            throw new InvalidDataException("封盘差异包引用了不存在或不完整的 PNG 资产：" + id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            using var existing = File.OpenRead(target); using var source = File.OpenRead(asset.Path);
            if (!SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(source)))
                throw new InvalidDataException("同一个 snapshot.id 指向不同的 PNG 资产。");
            return;
        }
        File.Copy(asset.Path, target, overwrite: false);
    }

    internal static void RestoreBaselines(List<LayerPacket> packets, CancellationToken token)
    {
        // Full canvases can be large. Keep the final capture and one reconstructed
        // starting canvas per layer rather than retaining a full image for every packet.
        foreach (var group in packets.GroupBy(packet => packet.Layer))
        {
            var ordered = group.OrderBy(packet => packet.TriggerTicks).ToArray();
            var last = ordered[^1];
            // Preserve the independent original baseline when its full capture was
            // archived. Reverse reconstruction is only a fallback for older bundles.
            if (ordered[0].Before is not null) continue;
            if (last.Current is null || ordered.Any(packet => !packet.FullResolution)) continue;
            ImageRgba baseline = last.Current.Copy();
            bool valid = true;
            for (int i = ordered.Length - 1; i >= 0 && valid; i--)
            {
                token.ThrowIfCancellationRequested();
                var packet = ordered[i];
                if (packet.CanvasWidth != baseline.Width || packet.CanvasHeight != baseline.Height ||
                    i > 0 && !packet.IsRecordingBaseline && !ordered[i - 1].IsRecordingBaseline &&
                    packet.AfterSnapshotId != ordered[i - 1].NowSnapshotId)
                { valid = false; break; }
                foreach (var image in packet.Images)
                {
                    for (int y = image.Mask.Top; y < image.Mask.Top + image.Mask.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = image.Mask.Left; x < image.Mask.Left + image.Mask.Width; x++)
                        {
                            if (!image.Mask.Contains(x, y)) continue;
                            int canvas = (y * baseline.Width + x) * 4;
                            int patch = ((y - image.Mask.Top) * image.Mask.Width + x - image.Mask.Left) * 4;
                            if (!baseline.Bytes.AsSpan(canvas, 4).SequenceEqual(image.Now.Bytes.AsSpan(patch, 4)))
                            { valid = false; break; }
                            image.After.Bytes.AsSpan(patch, 4).CopyTo(baseline.Bytes.AsSpan(canvas, 4));
                        }
                        if (!valid) break;
                    }
                    if (!valid) break;
                }
            }
            // An inconsistent historical chain remains loaded for diagnosis, but it
            // receives no trusted starting pixels and cannot bridge to a live layer.
            if (valid)
            {
                int first = packets.IndexOf(ordered[0]);
                baseline.Park(); // One layer's reconstruction at a time, not every layer's, stays inflated.
                packets[first] = ordered[0] with { Before = baseline };
            }
            EditingMemory.Collect(); // The inflated reconstruction is garbage now, valid or not.
        }
    }

    private static void ExtractEntry(ZipArchive archive, JsonElement info, string target, CancellationToken token)
    {
        string entry = RequiredText(info, "entry");
        string? codec = Text(info, "codec");
        if (codec is not (null or "brotli")) throw new InvalidDataException("不支持的封盘存储 codec。");
        using var stored = RequiredEntry(archive, entry + (codec == "brotli" ? ".br" : "")).Open();
        using Stream decoded = codec == "brotli" ? new BrotliStream(stored, CompressionMode.Decompress, leaveOpen: true) : stored;
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = decoded.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
        }
        using var restored = File.OpenRead(target);
        if (restored.Length != RequiredLong(info, "size") ||
            !HashEqual(Convert.ToHexString(SHA256.HashData(restored)), RequiredText(info, "sha256")))
            throw new InvalidDataException("封盘流解压后的长度或 SHA256 不一致。");
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name)
        => archive.GetEntry(name) ?? throw new InvalidDataException("封盘文件缺少条目：" + name);
    private static string PackagePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
            throw new InvalidDataException("封盘资产必须使用包内相对路径。");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("封盘资产路径越过缓存目录。");
        return target;
    }
    private static bool HashEqual(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;
    private static string? Text(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static string RequiredText(JsonElement value, string key) => Text(value, key) is { Length: > 0 } text ? text : throw new InvalidDataException("缺少字段：" + key);
    private static long RequiredLong(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var result) ? result : throw new InvalidDataException("缺少有效数值：" + key);
    private static long OptionalLong(JsonElement value, string key, long fallback) => Get(value, key).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback : RequiredLong(value, key);
    private static IEnumerable<JsonElement> Array(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray() : [];
}
