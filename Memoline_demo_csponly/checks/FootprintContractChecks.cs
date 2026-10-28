using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CanvasLayerWatcher;
using DirtyMatrix.Core;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class FootprintContractChecks
{
    internal static int Run()
    {
        int count = 0;
        var failures = new List<string>();
        void Check(bool value, string message)
        { count++; if (!value) { failures.Add(message); Console.WriteLine("FAIL: " + message); } }
        var matrices = JsonSerializer.SerializeToElement(new
        {
            screenToCanvas = new[] { 0, .25, 5, -.25, 0, -2.5 },
            canvasToScreen = new[] { 0, -4.0, -10, 4, 0, -20 },
            convention = "sct-affine-row-major/v1", canvasSpace = "normalized"
        });
        JsonElement View(JsonElement matrix, bool semantic = false) => JsonSerializer.SerializeToElement(new
        {
            module = "canvasViewState", status = "changed",
            state = new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0,
                transform = new { canvasPixelWidth = 8, canvasPixelHeight = 8, matrices = semantic ? (JsonElement?)matrix : null } },
            rawResult = new { snapshot = new { matrices = semantic ? (JsonElement?)null : matrix } },
            evidence = new { observedAfterEventId = 0 }
        });
        var rawView = View(matrices);
        var index = EditIndexBuilder.Build(EditIndexHardeningChecks.NativeMatrixFixture(rawView));
        Check(index.Strokes.Count == 1 && index.Strokes.Values.Single().ProbeMask.Contains(2, 1),
            "Native Probe consumes the published normalized matrix even when OCR geometry is a different estimate.");
        var semanticIndex = EditIndexBuilder.Build(EditIndexHardeningChecks.NativeMatrixFixture(View(matrices, semantic: true)));
        Check(semanticIndex.Strokes.Count == 1 && semanticIndex.Strokes.Values.Single().ProbeMask.Contains(2, 1),
            "Native Probe consumes the semantic matrix fallback when the raw snapshot does not publish it.");
        foreach (string invalid in new[] { "convention", "canvasSpace", "inverse" })
        {
            var node = JsonNode.Parse(matrices.GetRawText())!.AsObject();
            if (invalid == "inverse") node["canvasToScreen"]![2] = -11;
            else node[invalid] = "unsupported";
            Check(EditIndexBuilder.Build(EditIndexHardeningChecks.NativeMatrixFixture(View(JsonSerializer.SerializeToElement(node)))).Strokes.Count == 0,
                "Native Probe rejects an invalid published affine contract: " + invalid);
        }

        var evidence = MakeEvidence(rawView, "mm", 144, 25, true);
        var label = evidence.Labels.Single();
        var described = LayerDiff.Describe(evidence, new(8, 8));
        var manifest = JsonSerializer.SerializeToElement(new { labels = described }, SnapshotHistory.Json);
        var frozen = manifest.GetProperty("labels")[0].TryGetProperty("coverageSettings", out var recipe) ? recipe : default;
        Check(frozen.ValueKind == JsonValueKind.Object,
            "Watcher labels serialize the actual CoverageSettings recipe used to compute the frozen radius.");
        if (frozen.ValueKind == JsonValueKind.Object)
        {
            // The zoom is the canvas mapping's own (4 screen px per normalized unit on an 8 px wide canvas = 50 %), not the
            // navigator digits (25 here): OCR misread 18.6 % as "6" (memolinetest13) and inflated every coverage radius.
            Check(frozen.GetProperty("brushUnit").GetString() == "mm" && frozen.GetProperty("dpi").GetDouble() == 144 &&
                frozen.GetProperty("zoomPercent").GetDouble() == 50 && frozen.GetProperty("specifyBySizeOnScreen").GetBoolean(),
                "The recipe preserves mm, DPI, zoom and screen-fixed brush sizing.");
            var restored = ReadLabels(manifest).Single();
            Check(restored.CoverageSettings is { } settings && settings.Radius == label.Coverage.Radius,
                "LiveSessionReader preserves the exact serialized recipe and verifies its original radius.");
            foreach (string field in new[] { "brushSize", "brushUnit", "dpi", "zoomPercent", "specifyBySizeOnScreen",
                "brushEnvelopeFactor", "antiAliasRadius", "safetyRadius", "coveragePaddingScreenPixels" })
            {
                var broken = JsonNode.Parse(manifest.GetRawText())!.AsObject();
                broken["labels"]![0]!["coverageSettings"]!.AsObject().Remove(field);
                bool rejected = false;
                try { ReadLabels(JsonSerializer.SerializeToElement(broken)); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "A declared recipe missing " + field + " cannot inherit a default value.");
            }
            var inconsistent = JsonNode.Parse(manifest.GetRawText())!.AsObject();
            inconsistent["labels"]![0]!["coverageSettings"]!["brushSize"] = 4;
            bool mismatched = false;
            try { ReadLabels(JsonSerializer.SerializeToElement(inconsistent)); }
            catch (InvalidDataException) { mismatched = true; }
            Check(mismatched, "A declared recipe whose radius disagrees with the frozen label is rejected.");
        }
        else Check(false, "The absent recipe cannot be round-tripped through LiveSessionReader.");
        var legacy = JsonNode.Parse(manifest.GetRawText())!.AsObject();
        legacy["labels"]![0]!.AsObject().Remove("coverageSettings");
        Check(ReadLabels(JsonSerializer.SerializeToElement(legacy)).Single().CoverageSettings is null,
            "Legacy labels remain readable with an explicitly absent recipe for UnsupportedOverride handling.");
        // Word-level union and fill against the pixel-by-pixel definition, at unaligned offsets and widths.
        var random = new Random(20261013);
        bool unionsMatch = true;
        for (int trial = 0; trial < 300 && unionsMatch; trial++)
        {
            var masks = Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
            {
                int left = random.Next(-5, 140), top = random.Next(-5, 40), width = random.Next(0, 150), height = random.Next(0, 9);
                double density = random.NextDouble();
                return PixelMask.FromPredicate(left, top, width, height, (_, _) => random.NextDouble() < density);
            }).ToArray();
            var union = Footprints.UnionBounded(masks);
            var nonEmpty = masks.Where(m => !m.IsEmpty).ToArray();
            if (nonEmpty.Length == 0) { unionsMatch &= union.IsEmpty; continue; }
            int l = nonEmpty.Min(m => m.Left), t = nonEmpty.Min(m => m.Top);
            var expected = new PixelMask(l, t, nonEmpty.Max(m => m.Left + m.Width) - l, nonEmpty.Max(m => m.Top + m.Height) - t);
            foreach (var mask in nonEmpty) foreach (var (x, y) in mask.Pixels()) expected.Set(x, y);
            unionsMatch &= union.Equals(expected) && union.Count == expected.Count;
        }
        Check(unionsMatch, "Bounded union ORs whole words yet equals the pixel-by-pixel union.");
        Check(Enumerable.Range(0, 130).All(width => PixelMask.Filled(3, -2, width, 3) is var filled &&
                filled.Equals(PixelMask.FromPredicate(3, -2, width, 3, (_, _) => true)) && filled.Count == width * 3L),
            "A filled mask equals one set pixel by pixel, including partial last words.");
        bool runsMatch = true;
        for (int trial = 0; trial < 300 && runsMatch; trial++)
        {
            int left = random.Next(-3, 9), top = random.Next(-3, 4), width = random.Next(1, 200), height = random.Next(1, 6);
            PixelMask runs = new(left, top, width, height), pixels = new(left, top, width, height);
            for (int i = 0; i < 8; i++)
            {
                int y = top + random.Next(height), x0 = left + random.Next(width), x1 = Math.Min(left + width - 1, x0 + random.Next(-2, 140));
                runs.SetRun(x0, x1, y);
                for (int x = x0; x <= x1; x++) pixels.Set(x, y);
            }
            runsMatch &= runs.Equals(pixels) && runs.Count == pixels.Count;
        }
        Check(runsMatch, "Setting a row run a word at a time equals setting its pixels one by one, overlaps included.");
        bool intersectionsMatch = true; int intersecting = 0;
        for (int trial = 0; trial < 2000 && intersectionsMatch; trial++)
        {
            PixelMask Sparse()
            {
                int left = random.Next(-70, 70), top = random.Next(-6, 6), width = random.Next(0, 200), height = random.Next(0, 8);
                double density = random.Next(3) == 0 ? 0 : Math.Pow(random.NextDouble(), 3) * 0.05;
                var mask = PixelMask.FromPredicate(left, top, width, height, (_, _) => random.NextDouble() < density);
                if (width > 0 && height > 0 && random.Next(2) == 0) mask.Set(left + random.Next(width), top + random.Next(height));
                return mask;
            }
            var a = Sparse(); var b = random.Next(8) == 0 ? a : Sparse();
            bool expected = a.Pixels().Any(p => b.Contains(p.X, p.Y));
            intersecting += expected ? 1 : 0;
            intersectionsMatch &= a.Intersects(b) == expected && b.Intersects(a) == expected && PreciseUndoSelector.Intersects(a, b) == expected;
        }
        Check(intersectionsMatch && intersecting is > 100 and < 1900, "Word-level intersection agrees with the pixel definition at unaligned bounds.");
        if (failures.Count > 0) throw new InvalidOperationException(
            $"Footprint contract checks: {count - failures.Count}/{count} passed.\n" + string.Join("\n", failures));
        Console.WriteLine($"Footprint contract checks passed: {count}.");
        return count;
    }

    private static DirtyEvidence MakeEvidence(JsonElement view, string unit, double dpi, double scale, bool screenFixed)
    {
        var node = JsonNode.Parse(view.GetRawText())!.AsObject();
        node["state"]!["ocrScalePercent"] = scale;
        var states = new[]
        {
            new RecognizerState("view", "core.canvasViewState", 0, 0, JsonSerializer.SerializeToElement(node)),
            new RecognizerState("layer", "core.currentLayerState", 0, 0,
                JsonSerializer.SerializeToElement(new { status = "changed", state = "Ink" })),
            new RecognizerState("brush", "core.brushState", 0, 0, JsonSerializer.SerializeToElement(new
            { status = "changed", state = new { name = "G pen", properties = new object[]
                { new { key = "brush_size", value = .8, unit }, new { key = "brush_size.specify_by_size_on_screen", value = screenFixed } } } })),
            new RecognizerState("clip", "core.clipState", 0, 0,
                JsonSerializer.SerializeToElement(new { status = "changed", state = new { canvas = new { resolution = dpi } } }))
        };
        var inputs = new[] { new InputObservation(1, "tablet", "penBegin", 2,
            JsonSerializer.SerializeToElement(new { x = -10.75, y = -19.25, inCsp = true }), default,
            PenDownLocation: new("canvasViewport", "Canvas", -10.75, -19.25, "cursor", "matched")) };
        return RecognitionEvidence.Build(new("stage4", 1000, 0, 2, true, states, inputs, []),
            new(1, 2, "Ink", new(0, 0, 100, 0), [2]), new(8, 8));
    }

    private static IReadOnlyList<LabelEvidence> ReadLabels(JsonElement manifest)
    {
        var method = typeof(LiveSessionReader).GetMethod("ReadLabels", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { return (IReadOnlyList<LabelEvidence>)method.Invoke(null, [manifest])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException invalid) { throw invalid; }
    }
}
