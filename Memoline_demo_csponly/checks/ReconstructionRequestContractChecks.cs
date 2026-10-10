using MemolineDemo.Editing;
using StrokeReplay;
using System.Text.Json;

namespace MemolineDemo;

internal static class ReconstructionRequestContractChecks
{
    internal static int Run()
    {
        // These checks pin the optional strict undo/redo gate; the product default is off.
        ReconstructionPlanner.BlockOnUnresolvedUndo = true;
        try { return RunStrict(); }
        finally { ReconstructionPlanner.BlockOnUnresolvedUndo = false; }
    }

    private static int RunStrict()
    {
        int count = 0;
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            count++;
            if (!condition) { failures.Add(message); Console.WriteLine("FAIL: " + message); }
        }
        var current = new LayerKey("request-contract", 1, "current");
        var other = new LayerKey("request-contract", 1, "other");
        var key = new EditableStrokeKey("request-contract", 20);
        var stroke = new EditableStroke(key, 20, 20, 21,
            [new(20, .5, .5, 1, 0, 0, true, 20, 20), new(21, .5, .5, 0, 0, 0, false, 21, 21)],
            "Old pen", current, false, BrushNumbers: new Dictionary<string, double> { ["brush_size"] = 1 })
        { BeginEventId = 20, WriteMask = Mask(0), ProbeMask = Mask(0), ImpactMask = Mask() };
        var image = new DirtyImage(new(current, "selected", "i"), Mask(0), Pixels(0, 0, 0, 0), Pixels(10, 0, 0, 0), [key])
        { W = Mask(0), Probe = Mask(0), Impact = Mask() };
        stroke = stroke with { Images = [image.Key] };
        var packet = new LayerPacket("selected", current, 30, null, image.Now, [image], IsBoundary: true,
            AfterSnapshotId: "baseline", NowSnapshotId: "now", BaselineSource: "recordingStart", CanvasWidth: 4, CanvasHeight: 1);
        var layer = new LayerIndex(current, [packet], [stroke], true, [], Current: image.Now);
        var index = new EditIndex(new Dictionary<LayerKey, LayerIndex> { [current] = layer },
            new Dictionary<EditableStrokeKey, EditableStroke> { [key] = stroke },
            new Dictionary<DirtyImageKey, DirtyImage> { [image.Key] = image }, []);
        var request = EditRequest.Event(current, Mask(0), new Dictionary<EditableStrokeKey, EventChange> { [key] = new(Delete: true) });

        foreach (var code in new[] { PlanIssueCode.NoBaseline, PlanIssueCode.CaptureMismatch, PlanIssueCode.ChainLossy })
        {
            var issue = new PlanIssue(code, "Unrelated layer evidence must remain local.");
            var unrelatedPacket = packet with { Layer = other, PacketId = "unrelated", Images = [], IsBoundary = false };
            var unrelated = new LayerIndex(other, [unrelatedPacket], [], false, [issue], Current: image.Now);
            var layers = index.Layers.ToDictionary(); layers.Add(other, unrelated);
            Check(ReconstructionPlanner.Build(index with { Layers = layers, Issues = [issue] }, request).IsValid,
                "An unrelated layer's " + code + " does not block the selected matrix.");
            var affected = layer with { ChainValid = false, Issues = [issue] };
            var invalid = index with { Layers = new Dictionary<LayerKey, LayerIndex> { [current] = affected }, Issues = [issue] };
            Check(!ReconstructionPlanner.Build(invalid, request).IsValid,
                "The affected layer's " + code + " still blocks execution.");
        }

