using System.Text.Json;
using System.Text.Json.Nodes;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal static class StateInvalidationRecoveryChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string message)
        { count++; if (!value) throw new InvalidOperationException(message); }
        void Eligible(SessionSnapshot session, bool expected, string message)
        {
            var index = EditIndexBuilder.Build(session);
            Check(index.Strokes.Count == (expected ? 1 : 0) &&
                index.Images.Values.Single().HasUnreplayableEvents == !expected, message);
        }
        var baseline = Fixture();
        Eligible(baseline, true, "A complete native contact starts with four confirmed historical states.");
        foreach (string module in new[] { "brushState", "currentLayerState", "clipState", "canvasViewState" })
        {
            var invalidated = Insert(baseline, Invalidate(module));
            Eligible(invalidated, false, "Unreferenced activation invalidates the old " + module + ".");
            Eligible(Insert(invalidated, Update(module, 8, 95, 1, 95)), true,
                "A newly captured confirmed " + module + " restores eligibility after unreferenced invalidation.");
            Eligible(Insert(invalidated, Update(module, 8, 95, 1, 95, status: "unchanged")), true,
                "A fresh unchanged confirmation also restores " + module + ".");
            Eligible(Insert(invalidated, Update(module, 8, 95, 1, 95, status: "error")), false,
                "An unsuccessful fresh " + module + " cannot restore an old confirmation.");
            Eligible(Insert(invalidated, Update(module, 8, 95, 1, 95, ambiguous: true)), false,
                "causalAmbiguous remains disqualifying for historical " + module + ".");
            Eligible(Insert(invalidated, Update(module, 8, 95, 1, 85)), false,
                "A screenshot taken before activation cannot restore " + module + " through later publication.");
            Eligible(Insert(invalidated, Update(module, 8, 95, 1, 105)), false,
                "A screenshot taken after penBegin cannot supply its earlier " + module + ".");
            Eligible(Insert(invalidated, Update(module, 15, 130, 1, 95)), true,
                "A delayed result may confirm " + module + " only with a screenshot proven to precede penBegin.");
            Eligible(Insert(invalidated, Update(module, 15, 130, 1, null)), false,
                "A delayed " + module + " without capture timing cannot prove the past input state.");
            Eligible(Insert(invalidated, Update(module, 8, 95, 2, 95)), false,
                "An anchor at penBegin cannot confirm " + module + " before that same input.");

            var old = baseline.Frames.Single(f => J.Text(J.Get(f, "data"), "module") == module);
            var repeated = Insert(invalidated, With(old, append: 8, ticks: 95));
            repeated = repeated with { History = StateHistory.Resolve(invalidated.Frames) };
            Eligible(repeated, false, "Repeated publication cannot raise the original confirmation append for " + module + ".");
        }

        var restored = Insert(Insert(baseline, Invalidate("brushState")), Update("brushState", 8, 95, 1, 95));
        var reservation = Frame("statePackageReserved", 9, 96,
            new { packageId = "pending", afterEventId = 1, reservationOrder = 99, status = "pending" });
        Eligible(Insert(restored, reservation), false, "A still unresolved package remains a historical barrier after revalidation.");
        var futureResolution = Frame("statePackageResult", 16, 135,
            new { packageId = "pending", afterEventId = 1, result = new { updates = Array.Empty<object>() } });
        Eligible(Insert(Insert(restored, reservation), futureResolution), false,
            "Resolving a package after penBegin without capture timing cannot erase its earlier pending barrier.");
        var resolvedBefore = Frame("statePackageResult", 9, 97,
            new { packageId = "pending", afterEventId = 1, result = new { updates = Array.Empty<object>() } });
        var earlierReservation = With(reservation, append: 6, ticks: 89);
        Eligible(Insert(Insert(restored, earlierReservation), resolvedBefore), true,
            "A package resolved before the input does not keep a fresh confirmation pending.");

        var noInvalidationFuture = Insert(baseline, Update("brushState", 15, 130, 1, 105));
        Eligible(noInvalidationFuture, false, "A future screenshot with an old anchor is rejected even without an activation record.");
        var referenced = Insert(baseline, Invalidate("brushState", [1]));
        Eligible(referenced, false, "An input-referenced invalidation still blocks an older causal slot.");
        Eligible(Insert(referenced, Update("brushState", 8, 95, 1, 95)), true,
            "A later same-anchor confirmation can repair a referenced invalidation.");

        foreach (long? captured in new long?[] { 139, null, 141 })
        {
            var layerFrame = JsonNode.Parse(Update("currentLayerState", 15, 139, 4, captured).GetRawText())!.AsObject();
            layerFrame["data"]!["state"] = "Other";
            var undoSession = Insert(Insert(Insert(baseline,
                Frame("keyInput", 14, 140, new { }, eventId: 5, operation: 5)),
                JsonSerializer.SerializeToElement(layerFrame)),
                Frame("shortcutResolved", 16, 150, new { matches = new[] { new { command = "undo" } } }, refs: [5]));
            var undo = EditIndexBuilder.Build(undoSession).Layers.Values.Single().UndoRedo;
            Check(captured == 139 ? undo.Count == 0 : undo.Count == 1 && undo[0].Ambiguous,
                "Delayed other-layer undo attribution requires proven capture before the command: " +
                (captured?.ToString() ?? "missing timing") + ".");
        }
        Console.WriteLine($"State invalidation recovery checks passed: {count}.");
        return count;
    }

    private static object State(string module) => module switch
    {
        "brushState" => new { name = "G pen", properties = new[] { new { key = "brush_size", type = "number", status = "ok", value = 2 } } },
        "clipState" => new { layers = new[] { new { name = "Ink", uuid = "ink" }, new { name = "Other", uuid = "other" } } },
        "canvasViewState" => new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
        _ => "Ink"
    };
    private static JsonElement Update(string module, ulong append, long ticks, ulong anchor = 0,
        long? captured = null, string status = "changed", bool ambiguous = false)
    {
        var data = JsonSerializer.SerializeToNode(new { module, status, state = State(module),
            evidence = new { observedAfterEventId = anchor, causalAmbiguous = ambiguous } })!.AsObject();
        if (captured is { } capturedTicks) data["evidence"]!["capturedTicks"] = capturedTicks;
        return Frame("coreStateUpdated", append, ticks, data);
    }
    private static JsonElement Invalidate(string module, ulong[]? refs = null)
        => Frame("panelUpdateRequested", 7, 90, new { modules = new[] { module }, phase = "invalidate" }, refs: refs);
    private static JsonElement Frame(string kind, ulong append, long ticks, object data,
        ulong eventId = 0, ulong operation = 0, ulong[]? refs = null)
        => JsonSerializer.SerializeToElement(new { kind, appendId = append, ticks, eventId, operationId = operation,
            relatedEventIds = refs ?? [], path = operation > 0 ? "hardware" : "state", deviceSource = new { deviceType = "pen" }, data });
    private static JsonElement With(JsonElement frame, ulong append, long ticks)
    {
        var node = JsonNode.Parse(frame.GetRawText())!.AsObject(); node["appendId"] = append; node["ticks"] = ticks;
        return JsonSerializer.SerializeToElement(node);
    }
    private static SessionSnapshot Insert(SessionSnapshot session, JsonElement frame)
        => session with { Frames = session.Frames.Append(frame).OrderBy(f => J.U64(f, "appendId")).ToArray() };

    private static SessionSnapshot Fixture()
    {
        var frames = new List<JsonElement>
        {
            Frame("shortcutConfiguration", 1, 0, new { toolCatalog = new { nodes = new[] {
                new { id = "tool", kind = "tool", name = "Pen", parentId = "root", toolId = "tool" },
                new { id = "subtool", kind = "subtool", name = "G pen", parentId = "tool", toolId = "tool" } } } }),
            Update("brushState", 2, 0), Update("currentLayerState", 3, 0), Update("clipState", 4, 0), Update("canvasViewState", 5, 0),
            Frame("penBegin", 10, 100, new { penDownLocation = new { region = "canvasViewport", status = "matched" } }, 2, 2),
            Frame("penSample", 11, 110, new { }, 3, 2), Frame("penEnd", 12, 120, new { }, 4, 2),
            Frame("editBoundaryRequested", 20, 200, new { })
        };
        var samples = new MemolineReplaySample[] { new(100, 1.5, 1.5, 1, 0, 0, true, 10, 2),
            new(110, 2.5, 1.5, 1, 0, 0, true, 11, 3), new(120, 2.5, 1.5, 0, 0, 0, false, 12, 4) };
        var replay = new MemolineReplayDocument("fixture.part", "invalidate", 1000,
            [new(2, 100, 120, new(100, 0, 0, 0), samples)]);
        var layer = new LayerKey("invalidate", 1, "ink"); var mask = new PixelMask(8, 8); mask.Set(1, 1);
        var image = new DirtyImage(new(layer, "packet", "image"), mask, new(8, 8, new byte[256]),
            new(8, 8, new byte[256]), []) { LabelIds = ["label"] };
        var label = new LabelEvidence("label", 2, "tablet", 100, 120, 1, [new(1.5, 1.5, 2.5, 1.5)], ["image"]);
        var packet = new LayerPacket("packet", layer, 200, null, new(8, 8, new byte[256]), [image],
            IsBoundary: true, AfterSnapshotId: "baseline", NowSnapshotId: "current", BaselineSource: "recordingStart",
            CanvasWidth: 8, CanvasHeight: 8) { Labels = [label] };
        return new("fixture.part", "invalidate", 1000, frames, replay, [packet], 20, 200);
    }
}
