using System.Text.Json;
using StrokeReplay;

internal static partial class PacketChecks
{
    // A wheel zoom read 82 ms later can pair the new origin with the old zoom number. The stroke after it
    // must take the later reading Memoline requested at Alt+M, unless a real navigation input came between.
    private static void SettledViews()
    {
        static JsonElement Hardware(string kind, ulong append, ulong eventId, long ticks, object data, ulong? operation = null) =>
            Json(new { kind, path = "hardware", appendId = append, eventId, operationId = operation ?? eventId, ticks,
                deviceSource = new { deviceType = kind.StartsWith("pen") ? "pen" : kind == "keyInput" ? "keyboard" : "mouse" }, data });
        static JsonElement View(double scale, bool ambiguous, ulong? observedAfter) => Json(new
        {
            module = "canvasViewState", status = "changed",
            state = new { ocrScalePercent = scale, ocrRotationDegrees = 0.0, canvasOriginScreenPx = new { x = 895.0, y = 313.0 } },
            evidence = observedAfter is { } after ? (object)new { causalAmbiguous = ambiguous, observedAfterEventId = after } : new { causalAmbiguous = ambiguous }
        });
        static JsonElement Reading(string id, ulong append, ulong anchor, JsonElement view) => Json(new
        { kind = "statePackageResult", appendId = append, data = new { packageId = id, afterEventId = anchor, result = new { updates = new[] { view } } } });
        static JsonElement Reservation(string id, ulong append, ulong anchor) => Json(new
        { kind = "statePackageReserved", appendId = append, data = new { packageId = id, afterEventId = anchor, reservationOrder = append } });
        static JsonElement Resolved(ulong append, ulong eventId, params string[] targets) => Json(new
        {
            kind = "shortcutResolved", path = "immediate", appendId = append, relatedEventIds = new[] { eventId },
            data = targets.Length == 0 ? (object)new { resolution = "unmapped", matches = Array.Empty<object>() }
                : new { resolution = "matched", matches = new[] { new { targets } } }
        });
        static double StrokeScale(JsonElement key, JsonElement resolved, JsonElement later)
        {
            var pen = new { x = 1000.0, y = 600.0, normalizedPressure = 0.5, contactState = "Contact" };
            JsonElement[] frames =
            [
                Json(new { kind = "header", frequency = 1000, sessionId = "settle" }),
                Hardware("mouseWheel", 1, 1, 10, new { x = 1000, y = 600, delta = 120 }),
                Reservation("wheel", 2, 1), Reading("wheel", 3, 1, View(21.8, false, 1)),
                Hardware("penBegin", 4, 2, 20, pen), Hardware("penSample", 5, 3, 25, pen, 2),
                Hardware("penEnd", 6, 4, 30, new { x = 1000.0, y = 600.0 }, 2),
                key, resolved, Reservation("boundary", 9, 5), Reading("boundary", 10, 5, later),
            ];
            return MemolineReplayReader.Parse("settle", frames).Strokes.Single().View.ScalePercent;
        }
        var altM = Hardware("keyInput", 7, 5, 40, new { key = "M", modifiers = new[] { "Alt" } });
        Check(StrokeScale(altM, Resolved(8, 5), View(24, true, 5)) == 24,
            "A stroke takes the last reading before the next navigation input, including Memoline's own placed request.");
        Check(StrokeScale(altM, Resolved(8, 5, "画布视口", "导航器"), View(24, true, 5)) == 21.8,
            "A key that targets the canvas viewport ends the interval: a later reading must not move backwards over it.");
        Check(StrokeScale(altM, Resolved(8, 5, "工具组", "笔刷属性"), View(24, false, 5)) == 24,
            "Tool switches cannot move the canvas and do not end the interval.");
        Check(StrokeScale(altM, Resolved(8, 5), View(24, true, null)) == 21.8,
            "An ambiguous reading without an observed position is not used.");
    }
}
