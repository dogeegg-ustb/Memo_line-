using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MemolineDemo.Editing;

internal readonly record struct CanvasSize(int Width, int Height);
internal sealed record LayerExportEvidence(LayerKey Layer, int CanvasWidth, int CanvasHeight, bool FullCanvas, byte[] Png);
internal sealed record CapturedLayer(LayerExportEvidence Evidence, ImageRgba Pixels);

/// <summary>Validates export-id identity and decodes its authoritative full-canvas straight RGBA PNG.</summary>
internal static class LayerCapture
{
    internal static CapturedLayer Read(LayerExportEvidence export, LayerKey expected, CanvasSize size)
    {
        try
        {
            if (export.Layer != expected || !export.FullCanvas || size.Width <= 0 || size.Height <= 0 ||
                export.CanvasWidth != size.Width || export.CanvasHeight != size.Height)
                throw new InvalidDataException("导出层的 session、generation、UUID、整画布标记或尺寸不符。");
            var png = export.Png;
            if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != size.Width ||
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != size.Height ||
                png[24] != 8 || png[25] != 6 || png[26] != 0 || png[27] != 0 || png[28] > 1)
                throw new InvalidDataException("捕获 PNG 必须是原尺寸的 8-bit RGBA。");
            bool data = false, ended = false;
            for (int offset = 8; offset < png.Length;)
            {
                if (png.Length - offset < 12) throw new InvalidDataException("PNG 块不完整。");
                uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
                if (length > png.Length - offset - 12) throw new InvalidDataException("PNG 块长度无效。");
                int count = checked((int)length);
                var type = png.AsSpan(offset + 4, 4);
                if (Crc(png.AsSpan(offset + 4, count + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + count, 4)))
                    throw new InvalidDataException("PNG 块 CRC 不符。");
                if (offset != 8 && type.SequenceEqual("IHDR"u8)) throw new InvalidDataException("PNG 重复 IHDR。");
                if (type.SequenceEqual("IDAT"u8)) data = true;
                offset += count + 12;
                if (type.SequenceEqual("IEND"u8))
                {
                    if (count != 0 || !data || offset != png.Length) throw new InvalidDataException("PNG 结尾无效。");
                    ended = true; break;
                }
            }
            if (!ended) throw new InvalidDataException("PNG 缺少完整 IEND。");
            using var input = new MemoryStream(png, writable: false);
            using var bitmap = new Bitmap(input);
            if (bitmap.Width != size.Width || bitmap.Height != size.Height) throw new InvalidDataException("解码 PNG 尺寸不符。");
            var rgba = new byte[checked(size.Width * size.Height * 4)];
            var bits = bitmap.LockBits(new Rectangle(0, 0, size.Width, size.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = checked(size.Width * 4);
                for (int y = 0; y < size.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), rgba, y * stride, stride);
                    for (int x = 0; x < size.Width; x++)
                    { int p = y * stride + x * 4; (rgba[p], rgba[p + 2]) = (rgba[p + 2], rgba[p]); }
                }
            }
            finally { bitmap.UnlockBits(bits); }
            return new(export with { Png = png.ToArray() }, new(size.Width, size.Height, rgba));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or ExternalException or OverflowException or NullReferenceException)
        { throw new CspEditingException(new(PlanIssueCode.CaptureMismatch, "权威图层捕获无效：" + error.Message), error); }
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
        }
        return ~crc;
    }
}
