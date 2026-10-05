using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text.Json;

namespace BehaviorRecognizer.Storage.Memoline;

internal static class MemolineFormat
{
    internal const uint CurrentVersion = 2;
    internal const int MaximumFrameBytes = 64 * 1024 * 1024;

    internal static void WriteHeader(Stream stream)
    {
        stream.Write("MEMOLINE"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, CurrentVersion);
        stream.Write(version);
    }

    internal static void WriteFrame(Stream stream, byte[] json, CompressionLevel compression)
    {
        if (json.Length == 0 || json.Length > MaximumFrameBytes)
            throw new InvalidDataException("Invalid frame size.");
        byte[] stored = json;
        uint flags = 0;
        if (compression != CompressionLevel.NoCompression && json.Length >= 256)
        {
            using var buffer = new MemoryStream();
            using (var encoder = new BrotliStream(buffer, compression, leaveOpen: true)) encoder.Write(json);
            if (buffer.Length + 32 < json.Length)
            {
                stored = buffer.ToArray();
                flags = 1;
            }
        }
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, stored.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], flags);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Crc32.HashToUInt32(stored));
        stream.Write(header);
        stream.Write(stored);
    }

    internal static uint? ReadHeader(FileStream stream, bool allowIncomplete)
    {
        if (stream.Length < 12)
        {
            if (allowIncomplete) return null;
            throw new InvalidDataException("Truncated memoline header.");
        }
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (!header[..8].SequenceEqual("MEMOLINE"u8) || version is not (1 or 2))
            throw new InvalidDataException("Unsupported memoline header.");
        return version;
    }

    internal static JsonElement? ReadFrame(FileStream stream, uint version, bool allowIncomplete, out int decodedBytes)
    {
        decodedBytes = 0;
        long start = stream.Position;
        int headerSize = version == 1 ? 8 : 16;
        if (stream.Length - start < headerSize)
        {
            if (allowIncomplete) return null;
            throw new InvalidDataException("Truncated frame header.");
        }
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header[..headerSize]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        uint flags = version == 1 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        int jsonLength = version == 1 ? length : BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[(version == 1 ? 4 : 12)..]);
        if (length <= 0 || length > MaximumFrameBytes || jsonLength <= 0 || jsonLength > MaximumFrameBytes
            || flags > 1 || (flags == 0 && length != jsonLength))
            throw new InvalidDataException("Invalid frame encoding or size.");
        if (length > stream.Length - stream.Position)
        {
            stream.Position = start;
            if (allowIncomplete) return null;
            throw new InvalidDataException("Truncated payload.");
        }
        var stored = new byte[length];
        stream.ReadExactly(stored);
        if (Crc32.HashToUInt32(stored) != crc) throw new InvalidDataException("Frame CRC mismatch.");
        byte[] json = stored;
        if (flags == 1)
        {
            json = new byte[jsonLength];
            using var decoder = new BrotliDecoder();
            if (decoder.Decompress(stored, json, out int consumed, out int written) != OperationStatus.Done
                || consumed != stored.Length || written != jsonLength)
                throw new InvalidDataException("Invalid Brotli frame.");
        }
        using var document = JsonDocument.Parse(json);
        decodedBytes = jsonLength;
        return document.RootElement.Clone();
    }
}
