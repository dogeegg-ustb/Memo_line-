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
        var replay = MemolineReplayReader.Parse(path, frames, allowEmpty: true);
        long boundaryTicks = frames.Select(f => OptionalLong(f, "ticks", -1)).Where(t => t >= 0).DefaultIfEmpty(0).Max();
        var packets = ReadPackets(Path.GetFullPath(diffRoot), sessionId, boundaryTicks, token);
        return new(path, sessionId, frequency, frames, replay, packets, boundaryAppendId, boundaryTicks)
        { History = StateHistory.Resolve(frames) };
    }

    private static IReadOnlyList<LayerPacket> ReadPackets(string root, string sessionId, long boundaryTicks, CancellationToken token)
    {
        string packetsRoot = Path.Combine(root, "packets");
        if (!Directory.Exists(packetsRoot)) return [];
        var result = new List<LayerPacket>();
        foreach (string manifestPath in Directory.EnumerateFiles(packetsRoot, "manifest.json", SearchOption.AllDirectories).Order())
        {
            token.ThrowIfCancellationRequested();
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
            if (trigger > boundaryTicks) continue;
            int width = checked((int)RequiredLong(descriptor, "width"));
            int height = checked((int)RequiredLong(descriptor, "height"));
            if (width <= 0 || height <= 0) throw new InvalidDataException("差异包尺寸无效。");
            string packetDir = Path.GetDirectoryName(manifestPath)!;
            var labels = ReadLabels(rootElement);
            var images = ReadImages(rootElement, packetDir, layer, id, width, height, labels, fullResolution, token);
            string? afterId = Text(after, "id"); string? nowId = Text(now, "id");
            bool baseline = Text(rootElement, "kind") == "baseline" || now.ValueKind != JsonValueKind.Object;
            bool boundary = Text(Get(rootElement, "capture"), "triggerKind") == "editBoundary";
            result.Add(new(id, layer, trigger, null, null, images, boundary, baseline,
                afterId, nowId, Text(after, "baselineSource") ?? Text(rootElement, "baselineSource"), width, height, fullResolution)
            { Labels = labels, AfterDescriptor = after.Clone(), NowDescriptor = now.ValueKind == JsonValueKind.Object ? now.Clone() : default });
        }
        foreach (var group in result.GroupBy(p => p.Layer).ToArray())
        {
            var latest = group.OrderBy(p => p.TriggerTicks).Last();
            int index = result.IndexOf(latest);
            result[index] = latest with { Current = LoadSnapshot(root, latest.NowDescriptor, latest.CanvasWidth, latest.CanvasHeight, token) };
        }
        return result.OrderBy(p => p.Layer.Uuid, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.TriggerTicks).ToArray();
    }

    private static IReadOnlyList<DirtyImage> ReadImages(JsonElement manifest, string packetDir, LayerKey layer,
        string packetId, int canvasWidth, int canvasHeight, IReadOnlyList<LabelEvidence> labels, bool fullResolution, CancellationToken token)
    {
        var labelsById = labels.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var result = new List<DirtyImage>();
        if (!manifest.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return result;
        foreach (var image in images.EnumerateArray())
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
            result.Add(new(key, mask, afterImage, nowImage, [], false,
                labelIds.Any(labelId => labelsById[labelId].Source == "low-resolution-fallback"), false, fullResolution)
            { LabelIds = labelIds });
        }
        return result;
    }

    private static ImageRgba? LoadSnapshot(string root, JsonElement descriptor, int width, int height, CancellationToken token)
    {
        if (descriptor.ValueKind != JsonValueKind.Object) return null;
        string? id = Text(descriptor, "id"); if (string.IsNullOrWhiteSpace(id)) return null;
        string path = PackagePath(Path.Combine(root, "snapshots"), id + ".png");
        if (!File.Exists(path)) return null;
        token.ThrowIfCancellationRequested();
        return LoadRgba(path, width, height);
    }

    private static ImageRgba LoadRgba(string path, int expectedWidth, int expectedHeight)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("差异图片不存在", path);
        // Check the actual encoded format before GDI+ can silently convert RGB,
        // grayscale or palette PNGs into the requested RGBA LockBits format.
        var header = new byte[33];
        try
        {
            using var stream = File.OpenRead(path);
            stream.ReadExactly(header);
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
        using var source = new Bitmap(path);
        if (source.Width != expectedWidth || source.Height != expectedHeight)
            throw new InvalidDataException($"PNG 尺寸与 descriptor 不一致：{path}");
        var bytes = new byte[checked(source.Width * source.Height * 4)];
        var locked = source.LockBits(new(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[checked(source.Width * 4)];
            for (int y = 0; y < source.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * locked.Stride, row, 0, row.Length);
                for (int x = 0; x < source.Width; x++)
                { int i = (y * source.Width + x) * 4, s = x * 4; bytes[i] = row[s + 2]; bytes[i + 1] = row[s + 1]; bytes[i + 2] = row[s]; bytes[i + 3] = row[s + 3]; }
            }
        }
        finally { source.UnlockBits(locked); }
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
                label.GetProperty("imageIds").EnumerateArray().Select(i => i.GetString()!).ToArray()));
        }
        return result;
    }
}
