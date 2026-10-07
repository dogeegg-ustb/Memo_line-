using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DirtyMatrixPaster;
using MemolineDemo.Editing;

namespace MemolineDemo;

/// <summary>Compares the production C# compositor with engine.mjs --compose on actual PNG packets.</summary>
internal static class CompositorParityChecks
{
    private const int Width = 8, Height = 5;
    private static readonly LayerKey Layer = new("composition-parity", 1, "parity-layer");
    private sealed record Patch(int Left, int Top, int Width, int Height, byte[] Rgba, byte[] Mask);
    private sealed record Group(string Id, long Ticks, IReadOnlyList<Patch> Patches);
    private sealed record Scenario(string Name, IReadOnlyList<Group> Groups, bool Reject = false);
    private sealed record CaseData(Scenario Scenario, IReadOnlyList<LayerPacket> Packets,
        IReadOnlyDictionary<string, ImageRgba> HistoricalBefore, string[] Inputs);

    internal static async Task<int> RunAsync(string root)
    {
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; }
        string backend = LocateBackend();
        string runner = Path.Combine(backend, "checks", "compositor-parity-runner.mjs");
        if (!File.Exists(runner)) throw new FileNotFoundException("C#/Node production parity runner is missing; parity cannot be skipped.", runner);
        string fixtures = Path.Combine(root, "compositor-parity");
        Directory.CreateDirectory(fixtures);
        var cases = Scenarios().Select(s => WriteCase(fixtures, s)).ToArray();
        var node = await RunNodeAsync(backend, runner, cases);
        var results = node.GetProperty("cases").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);
        Check(results.Count == cases.Length, "Node must return every requested parity scenario.");
        foreach (var data in cases)
        {
            var scenario = data.Scenario;
            var result = results[scenario.Name];
            var actual = new ImageRgba(Width, Height, new byte[Width * Height * 4]);
            var known = new PixelMask(Width, Height);
            bool csharpRejected = false;
            try
            {
                foreach (var packet in data.Packets.OrderBy(p => p.TriggerTicks))
                {
                    ImageCompositor.ValidateGroup(packet.Images, Width, Height);
                    foreach (var image in packet.Images)
                    {
                        ImageCompositor.Replace(actual, image);
                        foreach (var (x, y) in image.Mask.Pixels()) known.Set(x, y);
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                csharpRejected = true;
                Check(ex.Message.Contains("冲突", StringComparison.Ordinal), scenario.Name + ": C# rejection must identify overlap conflict.");
            }
            bool nodeRejected = result.GetProperty("status").GetString() == "rejected";
            Check(csharpRejected == scenario.Reject && nodeRejected == scenario.Reject,
                scenario.Name + ": C# and the actual Node compositor must agree on acceptance/rejection.");
            if (scenario.Reject)
            {
                Check(result.GetProperty("exitCode").GetInt32() != 0, scenario.Name + ": the actual Node CLI must fail for a conflicting overlap.");
                Check(result.GetProperty("message").GetString()!.Contains("冲突", StringComparison.Ordinal),
                    scenario.Name + ": Node must reject the overlap, not an unrelated fixture error.");
                try { _ = new LayerTimeline(data.Packets, actual); }
                catch (InvalidDataException) { count++; continue; }
                throw new InvalidOperationException(scenario.Name + ": LayerTimeline accepted the conflicting group.");
            }
            Check(result.GetProperty("status").GetString() == "ready" && result.GetProperty("exitCode").GetInt32() == 0,
                scenario.Name + ": the actual Node CLI returned a complete composition and exit code zero.");
            var canvas = result.GetProperty("canvas");
            Check(canvas.GetProperty("width").GetInt32() == Width && canvas.GetProperty("height").GetInt32() == Height,
                scenario.Name + ": Node canvas dimensions match the full C# canvas.");
            byte[] nodeRgba = Expand(result, "rgba", channels: 4);
            byte[] nodeMask = Expand(result, "mask", channels: 1);
            Check(actual.Bytes.AsSpan().SequenceEqual(nodeRgba), scenario.Name + ": full-canvas straight RGBA differs from production Node output.");
            Check(nodeMask.Select((value, pixel) => value == (known.Contains(pixel % Width, pixel / Width) ? 255 : 0)).All(equal => equal),
                scenario.Name + ": full-canvas masks differ from the production PixelMask union.");
            Check(result.GetProperty("changedPixels").GetInt64() == known.Count, scenario.Name + ": mask count matches Node metadata.");
            var timeline = new LayerTimeline(data.Packets, new(Width, Height, nodeRgba));
            Check(timeline.VerifyRoundTrip(), scenario.Name + ": Node's final pixels reverse and forward through production LayerTimeline exactly.");
            foreach (var packet in data.Packets)
                Check(timeline.BeforePacket(packet.PacketId).Bytes.AsSpan().SequenceEqual(data.HistoricalBefore[packet.PacketId].Bytes),
                    scenario.Name + ": historical boundary differs before " + packet.PacketId);
        }
        Console.WriteLine($"C#/Node production compositor parity passed: {count} assertions across {cases.Length} scenarios. No CSP or clipboard input.");
        return count;
    }

    private static string LocateBackend()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "replayer", "dirty matrix paster", "backend");
                if (File.Exists(Path.Combine(candidate, "composition.mjs"))) return candidate;
            }
        throw new DirectoryNotFoundException("Cannot locate production dirty matrix composition.mjs for parity checks.");
    }

    private static async Task<JsonElement> RunNodeAsync(string backend, string runner, IReadOnlyList<CaseData> cases)
    {
        var start = new ProcessStartInfo("node") { WorkingDirectory = backend, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add(runner);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Node process could not start."); }
        catch (Win32Exception ex) { throw new InvalidOperationException("Node is required for production compositor parity checks and was not found or could not start.", ex); }
        using (process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { cases = cases.Select(c => new { name = c.Scenario.Name, inputs = c.Inputs }) }));
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout, error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("Production Node parity runner failed: " + error);
            using var parsed = JsonDocument.Parse(output);
            return parsed.RootElement.Clone();
        }
    }

    // Expands only Node's cropped transport output; no replacement or blend algorithm is implemented here.
    private static byte[] Expand(JsonElement result, string field, int channels)
    {
        var bounds = result.GetProperty("bounds");
        int left = bounds.GetProperty("left").GetInt32(), top = bounds.GetProperty("top").GetInt32();
        int width = bounds.GetProperty("right").GetInt32() - left, height = bounds.GetProperty("bottom").GetInt32() - top;
        byte[] patch = Convert.FromBase64String(result.GetProperty(field).GetString()!);
        if (left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > Width || top + height > Height || patch.Length != width * height * channels)
            throw new InvalidDataException("Node returned invalid cropped parity output.");
        var full = new byte[Width * Height * channels];
        for (int y = 0; y < height; y++) Array.Copy(patch, y * width * channels, full, ((y + top) * Width + left) * channels, width * channels);
        return full;
    }

    private static CaseData WriteCase(string root, Scenario scenario)
    {
        string caseRoot = Path.Combine(root, scenario.Name);
        var packets = new List<LayerPacket>();
        var before = new Dictionary<string, ImageRgba>();
        var inputs = new List<string>();
        var cursor = new ImageRgba(Width, Height, new byte[Width * Height * 4]);
        foreach (var group in scenario.Groups.OrderBy(g => g.Ticks))
        {
            string directory = Path.Combine(caseRoot, group.Id); Directory.CreateDirectory(directory); inputs.Add(directory);
            before.Add(group.Id, cursor.Copy());
            var images = new List<DirtyImage>();
            var manifests = new List<object>();
            var labels = new List<object>();
            for (int i = 0; i < group.Patches.Count; i++)
            {
                var patch = group.Patches[i]; string id = "image-" + i, label = "label-" + i;
                if (patch.Rgba.Length != patch.Width * patch.Height * 4 || patch.Mask.Length != patch.Width * patch.Height || patch.Mask.Any(m => m != 0 && m != 255))
                    throw new InvalidDataException("Parity fixture dimensions or mask values are invalid.");
                var mask = new PixelMask(patch.Left, patch.Top, patch.Width, patch.Height);
                var maskRgba = new byte[patch.Rgba.Length]; var old = new byte[patch.Rgba.Length];
                for (int y = 0; y < patch.Height; y++)
                    cursor.Bytes.AsSpan(((y + patch.Top) * Width + patch.Left) * 4, patch.Width * 4).CopyTo(old.AsSpan(y * patch.Width * 4));
                for (int pixel = 0; pixel < patch.Mask.Length; pixel++)
                {
                    if (patch.Mask[pixel] == 255) { mask.Set(patch.Left + pixel % patch.Width, patch.Top + pixel / patch.Width); maskRgba.AsSpan(pixel * 4, 4).Fill(255); }
                    else { maskRgba[pixel * 4] = 77; maskRgba[pixel * 4 + 1] = 88; maskRgba[pixel * 4 + 2] = 99; }
                }
                images.Add(new(new(Layer, group.Id, id), mask, new(patch.Width, patch.Height, old), new(patch.Width, patch.Height, patch.Rgba), []));
                File.WriteAllBytes(Path.Combine(directory, id + "-now.png"), ClipboardImageCodec.EncodePng(patch.Width, patch.Height, patch.Rgba));
                File.WriteAllBytes(Path.Combine(directory, id + "-after.png"), ClipboardImageCodec.EncodePng(patch.Width, patch.Height, old));
                File.WriteAllBytes(Path.Combine(directory, id + "-mask.png"), ClipboardImageCodec.EncodePng(patch.Width, patch.Height, maskRgba));
                manifests.Add(new { id, image = id + "-now.png", nowImage = id + "-now.png", afterImage = id + "-after.png", maskImage = id + "-mask.png",
                    bounds = new { left = patch.Left, top = patch.Top, right = patch.Left + patch.Width, bottom = patch.Top + patch.Height, width = patch.Width, height = patch.Height },
                    changedPixels = mask.Count, labelIds = new[] { label } });
                labels.Add(new { id = label, imageIds = new[] { id }, impactRange = new { canvasWidth = Width, canvasHeight = Height, tileSize = 1, originX = 0, originY = 0,
                    rowRuns = Enumerable.Range(patch.Top, patch.Height).Select(row => new { row, startColumn = patch.Left, endColumnExclusive = patch.Left + patch.Width }) } });
            }
            object Descriptor(string id) => new { id, width = Width, height = Height, triggerTicks = group.Ticks, sessionId = Layer.SessionId, generation = Layer.Generation,
                layer = new { id = 1, uuid = Layer.Uuid, name = "Parity layer" } };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { schema = "dirty-matrix-image-diff/v1", kind = "diff", comparison = "fullResolution/v2",
                id = group.Id, after = Descriptor(group.Id + "-before"), now = Descriptor(group.Id + "-now"), recognizer = new { frequency = 1000 }, images = manifests, labels }));
            var packet = new LayerPacket(group.Id, Layer, group.Ticks, null, null, images, CanvasWidth: Width, CanvasHeight: Height);
            packets.Add(packet);
            if (!scenario.Reject)
            {
                ImageCompositor.ValidateGroup(images, Width, Height);
                foreach (var image in images) ImageCompositor.Replace(cursor, image);
            }
        }
        // The production Node compositor must discover chronological order from packet metadata.
        inputs.Reverse(); packets.Reverse();
        return new(scenario, packets, before, inputs.ToArray());
    }

    private static IReadOnlyList<Scenario> Scenarios()
    {
        static Patch Pixels(int left, int top, int width, int height, int phase, bool sparse = false)
        {
            var rgba = new byte[width * height * 4]; var mask = new byte[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x, global = (top + y) * Width + left + x;
                rgba[pixel * 4] = (byte)(11 + global * 3 + phase); rgba[pixel * 4 + 1] = (byte)(22 + global + phase * 7);
                rgba[pixel * 4 + 2] = (byte)(33 + global * 2 + phase * 11);
                rgba[pixel * 4 + 3] = new byte[] { 0, 1, 83, 255 }[(global + phase) % 4];
                mask[pixel] = sparse && pixel % 3 == 1 ? (byte)0 : (byte)255;
            }
            return new(left, top, width, height, rgba, mask);
        }
        var opaque = Pixels(1, 1, 3, 2, 0, sparse: true);
        for (int i = 3; i < opaque.Rgba.Length; i += 4) opaque.Rgba[i] = 255;
        var erased = Pixels(1, 1, 2, 2, 1, sparse: true); erased.Rgba[3] = 0;
        Patch equalA = Pixels(2, 1, 3, 2, 0), equalB = Pixels(3, 1, 3, 2, 0);
        var badVisible = Pixels(3, 1, 3, 2, 0); badVisible.Rgba[0] ^= 1;
        var hiddenA = new Patch(2, 2, 2, 1, [11, 22, 33, 0, 44, 55, 66, 1], [255, 255]);
        var hiddenEqual = new Patch(2, 2, 1, 1, [11, 22, 33, 0], [255]);
        var hiddenConflict = new Patch(2, 2, 1, 1, [11, 22, 34, 0], [255]);
        return [
            new("multitime-sparse-erasure", [new("first", 10, [opaque, Pixels(6, 3, 2, 2, 3, sparse: true)]),
                new("second", 20, [erased]), new("third", 30, [Pixels(3, 1, 1, 3, 2)])]),
            new("nonoverlapping-cropped", [new("one", 10, [Pixels(1, 2, 2, 2, 1, sparse: true), Pixels(5, 1, 2, 2, 2, sparse: true)])]),
            new("same-group-equal-overlap", [new("one", 10, [equalA, equalB])]),
            new("same-group-conflicting-overlap", [new("one", 10, [equalA, badVisible])], Reject: true),
            new("same-group-hidden-rgb-equal", [new("one", 10, [hiddenA, hiddenEqual])]),
            new("same-group-hidden-rgb-conflict", [new("one", 10, [hiddenA, hiddenConflict])], Reject: true),
            new("multitime-alpha1-and-overwrite", [new("first", 3, [Pixels(2, 1, 3, 3, 1)]), new("last", 90, [Pixels(2, 1, 3, 3, 2, sparse: true)])])
        ];
    }
}
