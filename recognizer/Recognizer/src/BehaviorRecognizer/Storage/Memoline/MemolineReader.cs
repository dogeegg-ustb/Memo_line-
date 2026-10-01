using System.IO.Hashing;
using System.Text.Json;

namespace BehaviorRecognizer.Storage.Memoline;

public static class MemolineReader
{
    /// <summary>A .part yields only fully committed CRC-valid frames. Corruption is never skipped.</summary>
    public static IEnumerable<JsonElement> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("MEMOLINE"u8) || reader.ReadUInt32() != 1)
            throw new InvalidDataException("Unsupported memoline header.");
        bool partial = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase), footer = false;
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8) { if (partial) yield break; throw new InvalidDataException("Truncated frame."); }
            int length = reader.ReadInt32();
            uint crc = reader.ReadUInt32();
            if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Invalid frame size.");
            if (length > stream.Length - stream.Position) { if (partial) yield break; throw new InvalidDataException("Truncated payload."); }
            var payload = reader.ReadBytes(length);
            if (Crc32.HashToUInt32(payload) != crc) throw new InvalidDataException("Frame CRC mismatch.");
            using var document = JsonDocument.Parse(payload);
            if (footer) throw new InvalidDataException("Data after footer.");
            footer = document.RootElement.GetProperty("kind").GetString() == "footer";
            yield return document.RootElement.Clone();
        }
        if (!partial && !footer) throw new InvalidDataException("Missing session footer.");
    }

    public static async Task ExportAsync(string input, string output, CancellationToken token)
    {
        if (System.IO.Path.GetFullPath(input).Equals(System.IO.Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must differ from input.");
        using var writer = new StreamWriter(output, false, new System.Text.UTF8Encoding(false));
        foreach (var record in Read(input))
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(record.GetRawText());
        }
    }
}
