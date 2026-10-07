using System.Buffers.Binary;
using System.IO.Compression;
using DirtyMatrixPaster;

internal static partial class PacketChecks
{
    internal static void CodecChecks()
    {
        byte[] rgba = [11, 22, 33, 0, 54, 87, 120, 83, 100, 200, 255, 1, 255, 0, 17, 255];
        var png = ClipboardImageCodec.EncodePng(2, 2, rgba);
        Check(DecodeCodecPng(png).SequenceEqual(rgba), "PNG preserves straight channels including hidden RGB at alpha zero.");
        var dib = ClipboardImageCodec.EncodeDibV5(2, 2, rgba);
        Check(BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4)) == -2 && dib.Length == 124 + rgba.Length, "DIBV5 uses full canvas top-down rows.");
        Check(dib.AsSpan(124).SequenceEqual(new byte[] { 33, 22, 11, 0, 120, 87, 54, 83, 255, 200, 100, 1, 17, 0, 255, 255 }), "DIBV5 preserves straight BGRA without multiplying by alpha.");
        Reject<ArgumentException>(() => ClipboardImageCodec.EncodePng(2, 2, rgba.AsSpan(0, 4)));
        Reject<ArgumentOutOfRangeException>(() => ClipboardImageCodec.EncodeDibV5(0, 2, rgba));
        Reject<InvalidOperationException>(() => ClipboardImageCodec.SetImage(2, 2, rgba));
        Console.WriteLine($"Clipboard codec checks passed: {_assertions} assertions. The system clipboard was not modified.");
    }

    private static byte[] DecodeCodecPng(byte[] png)
    {
        Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature is standard.");
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, size);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]); height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                Check(data[8] == 8 && data[9] == 6 && data[12] == 0, "PNG is 8-bit non-interlaced RGBA.");
            }
            if (type == "IDAT") idat.Write(data);
            offset += size + 12;
        }
        idat.Position = 0;
        using var decoded = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true)) zlib.CopyTo(decoded);
        var rows = decoded.ToArray(); int stride = width * 4;
        var rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = rows[y * (stride + 1)];
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 4 ? rgba[y * stride + x - 4] : 0;
                int b = y > 0 ? rgba[(y - 1) * stride + x] : 0;
                int c = y > 0 && x >= 4 ? rgba[(y - 1) * stride + x - 4] : 0;
                int paeth = a + b - c;
                int pa = Math.Abs(paeth - a), pb = Math.Abs(paeth - b), pc = Math.Abs(paeth - c);
                int prediction = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => throw new InvalidDataException("PNG filter") };
                rgba[y * stride + x] = unchecked((byte)(rows[y * (stride + 1) + x + 1] + prediction));
            }
        }
        return rgba;
    }
}
