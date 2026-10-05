using System.Text;
using System.Text.Json;

namespace DirtyMatrixPaster;

internal static class LayerClipboard
{
    internal const string LayerFormat = "MemoLine.DirtyMatrix.Layer.v1";
    internal const string MetadataFormat = "MemoLine.DirtyMatrix.Metadata.v1";
    internal const string PsdFormat = "image/vnd.adobe.photoshop";
    internal static byte[] Encode(ExportResult result)
    {
        if (!result.Ready || !result.Root.GetProperty("verified").GetBoolean()) throw new InvalidDataException("合成数据尚未通过校验");
        var rgba = File.ReadAllBytes(result.String("rgbaPath")); var mask = File.ReadAllBytes(result.String("maskPath"));
        int count = checked(result.Number("width") * result.Number("height"));
        if (rgba.Length != checked(count * 4) || mask.Length != count) throw new InvalidDataException("图层数据尺寸不一致");
        var json = Encoding.UTF8.GetBytes(result.Json);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("DMLAYER1")); writer.Write((uint)json.Length);
        writer.Write((ulong)rgba.Length); writer.Write((ulong)mask.Length);
        writer.Write(json); writer.Write(rgba); writer.Write(mask);
        return stream.ToArray();
    }
    internal static byte[] DibV5(ExportResult result)
    {
        var canvas = result.Root.GetProperty("canvas"); int width = canvas.GetProperty("width").GetInt32(), height = canvas.GetProperty("height").GetInt32();
        var bounds = result.Root.GetProperty("bounds"); int left = bounds.GetProperty("left").GetInt32(), top = bounds.GetProperty("top").GetInt32();
        int patchWidth = result.Number("width"), patchHeight = result.Number("height");
        var rgba = File.ReadAllBytes(result.String("rgbaPath")); var mask = File.ReadAllBytes(result.String("maskPath"));
        var output = new byte[checked(124 + width * height * 4)];
        using (var stream = new MemoryStream(output, true)) using (var w = new BinaryWriter(stream))
        {
            w.Write(124u); w.Write(width); w.Write(-height); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(3u); w.Write((uint)(width * height * 4)); w.Write(0); w.Write(0); w.Write(0u); w.Write(0u);
            w.Write(0x00ff0000u); w.Write(0x0000ff00u); w.Write(0x000000ffu); w.Write(0xff000000u);
            w.Write(0x73524742u); // LCS_sRGB
            stream.Position = 108; w.Write(4u); // LCS_GM_IMAGES
        }
        for (int y = 0; y < patchHeight; y++) for (int x = 0; x < patchWidth; x++)
        {
            int pixel = y * patchWidth + x; if (mask[pixel] == 0) continue;
            int source = pixel * 4, target = 124 + ((y + top) * width + x + left) * 4;
            output[target] = rgba[source + 2]; output[target + 1] = rgba[source + 1];
            output[target + 2] = rgba[source]; output[target + 3] = rgba[source + 3];
        }
        return output;
    }
    internal static string[] Write(ExportResult result, bool compatibleImage)
    {
        var envelope = Encode(result);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(result.String("rgbaPath"))!, "layer.dmlayer"), envelope);
        var data = new DataObject();
        data.SetData(LayerFormat, false, new MemoryStream(envelope, false));
        data.SetData(MetadataFormat, false, result.Json);
        if (result.String("psdPath") is {Length: >0} psd)
        {
            data.SetData(PsdFormat, false, new MemoryStream(File.ReadAllBytes(psd), false));
            data.SetData(DataFormats.FileDrop, false, new[] { psd });
        }
        if (compatibleImage)
        {
            if (result.String("psdPath").Length == 0) data.SetData(DataFormats.FileDrop, false, new[] { result.String("clipboardImagePath") });
            data.SetData("PNG", false, new MemoryStream(File.ReadAllBytes(result.String("clipboardImagePath")), false));
            // CF_DIBV5 is predefined clipboard format 17, not a newly registered format name.
            data.SetData(DataFormats.GetFormat(17).Name, false, new MemoryStream(DibV5(result), false));
        }
        // Flush to the Windows clipboard so content survives the program exiting.
        Clipboard.SetDataObject(data, true, 10, 100);
        var stored = ReadStream(Clipboard.GetDataObject()!, LayerFormat);
        if (!stored.AsSpan().SequenceEqual(envelope)) throw new IOException("剪贴板图层数据读回不一致");
        return Clipboard.GetDataObject()!.GetFormats(false);
    }
    internal static byte[] ReadStream(IDataObject data, string format) => data.GetData(format, false) switch
    {
        MemoryStream s => s.ToArray(), byte[] b => b, _ => throw new InvalidDataException($"剪贴板没有 {format} 原始数据")
    };
    internal static string Inspect()
    {
        var data = Clipboard.GetDataObject() ?? throw new InvalidDataException("剪贴板为空");
        var bytes = ReadStream(data, LayerFormat);
        using var stream = new MemoryStream(bytes); using var r = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "DMLAYER1") throw new InvalidDataException("无效图层剪贴板签名");
        uint jsonLength = r.ReadUInt32(); ulong rgbaLength = r.ReadUInt64(), maskLength = r.ReadUInt64();
        if (jsonLength > int.MaxValue || rgbaLength > int.MaxValue || maskLength > int.MaxValue ||
            28L + jsonLength + (long)rgbaLength + (long)maskLength != bytes.LongLength) throw new InvalidDataException("无效剪贴板长度");
        using var metadata = JsonDocument.Parse(r.ReadBytes((int)jsonLength));
        return JsonSerializer.Serialize(new { formats = data.GetFormats(false), metadata = metadata.RootElement,
            bytes = bytes.Length, rgbaBytes = rgbaLength, maskBytes = maskLength, persisted = true });
    }
}
