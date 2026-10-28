namespace DirtyMatrixPaster;

/// <summary>
/// Encodes and publishes a complete straight-RGBA canvas image for CSP paste.
/// </summary>
internal static class ClipboardImageCodec
{
    // FastPng instead of GDI+: a full canvas encodes in ~80 ms instead of ~800 ms, to identical pixels.
    internal static byte[] EncodePng(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Validate(width, height, rgba);
        return CanvasLayerWatcher.FastPng.Encode(width, height, rgba, bgra: false);
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
        var pixels = output.AsSpan(124);
        rgba.CopyTo(pixels);
        CanvasLayerWatcher.FastPng.SwapRedBlue(pixels);
        return output;
    }

    internal static void SetImage(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Validate(width, height, rgba);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("剪贴板图像必须在 STA 线程中写入");
        Publish(EncodePng(width, height, rgba), EncodeDibV5(width, height, rgba));
    }

    /// <summary>Both clipboard formats of one image; pure, so it may run off the STA thread that publishes them.</summary>
    internal static (byte[] Png, byte[] Dib) Encode(int width, int height, byte[] rgba)
    {
        Validate(width, height, rgba);
        var dib = Task.Run(() => EncodeDibV5(width, height, rgba));
        var png = EncodePng(width, height, rgba);
        return (png, dib.GetAwaiter().GetResult());
    }

    /// <param name="dib">Null publishes the PNG alone (what CSP pastes).</param>
    internal static void Publish(byte[] png, byte[]? dib)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("剪贴板图像必须在 STA 线程中写入");
        var data = new DataObject();
        data.SetData("PNG", false, new MemoryStream(png, writable: false));
        if (dib is not null) data.SetData(DataFormats.GetFormat(17).Name, false, new MemoryStream(dib, writable: false));
        Clipboard.SetDataObject(data, true, 10, 100);
        VerifyReadBack(png, dib is not null);
    }

    // Clipboard history and other watchers can briefly hold or re-render the clipboard right after a
    // write: for a few tens of milliseconds a read may return nothing, or bytes of the right length that
    // are not ours (measured: about 40% of first reads, always correct by the second or third). Retry
    // both. A clipboard that stays unreadable is left to the caller's own verification of the pasted
    // result (the editing transaction exports and compares the pasted layer); one that keeps holding
    // other bytes was replaced by another program and fails. The PNG is compared in full (a replaced
    // clipboard cannot hold our exact PNG); the 139 MB CF_DIBV5 only has to be present.
    internal const int ReadBackAttempts = 10;
    private static void VerifyReadBack(byte[] png, bool withDib)
    {
        string dib = DataFormats.GetFormat(17).Name;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var readBack = Clipboard.GetDataObject() ?? throw new InvalidDataException("剪贴板写入后无法读回");
                if (!ReadBytes(readBack, "PNG").AsSpan().SequenceEqual(png))
                    throw new IOException("剪贴板 PNG 读回校验失败");
                if (withDib && !readBack.GetDataPresent(dib, false))
                    throw new IOException("剪贴板缺少 CF_DIBV5");
                return;
            }
            catch (Exception error) when (error is InvalidDataException or System.Runtime.InteropServices.ExternalException)
            {
                if (attempt >= ReadBackAttempts) return;
                Thread.Sleep(50);
            }
            catch (IOException) when (attempt < ReadBackAttempts)
            {
                Thread.Sleep(50);
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