        var foreignImage = image with { Key = new(other, "foreign", "i") };
        var unavailableReference=stroke with {Layer=other,Classification=new(true,true,[],true,[]){ReadsReferenceLayers=true}};
        var unavailableIssue=new PlanIssue(PlanIssueCode.CaptureMismatch,"Unrelated prospective reference layer lacks a boundary.");
        var prospectiveLayers=index.Layers.ToDictionary();
        prospectiveLayers.Add(other,new(other,[packet with {Layer=other,Images=[],Current=null}],[unavailableReference],false,[unavailableIssue]));
        Check(ReconstructionPlanner.Build(index with {Layers=prospectiveLayers,Issues=[unavailableIssue]},request).IsValid,
            "A prospective reference reader's missing boundary is deferred until exact replay shows it is affected.");
        var foreignStroke = stroke with { Layer = other, Images = [foreignImage.Key] };
        var foreignPacket = packet with { Layer = other, PacketId = "foreign", Images = [foreignImage] };
        var foreignLayer = new LayerIndex(other, [foreignPacket], [foreignStroke], true, [], Current: foreignImage.Now);
        var foreignIndex = new EditIndex(new Dictionary<LayerKey, LayerIndex> { [current] = layer with { Strokes = [], Packets = [packet with { Images = [] }] }, [other] = foreignLayer },
            new Dictionary<EditableStrokeKey, EditableStroke> { [key] = foreignStroke },
            new Dictionary<DirtyImageKey, DirtyImage> { [foreignImage.Key] = foreignImage }, []);
        var foreignPlan = ReconstructionPlanner.Build(foreignIndex, request);
        Check(foreignPlan.IsValid && foreignPlan.Layers.Count == 1 && foreignPlan.Layers[0].Layer == other,
            "Event editing reconstructs a selected image's layer even when CSP currently selects another layer.");
        Check(!ReconstructionPlanner.Build(foreignIndex, request with { Kind = EditKind.PreciseUndo }).IsValid,
            "Precise undo cannot use strokes outside the confirmed current layer.");

        var causalKey = new EditableStrokeKey("request-contract", 40);
        var causalStroke = stroke with { Key = causalKey, OperationId = 40, BeginEventId = 40, StartTicks = 40, EndTicks = 41,
            IsCausal = true, WriteMask = Mask(3), ProbeMask = Mask(3), ImpactMask = Mask(3) };
        var causalImage = new DirtyImage(new(current, "later", "causal"), Mask(3), image.Now, Pixels(10, 0, 0, 30), [causalKey], IsCausal: true)
        { W = Mask(3), Probe = Mask(3), Impact = Mask(3) };
        causalStroke = causalStroke with { Images = [causalImage.Key] };
        var later = packet with { PacketId = "later", TriggerTicks = 50, Images = [causalImage], Current = causalImage.Now,
            AfterSnapshotId = "now", NowSnapshotId = "later-now" };
        var overrideIndex = index with
        {
            Layers = new Dictionary<LayerKey, LayerIndex> { [current] = layer with { Packets = [packet with { IsBoundary = false }, later], Strokes = [stroke, causalStroke], Current = causalImage.Now } },
            Strokes = new Dictionary<EditableStrokeKey, EditableStroke> { [key] = stroke, [causalKey] = causalStroke },
            Images = new Dictionary<DirtyImageKey, DirtyImage> { [image.Key] = image, [causalImage.Key] = causalImage }
        };
        var brushRequest = request with { Changes = new Dictionary<EditableStrokeKey, EventChange> { [key] = new(BrushName: "Replacement pen") } };
        var brushPlan = ReconstructionPlanner.Build(overrideIndex, brushRequest);
        Check(brushPlan.IsValid, "A replacement subtool is not rejected merely because its name differs.");
        Check(brushPlan.Layers.Any(l => l.Untrusted.Contains(causalImage.Key)),
            "Unknown replacement subtool geometry conservatively invalidates later causal reads anywhere on the canvas.");
        Check(!ReconstructionPlanner.Build(index, brushRequest with { Changes = new Dictionary<EditableStrokeKey, EventChange> { [key] = new(BrushName: " ") } }).IsValid,
            "An empty replacement subtool name remains unsupported.");

