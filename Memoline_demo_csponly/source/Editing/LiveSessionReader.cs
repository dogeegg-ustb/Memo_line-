using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using StrokeReplay;
using PacketReplay;
using System.Buffers.Binary;

namespace MemolineDemo.Editing;

internal static class LiveSessionReader
{
    internal static async Task<SessionSnapshot> ReadAsync(string mechanicalPartPath, string diffRoot,
        ulong boundaryAppendId, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Read(mechanicalPartPath, diffRoot, boundaryAppendId, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static SessionSnapshot Read(string mechanicalPath, string diffRoot, ulong boundaryAppendId,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(mechanicalPath);
        var prefix = new List<JsonElement>();
        ulong previousAppend = 0;
        using var readFrames = EditingProfiler.Stage("  mechanical frames + replay + packets");
        foreach (var frame in MemolineReader.Read(path))
        {
            token.ThrowIfCancellationRequested();
            if (frame.TryGetProperty("appendId", out var appended))
            {
                ulong append = RequiredUlong(appended);
                if (append != previousAppend + 1) throw new InvalidDataException("memoline appendId 未按文件顺序连续。");
                if (boundaryAppendId > 0 && append > boundaryAppendId) break;
                previousAppend = append;
            }
            if (Text(frame, "kind") is not ("screenshotBlob" or "mouseCursorChanged")) prefix.Add(frame);
            if (boundaryAppendId > 0 && previousAppend == boundaryAppendId) break;
        }
        var all = prefix.ToArray();
        var headers = all.Where(f => Text(f, "kind") == "header").ToArray();
        if (headers.Length != 1) throw new InvalidDataException("memoline 必须且只能包含一个 header。");
        var header = headers[0];
        string sessionId = RequiredText(header, "sessionId");
        long frequency = RequiredLong(header, "frequency");
        if (frequency <= 0) throw new InvalidDataException("memoline frequency 必须为正数。");
        foreach (var frame in all)
        {
            token.ThrowIfCancellationRequested();
            if (Text(frame, "sessionId") is { } frameSession && frameSession != sessionId)
                throw new InvalidDataException("memoline 帧的 sessionId 与 header 不一致。");
        }
        if (boundaryAppendId > previousAppend) throw new InvalidDataException("编辑边界尚未刷入机械流。");
        if (boundaryAppendId == 0) boundaryAppendId = previousAppend;
        var frames = all.Where(f => !f.TryGetProperty("appendId", out var a) || RequiredUlong(a) <= boundaryAppendId).ToArray();
        if (!frames.Any(f => Text(f, "kind") == "header")) frames = new[] { header }.Concat(frames).ToArray();
        MemolineReplayDocument replay;
        using (EditingProfiler.Stage("  replay parse")) replay = MemolineReplayReader.Parse(path, frames, allowEmpty: true);
        long boundaryTicks = frames.Select(f => OptionalLong(f, "ticks", -1)).Where(t => t >= 0).DefaultIfEmpty(0).Max();
        IReadOnlyList<LayerPacket> packets;
        using (EditingProfiler.Stage("  read packets")) packets = ReadPackets(Path.GetFullPath(diffRoot), sessionId, boundaryTicks, token);
        // The watcher keeps only the last two full captures per layer, so a live session's starting
        // capture is often deleted. A complete diff chain rebuilds it exactly from the current capture.
        if (packets.GroupBy(p => p.Layer).Any(g => g.OrderBy(p => p.TriggerTicks).First().Before is null))
        {
            var restored = packets.ToList();
            ProjectSessionReader.RestoreBaselines(restored, token);
            packets = restored;
        }
        IReadOnlyList<HistorySlot> history;
        using (EditingProfiler.Stage("  state history")) history = StateHistory.Resolve(frames);
        return new(path, sessionId, frequency, frames, replay, packets, boundaryAppendId, boundaryTicks)
        { History = history };
    }

    private static IReadOnlyList<LayerPacket> ReadPackets(string root, string sessionId, long boundaryTicks, CancellationToken token)
    {
        string packetsRoot = Path.Combine(root, "packets");
        if (!Directory.Exists(packetsRoot)) return [];
        var result = new List<LayerPacket>();
        foreach (string manifestPath in Directory.EnumerateFiles(packetsRoot, "manifest.json", SearchOption.AllDirectories).Order())
        {
            token.ThrowIfCancellationRequested();
            var packet = CachedPacket(manifestPath, sessionId, token);
            if (packet.TriggerTicks <= boundaryTicks) result.Add(packet);
        }
        using var snapshots = EditingProfiler.Stage("    full-canvas snapshots");
        foreach (var group in result.GroupBy(p => p.Layer).ToArray())
        {
            var first = group.OrderBy(p => p.TriggerTicks).First();
            var latest = group.OrderBy(p => p.TriggerTicks).Last();
            // Resolve both positions before replacing either record. A one-packet
            // layer uses the same slot for its baseline and its current snapshot.
            int firstIndex = result.IndexOf(first);
            int index = result.IndexOf(latest);
            var currentDescriptor = latest.NowDescriptor.ValueKind == JsonValueKind.Object ? latest.NowDescriptor : latest.AfterDescriptor;
            var before = Task.Run(() => LoadSnapshot(root, first.AfterDescriptor, first.CanvasWidth, first.CanvasHeight, token), token);
            var current = LoadSnapshot(root, currentDescriptor, latest.CanvasWidth, latest.CanvasHeight, token);
            result[firstIndex] = first with { Before = before.GetAwaiter().GetResult() };
            result[index] = result[index] with { Current = current };
        }
        return result.OrderBy(p => p.Layer.Uuid, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.TriggerTicks).ToArray();
    }

    // Committed packets never change. Alt+M re-reads the same directory every time, so decoded
    // packets are kept per manifest file identity and only new packets are decoded again.
    private static readonly Dictionary<(string Path, string Session), (DateTime Written, long Length, LayerPacket Packet)> PacketCache = [];
    private static readonly Dictionary<string, (DateTime Written, long Length, ImageRgba Image)> SnapshotCache = new(StringComparer.OrdinalIgnoreCase);
    private const int SnapshotCacheLimit = 4;

    private static LayerPacket CachedPacket(string manifestPath, string sessionId, CancellationToken token)
    {
        var info = new FileInfo(manifestPath);
        // The identity covers the manifest and every image beside it.
        var files = info.Directory!.GetFiles();
        var written = files.Max(f => f.LastWriteTimeUtc);
        long length = files.Sum(f => f.Length) ^ ((long)files.Length << 48);
        lock (PacketCache)
            if (PacketCache.TryGetValue((info.FullName, sessionId), out var hit) && hit.Written == written && hit.Length == length)
                return hit.Packet;
        var packet = ReadPacket(manifestPath, sessionId, token);
        lock (PacketCache) PacketCache[(info.FullName, sessionId)] = (written, length, packet);
        return packet;
    }

    private static LayerPacket ReadPacket(string manifestPath, string sessionId, CancellationToken token)
    {
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var rootElement = document.RootElement;
            if (Text(rootElement, "schema") != "dirty-matrix-image-diff/v1")
                throw new InvalidDataException($"不支持的差异包 schema：{manifestPath}");
            string? comparison = Text(rootElement, "comparison");
            bool fullResolution = comparison == "fullResolution/v2";
            var after = rootElement.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            var now = rootElement.TryGetProperty("now", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
            var descriptor = after.ValueKind == JsonValueKind.Object ? after : now;
            if (descriptor.ValueKind != JsonValueKind.Object) throw new InvalidDataException("差异包缺少 after/now descriptor。");
            string id = RequiredText(rootElement, "id");
            var layer = ParseLayer(descriptor, sessionId);
            long trigger = OptionalLong(now.ValueKind == JsonValueKind.Object ? now : descriptor, "triggerTicks", -1);
            if (trigger < 0) trigger = OptionalLong(descriptor, "triggerTicks", -1);
            if (trigger < 0) throw new InvalidDataException("差异包缺少 triggerTicks。");
            int width = checked((int)RequiredLong(descriptor, "width"));
            int height = checked((int)RequiredLong(descriptor, "height"));
            if (width <= 0 || height <= 0) throw new InvalidDataException("差异包尺寸无效。");
            string packetDir = Path.GetDirectoryName(manifestPath)!;
            var labels = ReadLabels(rootElement);
            if (labels.Any(l => l.CoverageSettings is { } recipe &&
                (recipe.CanvasWidth != width || recipe.CanvasHeight != height)))
                throw new InvalidDataException("标签 frozen CoverageSettings 与差异包画布尺寸不一致。");
            var images = ReadImages(rootElement, packetDir, layer, id, width, height, labels, fullResolution, token);
            string? afterId = Text(after, "id"); string? nowId = Text(now, "id");
            bool baseline = Text(rootElement, "kind") == "baseline" || now.ValueKind != JsonValueKind.Object;
            bool boundary = Text(Get(rootElement, "capture"), "triggerKind") is "editBoundary" or "recordingEndLayer";
            return new(id, layer, trigger, null, null, images, boundary, baseline,
                afterId, nowId, Text(after, "baselineSource") ?? Text(rootElement, "baselineSource"), width, height, fullResolution)
            { Labels = labels, AfterDescriptor = after.Clone(), NowDescriptor = now.ValueKind == JsonValueKind.Object ? now.Clone() : default };
        }
    }

    private static IReadOnlyList<DirtyImage> ReadImages(JsonElement manifest, string packetDir, LayerKey layer,
        string packetId, int canvasWidth, int canvasHeight, IReadOnlyList<LabelEvidence> labels, bool fullResolution, CancellationToken token)
    {
        var labelsById = labels.ToDictionary(l => l.Id, StringComparer.Ordinal);
        if (!manifest.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return [];
        var entries = images.EnumerateArray().ToArray();
        var result = new DirtyImage[entries.Length];
        // PNG decoding dominates history loading; images of one packet decode independently.
        try { Parallel.For(0, entries.Length, new ParallelOptions { CancellationToken = token }, i => result[i] = ReadImage(entries[i])); }
        catch (AggregateException error) when (error.InnerExceptions.Count > 0)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw(); }
        return result;

        DirtyImage ReadImage(JsonElement image)
        {
            token.ThrowIfCancellationRequested();
            string imageId = RequiredText(image, "id");
            var bounds = ParseBounds(image, canvasWidth, canvasHeight);
            string maskName = Text(image, "maskImage") ?? throw new InvalidDataException("差异图片缺少 maskImage。");
            string afterName = Text(image, "afterImage") ?? throw new InvalidDataException("差异图片缺少 afterImage。");
            string nowName = Text(image, "nowImage") ?? Text(image, "image") ?? throw new InvalidDataException("差异图片缺少 nowImage/image。");
            var maskBytes = LoadRgba(PackagePath(packetDir, maskName), bounds.Width, bounds.Height).Bytes;
            var mask = new PixelMask(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            long changed = 0;
            for (int y = 0; y < bounds.Height; y++) for (int x = 0; x < bounds.Width; x++)
            {
                int i = (y * bounds.Width + x) * 4;
                if (maskBytes[i + 3] == 0) continue;
                if (maskBytes[i] != 255 || maskBytes[i + 1] != 255 || maskBytes[i + 2] != 255 || maskBytes[i + 3] != 255)
                    throw new InvalidDataException($"mask {imageId} 必须以不透明白色标记变化。");
                mask.Set(bounds.Left + x, bounds.Top + y); changed++;
            }
            long declared = OptionalLong(image, "changedPixels", -1);
            if (declared <= 0 || declared != changed) throw new InvalidDataException($"图片 {imageId} changedPixels 与 mask 不一致。");
            if (nowName == Text(manifest, "canvasPreviewImage") || nowName == Text(image, "differenceImage") || nowName == afterName)
                throw new InvalidDataException("主图不能使用预览、difference 或 after 图片。");
            var afterImage = LoadRgba(PackagePath(packetDir, afterName), bounds.Width, bounds.Height);
            var nowImage = LoadRgba(PackagePath(packetDir, nowName), bounds.Width, bounds.Height);
            var key = new DirtyImageKey(layer, packetId, imageId);
            var labelIds = image.GetProperty("labelIds").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (labelIds.Length == 0 || labelIds.Any(labelId => !labelsById.TryGetValue(labelId, out var label) || !label.ImageIds.Contains(imageId)))
                throw new InvalidDataException("图片与标签双向关联不一致。");
            return new(key, mask, afterImage, nowImage, [], false,
                labelIds.Any(labelId => labelsById[labelId].Source == "low-resolution-fallback"), false, fullResolution)
            { LabelIds = labelIds };
        }
    }

    internal static ImageRgba? LoadSnapshot(string root, JsonElement descriptor, int width, int height, CancellationToken token)
    {
        if (descriptor.ValueKind != JsonValueKind.Object) return null;
        string? id = Text(descriptor, "id"); if (string.IsNullOrWhiteSpace(id)) return null;
        string path = PackagePath(Path.Combine(root, "snapshots"), id + ".png");
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        token.ThrowIfCancellationRequested();
        lock (SnapshotCache)
            if (SnapshotCache.TryGetValue(info.FullName, out var hit) && hit.Written == info.LastWriteTimeUtc &&
                hit.Length == info.Length && hit.Image.Width == width && hit.Image.Height == height)
                return hit.Image.Copy();
        var image = LoadRgba(path, width, height);
        lock (SnapshotCache)
        {
            if (SnapshotCache.Count >= SnapshotCacheLimit) SnapshotCache.Remove(SnapshotCache.Keys.First());
            SnapshotCache[info.FullName] = (info.LastWriteTimeUtc, info.Length, image.Copy());
        }
        return image;
    }

    private static ImageRgba LoadRgba(string path, int expectedWidth, int expectedHeight)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("差异图片不存在", path);
        // Check the actual encoded format before GDI+ can silently convert RGB,
        // grayscale or palette PNGs into the requested RGBA LockBits format.
        var header = new byte[33];
        using var input = File.OpenRead(path);
        try
        {
            input.ReadExactly(header);
        }
        catch (EndOfStreamException error)
        { throw new InvalidDataException("PNG 的 IHDR 数据不完整：" + path, error); }
        if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) != 13 || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("PNG 缺少合法的首个 IHDR 数据块：" + path);
        if (header[24] != 8 || header[25] != 6)
            throw new InvalidDataException("只支持原尺寸 8 位 RGBA PNG 图像：" + path);
        if (header[26] != 0 || header[27] != 0 || header[28] > 1)
            throw new InvalidDataException("PNG 的压缩、过滤或交错方法无效：" + path);
        if (expectedWidth <= 0 || expectedHeight <= 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)) != (uint)expectedWidth ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)) != (uint)expectedHeight)
            throw new InvalidDataException("PNG 的 IHDR 尺寸与 descriptor 不一致：" + path);
        // GDI+ filename decoding observes the EXE's native MAX_PATH policy.
        // Project-history caches exceed that limit even when managed file IO works.
        // Keep the managed stream alive until GDI+ has finished reading the pixels.
        input.Position = 0;
        using var source = new Bitmap(input);
        if (source.Width != expectedWidth || source.Height != expectedHeight)
            throw new InvalidDataException($"PNG 尺寸与 descriptor 不一致：{path}");
        var bytes = new byte[checked(source.Width * source.Height * 4)];
        var locked = source.LockBits(new(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = checked(source.Width * 4);
            if (locked.Stride == rowBytes) System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, bytes, 0, bytes.Length);
            else
                for (int y = 0; y < source.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * locked.Stride, bytes, y * rowBytes, rowBytes);
        }
        finally { source.UnlockBits(locked); }
        // BGRA (little-endian 0xAARRGGBB) to RGBA: swap the R and B bytes of every pixel.
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        for (int i = 0; i < pixels.Length; i++)
        {
            uint v = pixels[i];
            pixels[i] = (v & 0xFF00FF00u) | ((v >> 16) & 0xFFu) | ((v & 0xFFu) << 16);
        }
        return new(source.Width, source.Height, bytes);
    }

    private static LayerKey ParseLayer(JsonElement descriptor, string sessionId)
    {
        var layer = descriptor.TryGetProperty("layer", out var l) ? l : default;
        string uuid = Text(layer, "uuid") ?? throw new InvalidDataException("差异包 layer.uuid 缺失。");
        long generation = RequiredLong(descriptor, "generation");
        if (generation < 0) throw new InvalidDataException("差异包 generation 无效。");
        string descriptorSession = RequiredText(descriptor, "sessionId");
        if (descriptorSession != sessionId) throw new InvalidDataException("差异包 sessionId 与机械流不一致。");
        return new LayerKey(sessionId, generation, uuid);
    }

    private static (int Left, int Top, int Width, int Height) ParseBounds(JsonElement image, int width, int height)
    {
        var b = image.TryGetProperty("bounds", out var bounds) ? bounds : throw new InvalidDataException("差异图片缺少 bounds。");
        int left = checked((int)RequiredLong(bounds, "left")), top = checked((int)RequiredLong(bounds, "top"));
        int right = checked((int)RequiredLong(bounds, "right")), bottom = checked((int)RequiredLong(bounds, "bottom"));
        int bw = checked((int)RequiredLong(bounds, "width")), bh = checked((int)RequiredLong(bounds, "height"));
        if (bw != right - left || bh != bottom - top || left < 0 || top < 0 || right > width || bottom > height || bw <= 0 || bh <= 0)
            throw new InvalidDataException("差异图片 bounds 越界或尺寸不一致。");
        return (left, top, bw, bh);
    }
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string RequiredText(JsonElement e, string key) => Text(e, key) is { } s && s.Length > 0 ? s : throw new InvalidDataException($"缺少 {key}。");
    private static ulong RequiredUlong(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? RequiredUlong(v) : throw new InvalidDataException($"缺少 {key}。");
    private static ulong RequiredUlong(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out var n) ? n : throw new InvalidDataException("数值不是有效的 ulong。");
    private static long RequiredLong(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : throw new InvalidDataException($"缺少有效的 {key}。");
    private static long OptionalLong(JsonElement e, string key, long fallback) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : fallback;
    private static long? Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    private static string PackagePath(string directory, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("PNG 必须使用包内相对路径。");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PNG 路径越过差异包。");
        return path;
    }
    private static IReadOnlyList<LabelEvidence> ReadLabels(JsonElement manifest)
    {
        var result = new List<LabelEvidence>();
        foreach (var label in manifest.GetProperty("labels").EnumerateArray())
        {
            var coverage = Get(label, "coverage");
            var segments = new List<CoverageSegment>();
            if (Get(coverage, "segments") is { ValueKind: JsonValueKind.Array } points)
                foreach (var segment in points.EnumerateArray())
                {
                    var start = segment.GetProperty("start"); var end = segment.GetProperty("end");
                    segments.Add(new(start.GetProperty("x").GetDouble(), start.GetProperty("y").GetDouble(),
                        end.GetProperty("x").GetDouble(), end.GetProperty("y").GetDouble()));
                }
            ulong? operation = Get(label, "operationId") is { ValueKind: JsonValueKind.Number } op && op.TryGetUInt64(out var value) ? value : null;
            double radius = Get(coverage, "radius") is { ValueKind: JsonValueKind.Number } r ? r.GetDouble() : 0;
            if (!double.IsFinite(radius) || radius < 0 || segments.Any(s => !double.IsFinite(s.StartX) || !double.IsFinite(s.StartY) || !double.IsFinite(s.EndX) || !double.IsFinite(s.EndY)))
                throw new InvalidDataException("标签覆盖范围不是有效的画布像素几何。");
            result.Add(new(RequiredText(label, "id"), operation, RequiredText(label, "source"),
                OptionalLong(label, "fromTicks", 0), OptionalLong(label, "toTicks", 0), radius, segments,
                label.GetProperty("imageIds").EnumerateArray().Select(i => i.GetString()!).ToArray())
                { CoverageSettings = ReadCoverageSettings(label, radius) });
        }
        return result;
    }

    private static DirtyMatrix.Core.CoverageSettings? ReadCoverageSettings(JsonElement label, double radius)
    {
        var recipe = Get(label, "coverageSettings");
        if (recipe.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (recipe.ValueKind != JsonValueKind.Object) throw new InvalidDataException("标签 coverageSettings 不是对象。");
        string[] required = ["canvasWidth", "canvasHeight", "brushSize", "brushUnit", "dpi", "zoomPercent",
            "specifyBySizeOnScreen", "brushEnvelopeFactor", "antiAliasRadius", "safetyRadius", "coveragePaddingScreenPixels"];
        if (required.Any(field => Get(recipe, field).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
            throw new InvalidDataException("标签 coverageSettings 缺少冻结半径计算字段，不能使用默认值补齐。");
        try
        {
            var settings = recipe.Deserialize<DirtyMatrix.Core.CoverageSettings>(CanvasLayerWatcher.SnapshotHistory.Json)
                ?? throw new InvalidDataException("标签 coverageSettings 为空。");
            settings.Validate();
            if (Math.Abs(settings.Radius - radius) > 1e-8 * Math.Max(1, Math.Abs(radius)))
                throw new InvalidDataException("标签 coverageSettings 与原始覆盖半径不一致。");
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("标签 coverageSettings 无效。", ex); }
    }
}
