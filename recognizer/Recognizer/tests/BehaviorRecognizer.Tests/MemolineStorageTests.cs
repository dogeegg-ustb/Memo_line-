using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public sealed class MemolineStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "memoline-storage-" + Guid.NewGuid().ToString("N"));
    private static readonly HardwareDeviceSource Source = new("pen", "test", "dev", "test");
    public MemolineStorageTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task SuccessfulStatesOnly_FailedAndRawAttemptsGoToDiagnosticLogWithoutLosingInputs()
    {
        await using var writer = new MemolineWriter(_directory, new { }, new() { SuccessfulStatesOnly = true });
        var input = writer.AppendHardware("penBegin", new { x = 10 }, Source);
        foreach (string status in new[] { "unknown", "ambiguous", "error" })
            writer.AppendState("coreStateUpdated", input.Ticks, [input.EventId], new { module = "canvasViewState", status,
                state = (object?)null, error = status == "error" ? "validation failed" : null });
        writer.AppendState("stateResult", input.Ticks, [input.EventId], new { result = new { success = false, message = "raw failure" } });
        foreach (string kind in new[] { "analysisError", "captureUnavailable", "clipParseError", "updateActivatorError" })
            writer.AppendState(kind, input.Ticks, [input.EventId], new { error = "failed" });
        writer.AppendState("coreStateUpdated", input.Ticks, [input.EventId], new { module = "canvasViewState", status = "changed",
            state = new { origin = new[] { 5, 6 } }, rawResult = new { success = true }, error = (string?)null });
        writer.AppendState("coreStateUpdated", input.Ticks, [input.EventId], new { module = "canvasViewState", status = "unchanged",
            state = new { origin = new[] { 5, 6 } } });
        writer.AppendHardware("penEnd", new { x = 10 }, Source);
        await writer.FlushAsync();
        var live = MemolineReader.Read(writer.FilePath).ToArray();
        Assert.Equal(new[] { "header", "penBegin", "coreStateUpdated", "coreStateUpdated", "penEnd" }, live.Select(Kind));
        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, live.Skip(1).Select(f => f.GetProperty("appendId").GetUInt64()));
        using var logReader = new StreamReader(new FileStream(writer.DiagnosticFilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
        var log = logReader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l)).ToArray();
        Assert.Equal(8, log.Length);
        Assert.Contains(log, f => f.GetProperty("data").TryGetProperty("error", out var error) && error.GetString() == "validation failed");
        Assert.All(log, f => Assert.Equal(0ul, f.GetProperty("appendId").GetUInt64()));
    }

    [Fact]
    public async Task SuccessfulStatesOnly_PartialPackagesKeepConfirmedModulesAndNoFailedOrUnfinishedSlots()
    {
        await using var writer = new MemolineWriter(_directory, new { }, new() { SuccessfulStatesOnly = true });
        string initial = writer.ReserveStatePackage(0, 0, "initial");
        var input = writer.AppendHardware("keyInput", new { key = "S" }, Source);
        string failed = writer.ReserveStatePackage(input.EventId, input.Ticks, "failure");
        string mixed = writer.ReserveStatePackage(input.EventId, input.Ticks, "mixed");
        writer.ReserveStatePackage(input.EventId, input.Ticks, "unfinished");
        await writer.FlushAsync();
        Assert.Equal(new[] { "header", "keyInput" }, MemolineReader.Read(writer.FilePath).Select(Kind));
        writer.AppendPackageResult(failed, new { status = "error", updates = new[] { Update("canvasViewState", "error", null) } });
        writer.AppendPackageResult(mixed, new { status = "error", updates = new[] {
            Update("brushState", "changed", "pen"), Update("colorState", "unchanged", "#FFFFFF"),
            Update("canvasViewState", "error", null), Update("currentLayerState", "unknown", null) } });
        writer.AppendPackageResult(initial, new { status = "ambiguous", initial = true, updates = new[] {
            Update("currentLayerState", "changed", "Layer 1"), Update("colorState", "ambiguous", "unconfirmed") } });
        await writer.DisposeAsync();
        var frames = MemolineReader.Read(writer.FilePath).ToArray();
        Assert.Equal(2, frames.Count(f => Kind(f) == "statePackageReserved"));
        var results = frames.Where(f => Kind(f) == "statePackageResult").Select(f => f.GetProperty("data").GetProperty("result")).ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.Equal("changed", r.GetProperty("status").GetString()));
        Assert.Equal(new[] { "brushState", "colorState" }, results[0].GetProperty("updates").EnumerateArray().Select(u => u.GetProperty("module").GetString()));
        Assert.Single(results[1].GetProperty("updates").EnumerateArray());
        var logical = ReadTimeline(frames);
        Assert.Equal(0, logical.GetProperty("pendingStatePackages").GetInt32());
        Assert.Equal(input.EventId, logical.GetProperty("replayableThroughEventId").GetUInt64());
        Assert.All(logical.GetProperty("timeline").EnumerateArray().Where(f => Kind(f) is "initialState" or "statePackage"),
            f => Assert.False(f.GetProperty("replayBlocked").GetBoolean()));
        string diagnostics = File.ReadAllText(writer.DiagnosticFilePath);
        Assert.Contains("sessionEndedBeforeStateResolution", diagnostics);
        Assert.Contains("unconfirmed", diagnostics);
        Assert.DoesNotContain("unconfirmed", string.Join("", frames.Select(f => f.GetRawText())));
    }

    [Fact]
    public async Task SuccessfulStatesOnly_ResultsResolvingOutOfOrderRetainOriginalOrderAtSameInput()
    {
        await using var writer = new MemolineWriter(_directory, new { }, new() { SuccessfulStatesOnly = true });
        var input = writer.AppendHardware("keyInput", new { key = "S" }, Source);
        string first = writer.ReserveStatePackage(input.EventId, input.Ticks, "first");
        string second = writer.ReserveStatePackage(input.EventId, input.Ticks, "second");
        writer.AppendPackageResult(second, new { status = "changed", updates = new[] { Update("colorState", "changed", "second") } });
        writer.AppendPackageResult(first, new { status = "changed", updates = new[] { Update("colorState", "changed", "first") } });
        await writer.DisposeAsync();
        var timeline = ReadTimeline(MemolineReader.Read(writer.FilePath));
        Assert.Equal(new[] { first, second }, timeline.GetProperty("timeline").EnumerateArray().Skip(1)
            .Select(f => f.GetProperty("packageId").GetString()));
        Assert.False(File.Exists(writer.DiagnosticFilePath));
    }

    private static object Update(string module, string status, object? state) =>
        new { module, status, state, error = status == "error" ? "parse failed" : null };

    private static JsonElement ReadTimeline(IEnumerable<JsonElement> frames)
    {
        var timeline = new MemolineTimeline();
        foreach (var frame in frames) timeline.Add(frame);
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output)) { json.WriteStartObject(); timeline.Write(json); json.WriteEndObject(); }
        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task FlushAndIncrementalRead_WorkBeforeCloseAndAcrossRename()
    {
        await using var writer = new MemolineWriter(_directory, new { purpose = "live" });
        writer.AppendHardware("penBegin", new { x = 10, pressure = 100 }, Source);
        await writer.FlushAsync();
        Assert.Equal(2, MemolineReader.Read(writer.FilePath).Count());
        using var reader = MemolineReader.Open(writer.LiveFilePath);
        Assert.Equal(new[] { "header", "penBegin" }, reader.ReadAvailable().Select(Kind));
        Assert.Equal(2u, reader.FormatVersion);
        Assert.False(reader.IsComplete);
        Assert.Empty(reader.ReadAvailable());
        writer.AppendHardware("penEnd", new { x = 11, pressure = 0 }, Source);
        await writer.FlushAsync();
        Assert.Equal("penEnd", Kind(Assert.Single(reader.ReadAvailable())));
        await writer.DisposeAsync();
        Assert.True(File.Exists(writer.FilePath));
        Assert.False(File.Exists(writer.LiveFilePath));
        Assert.Equal("footer", Kind(Assert.Single(reader.ReadAvailable())));
        Assert.True(reader.IsComplete);
        Assert.Empty(reader.ReadAvailable());
        // A client retaining the old .part path can open the completed file too.
        Assert.Equal(4, MemolineReader.Read(writer.LiveFilePath).Count());
    }

    [Fact]
    public async Task Follow_DeliversNewRecordsOnceAndEndsAtFooter()
    {
        await using var writer = new MemolineWriter(_directory, new { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<List<JsonElement>> Consume()
        {
            var frames = new List<JsonElement>();
            await foreach (var frame in MemolineReader.FollowAsync(writer.LiveFilePath,
                TimeSpan.FromMilliseconds(5), timeout.Token))
            {
                frames.Add(frame);
                if (Kind(frame) == "penBegin") observed.TrySetResult();
            }
            return frames;
        }
        var follow = Consume();
        writer.AppendHardware("penBegin", new { x = 10 }, Source);
        await writer.FlushAsync();
        await observed.Task.WaitAsync(timeout.Token);
        writer.AppendHardware("penEnd", new { x = 10 }, Source);
        await writer.DisposeAsync();
        Assert.Equal(new[] { "header", "penBegin", "penEnd", "footer" }, (await follow).Select(Kind));
    }

    [Fact]
    public async Task Follow_IdleRecordingCanBeCancelled()
    {
        await using var writer = new MemolineWriter(_directory, new { });
        await writer.FlushAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var frame in MemolineReader.FollowAsync(writer.LiveFilePath,
                TimeSpan.FromMilliseconds(5), cancellation.Token)) { }
        });
    }

    [Fact]
    public async Task AutomaticFlush_ExposesIdleInputWithoutClosingWriter()
    {
        await using var writer = new MemolineWriter(_directory, new { }, new()
        { DurableFlushInterval = TimeSpan.FromMilliseconds(20) });
        writer.AppendHardware("penBegin", new { x = 10 }, Source);
        using var reader = MemolineReader.Open(writer.LiveFilePath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool seen = false;
        while (!seen)
        {
            seen = reader.ReadAvailable().Any(f => Kind(f) == "penBegin");
            if (!seen) await Task.Delay(5, timeout.Token);
        }
        Assert.False(reader.IsComplete);
    }

    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(2u, true)]
    public void IncompleteFrameHeaderAndBody_AreRetriedAtSameBoundary(uint version, bool compress)
    {
        string path = Path.Combine(_directory, "partial.memoline.part");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        WriteFileHeader(output, version);
        byte[] frame = Frame(version, "{\"kind\":\"sample\",\"data\":\"" + new string('x', 5000) + "\"}", compress);
        int headerSize = version == 1 ? 8 : 16;
        output.Write(frame.AsSpan(0, headerSize - 2));
        output.Flush();
        using var reader = MemolineReader.Open(path);
        Assert.Empty(reader.ReadAvailable());
        Assert.Equal(12, reader.Offset);
        output.Write(frame.AsSpan(headerSize - 2, 3));
        output.Flush();
        Assert.Empty(reader.ReadAvailable());
        Assert.Equal(12, reader.Offset);
        output.Write(frame.AsSpan(headerSize + 1));
        output.Flush();
        Assert.Equal("sample", Kind(Assert.Single(reader.ReadAvailable())));
        Assert.Equal(12 + frame.Length, reader.Offset);
        Assert.Empty(reader.ReadAvailable());
    }

    [Fact]
    public void IncompleteFileHeader_IsRetriedFromZero()
    {
        string path = Path.Combine(_directory, "header.memoline.part");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        output.Write("MEMOLINE"u8);
        output.Flush();
        using var reader = MemolineReader.Open(path);
        Assert.Empty(reader.ReadAvailable());
        Assert.Equal(0, reader.Offset);
        output.Write(new byte[] { 2, 0, 0, 0 });
        output.Flush();
        Assert.Empty(reader.ReadAvailable());
        Assert.Equal(12, reader.Offset);
        Assert.Equal(2u, reader.FormatVersion);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void CorruptStoredPayload_IsRejectedEvenInPartFile(uint version)
    {
        string path = Path.Combine(_directory, "corrupt.memoline.part");
        byte[] frame = Frame(version, "{\"kind\":\"sample\",\"value\":123}", version == 2);
        frame[^1] ^= 0x10;
        using (var stream = File.Create(path)) { WriteFileHeader(stream, version); stream.Write(frame); }
        Assert.Throws<InvalidDataException>(() => MemolineReader.Read(path).ToArray());
    }

    [Theory]
    [InlineData(-1, 20, 0u)]
    [InlineData(20, 67108865, 1u)]
    [InlineData(20, 20, 4u)]
    [InlineData(20, 21, 0u)]
    public void InvalidEncodingOrExcessiveDecompressedSize_IsRejected(int length, int jsonLength, uint flags)
    {
        string path = Path.Combine(_directory, "invalid.memoline.part");
        byte[] header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), flags);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), jsonLength);
        using (var stream = File.Create(path)) { WriteFileHeader(stream, 2); stream.Write(header); }
        Assert.Throws<InvalidDataException>(() => MemolineReader.Read(path).ToArray());
    }

    [Fact]
    public void CompressedFrameWithWrongDecodedLength_IsRejected()
    {
        string path = Path.Combine(_directory, "bad-brotli.memoline.part");
        byte[] frame = Frame(2, "{\"kind\":\"sample\",\"value\":\"" + new string('x', 1000) + "\"}", true);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), 10);
        using (var stream = File.Create(path)) { WriteFileHeader(stream, 2); stream.Write(frame); }
        Assert.Throws<InvalidDataException>(() => MemolineReader.Read(path).ToArray());
    }

    [Fact]
    public void CompleteFileWithoutFooter_IsRejected()
    {
        string path = Path.Combine(_directory, "unfinished.memoline");
        using (var stream = File.Create(path))
        { WriteFileHeader(stream, 1); stream.Write(Frame(1, "{\"kind\":\"header\",\"version\":1}", false)); }
        Assert.Throws<InvalidDataException>(() => MemolineReader.Read(path).ToArray());
    }

    [Fact]
    public void DataAfterFooter_IsRejected()
    {
        string path = Path.Combine(_directory, "extra.memoline.part");
        using (var stream = File.Create(path))
        {
            WriteFileHeader(stream, 2);
            stream.Write(Frame(2, "{\"kind\":\"footer\"}", false));
            stream.WriteByte(1);
        }
        Assert.Throws<InvalidDataException>(() => MemolineReader.Read(path).ToArray());
    }

    [Fact]
    public async Task Compression_PreservesLegacyRecordsAndScreenshotBytes()
    {
        string input = Path.Combine(_directory, "old.memoline"), output = Path.Combine(_directory, "compressed.memoline");
        string image = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("截图证据", 5000))));
        using (var stream = File.Create(input))
        {
            WriteFileHeader(stream, 1);
            stream.Write(Frame(1, "{\"kind\":\"header\",\"version\":1,\"metadata\":{\"name\":\"测试\"}}", false));
            stream.Write(Frame(1, JsonSerializer.Serialize(new { kind = "screenshotBlob", data = new { image } }), false));
            for (int i = 0; i < 100; i++) stream.Write(Frame(1, JsonSerializer.Serialize(new
            { kind = "penSample", eventId = i + 1, data = new { pressure = 123, x = i, text = "原始压力与坐标保留" } }), false));
            stream.Write(Frame(1, "{\"kind\":\"footer\",\"hardwareEvents\":100}", false));
        }
        byte[] original = File.ReadAllBytes(input);
        var result = await MemolineReader.CompactAsync(input, output);
        Assert.Equal(103, result.Records);
        Assert.True(result.OutputBytes < result.InputBytes / 2);
        Assert.Equal(original, File.ReadAllBytes(input));
        var before = MemolineReader.Read(input).ToArray();
        var after = MemolineReader.Read(output).ToArray();
        Assert.Equal(before.Length, after.Length);
        for (int i = 1; i < before.Length; i++)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before[i].GetRawText()), JsonNode.Parse(after[i].GetRawText())));
        Assert.Equal(2, after[0].GetProperty("version").GetInt32());
        Assert.Equal(image, after[1].GetProperty("data").GetProperty("image").GetString());
        await Assert.ThrowsAsync<IOException>(() => MemolineReader.CompactAsync(input, output));
    }

    [Fact]
    public async Task Compression_RefusesToMarkAnUnfinishedRecordingComplete()
    {
        string input = Path.Combine(_directory, "old.memoline.part"), output = Path.Combine(_directory, "copy.memoline");
        using (var stream = File.Create(input))
        { WriteFileHeader(stream, 1); stream.Write(Frame(1, "{\"kind\":\"header\",\"version\":1}", false)); }
        await Assert.ThrowsAsync<InvalidDataException>(() => MemolineReader.CompactAsync(input, output));
        Assert.False(File.Exists(output));
        await MemolineReader.CompactAsync(input, output + ".part");
        Assert.Single(MemolineReader.Read(output + ".part"));
    }

    [Fact]
    public async Task UncompressedOptionAndJsonExport_RoundTrip()
    {
        string path;
        await using (var writer = new MemolineWriter(_directory, new { }, new() { CompressionLevel = CompressionLevel.NoCompression }))
        {
            path = writer.FilePath;
            writer.AppendHardware("penBegin", new { note = "数位笔", pressure = 123 }, Source);
            await writer.FlushAsync(durable: false);
        }
        string output = Path.Combine(_directory, "events.jsonl");
        await MemolineReader.ExportAsync(path, output, default);
        var lines = File.ReadAllLines(output);
        Assert.Equal(3, lines.Length);
        Assert.Equal("数位笔", JsonNode.Parse(lines[1])!["data"]!["note"]!.GetValue<string>());
        Assert.Equal("none", JsonNode.Parse(lines[0])!["compression"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExportAndCompact_CannotOverwriteInputThroughPartPathAlias()
    {
        string path = Path.Combine(_directory, "alias.memoline.part");
        using (var stream = File.Create(path))
        { WriteFileHeader(stream, 1); stream.Write(Frame(1, "{\"kind\":\"header\",\"version\":1}", false)); }
        byte[] original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<ArgumentException>(() => MemolineReader.ExportAsync(path[..^5], path, default));
        await Assert.ThrowsAsync<ArgumentException>(() => MemolineReader.CompactAsync(path[..^5], path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static string? Kind(JsonElement frame) => frame.GetProperty("kind").GetString();

    private static void WriteFileHeader(Stream stream, uint version)
    {
        stream.Write("MEMOLINE"u8);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, version);
        stream.Write(bytes);
    }

    private static byte[] Frame(uint version, string text, bool compress)
    {
        byte[] json = Encoding.UTF8.GetBytes(text), payload = json;
        if (compress)
        {
            using var buffer = new MemoryStream();
            using (var encoder = new BrotliStream(buffer, CompressionLevel.Fastest, true)) encoder.Write(json);
            payload = buffer.ToArray();
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(payload.Length);
        if (version == 2) { writer.Write(compress ? 1u : 0u); writer.Write(json.Length); }
        writer.Write(Crc32.HashToUInt32(payload));
        writer.Write(payload);
        return stream.ToArray();
    }
}
