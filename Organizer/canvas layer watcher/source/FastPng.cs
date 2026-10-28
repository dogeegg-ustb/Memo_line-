using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace CanvasLayerWatcher;

/// <summary>
/// 8-bit RGBA PNG without GDI+. A full A4 canvas (4961×7016, 139 MB of pixels) decodes in about a tenth of the
/// time GDI+ takes and encodes several times faster; pixels are identical either way (PNG is lossless). Decoding
/// covers what the CLIP bridge and GDI+ write (non-interlaced 8-bit RGBA); anything else returns null so callers
/// keep their GDI+ path for it.
/// </summary>
internal static class FastPng
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Width and height from the IHDR of an 8-bit RGBA PNG; false for any other or a damaged header.</summary>
    internal static bool TryReadSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature) || BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(png[16..]), h = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue || png[24] != 8 || png[25] != 6 || png[26] != 0 || png[27] != 0 || png[28] != 0)
            return false;
        width = (int)w; height = (int)h;
        return true;
    }

    internal static byte[]? TryDecodeFile(string path, bool bgra, out int width, out int height)
        => TryDecode(File.ReadAllBytes(path), bgra, out width, out height);

    /// <summary>Straight RGBA (or BGRA, GDI+'s 32bpp ARGB byte order) pixels, top row first; null when the PNG is not a
    /// non-interlaced 8-bit RGBA image. Damaged data throws <see cref="InvalidDataException"/>.</summary>
    internal static byte[]? TryDecode(ReadOnlySpan<byte> png, bool bgra, out int width, out int height)
    {
        if (!TryReadSize(png, out width, out height)) return null;
        long rowBytes = (long)width * 4, total = rowBytes * height;
        if (total > Array.MaxLength || (rowBytes + 1) * height > Array.MaxLength) return null;
        // IDAT chunks form one zlib stream; they are normally a single chunk or a few.
        var idat = new MemoryStream();
        bool ended = false;
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length > (uint)(png.Length - offset - 12)) throw new InvalidDataException("PNG 数据块长度无效。");
            var type = png.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8)) idat.Write(png.Slice(offset + 8, (int)length));
            else if (type.SequenceEqual("IEND"u8)) { ended = true; break; }
            offset += (int)length + 12;
        }
        if (!ended || idat.Length == 0) throw new InvalidDataException("PNG 缺少图像数据或结尾。");
        idat.Position = 0;
        var raw = new byte[(rowBytes + 1) * height];
        try
        {
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
            zlib.ReadExactly(raw);
        }
        catch (Exception error) when (error is EndOfStreamException or InvalidDataException or IOException)
        { throw new InvalidDataException("PNG 压缩数据无效。", error); }
        var pixels = GC.AllocateUninitializedArray<byte>((int)total);
        int stride = (int)rowBytes;
        for (int y = 0; y < height; y++)
        {
            var source = raw.AsSpan((int)((rowBytes + 1) * y) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var up = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : default;
            Unfilter(raw[(rowBytes + 1) * y], source, row, up);
        }
        if (bgra) SwapRedBlue(pixels);
        return pixels;
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, Span<byte> row, ReadOnlySpan<byte> up)
    {
        bool first = up.IsEmpty;
        switch (filter)
        {
            case 0: source.CopyTo(row); break;
            case 1:
                source[..4].CopyTo(row);
                for (int x = 4; x < row.Length; x++) row[x] = (byte)(source[x] + row[x - 4]);
                break;
            case 2:
                if (first) { source.CopyTo(row); break; }
                Add(source, up, row);
                break;
            case 3:
                for (int x = 0; x < row.Length; x++)
                {
                    int left = x >= 4 ? row[x - 4] : 0, above = first ? 0 : up[x];
                    row[x] = (byte)(source[x] + ((left + above) >> 1));
                }
                break;
            case 4:
                for (int x = 0; x < row.Length; x++)
                {
                    int a = x >= 4 ? row[x - 4] : 0, b = first ? 0 : up[x], c = x >= 4 && !first ? up[x - 4] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[x] = (byte)(source[x] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                break;
            default: throw new InvalidDataException("PNG 行过滤类型无效。");
        }
    }

    private static void Add(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> result)
    {
        int i = 0;
        for (; i <= a.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            (new Vector<byte>(a[i..]) + new Vector<byte>(b[i..])).CopyTo(result[i..]);
        for (; i < a.Length; i++) result[i] = (byte)(a[i] + b[i]);
    }

    /// <summary>RGBA ⇄ BGRA in place.</summary>
    internal static void SwapRedBlue(Span<byte> pixels)
    {
        var mask = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        int i = 0;
        for (; i <= pixels.Length - 16; i += 16)
            Vector128.Shuffle(Vector128.Create<byte>(pixels[i..]), mask).CopyTo(pixels[i..]);
        for (; i + 3 < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    /// <summary>A standard 8-bit RGBA PNG from RGBA or BGRA pixels. Unfiltered rows with zlib's default level: on a
    /// line-art canvas that is as small as GDI+'s file (205 KB against 211 KB) in a tenth of the time (76 ms against
    /// 790 ms); the Up filter or the fastest level only made it larger.</summary>
    internal static byte[] Encode(int width, int height, ReadOnlySpan<byte> pixels, bool bgra)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA 数据尺寸与图像不一致。", nameof(pixels));
        int stride = width * 4;
        using var output = new MemoryStream(Math.Max(1024, pixels.Length / 64));
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);
        // IDAT is written in place: a length placeholder, the type, the zlib stream, then the length and CRC.
        long lengthAt = output.Position;
        output.Write(stackalloc byte[4]);
        output.Write("IDAT"u8);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            const int Batch = 32;
            var buffer = new byte[(stride + 1) * Math.Min(Batch, height)];
            for (int y = 0; y < height;)
            {
                int rows = Math.Min(Batch, height - y), used = 0;
                for (int r = 0; r < rows; r++, y++)
                {
                    var target = buffer.AsSpan(used + 1, stride);
                    buffer[used] = 0;
                    pixels.Slice(y * stride, stride).CopyTo(target);
                    if (bgra) SwapRedBlue(target);
                    used += stride + 1;
                }
                zlib.Write(buffer, 0, used);
            }
        }
        long end = output.Position;
        int length = checked((int)(end - lengthAt - 8));
        var bytes = output.GetBuffer();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)lengthAt), (uint)length);
        uint crc = Crc32(bytes.AsSpan((int)lengthAt + 4, length + 4));
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        output.Write(word);
        var typed = new byte[4 + data.Length];
        type.CopyTo(typed); data.CopyTo(typed.AsSpan(4));
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typed));
        output.Write(word);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256 * 8];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        for (int t = 1; t < 8; t++)
            for (int n = 0; n < 256; n++)
                table[t * 256 + n] = (table[(t - 1) * 256 + n] >> 8) ^ table[table[(t - 1) * 256 + n] & 0xff];
        return table;
    }

    /// <summary>The PNG/zlib CRC-32, eight bytes per step.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        var t = CrcTable;
        while (bytes.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(bytes) ^ crc, two = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            crc = t[7 * 256 + (one & 0xff)] ^ t[6 * 256 + ((one >> 8) & 0xff)] ^ t[5 * 256 + ((one >> 16) & 0xff)] ^ t[4 * 256 + (one >> 24)] ^
                  t[3 * 256 + (two & 0xff)] ^ t[2 * 256 + ((two >> 8) & 0xff)] ^ t[256 + ((two >> 16) & 0xff)] ^ t[two >> 24];
            bytes = bytes[8..];
        }
        foreach (byte value in bytes) crc = t[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>Checks every chunk CRC and the IEND placement (what the GDI+ paths verified before decoding).</summary>
    internal static bool ChunksValid(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) return false;
        for (int offset = 8; ;)
        {
            if (png.Length - offset < 12) return false;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length > (uint)(png.Length - offset - 12)) return false;
            if (Crc32(png.Slice(offset + 4, (int)length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + (int)length)..])) return false;
            bool end = png.Slice(offset + 4, 4).SequenceEqual("IEND"u8);
            offset += (int)length + 12;
            if (end) return length == 0 && offset == png.Length;
        }
    }

    /// <summary>A GDI+ 32bpp ARGB bitmap over decoded BGRA pixels (the caller keeps <paramref name="pixels"/> alive and
    /// pinned through <paramref name="handle"/> until the bitmap is disposed).</summary>
    internal static Bitmap Wrap(byte[] pixels, int width, int height, out GCHandle handle)
    {
        handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { return new Bitmap(width, height, width * 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject()); }
        catch { handle.Free(); throw; }
    }
}
