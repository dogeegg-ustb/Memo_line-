using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Realtime;
using CanvasLayerWatcher;
using DirtyMatrix.Core;

internal static class AffineMatrixChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // A 512x256 canvas has its origin at screen (-300,-200), with
        // 50% zoom and a quarter-turn clockwise. Canvas x advances down the
        // screen; canvas y advances left. These basis-point expectations are
        // fixed coordinates, independent of the code under test.
        double[] toCanvas = [0, .00390625, .78125, -.0078125, 0, -2.34375];
        double[] toScreen = [0, -128, -300, 256, 0, -200];
        var published = JsonSerializer.SerializeToElement(new { screenToCanvas = toCanvas, canvasToScreen = toScreen,
            convention = "sct-affine-row-major/v1", canvasSpace = "normalized" });
        RecorderRealtimeEvent Event(string channel, string kind, long ticks, object data, ulong? operation = null)
            => new(1, "affine", ticks, channel, kind, false, 0, (ulong)ticks, ticks, ticks, ticks,
                operation, null, null, JsonSerializer.SerializeToElement(data));
        DirtyEvidence Stroke(JsonElement matrices, double x, double y, bool semantic = false, bool geometryFallback = false,
            int publishedWidth = 512, bool legacy = false, bool nativeRotation = false, double ocrScale = 50)
        {
            var evidence = new RecognitionEvidence(); evidence.Reset("affine", 1000, 0);
            evidence.Accept(Event("core.currentLayerState", "stateUpdated", 1, new { status = "changed", state = "Ink" }));
            evidence.Accept(Event("core.brushState", "stateUpdated", 1, new { status = "changed", state = new
            { name = "Pen", properties = new[] { new { key = "brush_size", value = 10, unit = "px" } } } }));
            evidence.Accept(Event("core.canvasViewState", "stateUpdated", 2, new { status = "changed", state = new
            {
                canvasOriginScreenPx = new { x = -300, y = -200 }, ocrScalePercent = ocrScale, ocrRotationDegrees = geometryFallback ? 0 : 90,
                transform = new { canvasPixelWidth = publishedWidth, canvasPixelHeight = 256,
                    rotationDegrees = nativeRotation ? (double?)90 : geometryFallback ? (double?)0 : null,
                    matrices = semantic ? matrices : (JsonElement?)null }
            }, rawResult = new { snapshot = new { matrices = semantic || legacy || nativeRotation ? (JsonElement?)null : matrices,
                screenToCanvas = legacy ? new { m0 = 0.0, m1 = .00390625, m2 = .78125, m3 = -.0078125, m4 = 0.0, m5 = -2.34375 } : null } } }));
            evidence.Accept(Event("tablet", "penBegin", 3, new { x, y, inCsp = true, heldKeys = Array.Empty<int>(),
                penDownLocation = new { region = "canvasViewport", name = "Canvas", status = "matched" } }, 3));
            return RecognitionEvidence.Build(evidence.Snapshot(0, 4), new(1, 4, "Ink", new(-300, -200, 50, 90), [3]), new(512, 256));
        }
        var mapped = Stroke(published, -332, -184);
        check(mapped.Labels.Length == 1 && mapped.Labels[0].Coverage.Segments[0].Start == new PointD(32, 64),
            "Published raw matrices map negative screen origin and rotation to exact non-square canvas pixels");
        // memolinetest13: the navigator digits read 6 for 18.6 % all session, and the screen-pixel padding swelled to 200 px.
        var misread = Stroke(published, -332, -184, ocrScale: 6).Labels[0];
        check(misread.CoverageSettings is { ZoomPercent: 50 } && misread.Coverage.Radius == mapped.Labels[0].Coverage.Radius,
            "Coverage takes its zoom from the published affine, not from misread navigator digits");
        check(Stroke(published, -300, -200).Labels[0].Coverage.Segments[0].Start == new PointD(0, 0),
            "Published affine maps the negative screen origin to canvas zero");
        check(Stroke(published, -300, -184).Labels[0].Coverage.Segments[0].Start == new PointD(32, 0)
            && Stroke(published, -332, -200).Labels[0].Coverage.Segments[0].Start == new PointD(0, 64),
            "Normalized x and y use their own canvas width and height");
        check(Stroke(published, -332, -184, semantic: true).Labels[0].Coverage.Segments[0].Start == new PointD(32, 64),
            "Semantic transform matrices are consumed when raw matrices are absent");
        check(Stroke(published, -332, -184, legacy: true).Labels[0].Coverage.Segments[0].Start == new PointD(32, 64),
            "Legacy m0..m5 affine objects remain compatible");
        var native = Stroke(published, -332, -184, nativeRotation: true).Labels[0].Coverage.Segments[0].Start;
        check(Math.Abs(native.X - 32) <= .5 && Math.Abs(native.Y - 64) <= .5,
            "Native rotation fallback agrees with the published affine at the same known canvas point");
        var wrongDimensions = Stroke(published, -332, -184, publishedWidth: 1024);
        check(wrongDimensions.Labels.Length == 0 && wrongDimensions.Warnings.Contains("canvasAffineCanvasSizeMismatch"),
            "Affine canvas dimensions must match the captured full canvas");

        void Reject(Action<JsonObject> change, string expectedWarning, string name)
        {
            var matrix = JsonNode.Parse(published.GetRawText())!.AsObject(); change(matrix);
            var rejected = Stroke(JsonSerializer.SerializeToElement(matrix), -332, -184, geometryFallback: true);
            check(rejected.Labels.Length == 0 && rejected.Warnings.Contains(expectedWarning), name);
        }
        Reject(matrix => matrix["convention"] = "column-major/v1", "canvasAffineConventionMismatch",
            "An unknown affine convention is rejected even with an available geometry fallback");
        Reject(matrix => matrix["canvasSpace"] = "pixels", "canvasAffineConventionMismatch",
            "An affine with an unexpected canvas space is rejected");
        Reject(matrix => matrix["screenToCanvas"] = new JsonArray(0, 1, 2, 3, 4), "invalidCanvasAffineMapping",
            "Affine arrays must contain exactly six coefficients");
        Reject(matrix => matrix["screenToCanvas"]![2] = "Infinity", "invalidCanvasAffineMapping",
            "Named non-finite affine coefficients are rejected");
        Reject(matrix => matrix["screenToCanvas"] = new JsonArray(1, 2, 0, 2, 4, 0), "invalidCanvasAffineMapping",
            "A singular affine is rejected");
        Reject(matrix => { matrix["screenToCanvas"] = new JsonArray(1e308, 0, 0, 0, 1e308, 0);
                matrix["canvasToScreen"] = new JsonArray(1e-308, 0, 0, 0, 1e-308, 0); }, "invalidCanvasAffineMapping",
            "An affine that produces non-finite canvas coordinates is rejected");
        Reject(matrix => matrix["canvasToScreen"] = new JsonArray(0, 0, 0, 0, 0, 0), "invalidCanvasAffineMapping",
            "A published inverse affine must also be invertible");
        Reject(matrix => matrix["canvasToScreen"]![2] = -301, "invalidCanvasAffineMapping",
            "Two individually invertible published affines must be inverse to each other");
    }
}
