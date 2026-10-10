using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal static class RecordedCanvasViewChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Recorded canvas view: " + message); checks++; }
        void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { checks++; return; } throw new InvalidOperationException(message); }

        using var frame = JsonDocument.Parse(RecordedFrame);
        var data = frame.RootElement.GetProperty("data");
        Check(frame.RootElement.GetProperty("appendId").GetUInt64() == 374 &&
            data.GetProperty("status").GetString() == "unchanged" &&
            data.GetProperty("error").ValueKind == JsonValueKind.Null &&
            data.GetProperty("evidence").GetProperty("causalAmbiguous").GetBoolean(),
            "The fixture must retain the actual successful observation that blocked selection.");
        Check(!CanvasViewSnapshot.TryParse(data, out _),
            "Historical replay must still reject observations without an unambiguous causal anchor.");
        Check(CanvasViewSnapshot.TryParse(data, data.GetProperty("rawResult"), out var parsed, requireCausalAnchor: false) &&
            parsed is { PixelWidth: 4961, PixelHeight: 7016, OriginX: > 967.99 and < 968.01, OriginY: 315 },
            "Current-view parsing must accept a successful observation even when input attribution is ambiguous.");

        // Replay the actual response through the active-request path. Only its observation timestamp is
        // rebased to this test's clock; the status, geometry, matrices and causal evidence stay unchanged.
        await using var writer = new MemolineWriter(Path.Combine(root, "recorded-canvas-request"),
            new { test = "native appendId 374 canvas selection regression" });
        await using var hub = new RecorderRealtimeHub(writer);
        await using var server = new RecorderRealtimePipeServer(hub);
        var control = new RecordedControl(writer, data);
        await using var feed = new LivePacketFeed(server.ManifestPath, control);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = feed.View.RunAsync();
        await feed.View.WaitForConnectionAsync(deadline.Token);
        await feed.RequestAsync(["canvasViewState"], deadline.Token);
        Check(control.Calls == 1 && control.RequestedSession == writer.SessionId &&
            control.RequestId?.StartsWith("packet-state-", StringComparison.Ordinal) == true,
            "The recorded response must be requested against the connected session and a fresh request identity.");
        var bound = feed.RequestedCanvasView;
        Check(bound.GetProperty("sessionId").GetString() == writer.SessionId &&
            bound.GetProperty("evidence").GetProperty("causalAmbiguous").GetBoolean() &&
            bound.GetProperty("state").GetRawText() == data.GetProperty("state").GetRawText(),
            "Session binding must preserve native geometry and causal ambiguity rather than hiding the flag.");
        Check(feed.RequestedView is { PixelWidth: 4961, PixelHeight: 7016, ScalePercent: 14.6 },
            "The actual active request must establish the current view.");

        var size = new Size(4961, 7016);
        var layer = new LayerKey(writer.SessionId, 1, "recorded-current-layer");
        var image = new ImageRgba(size.Width, size.Height,
            GC.AllocateUninitializedArray<byte>(checked(size.Width * size.Height * 4)));
        var index = new EditIndex(new Dictionary<LayerKey, LayerIndex>
            { [layer] = new(layer, [], [], true, [], Current: image) },
            new Dictionary<EditableStrokeKey, EditableStroke>(), new Dictionary<DirtyImageKey, DirtyImage>(), []);
        var session = new EditSession(new("recorded.memoline", writer.SessionId, Stopwatch.Frequency, [],
            new MemolineReplayDocument("recorded.memoline", writer.SessionId, Stopwatch.Frequency, []), [], 374, 778748395),
            index, layer) { CanvasViewEvidence = bound };
        // This is the real failure boundary: ApplyRequestedView above succeeds, but the former
        // CanvasAffineTransform gate threw "画布视图状态尚未确认。" here.
        var model = new CanvasInteraction(session);
        var transform = model.Transform;
        Check(transform.CanvasSize == size && transform.Viewport == Rectangle.FromLTRB(409, 149, 2238, 1476),
            "Selection must use the recorded canvas dimensions and physical viewport.");
        var corner = transform.CanvasToScreen(new(size.Width, size.Height));
        Check(Math.Abs(corner.X - 1694) < .001 && Math.Abs(corner.Y - 1341) < .001,
            "The recorded normalized matrix must map the canvas corner to its actual screen position.");
        var center = new PointF(size.Width / 2f, size.Height / 2f);
        var roundtrip = transform.ScreenToCanvas(transform.CanvasToScreen(center));
        Check(Math.Abs(roundtrip.X - center.X) < .01 && Math.Abs(roundtrip.Y - center.Y) < .01 &&
            transform.IsCanvasPoint(transform.CanvasToScreen(center)),
            "Recorded forward and inverse matrices must roundtrip a selectable canvas point.");

        Reject(() => new CanvasAffineTransform(bound, "foreign-session", size),
            "Current selection must still reject a foreign session.");
        Reject(() => new CanvasAffineTransform(bound, writer.SessionId, new(size.Width + 1, size.Height)),
            "Current selection must still reject mismatched capture dimensions.");
        Reject(() => new CanvasAffineTransform(Changed(bound, n => n["status"] = "unknown"), writer.SessionId, size),
            "Relaxing attribution must not allow an unconfirmed recognition status.");
        Reject(() => new CanvasAffineTransform(Changed(bound, n => n["rawResult"]!["success"] = false), writer.SessionId, size),
            "Relaxing attribution must not allow a failed native recognition result.");
        Reject(() => new CanvasAffineTransform(Changed(bound,
            n => n["state"]!["transform"]!["matrices"]!["canvasToScreen"]![2] = 1000), writer.SessionId, size),
            "Relaxing attribution must not allow inconsistent native inverse matrices.");
        Reject(() => new CanvasAffineTransform(Changed(bound,
            n => n["state"]!["transform"]!["matrices"]!["convention"] = "unknown"), writer.SessionId, size),
            "Relaxing attribution must not allow an unsupported matrix convention.");

        control.Old = true;
        try { await feed.RequestAsync(["canvasViewState"], deadline.Token); }
        catch (IOException) { checks++; return checks; }
        throw new InvalidOperationException("The active request must still reject an old capture despite valid recorded matrices.");
    }

    private static JsonElement Changed(JsonElement source, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(source.GetRawText())!.AsObject();
        change(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed class RecordedControl(MemolineWriter writer, JsonElement recorded) : IPacketStateControl
    {
        internal int Calls;
        internal bool Old;
        internal string? RequestedSession, RequestId;
        public Task<IReadOnlyDictionary<string, JsonElement>> RequestAsync(string sessionId, string requestId,
            string[] modules, string? saveId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++; RequestedSession = sessionId; RequestId = requestId;
            if (!modules.SequenceEqual(["canvasViewState"]) || sessionId != writer.SessionId)
                throw new InvalidOperationException("Unexpected recorded canvas request.");
            var result = Changed(recorded, n => n["evidence"]!["capturedTicks"] = Old ? 0 : writer.NowTicks);
            IReadOnlyDictionary<string, JsonElement> results = new Dictionary<string, JsonElement> { ["canvasViewState"] = result };
            return Task.FromResult(results);
        }
    }

    // Exact native frame from the user's 2026-10-08 recording, session
    // b1d2cf2b8cbe4077bbe0b5e6b9bce726, appendId 374. No screenshots or image bytes.
    private const string RecordedFrame = """
{"appendId":374,"path":"delayed","kind":"coreStateUpdated","eventId":0,"ticks":778748395,"appendedTicks":782010186,"operationId":null,"relatedEventIds":[],"deviceSource":null,"data":{"packageId":"14cb248179fc4e2387648fda411e1c95","initial":false,"rawResult":{"success":true,"canvasWindowRoiScreenPx":{"left":409,"top":149,"right":2238,"bottom":1476,"width":1829,"height":1327,"isEmpty":false},"navigatorThumbnailRoiScreenPx":{"left":2278,"top":145,"right":2560,"bottom":669,"width":282,"height":524,"isEmpty":false},"screenCoordinateOriginScreenPx":{"x":0,"y":0},"canvasOriginScreenPx":{"x":968.0000000000001,"y":315},"ocrScalePercent":14.6,"ocrRotationDegrees":0,"completedViewportScreenPx":null,"ocrLayout":{"scaleSlotScreen":{"left":2296,"top":675,"right":2353,"bottom":705,"width":57,"height":30,"isEmpty":false},"rotationSlotScreen":{"left":2296,"top":705,"right":2353,"bottom":736,"width":57,"height":31,"isEmpty":false},"scaleDigitsScreen":{"left":2298,"top":675,"right":2353,"bottom":705,"width":55,"height":30,"isEmpty":false},"rotationDigitsScreen":{"left":2299,"top":711,"right":2353,"bottom":736,"width":54,"height":25,"isEmpty":false}},"snapshot":{"status":0,"snapshotId":"b89f166eaef344c29e525fe97a911d4c-7","generation":7,"recomputeGeneration":3,"captureId":"b89f166eaef344c29e525fe97a911d4c","canvasPixelWidth":4961,"canvasPixelHeight":7016,"workspaceRoi":{"left":409,"top":149,"right":2238,"bottom":1476,"width":1829,"height":1327,"isEmpty":false},"navigatorRoi":{"left":2278,"top":145,"right":2560,"bottom":736,"width":282,"height":591,"isEmpty":false},"navigatorThumbnailRoi":{"left":2278,"top":145,"right":2560,"bottom":669,"width":282,"height":524,"isEmpty":false},"workspaceCanvas":{"status":0,"boundsCapture":{"left":968,"top":315,"right":1694,"bottom":1341,"width":726,"height":1026,"isEmpty":false},"boundsScreen":{"left":968,"top":315,"right":1694,"bottom":1341,"width":726,"height":1026,"isEmpty":false},"aspectRatio":0.7076023,"confidence":1,"visibleEdgesMask":15,"boundarySupport":[1,1,1,1],"fourSidesComplete":true,"hasAxisAlignedBoundary":true,"ambiguous":false,"ambiguityReason":""},"navigatorCanvas":{"status":0,"boundsCapture":{"left":2280,"top":211,"right":2558,"bottom":604,"width":278,"height":393,"isEmpty":false},"boundsScreen":{"left":2280,"top":211,"right":2558,"bottom":604,"width":278,"height":393,"isEmpty":false},"aspectRatio":0.70737916,"confidence":0.35,"visibleEdgesMask":0,"boundarySupport":[0,0,0,0],"fourSidesComplete":false,"hasAxisAlignedBoundary":false,"ambiguous":false,"ambiguityReason":""},"numbers":{"scalePercent":14.6,"rotationDegrees":0,"scaleConfidence":0.85,"rotationConfidence":0.85,"scaleRawText":"14.6","rotationRawText":"0.0","scaleDigitsCapture":null,"rotationDigitsCapture":null,"sourceCaptureId":"b89f166eaef344c29e525fe97a911d4c","capturedAt":"2026-10-08T12:51:17.1388443Z"},"scaleReference":14.6,"relativeScale":1,"cumulativeRelativeScale":1,"rotationDegreesGeometry":0,"rotationDegreesOcrOrInjected":0,"rotationDegrees":0,"scalePercentOcrOrInjected":14.6,"scaleGeometryEstimate":14.628931,"scaleConsistencyError":0.0019815522,"marker":{},"confidence":0.59,"usedDirectWorkspacePath":true,"viewportCompletionStrategy":0,"confirmedCompleteEdgeCount":0,"completeEdges":[],"observedRedEdgeCount":0,"observedRedEdges":[],"sourceRevision":"sct-embedded-wb","coordinateConventionVersion":1,"failureMessage":"","failureStatus":0,"raw":{},"matrices":{"screenToCanvas":[0.0013774104683195593,0,-1.3333333333333333,0,0.0009746588693957114,-0.3070175438596491],"canvasToScreen":[726.0000000000001,0,968.0000000000001,0,1026.0000000000002,315],"convention":"sct-affine-row-major/v1","canvasSpace":"normalized"}},"failedStage":null,"status":0,"message":""},"module":"canvasViewState","status":"unchanged","state":{"canvasOriginScreenPx":{"x":968.0000000000001,"y":315},"ocrScalePercent":14.6,"ocrRotationDegrees":0,"transform":{"canvasPixelWidth":4961,"canvasPixelHeight":7016,"scaleReference":14.6,"cumulativeRelativeScale":1,"rotationDegrees":0,"scaleGeometryEstimate":14.628931,"matrices":{"screenToCanvas":[0.0013774104683195593,0,-1.3333333333333333,0,0.0009746588693957114,-0.3070175438596491],"canvasToScreen":[726.0000000000001,0,968.0000000000001,0,1026.0000000000002,315],"convention":"sct-affine-row-major/v1","canvasSpace":"normalized"}}},"error":null,"evidence":{"screenshotIds":["e87c50827a7d43e6b371ab7e3323fc8f","0bc6b5de8f8846fba54d1a64ab6588d2","dfb96b97f8f54fcabce01e2dbf58316f","5721ba4a94a24b5b91dad44a195c4267"],"capturedTicks":778748395,"captureId":"5f087b693f9c4f4697ee14348bdc3845","triggerTicks":777507864,"causalAmbiguous":true,"relatedEventIds":[],"completedTicks":782005147,"analysisStartedTicks":780199247,"analysisQuietMs":150,"analysisToken":null,"finalEvidence":true,"observedAfterEventId":3}}}
""";
}

