using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using System.Drawing;
using System.IO.Compression;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class LiveReaderChecks
{
    internal static int Run(string root)
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { count++; return; } throw new InvalidOperationException(message); }
        static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
        var header = Json("""{"kind":"header","sessionId":"live","frequency":1000}""");
        var boundary = Json("""{"appendId":1,"kind":"editBoundaryRequested","ticks":20,"data":{}}""");
        var future = Json("""{"appendId":2,"kind":"editBoundaryRequested","ticks":30,"data":{}}""");
        string path = Path.Combine(root, "live-check.memoline.part");
        Write(path,[header,boundary,future]);
        var snapshot = LiveSessionReader.Read(path, root, 1);
        Check(snapshot.Frames.Count == 2 && snapshot.BoundaryTicks == 20, "Live reader returns exactly the requested boundary prefix.");
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) stream.Write(new byte[] { 1, 2, 3 });
        Check(LiveSessionReader.Read(path, root, 2).Frames.Count == 3, "Incomplete final frame bytes are ignored after the complete CRC-valid prefix.");
        Write(path,[header,boundary]);
        var corrupted = File.ReadAllBytes(path); corrupted[^2] ^= 1; File.WriteAllBytes(path, corrupted);
        Reject(() => LiveSessionReader.Read(path, root, 1), "CRC-corrupt complete frames cannot be silently ignored.");
        Write(path,[header,future,boundary]);
        Reject(() => LiveSessionReader.Read(path, root, 2), "Out-of-order append IDs cannot be repaired by sorting.");
        Write(path,[header,boundary]);
        Reject(() => LiveSessionReader.Read(path, root, 2), "A boundary that is not durable yet is rejected.");
        string packetRoot = Path.Combine(root, "png-reader");
        string packet = Path.Combine(packetRoot, "packets", "fixture");
        Directory.CreateDirectory(packet);
        var descriptor = new { id = "snapshot", sessionId = "live", generation = 1, triggerTicks = 20,
            width = 2, height = 1, layer = new { uuid = "ink", name = "Ink" } };
        File.WriteAllText(Path.Combine(packet, "manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "dirty-matrix-image-diff/v1", comparison = "fullResolution/v2", kind = "diff", id = "packet",
            after = descriptor, now = descriptor,
            labels = new[] { new { id = "label", source = "tablet", operationId = 1, fromTicks = 0, toTicks = 20,
                coverage = new { radius = 1, segments = Array.Empty<object>() }, imageIds = new[] { "image" } } },
            images = new[] { new { id = "image", bounds = new { left = 0, top = 0, right = 2, bottom = 1, width = 2, height = 1 },
                changedPixels = 1, afterImage = "after.png", nowImage = "now.png", maskImage = "mask.png", labelIds = new[] { "label" } } }
        }));
        byte[] rgba = Png(6);
        File.WriteAllBytes(Path.Combine(packet, "after.png"), rgba);
        File.WriteAllBytes(Path.Combine(packet, "now.png"), rgba);
        File.WriteAllBytes(Path.Combine(packet, "mask.png"), DirtyMatrixPaster.ClipboardImageCodec.EncodePng(2, 1,
            new byte[] { 77, 88, 99, 0, 255, 255, 255, 255 }));
        var pngSnapshot = LiveSessionReader.Read(path, packetRoot, 1);
        Check(pngSnapshot.Packets[0].Images[0].Mask.Count == 1 && !pngSnapshot.Packets[0].Images[0].Mask.Contains(0, 0),
            "A transparent mask pixel remains mask=0 even if its stored RGB is nonzero.");
        string longRoot = Path.Combine(root, "project-history", new string('a', 96), new string('b', 96), "session-0", "diff");
        string longPacket = Path.Combine(longRoot, "packets", "fixture");
        Directory.CreateDirectory(longPacket);
        foreach (string file in Directory.EnumerateFiles(packet))
            File.WriteAllBytes(Path.Combine(longPacket, Path.GetFileName(file)), File.ReadAllBytes(file));
        Check(Path.Combine(longPacket, "now.png").Length > 260, "The history PNG fixture must exceed the native filename limit.");
        var longSnapshot = LiveSessionReader.Read(path, longRoot, 1);
        Check(longSnapshot.Packets[0].Images[0].Now.Bytes.AsSpan().SequenceEqual(pngSnapshot.Packets[0].Images[0].Now.Bytes),
            "Nested history packet PNGs must decode without depending on GDI+ filename limits.");
        Directory.CreateDirectory(Path.Combine(longRoot, "snapshots"));
        File.WriteAllBytes(Path.Combine(longRoot, "snapshots", "snapshot.png"), rgba);
        var longBaseline = LiveSessionReader.LoadSnapshot(longRoot, JsonSerializer.SerializeToElement(descriptor), 2, 1, CancellationToken.None);
        Check(longBaseline!.Bytes.AsSpan().SequenceEqual(pngSnapshot.Packets[0].Images[0].Now.Bytes),
            "History baselines must decode at long paths and preserve straight RGBA pixels.");
        string singleRoot = Path.Combine(root, "single-packet");
        string singlePacket = Path.Combine(singleRoot, "packets", "fixture");
        string singleSnapshots = Path.Combine(singleRoot, "snapshots");
        Directory.CreateDirectory(singlePacket); Directory.CreateDirectory(singleSnapshots);
        foreach (string file in Directory.EnumerateFiles(packet))
            File.WriteAllBytes(Path.Combine(singlePacket, Path.GetFileName(file)), File.ReadAllBytes(file));
        var singleManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(singlePacket, "manifest.json")))!;
        singleManifest["now"]!["id"] = "current";
        File.WriteAllText(Path.Combine(singlePacket, "manifest.json"), singleManifest.ToJsonString());
        byte[] currentPixels = [1, 2, 3, 255, 4, 5, 6, 255];
        File.WriteAllBytes(Path.Combine(singleSnapshots, "snapshot.png"), rgba);
        File.WriteAllBytes(Path.Combine(singleSnapshots, "current.png"), DirtyMatrixPaster.ClipboardImageCodec.EncodePng(2, 1, currentPixels));
        var single = LiveSessionReader.Read(path, singleRoot, 1).Packets.Single();
        Check(single.Before is not null && single.Current is not null &&
            single.Before.Bytes.AsSpan().SequenceEqual(pngSnapshot.Packets[0].Images[0].Now.Bytes) &&
            single.Current.Bytes.AsSpan().SequenceEqual(currentPixels),
            "A single capture packet must keep both its original baseline and its current pixels after record replacement.");
        var accepted = new List<string>();
        foreach (var (format, colorType) in new[] { ("RGB", (byte)2), ("grayscale", (byte)0), ("indexed", (byte)3) })
        {
            var bytes = Png(colorType);
            using (var decoded = new Bitmap(new MemoryStream(bytes)))
                Check(decoded.Width == 2 && decoded.Height == 1 && decoded.GetPixel(1, 0).A == 255,
                    "The rejection fixture is a genuine decodable " + format + " PNG.");
            File.WriteAllBytes(Path.Combine(packet, "now.png"), bytes);
            try { LiveSessionReader.Read(path, packetRoot, 1); accepted.Add(format); }
            catch (InvalidDataException) { count++; }
        }
        if (accepted.Count > 0) throw new InvalidOperationException("Live reader silently converted non-RGBA PNG fixtures: " + string.Join(", ", accepted));
        File.WriteAllBytes(Path.Combine(packet, "now.png"), Png(6, interlace: 1));
        Check(LiveSessionReader.Read(path, packetRoot, 1).Packets[0].Images[0].Now.Bytes.AsSpan().SequenceEqual(new byte[] { 11, 22, 33, 0, 44, 55, 66, 255 }),
            "Legal Adam7 RGBA8 PNGs preserve straight RGBA including transparent RGB.");
        foreach (var field in new[] { "length", "type", "bitDepth", "compression", "filter", "interlace", "width" })
        {
            var invalid = rgba.ToArray();
            switch (field)
            {
                case "length": BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(8), 12); break;
                case "type": "IDAT"u8.CopyTo(invalid.AsSpan(12)); break;
                case "bitDepth": invalid[24] = 16; break;
                case "compression": invalid[26] = 1; break;
                case "filter": invalid[27] = 1; break;
                case "interlace": invalid[28] = 2; break;
                case "width": BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(16), 3); break;
            }
            BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(29), Crc(invalid.AsSpan(12, 17)));
            File.WriteAllBytes(Path.Combine(packet, "now.png"), invalid);
            Reject(() => LiveSessionReader.Read(path, packetRoot, 1), "Malformed/unsupported PNG IHDR field accepted: " + field);
        }
        File.WriteAllBytes(Path.Combine(packet, "now.png"), rgba.AsSpan(0, 28).ToArray());
        Reject(() => LiveSessionReader.Read(path, packetRoot, 1), "Truncated PNG header must report invalid data before decoding.");
        Console.WriteLine($"Live session reader checks passed: {count}.");
        return count;
    }

    // The public v1 wire fixture deliberately allows out-of-order append IDs.
    private static void Write(string path, IReadOnlyList<JsonElement> frames)
    {
        BundleChecks.WriteV1(path, frames.Select(f => JsonNode.Parse(f.GetRawText())!));
    }

    private static byte[] Png(byte colorType, byte interlace = 0)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8; header[9] = colorType; header[12] = interlace;
        Chunk(png, "IHDR"u8, header);
        if (colorType == 3) Chunk(png, "PLTE"u8, new byte[] { 11, 22, 33, 44, 55, 66 });
        byte[] scanline = colorType switch
        {
            0 => [0, 11, 44],
            2 => [0, 11, 22, 33, 44, 55, 66],
            3 => [0, 0, 1],
            6 when interlace == 1 => [0, 11, 22, 33, 0, 0, 44, 55, 66, 255],
            6 => [0, 11, 22, 33, 0, 44, 55, 66, 255],
            _ => throw new ArgumentException("Unsupported test PNG color type.")
        };
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) encoder.Write(scanline);
        Chunk(png, "IDAT"u8, compressed.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, (uint)payload.Length); png.Write(value);
        png.Write(type); png.Write(payload);
        var checksumBytes = new byte[type.Length + payload.Length];
        type.CopyTo(checksumBytes); payload.CopyTo(checksumBytes.AsSpan(type.Length));
        BinaryPrimitives.WriteUInt32BigEndian(value, Crc(checksumBytes)); png.Write(value);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xEDB88320U : 0); }
        return ~crc;
    }
}
