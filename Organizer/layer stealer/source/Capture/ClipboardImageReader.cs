using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LayerStealer.Windows;

namespace LayerStealer.Capture;

internal sealed record ClipboardFrame(Bitmap Image, string Format, uint Sequence, uint OwnerProcessId) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

internal static class ClipboardImageReader
{
    private const int MaxBytes = 256 * 1024 * 1024;
    private static readonly uint Png = Native.RegisterClipboardFormat("PNG");
    private static readonly uint MimePng = Native.RegisterClipboardFormat("image/png");

    public static ClipboardFrame? TryRead(nint window, uint? previousSequence = null, uint? expectedOwner = null)
    {
        if (!Native.OpenClipboard(window)) return null;
        try
        {
            uint sequence = Native.GetClipboardSequenceNumber();
            Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out uint owner);
            if (!IsFresh(sequence, owner, previousSequence, expectedOwner)) return null;
            List<string> errors = [];
            foreach (var (format, name) in new[] { (Png, "PNG"), (MimePng, "image/png"), (17u, "DIBV5"), (8u, "DIB") })
            {
                if (format == 0 || !Native.IsClipboardFormatAvailable(format)) continue;
                try
                {
                    nint handle = Native.GetClipboardData(format);
                    if (handle == 0) continue;
                    byte[] data = ReadMemory(handle);
                    var bitmap = format == Png || format == MimePng ? DecodePng(data) : DibDecoder.Decode(data);
                    return new(bitmap, name, Native.GetClipboardSequenceNumber(), owner);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or ExternalException or OverflowException)
                { errors.Add($"{name}: {ex.Message}"); }
            }
            if (Native.IsClipboardFormatAvailable(2))
            {
                nint handle = Native.GetClipboardData(2);
                if (handle != 0)
                {
                    using var source = Image.FromHbitmap(handle);
                    ValidateSize(source.Width, source.Height);
                    return new(new Bitmap(source), "Bitmap（无 Alpha）", Native.GetClipboardSequenceNumber(), owner);
                }
            }
            if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
            return null;
        }
        finally { Native.CloseClipboard(); }
    }

    internal static bool IsFresh(uint sequence, uint owner, uint? previousSequence, uint? expectedOwner)
        => (!previousSequence.HasValue || sequence != previousSequence.Value)
        && (!expectedOwner.HasValue || owner == expectedOwner.Value);

    private static byte[] ReadMemory(nint handle)
    {
        nuint size = Native.GlobalSize(handle);
        if (size == 0 || size > MaxBytes) throw new InvalidDataException("剪贴板图像数据为空或超过 256 MB。");
        nint pointer = Native.GlobalLock(handle);
        if (pointer == 0) throw new InvalidDataException("剪贴板数据暂时不可读取。");
        try
        {
            byte[] bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Native.GlobalUnlock(handle); }
    }

    internal static Bitmap DecodePng(byte[] data)
    {
        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("剪贴板 PNG 数据无效。");
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
        if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("PNG 尺寸无效。");
        ValidateSize((int)width, (int)height);
        using var stream = new MemoryStream(data, false);
        using var source = Image.FromStream(stream, false, true);
        ValidateSize(source.Width, source.Height);
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > DibDecoder.MaxPixels)
            throw new InvalidDataException("图像尺寸无效（最大 6400 万像素）。");
    }
}
