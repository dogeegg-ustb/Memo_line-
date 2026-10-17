using System.Text.Json;
using System.Text.Json.Nodes;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class EditingChecks
{
    internal static int Run(string root)
    {
        int checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
        var classifier = new BrushClassifier();
        Check(!classifier.Classify(Json("""{"name":"Pen","properties":[]}""")).Eligible,
            "A brush name must not substitute for a confirmed tool catalog route.");
        var catalog = ToolCatalog.FromJson(Json("""
            {"status":"ok","nodes":[
              {"id":"t","kind":"tool","name":"Pen","parentId":"r","toolId":"t"},
              {"id":"g","kind":"group","name":"Ink","parentId":"t","toolId":"t"},
              {"id":"s","kind":"subtool","name":"G pen","parentId":"g","toolId":"t"}]}
            """));
        var disabledMixing = Json("""{"name":"G pen","properties":[{"key":"ink.color_mixing","enabled":"disabled","status":"unknown","value":false}]}""");
        var causal = classifier.Classify(disabledMixing, catalog);
        Check(causal.Eligible && causal.Causal, "Catalog parent tool and disabled property presence classify a causal pen.");
        foreach (var propertyKey in new[] { "2_brush_shape.blending_mode", "texture.blending_mode", "unknown.setting", "correction.unknown" })
        {
            var classification = classifier.Classify(JsonSerializer.SerializeToElement(new
            { name = "G pen", properties = new[] { new { key = propertyKey } } }), catalog);
            Check(!classification.Causal && !classification.ProbeGuaranteed, "Non-canvas or unknown property is not causal/guaranteed: " + propertyKey);
        }
        var ambiguous = ToolCatalog.FromJson(Json("""
            {"nodes":[{"id":"p","kind":"tool","name":"Pen","toolId":"p"},
            {"id":"f","kind":"tool","name":"Fill","toolId":"f"},
            {"id":"a","kind":"subtool","name":"Same","toolId":"p","parentId":"p"},
            {"id":"b","kind":"subtool","name":"Same","toolId":"f","parentId":"f"}]}
            """));
        Check(!classifier.Classify(Json("""{"name":"Same","properties":[]}"""), ambiguous).Eligible,
            "Duplicate subtool names cannot silently choose the last parent.");
        var layer = new LayerKey("test", 1, "ink");
        var mask = new PixelMask(1, 0, 1, 1); mask.Set(1, 0);
        var patch = new DirtyImage(new(layer, "p1", "i1"), mask,
            new(1, 1, new byte[] { 200, 0, 0, 255 }), new(1, 1, new byte[] { 9, 8, 7, 0 }), []);
        var destination = new ImageRgba(2, 1, new byte[] { 1, 2, 3, 255, 200, 0, 0, 255 });
        ImageCompositor.Replace(destination, patch, before: false);
        Check(destination.Bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 255, 9, 8, 7, 0 }),
            "Mask replacement preserves RGB even when the replacing alpha is zero.");
        var packets = new[] { new LayerPacket("p1", layer, 10, null, destination, [patch],
            IsBoundary: true, AfterSnapshotId: "baseline", NowSnapshotId: "s1", BaselineSource: "recordingStart", CanvasWidth: 2, CanvasHeight: 1) };
        var timeline = new LayerTimeline(packets, destination);
        var before = timeline.BeforePacket("p1");
        Check(before.Bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 255, 200, 0, 0, 255 }),
            "Historical seed is reconstructed from before patches.");
        Check(timeline.VerifyRoundTrip(), "Reverse then forward patches equal the boundary capture byte for byte.");
        var pixels = Enumerable.Range(0, 64 * 48 * 4).Select(i => (byte)(i * 7 % 251)).ToArray();
        var parked = new ImageRgba(64, 48, pixels.ToArray());
        var twin = new ImageRgba(64, 48, pixels.ToArray());
        parked.Park();
        Check(parked.IsParked && parked.SamePixels(twin) && twin.SamePixels(parked),
            "A parked image compares by digest without being restored.");
        var copied = parked.Copy();
        Check(parked.IsParked && copied.Bytes.AsSpan().SequenceEqual(pixels), "Copying a parked image leaves it compressed.");
        Check(parked.Bytes.AsSpan().SequenceEqual(pixels) && !parked.IsParked, "Reading a parked image restores identical pixels.");
        parked.Bytes[0] ^= 1; parked.Park();
        Check(!parked.SamePixels(twin) && parked.Bytes[0] == (pixels[0] ^ 1),
            "A write after restoring is kept when the image is parked again.");
        var stored = twin.ParkedCopy();
        Check(stored.IsParked && !twin.IsParked && stored.SamePixels(twin), "A parked copy leaves its source decoded.");
        var conflict = patch with { Key = new(layer, "p1", "i2"), Now = new(1, 1, new byte[] { 0, 0, 0, 255 }) };
        try { ImageCompositor.ValidateGroup([patch, conflict], 2, 1); }
        catch (InvalidDataException) { checks++; goto overlapChecked; }
        throw new InvalidOperationException("Conflicting overlapping images in a capture group were accepted.");
        overlapChecked:
        var frames = new[]
        {
            Json("""{"appendId":1,"kind":"shortcutConfiguration","data":{"toolCatalog":{"nodes":[{"id":"t","kind":"tool","name":"Pen"},{"id":"s","kind":"subtool","name":"G pen","parentId":"t","toolId":"t"}]}}}"""),
            Json("""{"appendId":2,"kind":"coreStateUpdated","data":{"module":"brushState","status":"changed","state":{"name":"G pen","properties":[{"key":"brush_size","type":"number","status":"ok","value":2}]},"evidence":{"observedAfterEventId":0}}}"""),
            Json("""{"appendId":3,"kind":"coreStateUpdated","data":{"module":"currentLayerState","status":"changed","state":"Ink","evidence":{"observedAfterEventId":0}}}"""),
            Json("""{"appendId":4,"kind":"coreStateUpdated","data":{"module":"clipState","status":"changed","state":{"layers":[{"name":"Ink","uuid":"ink"},{"name":"Other","uuid":"other"}]},"evidence":{"observedAfterEventId":0}}}"""),
            Json("""{"appendId":5,"kind":"coreStateUpdated","data":{"module":"canvasViewState","status":"changed","state":{"canvasOriginScreenPx":{"x":0,"y":0},"ocrScalePercent":100,"ocrRotationDegrees":0},"evidence":{"observedAfterEventId":0}}}"""),
            Json("""{"appendId":6,"eventId":1,"operationId":1,"ticks":100,"path":"hardware","kind":"penBegin","deviceSource":{"deviceType":"pen"},"data":{"penDownLocation":{"region":"canvasViewport","status":"matched"}}}"""),
            Json("""{"appendId":7,"eventId":2,"operationId":1,"ticks":110,"path":"hardware","kind":"penSample","deviceSource":{"deviceType":"pen"},"data":{}}"""),
            Json("""{"appendId":8,"eventId":3,"operationId":1,"ticks":120,"path":"hardware","kind":"penEnd","deviceSource":{"deviceType":"pen"},"data":{}}"""),
            Json("""{"appendId":9,"ticks":200,"kind":"editBoundaryRequested","data":{}}""")
        };
        var samples = new StrokeReplay.MemolineReplaySample[]
        { new(100,1,0,1,0,0,true,6,1), new(110,1.5,0,1,0,0,true,7,2), new(120,1.5,0,0,0,0,false,8,3) };
        var stroke = new StrokeReplay.MemolineReplayStroke(1,100,120,new(100,0,0,0), samples);
        var replay = new StrokeReplay.MemolineReplayDocument("fixture.part","test",1000,[stroke]);
        var label = new LabelEvidence("l1",1,"tablet",100,120,1,[new(1,0,1.5,0)],["i1"]);
        var p1 = packets[0] with { IsBoundary = false, TriggerTicks = 115, Current = null,
            Images = [patch with { LabelIds = ["l1"] }], Labels = [label] };
        var p2 = p1 with { PacketId = "p2", TriggerTicks = 200, IsBoundary = true, Current = destination,
            AfterSnapshotId = "s1", NowSnapshotId = "s2", Images = [patch with { Key = new(layer,"p2","i1"), LabelIds = ["l1"] }] };
        var other = new LayerKey("test",1,"other");
        var otherPacket = p1 with { PacketId = "p3", Layer = other, IsBoundary = true, Current = destination,
            Images = [patch with { Key = new(other,"p3","i1"), LabelIds = ["l1"] }] };
        var session = new SessionSnapshot("fixture.part","test",1000,frames,replay,[p1,p2,otherPacket],9,200);
        var index = EditIndexBuilder.Build(session);
        var key = new EditableStrokeKey("test",6);
        Check(index.Strokes.Count == 1 && index.Strokes[key].Samples.Count == 3,
            "One complete contact across packets is indexed once with all native samples.");
        Check(index.Strokes[key].Images.Count == 2 && index.Images[p2.Images[0].Key].Events.SequenceEqual(new[] { key }),
            "Label operationId resolves to penBegin appendId, with bidirectional associations.");
        Check(index.Images[otherPacket.Images[0].Key].Events.Count == 0,
            "A label overlapping another layer cannot associate that layer's image with this contact.");
        foreach (var changed in new[]
        { frames[5].GetRawText().Replace("\"deviceType\":\"pen\"", "\"deviceType\":\"mouse\""),
          frames[5].GetRawText().Replace("canvasViewport", "layers") })
        {
            var excludedFrames = frames.ToArray(); excludedFrames[5] = Json(changed);
            var excluded = EditIndexBuilder.Build(session with { Frames = excludedFrames });
            Check(excluded.Strokes.Count == 0 && excluded.Images[p1.Images[0].Key].HasUnreplayableEvents,
                "Mouse/outside contacts are excluded but prevent mandatory image replay.");
        }
        var inTransaction = frames.Select(frame =>
        {
            var node = JsonNode.Parse(frame.GetRawText())!.AsObject();
            ulong append = node["appendId"]!.GetValue<ulong>();
            if (append >= 6) node["appendId"] = append + 1;
            return JsonSerializer.SerializeToElement(node);
        }).Append(Json("""{"appendId":6,"ticks":99,"kind":"editTransactionBegan","data":{"transactionId":"t"}}""")).ToArray();
        Check(EditIndexBuilder.Build(session with { Frames = inTransaction, BoundaryAppendId = 10 }).Strokes.Count == 0,
            "An unfinished edit transaction excludes contacts until the editing boundary.");
        var fillCatalog = ToolCatalog.FromJson(Json("""{"nodes":[{"id":"t","kind":"tool","name":"Fill"},{"id":"s","kind":"subtool","name":"G pen","parentId":"t","toolId":"t"}]}"""));
        Check(!classifier.Classify(disabledMixing, fillCatalog).Causal,
            "A non-brush catalog category cannot become a causal brush from a similar property name.");
        var lossy = EditIndexBuilder.Build(session with { Packets = [p1 with { FullResolution = false }, p2] });
        Check(lossy.Issues.Any(i => i.Code == PlanIssueCode.ChainLossy), "Coarse historical comparison blocks exact reconstruction.");
        var broken = EditIndexBuilder.Build(session with { Packets = [p1, p2 with { AfterSnapshotId = "missing" }] });
        Check(broken.Issues.Any(i => i.Code == PlanIssueCode.ChainBroken), "A snapshot link breakpoint is reported explicitly.");
        var noBaseline = EditIndexBuilder.Build(session with { Packets = [p1 with { BaselineSource = null }, p2] });
        Check(noBaseline.Issues.Any(i => i.Code == PlanIssueCode.NoBaseline), "An unproven first snapshot cannot be used as a baseline.");
        checks += LiveReaderChecks.Run(root);
        Console.WriteLine($"Editing core checks passed: {checks}.");
        return checks;
    }
}
