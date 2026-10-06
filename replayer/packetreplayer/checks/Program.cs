using System.Text.Json;
using System.IO.Compression;
using MemolineDemo;

try
{
if (args.Length == 3 && args[0] == "--render-ui")
{
    var renderThread = new Thread(() => PacketChecks.RenderUi(args[1], args[2]));
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start(); renderThread.Join();
}
else if (args.Length == 2 && args[0] == "--check-config")
{
    await PacketChecks.CheckPublishedConfigAsync(args[1]);
}
else if (args.Length == 3 && args[0] == "--check-subtools")
{
    await PacketChecks.CheckPublishedSubtoolsAsync(args[1], args[2]);
}
else if (args.Length == 2 && args[0] == "--dump")
{
    var path = args[1];
    Console.WriteLine(JsonSerializer.Serialize(BundleArchive.Verify(path)));
    var frames = BundleArchive.ReadMechanical(path).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(frames.GroupBy(f => f.GetProperty("kind").GetString()).ToDictionary(g => g.Key!, g => g.Count())));
    foreach (var group in frames.Where(f => f.GetProperty("kind").GetString() == "coreStateUpdated").GroupBy(f => f.GetProperty("data").GetProperty("module").GetString()))
    {
        Console.WriteLine("CORE " + group.Key + " count=" + group.Count());
        foreach (var f in group.Take(2))
        {
            var d = f.GetProperty("data");
            Console.WriteLine(JsonSerializer.Serialize(new { appendId = f.GetProperty("appendId"), ticks = f.GetProperty("ticks"), data = d }));
        }
    }
    Console.WriteLine("INIT " + frames.FirstOrDefault(f => f.GetProperty("kind").GetString() == "initializationConfiguration"));
    foreach (var f in frames.Where(f => f.GetProperty("kind").GetString() == "shortcutResolved").Take(8)) Console.WriteLine("SHORTCUT " + f);
    using var zip = ZipFile.OpenRead(path);
    using var indexStream = zip.GetEntry("index.json")!.Open();
    using var index = JsonDocument.Parse(indexStream);
    var meta = index.RootElement.GetProperty("aggregation");
    var codec = meta.TryGetProperty("codec", out var c) ? c.GetString() : null;
    using var encoded = zip.GetEntry(meta.GetProperty("entry").GetString() + (codec == "brotli" ? ".br" : ""))!.Open();
    using var decoded = codec == "brotli" ? new BrotliStream(encoded, CompressionMode.Decompress) : (Stream)encoded;
    using var reader = new StreamReader(decoded);
    while (reader.ReadLine() is { } line)
    {
        using var row = JsonDocument.Parse(line);
        if (row.RootElement.GetProperty("kind").GetString() == "packet")
        {
            var p = row.RootElement;
            Console.WriteLine("PACKET " + JsonSerializer.Serialize(p.EnumerateObject().Where(x => x.Name != "dirtyMatrices" && x.Name != "manifest" && x.Name != "eventPointers").ToDictionary(x => x.Name, x => x.Value)));
            Console.WriteLine("POINTERS count=" + p.GetProperty("eventPointers").GetArrayLength() + " " + JsonSerializer.Serialize(p.GetProperty("eventPointers").EnumerateArray().Take(3)));
        }
    }
}
else
{
    await PacketChecks.RunAsync(args.Length > 0 ? args[0] : null);
}
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
