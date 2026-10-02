using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace LayerStealer.Capture;

internal static class DibDecoder
{
    internal const long MaxPixels = 64_000_000;

    public static Bitmap Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40) throw new InvalidDataException("DIB 头部不完整。");
        uint size = U32(data, 0);
        if (size is not (40 or 52 or 56 or 108 or 124) || size > data.Length)
            throw new InvalidDataException("不支持的 DIB 头部。");
        int width = I32(data, 4), rawHeight = I32(data, 8);
        long heightValue = Math.Abs((long)rawHeight);
        if (width <= 0 || heightValue <= 0 || (long)width * heightValue > MaxPixels || U16(data, 12) != 1)
            throw new InvalidDataException("DIB 尺寸或颜色平面无效（最大 6400 万像素）。");
        int height = (int)heightValue;
        ushort bits = U16(data, 14);
        uint compression = U32(data, 16), colorsUsed = U32(data, 32);
        if (bits is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6)
            || (compression != 0 && bits is not (16 or 32)))
            throw new InvalidDataException("不支持的 DIB 压缩或位深。");

        int offset = (int)size;
        uint red = bits == 16 ? 0x7C00u : 0x00FF0000u;
        uint green = bits == 16 ? 0x03E0u : 0x0000FF00u;
        uint blue = bits == 16 ? 0x001Fu : 0x000000FFu;
        uint alpha = 0;
        if (compression != 0)
        {
            int masks = size == 40 ? offset : 40;
            int maskBytes = compression == 6 ? 16 : 12;
            if (size == 52 && compression == 6) throw new InvalidDataException("DIB 缺少 Alpha 掩码。");
            if (masks + maskBytes > data.Length) throw new InvalidDataException("DIB 颜色掩码不完整。");
            red = U32(data, masks); green = U32(data, masks + 4); blue = U32(data, masks + 8);
            if (compression == 6 || size >= 56) alpha = U32(data, masks + 12);
            if (size == 40) offset += maskBytes;
            ValidateMasks(red, green, blue, alpha, bits);
        }
        else if (size >= 56 && bits == 32)
        {
            // Some producers declare an Alpha mask even for BI_RGB V4/V5 headers.
            alpha = U32(data, 52);
            ValidateMasks(red, green, blue, alpha, bits);
        }

        long paletteCount = bits <= 8 ? (colorsUsed == 0 ? 1L << bits : colorsUsed) : colorsUsed;
        if (paletteCount > 256 || (bits <= 8 && paletteCount > (1L << bits)))
            throw new InvalidDataException("DIB 调色板无效。");
        int paletteOffset = offset;
        long pixelsOffset = offset + paletteCount * 4;
        long stride = (((long)width * bits + 31) / 32) * 4;
        long pixelBytes = stride * height;
        if (size == 124)
        {
            uint profileOffset = U32(data, 112), profileSize = U32(data, 116);
            if (profileSize != 0)
            {
                if ((long)profileOffset + profileSize > data.Length || profileOffset < pixelsOffset)
                    throw new InvalidDataException("DIB 色彩配置文件越界。");
                // Packed DIB may place its profile before the pixel array.
                if (profileOffset == pixelsOffset) pixelsOffset += profileSize;
                else if (profileOffset < pixelsOffset + pixelBytes)
                    throw new InvalidDataException("DIB 像素与色彩配置文件重叠。");
            }
        }
        if (pixelsOffset + pixelBytes > data.Length) throw new InvalidDataException("DIB 像素数据被截断。");

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData? locked = null;
        try
        {
            locked = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            byte[] row = new byte[checked(width * 4)];
            for (int y = 0; y < height; y++)
            {
                int sourceY = rawHeight > 0 ? height - 1 - y : y;
                var source = data.Slice(checked((int)(pixelsOffset + sourceY * stride)), (int)stride);
                for (int x = 0; x < width; x++)
                {
                    int dest = x * 4;
                    if (bits <= 8)
                    {
                        int index = bits switch
                        {
                            8 => source[x],
                            4 => (source[x / 2] >> (x % 2 == 0 ? 4 : 0)) & 15,
                            _ => (source[x / 8] >> (7 - x % 8)) & 1
                        };
                        if (index >= paletteCount) throw new InvalidDataException("DIB 调色板索引越界。");
                        int entry = paletteOffset + index * 4;
                        row[dest] = data[entry]; row[dest + 1] = data[entry + 1]; row[dest + 2] = data[entry + 2];
                        row[dest + 3] = 255;
                    }
                    else if (bits == 24)
                    {
                        row[dest] = source[x * 3]; row[dest + 1] = source[x * 3 + 1]; row[dest + 2] = source[x * 3 + 2];
                        row[dest + 3] = 255;
                    }
                    else
                    {
                        uint pixel = bits == 16 ? U16(source, x * 2) : U32(source, x * 4);
                        row[dest] = Component(pixel, blue); row[dest + 1] = Component(pixel, green);
                        row[dest + 2] = Component(pixel, red); row[dest + 3] = alpha == 0 ? (byte)255 : Component(pixel, alpha);
                    }
                }
                Marshal.Copy(row, 0, locked.Scan0 + y * locked.Stride, row.Length);
            }
            bitmap.UnlockBits(locked); locked = null;
            return bitmap;
        }
        catch
        {
            if (locked is not null) bitmap.UnlockBits(locked);
            bitmap.Dispose();
            throw;
        }
    }

    private static void ValidateMasks(uint red, uint green, uint blue, uint alpha, int bits)
    {
        uint used = 0;
        foreach (uint mask in new[] { red, green, blue, alpha })
        {
            if (mask == 0) continue;
            uint shifted = mask >> BitOperations.TrailingZeroCount(mask);
            if ((shifted & (shifted + 1)) != 0 || (used & mask) != 0 || (bits == 16 && mask > 0xFFFF))
                throw new InvalidDataException("DIB 颜色掩码无效。");
            used |= mask;
        }
        if (red == 0 || green == 0 || blue == 0) throw new InvalidDataException("DIB 缺少颜色掩码。");
    }
    private static byte Component(uint pixel, uint mask)
    {
        int shift = BitOperations.TrailingZeroCount(mask);
        uint maximum = mask >> shift;
        return (byte)((((ulong)(pixel & mask) >> shift) * 255 + maximum / 2) / maximum);
    }
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static int I32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