        foreach (bool ambiguousLayer in new[] { false, true })
        {
            var before = index with { Layers = new Dictionary<LayerKey, LayerIndex> { [current] = layer with
                { UndoRedo = [new(10, "undo", ambiguousLayer)] } } };
            Check(ReconstructionPlanner.Build(before, request).IsValid,
                "A referenced undo before the affected contact is harmless even when its target layer is unresolved.");
            var after = index with { Layers = new Dictionary<LayerKey, LayerIndex> { [current] = layer with
                { UndoRedo = [new(25, "redo", ambiguousLayer)] } } };
            Check(ReconstructionPlanner.Build(after, request).Issues.Any(issue => issue.Code == PlanIssueCode.UndoRedoInRange),
                "A referenced redo after the affected contact blocks the request, including an unresolved target layer.");
        }
        var unanchored = index with { Layers = new Dictionary<LayerKey, LayerIndex> { [current] = layer with
            { UndoRedo = [new(0, "undo", true)] } } };
        Check(ReconstructionPlanner.Build(unanchored, request).Issues.Any(issue => issue.Code == PlanIssueCode.UndoRedoInRange),
            "An undo without a confirmed input position cannot be filtered out as an earlier command.");
        var swapAndResize = brushRequest with { Changes = new Dictionary<EditableStrokeKey, EventChange>
            { [key] = new(BrushName: "Replacement pen", Numbers: new Dictionary<string, double> { ["brush_size"] = 3 })
              { ReplacementBrushState = JsonDocument.Parse("""{"status":"changed","state":{"name":"Replacement pen","properties":[{"key":"brush_size","type":"number","status":"ok","value":3},{"key":"flow","type":"number","status":"ok","value":25}]}}""").RootElement.Clone() } } };
        Check(ReconstructionPlanner.Build(overrideIndex, swapAndResize).IsValid,
            "Replacing a subtool uses conservative geometry even when the original size recipe is absent.");
        var newProperty = new EventChange(BrushName: "Replacement pen", Numbers: new Dictionary<string,double> { ["flow"] = 50 })
            { ReplacementBrushState = swapAndResize.Changes[key].ReplacementBrushState };
        var replacementRequest = swapAndResize with { Changes = new Dictionary<EditableStrokeKey,EventChange> { [key] = newProperty } };
        Check(ReconstructionPlanner.Build(overrideIndex, replacementRequest).IsValid,
            "A confirmed replacement accepts target-only numeric properties.");
        Check(!ReconstructionPlanner.Build(overrideIndex, replacementRequest with { Changes = new Dictionary<EditableStrokeKey,EventChange>
            { [key] = new(BrushName: "Replacement pen", Numbers: new Dictionary<string,double> { ["missing"] = 50 })
                { ReplacementBrushState = newProperty.ReplacementBrushState } } }).IsValid,
            "A property absent from the confirmed replacement snapshot remains unsupported.");

        var sharedImage = causalImage with { Events = [key, causalKey] };
        var sharedStroke = stroke with { Images = [image.Key, sharedImage.Key] };
        var closureLayer = overrideIndex.Layers[current] with
        { Packets = [packet with { IsBoundary = false }, later with { Images = [sharedImage] }],
            Strokes = [sharedStroke, causalStroke], UndoRedo = [new(30, "undo", false)] };
        var closureIndex = overrideIndex with
        {
            Layers = new Dictionary<LayerKey, LayerIndex> { [current] = closureLayer },
            Strokes = new Dictionary<EditableStrokeKey, EditableStroke> { [key] = sharedStroke, [causalKey] = causalStroke },
            Images = new Dictionary<DirtyImageKey, DirtyImage> { [image.Key] = image, [sharedImage.Key] = sharedImage }
        };
        var closureRequest = request with { Selection = Mask(3), Changes = new Dictionary<EditableStrokeKey, EventChange>
            { [causalKey] = new(Delete: true) } };
        Check(ReconstructionPlanner.Build(closureIndex, closureRequest).Issues.Any(issue => issue.Code == PlanIssueCode.UndoRedoInRange),
            "Shared-contact closure moves the command window back to every event in the affected matrices.");

        if (failures.Count > 0) throw new InvalidOperationException($"Request contract checks: {count - failures.Count}/{count} passed.\n" + string.Join("\n", failures));
        Console.WriteLine($"Reconstruction request contract checks passed: {count}.");
        return count;
    }

    private static PixelMask Mask(params int[] pixels)
    { var mask = new PixelMask(4, 1); foreach (int x in pixels) mask.Set(x, 0); return mask; }
    private static ImageRgba Pixels(params byte[] colors)
    {
        var bytes = new byte[16];
        for (int x = 0; x < 4; x++) { bytes[x * 4] = colors[x]; bytes[x * 4 + 3] = colors[x] == 0 ? (byte)0 : (byte)255; }
        return new(4, 1, bytes);
    }
}
