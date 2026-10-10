using MemolineDemo.Editing;
using System.Text.Json;

namespace MemolineDemo;

internal static class ReconstructionCrossLayerChecks
{
    private const int Width = 8;
    private static readonly LayerKey X = new("cross-reference", 1, "x");
    private static readonly LayerKey Y = new("cross-reference", 1, "y");
    private static readonly LayerKey Z = new("cross-reference", 1, "z");

    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) failures.Add(message);
        }

        // Y at t20 must read Z's t0 value 40, before Z's t40 value 90.
        {
            var scene = new Scene();
            var z0 = scene.Stroke(Z, 1, 40, Mask(0));
            scene.Group(Z, 0, Mask(0), Pixels(40), z0);
            var a = scene.Stroke(X, 10, 10, Mask(0));
            scene.Group(X, 10, Mask(0), Pixels(10), a);
            var y = scene.Stroke(Y, 20, 40, Mask(1), new(Z, 0), Mask(0));
            scene.Group(Y, 20, Mask(1), Pixels(0, 40), y);
            var z1 = scene.Stroke(Z, 40, 90, Mask(0));
            scene.Group(Z, 40, Mask(0), Pixels(90), z1);
            var plan = scene.Delete(a);
            Check(plan.IsValid && plan.Layers.Any(l => l.Layer == Y) && plan.Layers.All(l => l.Layer != Z),
                "Future reference fixture must have affected Y and statically unaffected Z.");
            var fake = new ReferenceFake(scene);
            var result = await ReconstructionEngine.RunAsync(plan, fake, CancellationToken.None);
            Check(result.Issues.Count == 0 && fake.Replayed.Contains(y),
                "The reference-reading Y stroke must execute successfully.");
            Check(fake.Flushes.Where(f => f.Layer == Y).All(f => f.ReferenceZ == 40),
                "Future reference bug: Y(t20) received Z's boundary value instead of its t20 historical pixels.");
            Check(result.Layers.TryGetValue(Y, out var actual) && actual.Bytes.AsSpan().SequenceEqual(Pixels(0, 40).Bytes),
                "Future reference bug: Y's actual captured RGBA must use historical Z=40, not future Z=90.");
        }

        // Actual replay discovers D at x4 outside the static estimate. Y was not planned, but reads x4 later.
        {
            var scene = new Scene();
            var a = scene.Stroke(X, 10, 10, Mask(0));
            var b = scene.Stroke(X, 11, 20, Mask(1));
            scene.Group(X, 10, Mask(0, 1), Pixels(10, 20), a, b);
            var y = scene.Stroke(Y, 30, 30, Mask(2), new(X, 4, 30), Mask(4));
            scene.Group(Y, 30, Mask(2), Pixels(0, 0, 30), y);
            var plan = scene.Delete(a);
            Check(plan.IsValid && plan.Layers.Any(l => l.Layer == X && l.Untrusted.Count > 0) &&
                  plan.Layers.Where(l => l.Layer == Y).All(l => l.Untrusted.Count == 0),
                "Runtime cross-layer fixture must leave Y out of static replay, even if a prospective cursor is planned.");
            var fake = new ReferenceFake(scene) { AddUnexpectedXPixel = true };
            var result = await ReconstructionEngine.RunAsync(plan, fake, CancellationToken.None);
            Check(result.Issues.Count == 0 && fake.Replayed.Contains(y),
                "Runtime cross-layer bug: a new actual D must promote and replay a later reference image in previously unaffected Y.");
            Check(result.Promoted.Any(i => i.Layer == Y),
                "The newly affected Y image must be recorded as an online promotion.");
            Check(result.Layers.TryGetValue(Y, out var actual) && actual.Bytes.AsSpan().SequenceEqual(Pixels(0, 0, 37).Bytes),
                "Runtime cross-layer bug: Y must read actual X(x4)=7 and capture output 7+30, rather than retain historical 30.");
            Check(result.Layers.TryGetValue(X, out var xActual) && xActual.Bytes.AsSpan().SequenceEqual(Pixels(0, 20, 0, 0, 7).Bytes),
                "Runtime promotion must preserve X's exact captured deviation while removing the deleted stroke.");
        }

        // A layer first affected by exact feedback must enforce its own command
        // interval before sending any of that layer's newly promoted contacts.
        {
            var scene = new Scene();
            var a = scene.Stroke(X, 10, 10, Mask(0));
            var b = scene.Stroke(X, 11, 20, Mask(1));
            scene.Group(X, 10, Mask(0, 1), Pixels(10, 20), a, b);
            var y = scene.Stroke(Y, 30, 30, Mask(2), new(X, 4, 30), Mask(4));
            scene.Group(Y, 30, Mask(2), Pixels(0, 0, 30), y);
            var initial = scene.Delete(a);
            var layers = initial.Index.Layers.ToDictionary();
            layers[Y] = layers[Y] with { UndoRedo = [new(35, "undo", false)] };
            var plan = ReconstructionPlanner.Build(initial.Index with { Layers = layers }, initial.Request);
            Check(plan.IsValid && plan.Layers.Single(layer => layer.Layer == Y).Untrusted.Count == 0,
                "An unrelated layer's command evidence must not preempt an otherwise valid static plan.");
            var fake = new ReferenceFake(scene) { AddUnexpectedXPixel = true };
            var result = await ReconstructionEngine.RunAsync(plan, fake, CancellationToken.None);
            Check(result.Issues.Any(issue => issue.Code == PlanIssueCode.UndoRedoInRange) && !fake.Replayed.Contains(y),
                "Online promotion must stop before replaying a reference layer with undo inside its affected interval.");
            Check(result.Layers.Count == 0,
                "An online command-range failure must stop before installing any final reproduction layers.");
        }

        // Two Y reference events straddle Z's t20 update. A single replay snapshot cannot serve both.
        foreach (bool hitUnavailableLayer in new[] { false, true })
        {
            var scene = new Scene();
            var a = scene.Stroke(X, 10, 10, Mask(0));
            var b = scene.Stroke(X, 11, 20, Mask(1));
            scene.Strokes[b]=scene.Strokes[b] with {IsCausal=true,
                Classification=new(true,true,[],false,[]){ReadsReferenceLayers=true},
                BrushState=JsonSerializer.SerializeToElement(new {name="Reference fixture pen",properties=new[]{new{
                    key="anti_overflow.do_not_cross_lines_of_reference_layer",type="checkbox",status="ok",value="unchecked"}}})};
            scene.Group(X, 10, Mask(0, 1), Pixels(10, 20), a, b);
            var y = scene.Stroke(Y, 30, 30, Mask(2), new(X, 4, 30), Mask(4));
            scene.Group(Y, 30, Mask(2), Pixels(0, 0, 30), y);
            var initial = scene.Delete(a);
            var layers = initial.Index.Layers.ToDictionary();
            var missing = new PlanIssue(PlanIssueCode.CaptureMismatch, "Prospective Y has no boundary capture.");
            layers[Y] = layers[Y] with { Current = null, ChainValid = false, Issues = [missing],
                Packets = layers[Y].Packets.Select(packet => packet with { Current = null }).ToArray() };
            var plan = ReconstructionPlanner.Build(initial.Index with { Layers = layers, Issues = [missing] }, initial.Request);
            Check(plan.IsValid, "A missing prospective capture is deferred until actual replay demonstrates a dependency.");
            var fake = new ReferenceFake(scene) { AddUnexpectedXPixel = hitUnavailableLayer };
            var result = await ReconstructionEngine.RunAsync(plan, fake, CancellationToken.None);
            if (hitUnavailableLayer)
            {
                Check(result.Issues.Any(issue => issue.Code == PlanIssueCode.CaptureMismatch) && !fake.Replayed.Contains(y),
                    "An actual hit on a layer with a missing capture must fail before its replay.");
                Check(fake.InstallCount == 0, "An unavailable promoted layer must stop all final installation.");
            }
            else
            {
                Check(result.Issues.Count == 0 && result.Layers.ContainsKey(X) && !fake.Replayed.Contains(y),
                    "An unrelated missing boundary must allow X's valid reconstruction to finish.");
                Check(fake.InstallCount == 1, "Only the actually affected layer is installed.");
            }
        }

        // Two Y reference events straddle Z's t20 update. A single replay snapshot cannot serve both.
        {
            var scene = new Scene();
            var z0 = scene.Stroke(Z, 1, 40, Mask(0));
            scene.Group(Z, 0, Mask(0), Pixels(40), z0);
            var a = scene.Stroke(X, 10, 10, Mask(0));
            scene.Group(X, 1, Mask(0), Pixels(10), a);
            var early = scene.Stroke(Y, 20, 40, Mask(1), new(Z, 0), Mask(0));
            scene.Group(Y, 10, Mask(1), Pixels(0, 40), early);
            var z1 = scene.Stroke(Z, 30, 90, Mask(0));
            scene.Group(Z, 20, Mask(0), Pixels(90), z1);
            var late = scene.Stroke(Y, 40, 90, Mask(2), new(Z, 0), Mask(0));
            scene.Group(Y, 30, Mask(2), Pixels(0, 40, 90), late);
            var plan = scene.Delete(a);
            Check(plan.IsValid && plan.Layers.Single(l => l.Layer == Y).Untrusted.Count == 2,
                "Pending reference fixture must statically replay both Y reference events.");
            var fake = new ReferenceFake(scene);
            var result = await ReconstructionEngine.RunAsync(plan, fake, CancellationToken.None);
            var yFlushes = fake.Flushes.Where(f => f.Layer == Y).ToArray();
            Check(result.Issues.Count == 0 && fake.Replayed.Where(k => k == early || k == late).SequenceEqual(new[] { early, late }),
                "Reference event order must remain the original BeginEventId order.");
            Check(yFlushes.Length == 2 && yFlushes[0].Events.SequenceEqual(new ulong[] { 20 }) &&
                  yFlushes[1].Events.SequenceEqual(new ulong[] { 40 }),
                "Pending reference bug: a pending reference batch must flush before another layer's historical state changes.");
            Check(yFlushes.Select(f => f.ReferenceZ).SequenceEqual(new byte[] { 40, 90 }),
                "Pending reference bug: events before and after Z's update must receive their respective 40/90 snapshots.");
            Check(result.Layers.TryGetValue(Y, out var actual) && actual.Bytes.AsSpan().SequenceEqual(Pixels(0, 40, 90).Bytes),
                "Pending reference bug: captured Y RGBA must preserve both events' correct historical reference outputs.");
        }

        foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
        if (failures.Count > 0)
            throw new InvalidOperationException($"Cross-layer reconstruction checks failed: {failures.Count} of {checks}.");
        Console.WriteLine($"Cross-layer reconstruction checks passed: {checks}.");
        return checks;
    }

    private sealed record ReferenceRule(LayerKey Layer, int Pixel, byte Bias = 0);
    private sealed record FlushObservation(LayerKey Layer, IReadOnlyList<ulong> Events, byte ReferenceZ);

    private sealed class Scene
    {
        internal readonly Dictionary<EditableStrokeKey, EditableStroke> Strokes = [];
        internal readonly Dictionary<EditableStrokeKey, byte> Colors = [];
        internal readonly Dictionary<EditableStrokeKey, ReferenceRule> References = [];
        private readonly List<LayerPacket> _packets = [];
        private readonly Dictionary<LayerKey, ImageRgba> _history = [];

        internal EditableStrokeKey Stroke(LayerKey layer, ulong id, byte color, PixelMask write,
            ReferenceRule? reference = null, PixelMask? impact = null)
        {
            var key = new EditableStrokeKey(layer.SessionId, id);
            Strokes.Add(key, new(key, id, (long)id, (long)id + 1,
                [new((long)id, .5, .5, 1, 0, 0, true, id + 1000, id),
                 new((long)id + 1, .5, .5, 0, 0, 0, false, id + 1001, id + 1)],
                "Reference fixture pen", layer, reference is not null)
            {
                BeginEventId = id, WriteMask = write, ProbeMask = write, ImpactMask = impact ?? Mask(),
                Classification = new(true, reference is not null, [], true, []) { ReadsReferenceLayers = reference is not null }
            });
            Colors.Add(key, color);
            if (reference is not null) References.Add(key, reference);
            return key;
        }

        internal void Group(LayerKey layer, long ticks, PixelMask mask, ImageRgba next, params EditableStrokeKey[] events)
        {
            bool first = !_history.TryGetValue(layer, out var before);
            before ??= Pixels();
            string id = "p" + _packets.Count;
            var write = Mask(); var impact = Mask();
            foreach (var key in events)
            { write.UnionInPlace(Strokes[key].WriteMask); impact.UnionInPlace(Strokes[key].ImpactMask); }
            var image = new DirtyImage(new(layer, id, "i" + _packets.Count), mask, before.Copy(), next.Copy(), events,
                IsCausal: events.Any(k => Strokes[k].IsCausal)) { W = write, Probe = write, Impact = impact };
            _packets.Add(new(id, layer, ticks, null, null, [image], BaselineSource: first ? "recordingStart" : null,
                CanvasWidth: Width, CanvasHeight: 1));
            var updated = before.Copy();
            ImageCompositor.Replace(updated, image);
            _history[layer] = updated;
        }

        internal ReconstructionPlan Delete(EditableStrokeKey key)
        {
            var images = _packets.SelectMany(p => p.Images).ToDictionary(i => i.Key);
            var strokes = Strokes.ToDictionary(kv => kv.Key, kv => kv.Value with
            { Images = images.Values.Where(i => i.Events.Contains(kv.Key)).Select(i => i.Key).ToArray() });
            var layers = _packets.GroupBy(p => p.Layer).ToDictionary(g => g.Key, g =>
            {
                var packets = g.OrderBy(p => p.TriggerTicks).ToArray();
                packets[^1] = packets[^1] with { Current = _history[g.Key].Copy(), IsBoundary = true };
                return new LayerIndex(g.Key, packets, strokes.Values.Where(s => s.Layer == g.Key).ToArray(), true, [], Current: _history[g.Key].Copy());
            });
            var index = new EditIndex(layers, strokes, images, []);
            return ReconstructionPlanner.Build(index, EditRequest.Event(strokes[key].Layer, Mask(0),
                new Dictionary<EditableStrokeKey, EventChange> { [key] = new(Delete: true) }));
        }
    }

    private sealed class ReferenceFake(Scene scene) : ICspReconstructionExecutor
    {
        internal readonly List<EditableStrokeKey> Replayed = [];
        internal readonly List<FlushObservation> Flushes = [];
        internal bool AddUnexpectedXPixel;
        internal int InstallCount;

        public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Flushes.Add(new(layer, events.Select(e => e.Stroke.BeginEventId).ToArray(),
                referenceLayers.TryGetValue(Z, out var z) ? z.Bytes[0] : (byte)0));
            foreach (var e in events)
            {
                Replayed.Add(e.Stroke.Key);
                byte color = scene.References.TryGetValue(e.Stroke.Key, out var reference)
                    ? checked((byte)(referenceLayers[reference.Layer].Bytes[reference.Pixel * 4] + reference.Bias))
                    : scene.Colors[e.Stroke.Key];
                foreach (var (x, _) in e.Stroke.WriteMask.Pixels()) Put(seed, x, color);
            }
            if (AddUnexpectedXPixel && layer == X) Put(seed, 4, 7);
            return Task.FromResult(seed);
        }

        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
        { token.ThrowIfCancellationRequested(); InstallCount++; return Task.FromResult(expected.Copy()); }
    }

    private static PixelMask Mask(params int[] xs)
    { var result = new PixelMask(Width, 1); foreach (int x in xs) result.Set(x, 0); return result; }
    private static ImageRgba Pixels(params byte[] colors)
    {
        var result = new ImageRgba(Width, 1, new byte[Width * 4]);
        for (int x = 0; x < colors.Length; x++) Put(result, x, colors[x]);
        return result;
    }
    private static void Put(ImageRgba image, int x, byte red)
    { image.Bytes[x * 4] = red; image.Bytes[x * 4 + 3] = red == 0 ? (byte)0 : (byte)255; }
}
