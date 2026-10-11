using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemolineDemo;

string input = Path.GetFullPath(args[0]);
string root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var timer = Stopwatch.StartNew();
var verified = BundleArchive.Verify(input);
double verifySeconds = timer.Elapsed.TotalSeconds;
using var zip = ZipFile.OpenRead(input);
using var indexStream = zip.GetEntry("index.json")!.Open();
using var index = JsonDocument.Parse(indexStream);
string Extract(string key, string output)
{
    var info = index.RootElement.GetProperty(key);
    string name = info.GetProperty("entry").GetString()!;
    bool compressed = info.TryGetProperty("codec", out var codec) && codec.GetString() == "brotli";
    string destination = Path.Combine(root, output);
    using var source = zip.GetEntry(name + (compressed ? ".br" : ""))!.Open();
    using var target = File.Create(destination);
    if (compressed) { using var decoder = new BrotliStream(source, CompressionMode.Decompress); decoder.CopyTo(target); }
    else source.CopyTo(target);
    return destination;
}
string mechanical = Extract("mechanical", "mechanical.memoline"), aggregation = Extract("aggregation", "aggregation.jsonl");
var native = new Dictionary<string, Totals>();
var aggregate = new Dictionary<string, Totals>();
var fields = new Dictionary<string, long>();
var images = new Dictionary<string, (string Panel, string Hash, long Bytes)>();
var strings = new Dictionary<string, (int Count, long Bytes, HashSet<string> Locations)>();
void Walk(JsonElement item, string path)
{
    if (item.ValueKind == JsonValueKind.Object)
        foreach (var property in item.EnumerateObject()) Walk(property.Value, path + "." + property.Name);
    else if (item.ValueKind == JsonValueKind.Array)
        foreach (var value in item.EnumerateArray()) Walk(value, path + "[]");
    else if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: >= 4096 } value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (strings.TryGetValue(hash, out var old))
        { old.Locations.Add(path); strings[hash] = (old.Count + 1, old.Bytes, old.Locations); }
        else strings[hash] = (1, bytes.Length, new HashSet<string> { path });
    }
}
void NativeFrame(JsonElement frame, int stored, int decoded)
{
    string kind = frame.GetProperty("kind").GetString()!;
    string group = kind;
    if (frame.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
    {
        if (kind == "coreStateUpdated" && data.TryGetProperty("module", out var module)) group += "/" + module.GetString();
        if (kind == "screenshotBlob")
        {
            string id = data.GetProperty("screenshotId").GetString()!;
            images.TryAdd(id, (data.GetProperty("panel").GetString()!, data.GetProperty("sha256").GetString()!, data.GetProperty("byteLength").GetInt64()));
        }
        foreach (var property in data.EnumerateObject())
        {
            string key = group + ".data." + property.Name;
            fields[key] = fields.GetValueOrDefault(key) + Encoding.UTF8.GetByteCount(property.Value.GetRawText());
        }
        Walk(data, "native." + group + ".data");
    }
    if (!native.TryGetValue(group, out var total)) native[group] = total = new();
    total.Count++; total.StoredBytes += stored; total.JsonBytes += decoded;
}
using (var file = File.OpenRead(mechanical))
using (var reader = new BinaryReader(file))
{
    if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "MEMOLINE") throw new Exception("Invalid native header");
    uint version = reader.ReadUInt32();
    while (file.Position < file.Length)
    {
        int length = reader.ReadInt32();
        uint flags = version == 1 ? 0 : reader.ReadUInt32();
        int decoded = version == 1 ? length : reader.ReadInt32();
        reader.ReadUInt32();
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new Exception("Truncated native frame");
        if (flags == 1)
        {
            using var source = new MemoryStream(bytes);
            using var decoder = new BrotliStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream(decoded); decoder.CopyTo(target); bytes = target.ToArray();
        }
        using var doc = JsonDocument.Parse(bytes);
        NativeFrame(doc.RootElement, length + (version == 1 ? 8 : 16), decoded);
    }
}
foreach (string line in File.ReadLines(aggregation))
{
    using var doc = JsonDocument.Parse(line);
    string kind = doc.RootElement.GetProperty("kind").GetString()!;
    if (!aggregate.TryGetValue(kind, out var total)) aggregate[kind] = total = new();
    total.Count++; total.JsonBytes += Encoding.UTF8.GetByteCount(line) + 1;
    Walk(doc.RootElement, "aggregate." + kind);
}
var screenshotStats = images.Values.GroupBy(value => value.Panel).Select(panel => new {
    panel = panel.Key, count = panel.Count(), unique = panel.Select(x => x.Hash).Distinct().Count(),
    pngBytes = panel.Sum(x => x.Bytes), duplicatePngBytes = panel.GroupBy(x => x.Hash).Sum(g => g.Skip(1).Sum(x => x.Bytes))
}).OrderByDescending(x => x.pngBytes).ToArray();
Console.WriteLine("Analysis complete; benchmarking fast seal...");
timer.Restart();
string fast = Path.Combine(root, "fast-session.memoline");
var seal = BundleArchive.Seal(mechanical, aggregation, fast);
double sealSeconds = timer.Elapsed.TotalSeconds;
var fastVerified = BundleArchive.Verify(fast);
if (verified.MechanicalSha256 != fastVerified.MechanicalSha256 || verified.AggregationSha256 != fastVerified.AggregationSha256
    || verified.FixedSha256 != fastVerified.FixedSha256) throw new Exception("Fast seal changed source bytes or fixed packets");
var report = new {
    source = input, originalProjectBytes = new FileInfo(input).Length, nativeBytes = new FileInfo(mechanical).Length,
    aggregationBytes = new FileInfo(aggregation).Length, originalVerifySeconds = verifySeconds, fastSealSeconds = sealSeconds,
    fastSessionBytes = new FileInfo(fast).Length,
    originalActiveEntries = zip.Entries.Where(e => e.FullName is "mechanical/recording.memoline.br" or "aggregation/events.jsonl.br")
        .Select(e => new { e.FullName, e.Length, e.CompressedLength }).ToArray(),
    native = native.OrderByDescending(x => x.Value.StoredBytes).Select(x => new { kind = x.Key, x.Value.Count, x.Value.StoredBytes, x.Value.JsonBytes }).ToArray(),
    aggregation = aggregate.OrderByDescending(x => x.Value.JsonBytes).Select(x => new { kind = x.Key, x.Value.Count, x.Value.JsonBytes }).ToArray(),
    dataFields = fields.OrderByDescending(x => x.Value).Take(20).Select(x => new { field = x.Key, bytes = x.Value }).ToArray(),
    screenshots = screenshotStats,
    repeatedLargeStrings = strings.Values.Where(x => x.Count > 1).OrderByDescending(x => x.Bytes * (x.Count - 1)).Take(15)
        .Select(x => new { x.Count, x.Bytes, redundantJsonBytes = x.Bytes * (x.Count - 1), locations = x.Locations.Order().ToArray() }).ToArray(),
    repeatedLargeStringsTotalBytes = strings.Values.Sum(x => x.Bytes * (x.Count - 1)),
    preservedHashes = new { seal.MechanicalSha256, seal.AggregationSha256, seal.FixedSha256 }
};
File.WriteAllText(Path.Combine(root, "size-analysis.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { report.fastSealSeconds, report.fastSessionBytes, report.nativeBytes, report.aggregationBytes, report.screenshots }));

sealed class Totals
{
    public int Count;
    public long StoredBytes, JsonBytes;
}
