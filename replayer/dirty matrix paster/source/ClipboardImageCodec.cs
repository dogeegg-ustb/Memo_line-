using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DirtyMatrixPaster;

/// <summary>
/// Encodes and publishes a complete straight-RGBA canvas image for CSP paste.
/// </summary>
internal static class ClipboardImageCodec
{
    internal static byte[] EncodePng(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Validate(width, height, rgba);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rectangle = new Rectangle(0, 0, width, height);
        var bits = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int sourceStride = checked(width * 4);
            for (int y = 0; y < height; y++)
            {
                var row = new byte[sourceStride];
                rgba.Slice(y * sourceStride, sourceStride).CopyTo(row);
                for (int x = 0; x < width; x++)
                {
                    int source = x * 4;
                    (row[source], row[source + 2]) = (row[source + 2], row[source]);
                }

                Marshal.Copy(row, 0, IntPtr.Add(bits.Scan0, y * bits.Stride), row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    internal static byte[] EncodeDibV5(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Validate(width, height, rgba);
        var output = new byte[checked(124 + width * height * 4)];
        using (var stream = new MemoryStream(output, writable: true))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(124u);
            writer.Write(width);
            writer.Write(-height);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(3u); // BI_BITFIELDS
            writer.Write((uint)(width * height * 4));
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0x00ff0000u);
            writer.Write(0x0000ff00u);
            writer.Write(0x000000ffu);
            writer.Write(0xff000000u);
            writer.Write(0x73524742u); // LCS_sRGB
            stream.Position = 108;
            writer.Write(4u); // LCS_GM_IMAGES
        }

        // CF_DIBV5 stores top-down BGRA rows. The RGBA input is straight alpha.
        int rowBytes = checked(width * 4);
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * rowBytes;
            int targetRow = 124 + sourceRow;
            for (int x = 0; x < width; x++)
            {
                int source = sourceRow + x * 4;
                int target = targetRow + x * 4;
                output[target] = rgba[source + 2];
                output[target + 1] = rgba[source + 1];
                output[target + 2] = rgba[source];
                output[target + 3] = rgba[source + 3];
            }
        }

        return output;
    }

    internal static void SetImage(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Validate(width, height, rgba);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("剪贴板图像必须在 STA 线程中写入");

        byte[] png = EncodePng(width, height, rgba);
        byte[] dib = EncodeDibV5(width, height, rgba);
        var data = new DataObject();
        data.SetData("PNG", false, new MemoryStream(png, writable: false));
        data.SetData(DataFormats.GetFormat(17).Name, false, new MemoryStream(dib, writable: false));
        Clipboard.SetDataObject(data, true, 10, 100);
        VerifyReadBack(png, dib);
    }

    // Clipboard history and other watchers can briefly hold or re-render the clipboard right after a
    // write. Retry the read; when it stays unreadable, rely on the caller's own verification of the
    // pasted result (the editing transaction exports and compares the pasted layer).
    private static void VerifyReadBack(byte[] png, byte[] dib)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var readBack = Clipboard.GetDataObject() ?? throw new InvalidDataException("剪贴板写入后无法读回");
                if (!ReadBytes(readBack, "PNG").AsSpan().SequenceEqual(png))
                    throw new IOException("剪贴板 PNG 读回校验失败");
                if (!ReadBytes(readBack, DataFormats.GetFormat(17).Name).AsSpan().SequenceEqual(dib))
                    throw new IOException("剪贴板 CF_DIBV5 读回校验失败");
                return;
            }
            catch (Exception error) when (error is InvalidDataException or System.Runtime.InteropServices.ExternalException)
            {
                if (attempt >= 4) return;
                Thread.Sleep(100);
            }
        }
    }

    private static byte[] ReadBytes(IDataObject data, string format) => data.GetData(format, false) switch
    {
        MemoryStream stream => stream.ToArray(),
        byte[] bytes => bytes,
        _ => throw new InvalidDataException($"剪贴板没有 {format} 原始数据")
    };

    private static void Validate(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int expected = checked(width * height * 4);
        if (rgba.Length != expected) throw new ArgumentException("RGBA 数据尺寸与图像不一致", nameof(rgba));
    }
}
