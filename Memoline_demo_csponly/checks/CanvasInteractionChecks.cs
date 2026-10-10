using System.Drawing;
using System.Text.Json;
using MemolineDemo.Editing;
using StrokeReplay;

namespace MemolineDemo;

internal static class CanvasInteractionChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { checks++; return; } throw new InvalidOperationException(message); }
        var session = Session();
        var model = new CanvasInteraction(session);
        var transform = model.Transform;
        var point = transform.CanvasToScreen(new(13, 29));
        var roundtrip = transform.ScreenToCanvas(point);
        Check(Math.Abs(roundtrip.X - 13) < .001 && Math.Abs(roundtrip.Y - 29) < .001,
            "Native normalized affine mapping must roundtrip a rotated canvas in physical screen pixels.");
        Reject(() => new CanvasInteraction(session with { CanvasViewEvidence = Evidence("other") }), "Another session's affine mapping must be rejected.");
        Reject(() => new CanvasInteraction(session with { CanvasViewEvidence = Evidence(width: 101) }), "Canvas dimensions must be checked.");
        Reject(() => new CanvasInteraction(session with { CanvasViewEvidence = Evidence(invalidInverse: true) }), "Non-inverse matrices must be rejected.");
        Reject(() => new CanvasInteraction(session with { CanvasViewEvidence = Evidence(convention: "unknown") }), "Unknown affine conventions must be rejected.");
        Reject(() => new CanvasInteraction(session with { CanvasViewEvidence = Evidence(status: "unknown") }), "Unconfirmed view evidence must be rejected.");
        Reject(() => new CanvasInteraction(session with { CanvasViewEvidence = Evidence(omitMatrices: true) }), "UI selection must not silently fall back to OCR scaling.");
        model.SetRegion([transform.CanvasToScreen(new(2, 2)), transform.CanvasToScreen(new(20, 2)),
            transform.CanvasToScreen(new(20, 20)), transform.CanvasToScreen(new(2, 20))]);
        Check(model.RegionSelection?.Count == 324 && model.RegionSelection.Contains(8, 8), "Screen selection must become the exact rotated canvas polygon.");
        Check(model.Candidates.Count == 2 && model.Candidates.Select(c => c.Image.Key.Layer.Uuid).ToHashSet().SetEquals(["ink", "other"]),
            "The region must list individual dirty images across layers, using actual masks.");
        var chosen = model.Candidates.Single(c => c.Image.Key.Layer.Uuid == "ink");
        Check(chosen.LayerName == "Ink" && chosen.CanSelect && chosen.Image.Now.Width == 100, "Candidates must expose their layer, now patch, and selectability.");
        model.SelectImage(chosen.Image.Key);
        Check(model.EligibleStrokes.Count == 1 && model.SelectedEvents.Count == 0, "Choosing one image must limit paths to its eligible events without selecting them.");
        var near = transform.CanvasToScreen(new(20, 8));
        Check(model.HoverPaths(new(near.X + 47.9f, near.Y)).Count == 1 && model.HoverPaths(new(near.X + 48.1f, near.Y)).Count == 0,
            "White hover paths must use a 48 physical-screen-pixel radius.");
        model.BeginSweep(new(near.X + 12.1f, near.Y)); model.EndSweep();
        Check(model.SelectedEvents.Count == 0, "A sweep outside 12 screen pixels must miss.");
        model.BeginSweep(new(near.X + 11.9f, near.Y)); model.ContinueSweep(new(near.X + 11.9f, near.Y + 40)); model.EndSweep();
        Check(model.SelectedEvents.SetEquals([chosen.Image.Events[0]]), "A sweep must select the whole contact once despite many intersecting samples.");
        model.BeginSweep(new(near.X - 30, near.Y)); model.ContinueSweep(new(near.X + 30, near.Y)); model.EndSweep();
        Check(model.SelectedEvents.Count == 0, "Crossing a path between sampled cursor endpoints must hit and toggle an already selected stroke.");
        Reject(()=>model.SetSelectedEvents(chosen.Image.Events),"Ineligible events sharing the image must not become selected.");
        model.SetSelectedEvents(model.EligibleStrokes.Select(s=>s.Key));
        Check(model.SelectedEvents.Count == 1, "Explicit selection must stay limited to chosen-image eligible events.");
        model.ClearSelection();
        Check(model.RegionSelection is null && model.Candidates.Count == 0 && model.ChosenImage is null && model.SelectedEvents.Count == 0,
            "Clearing a region must reset the image and event selection.");
        var blocked = new CanvasInteraction(Session(chainValid: false));
        blocked.SetRegion([transform.CanvasToScreen(new(2, 2)), transform.CanvasToScreen(new(20, 2)),
            transform.CanvasToScreen(new(20, 20)), transform.CanvasToScreen(new(2, 20))]);
        var failed = blocked.Candidates.Single(c => c.Image.Key.Layer.Uuid == "ink");
        Check(!failed.CanSelect && failed.Reason.Contains("baseline", StringComparison.OrdinalIgnoreCase), "Unavailable matrices must keep a visible reason.");
        Reject(() => blocked.SelectImage(failed.Image.Key), "A failed chain cannot become an editable chosen image.");
        var lassoModel = new CanvasInteraction(session);
        lassoModel.SetRegion([transform.CanvasToScreen(new(0, 0)), transform.CanvasToScreen(new(100, 0)), transform.CanvasToScreen(new(0, 100))]);
        Check(lassoModel.RegionSelection!.Contains(8, 8) && !lassoModel.RegionSelection.Contains(80, 80) && lassoModel.Candidates.Count == 2,
            "A native-mapped lasso must test actual mask pixels and retain its unselected corner.");
        int selectionEvents = 0; lassoModel.SelectionChanged += (_, _) => selectionEvents++;
        lassoModel.SelectImage(lassoModel.Candidates.Single(c => c.Image.Key.Layer.Uuid == "ink").Image.Key);
        lassoModel.BeginSweep(transform.CanvasToScreen(new(30, 8))); lassoModel.ContinueSweep(transform.CanvasToScreen(new(40, 8))); lassoModel.EndSweep();
        Check(selectionEvents == 2, "One sweep must notify exactly one whole-event toggle even if the cursor stays on the path.");
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var freeze = new FrozenCspDesktop(new Bitmap(800, 600), new(-100, -100, 800, 600), new(300, 100, 200, 200), 0);
                using var overlay = new TestOverlay(new CanvasInteraction(session), freeze);
                Check(overlay.Bounds == freeze.Bounds && overlay.TopMost && !overlay.ShowInTaskbar && overlay.FormBorderStyle == FormBorderStyle.None,
                    "The frozen overlay must cover the physical virtual desktop without becoming an ordinary preview window.");
                Check((overlay.Styles & 0x80088) == 0x80088 && (overlay.Styles & 0x20) == 0,
                    "Selection must use a layered topmost toolwindow that consumes input.");
                overlay.InteractionEnabled = false;
                Check((overlay.Styles & 0x20) != 0, "Outside selection, the overlay must support explicit input passthrough.");
            }
            catch (Exception caught) { error = caught; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Frozen overlay checks did not finish.");
        if (error is not null) throw new InvalidOperationException("Frozen overlay checks failed.", error);
        return checks;
    }

    private sealed class TestOverlay(CanvasInteraction model, FrozenCspDesktop freeze) : EventSelectionOverlay(model, freeze)
    { internal int Styles => CreateParams.ExStyle; }

    private static JsonElement Evidence(string session = "selection", int width = 100, bool invalidInverse = false,
        string convention = "sct-affine-row-major/v1", string status = "changed", bool omitMatrices = false)
        => JsonSerializer.SerializeToElement(new { sessionId = session, module = "canvasViewState", status,
            state = new { canvasOriginScreenPx = new { x = 500, y = 100 }, transform = new { canvasPixelWidth = width, canvasPixelHeight = 100,
                matrices = omitMatrices ? null : new { convention, canvasSpace = "normalized",
                    screenToCanvas = new double[] { 0, .005, -.5, -.005, 0, 2.5 },
                    canvasToScreen = new double[] { 0, -200, invalidInverse ? 501 : 500, 200, 0, 100 } } } },
            rawResult = new { canvasWindowRoiScreenPx = new[] { 300, 100, 200, 200 } } });

    private static EditSession Session(bool chainValid = true)
    {
        var layer = new LayerKey("selection", 1, "ink"); var other = new LayerKey("selection", 1, "other");
        var image = new ImageRgba(100, 100, new byte[40000]);
        EditableStroke Stroke(ulong id, LayerKey target, int y, bool eligible = true) => new(new("selection", id), id, (long)id, (long)id + 1,
            [], "Pencil", target, false, eligible) { BeginEventId = id, CoverageLabels = [new("label-" + id, id, "pen", 1, 2, 1,
                [new(5, y, 70, y)], ["image"])], WriteMask = new(100, 100) };
        var first = Stroke(1, layer, 8); var second = Stroke(2, other, 10); var ineligible = Stroke(3, layer, 8, false);
        DirtyImage Dirty(LayerKey target, string name, int x, int y, params EditableStroke[] strokes)
        {
            var mask = new PixelMask(100, 100); mask.Set(x, y);
            return new(new(target, "packet", name), mask, image, image, strokes.Select(s => s.Key).ToArray());
        }
        var one = Dirty(layer, "one", 8, 8, first, ineligible); var two = Dirty(other, "two", 10, 10, second);
        var hole = Dirty(layer, "hole", 80, 80, first);
        var packets = new[] { new LayerPacket("packet", layer, 10, image, image, [one, hole], CanvasWidth: 100, CanvasHeight: 100)
                { NowDescriptor = JsonSerializer.SerializeToElement(new { layer = new { name = "Ink" } }) },
            new LayerPacket("packet", other, 11, image, image, [two], CanvasWidth: 100, CanvasHeight: 100)
                { NowDescriptor = JsonSerializer.SerializeToElement(new { layer = new { name = "Other" } }) } };
        EditableStroke[] strokes = [first, second, ineligible];
        var issues = chainValid ? Array.Empty<PlanIssue>() : [new PlanIssue(PlanIssueCode.NoBaseline, "No baseline exists.")];
        var layers = new Dictionary<LayerKey, LayerIndex> { [layer] = new(layer, [packets[0]], [first, ineligible], chainValid, issues, Current: image),
            [other] = new(other, [packets[1]], [second], true, [], Current: image) };
        var index = new EditIndex(layers, strokes.ToDictionary(s => s.Key), new Dictionary<DirtyImageKey, DirtyImage> { [one.Key] = one, [two.Key] = two, [hole.Key] = hole }, issues);
        return new(new("fake", "selection", 1000, [], new MemolineReplayDocument("fake", "selection", 1000, []), packets, 3, 10), index, layer)
            { CanvasViewEvidence = Evidence() };
    }
}
