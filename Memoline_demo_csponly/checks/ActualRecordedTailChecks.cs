using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using MemolineDemo.Editing;
using PacketReplay;

namespace MemolineDemo;

/// <summary>Explains the evidence interval after the last successful full-layer capture.</summary>
internal static class ActualRecordedTailChecks
{
    internal static void Run(string ownedSessionCacheDirectory, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        string supplied = Path.GetFullPath(ownedSessionCacheDirectory);
        string oldDirectory = File.Exists(Path.Combine(supplied, "recording.memoline"))
            ? supplied : Path.Combine(supplied, "session-1");
        string cacheRoot = Directory.GetParent(oldDirectory)!.FullName;
        string nativePath = Path.Combine(oldDirectory, "recording.memoline");
        string eventsPath = Path.Combine(oldDirectory, "events.jsonl");
        var result = new Dictionary<string, object?>
        {
            ["sourceSessionCache"] = oldDirectory,
            ["scope"] = "Original verified native frames, packet rows, and cached original PNGs only. Recorded input and pixel differences do not prove causation or timestamp an uncaptured document change."
        };
        try
        {
            string nativeHash = Hash(nativePath), aggregateHash = Hash(eventsPath);
            var packets = Packets(eventsPath);
            var captures = packets.Where(p => (J.Text(p, "status") is "captured" or "baseline") &&
                J.Text(p, "fullCanvasAssetId") is not null).ToArray();
            var lastCapture = captures.MaxBy(p => Number(p, "triggerTicks"));
            if (lastCapture.ValueKind != JsonValueKind.Object) throw new InvalidDataException("No successful full-canvas PNG in the session.");
            long captureTicks = Number(lastCapture, "triggerTicks");
            var tailCounts = new Dictionary<string, int>();
            var allCounts = new Dictionary<string, int>();
            var pens = new List<object>();
            var shortcuts = new List<object>();
            var updates = new List<object>();
            var commands = new List<object>();
            var headers = new List<JsonElement>();
            JsonElement footer = default;
            long lastPenTicks = -1, maxFrameTicks = -1, maxObservedCaptureTicks = -1;
            ulong lastPenAppend = 0;
            int importantCount = 0;
            string importantPath = Path.Combine(reportDirectory, "native-tail-important.jsonl");
            using (var importantOutput = new StreamWriter(importantPath, false, new UTF8Encoding(false)))
            {
                foreach (var frame in MemolineReader.Read(nativePath))
                {
                    string kind = J.Text(frame, "kind") ?? "(missing)";
                    allCounts[kind] = allCounts.GetValueOrDefault(kind) + 1;
                    long ticks = Number(frame, "ticks", -1);
                    maxFrameTicks = Math.Max(maxFrameTicks, ticks);
                    if (kind == "header") headers.Add(frame.Clone());
                    if (kind == "footer") footer = frame.Clone();
                    if (kind is "penBegin" or "penEnd" or "penInterrupted")
                    {
                        if (ticks >= lastPenTicks) { lastPenTicks = ticks; lastPenAppend = Unsigned(frame, "appendId"); }
                    }
                    if (kind == "coreStateUpdated")
                    {
                        var evidence = J.Get(J.Get(frame, "data"), "evidence");
                        maxObservedCaptureTicks = Math.Max(maxObservedCaptureTicks, Number(evidence, "capturedTicks", -1));
                    }
                    if (ticks < captureTicks) continue;
                    tailCounts[kind] = tailCounts.GetValueOrDefault(kind) + 1;
                    var data = J.Get(frame, "data");
                    if (kind is "penBegin" or "penEnd" or "penInterrupted")
                        pens.Add(new { Kind = kind, Ticks = ticks, AppendId = Unsigned(frame, "appendId"),
                            EventId = Unsigned(frame, "eventId"), OperationId = Unsigned(frame, "operationId"),
                            SessionId = J.Text(frame, "sessionId"), Position = Position(frame), DataPosition = Position(data),
                            RelatedEventIds = Elements(J.Get(frame, "relatedEventIds")) });
                    if (kind == "shortcutResolved")
                        shortcuts.Add(new { Ticks = ticks, AppendId = Unsigned(frame, "appendId"),
                            RelatedEventIds = Elements(J.Get(frame, "relatedEventIds")), Data = Safe(data) });
                    if (kind is "coreStateUpdated" or "panelUpdateRequested" or "statePackageReserved" or "statePackageResult")
                        updates.Add(new { Kind = kind, Ticks = ticks, AppendId = Unsigned(frame, "appendId"),
                            Module = J.Text(data, "module"), Status = J.Text(data, "status"),
                            Phase = J.Text(data, "phase"), Reason = J.Text(data, "reason"),
                            TriggerReason = J.Text(data, "triggerReason"),
                            RelatedEventIds = Elements(J.Get(frame, "relatedEventIds")),
                            Evidence = Safe(J.Get(data, "evidence")) });
                    // A refreshed shortcut catalog may contain Undo without any Undo invocation.
                    // Count commands only on actual shortcut/command event rows.
                    bool commandEvent = kind == "shortcutResolved" || kind.Contains("command", StringComparison.OrdinalIgnoreCase);
                    var commandNames = commandEvent ? CommandNames(data).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                    if (commandNames.Length > 0)
                        commands.Add(new { Kind = kind, Ticks = ticks, AppendId = Unsigned(frame, "appendId"), Commands = commandNames,
                            RelatedEventIds = Elements(J.Get(frame, "relatedEventIds")) });
                    bool important = kind is "penBegin" or "penEnd" or "penInterrupted" or "shortcutResolved" or
                        "coreStateUpdated" or "panelUpdateRequested" or "statePackageReserved" or "statePackageResult" or
                        "footer" or "keyboard" or "keyInput" or "keyboardStateChanged" ||
                        kind.Contains("Boundary", StringComparison.OrdinalIgnoreCase) || commandNames.Length > 0;
                    if (important)
                    {
                        importantOutput.WriteLine(frame.GetRawText());
                        importantCount++;
                    }
                }
            }
            var header = headers.Single();
            long frequency = Number(header, "frequency");
            long footerTicks = Number(footer, "ticks", maxFrameTicks);
            result["sessionId"] = J.Text(header, "sessionId");
            result["createdAt"] = Safe(J.Get(header, "createdAt"));
            result["frequency"] = frequency;
            result["lastSuccessfulFullCanvasCaptureTicks"] = captureTicks;
            result["footerTicks"] = footerTicks;
            result["uncapturedTailSeconds"] = (footerTicks - captureTicks) / (double)frequency;
            result["lastPenTicks"] = lastPenTicks;
            result["lastPenAppendId"] = lastPenAppend;
            result["penAfterLastFullCanvasCapture"] = lastPenTicks > captureTicks;
            result["maxNativeFrameTicks"] = maxFrameTicks;
            result["maxNativeCoreObservationCapturedTicks"] = maxObservedCaptureTicks;
            result["maxPacketTriggerTicks"] = packets.Max(p => Number(p, "triggerTicks", -1));
            result["maxSuccessfulFullCanvasCaptureTicks"] = captureTicks;
            result["nativeFrameCounts"] = allCounts;
            result["tailFrameCounts"] = tailCounts;
            result["tailPens"] = pens;
            result["tailShortcuts"] = shortcuts;
            result["tailCommandNames"] = commands;
            result["tailCoreAndPanelUpdates"] = updates;
            result["packetSummary"] = packets.Select(PacketSummary).ToArray();
            string tailPacketsPath = Path.Combine(reportDirectory, "aggregation-tail-important.jsonl");
            using (var packetOutput = new StreamWriter(tailPacketsPath, false, new UTF8Encoding(false)))
                foreach (var packet in packets.Where(p => Number(p, "triggerTicks", -1) >= captureTicks))
                    packetOutput.WriteLine(packet.GetRawText());
            result["importantPacketRowsFile"] = tailPacketsPath;
            result["tailPacketRows"] = packets.Where(p => Number(p, "triggerTicks", -1) >= captureTicks)
                .Select(p => (object)new { Summary = PacketSummary(p), EventPointers = Safe(J.Get(p, "eventPointers")),
                    UncapturedNativePointers = Safe(J.Get(p, "uncapturedNativePointers")) }).ToArray();
            result["footer"] = Safe(footer);
            result["importantFrameCount"] = importantCount;
            result["importantFramesFile"] = importantPath;
            string nextDirectory = Path.Combine(cacheRoot, "session-5");
            if (File.Exists(Path.Combine(nextDirectory, "events.jsonl")))
            {
                var nextPacket = Packets(Path.Combine(nextDirectory, "events.jsonl"))
                    .Where(p => (J.Text(p, "status") is "captured" or "baseline") && J.Text(p, "fullCanvasAssetId") is not null)
                    .MinBy(p => Number(p, "triggerTicks"));
                result["nextObservedSessionCapture"] = PacketSummary(nextPacket);
                result["rgbaComparison"] = ComparePngs(oldDirectory, lastCapture, nextDirectory, nextPacket);
            }
            else result["rgbaComparison"] = new { Status = "Unavailable", Reason = "The session-5 cache was not present." };
            result["nativeFileHash"] = nativeHash;
            result["aggregationFileHash"] = aggregateHash;
            result["cachedInputBytesUnchanged"] = nativeHash == Hash(nativePath) && aggregateHash == Hash(eventsPath);
            Console.WriteLine($"Tail interval: {captureTicks} to {footerTicks}; {(footerTicks - captureTicks) / (double)frequency:F3} seconds.");
            Console.WriteLine($"Tail pens: {pens.Count}; shortcuts: {shortcuts.Count}; command-bearing frames: {commands.Count}; important frames: {importantCount}.");
        }
        catch (Exception error) { result["error"] = error.ToString(); }
        finally
        {
            string reportPath = Path.Combine(reportDirectory, "recorded-tail-summary.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Recorded-tail report: {reportPath}");
        }
    }

    private static List<JsonElement> Packets(string path)
    {
        var packets = new List<JsonElement>();
        using var input = new StreamReader(path, new UTF8Encoding(false, true));
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            if (J.Text(document.RootElement, "kind") == "packet") packets.Add(document.RootElement.Clone());
        }
        return packets;
    }

    private static object PacketSummary(JsonElement packet)
    {
        var manifest = J.Get(packet, "manifest");
        var recognizer = J.Get(manifest, "recognizer");
        var now = J.Get(manifest, "now");
        var after = J.Get(manifest, "after");
        return new
        {
            SessionId = J.Text(packet, "sessionId"), Status = J.Text(packet, "status"),
            Reason = J.Text(packet, "reason"), TriggerReason = J.Text(packet, "triggerReason"),
            Error = Safe(J.Get(packet, "error")), BoundaryKind = J.Text(packet, "boundaryKind"),
            TriggerTicks = Number(packet, "triggerTicks", -1),
            PacketId = J.Text(packet, "packetId"), ManifestId = J.Text(manifest, "id"),
            FullCanvasAssetId = J.Text(packet, "fullCanvasAssetId"),
            RecognizerFromTicks = Number(recognizer, "fromTicks", -1),
            RecognizerToTicks = Number(recognizer, "toTicks", -1),
            AfterSnapshotId = J.Text(after, "id"), NowSnapshotId = J.Text(now, "id"),
            Width = Number(now, "width", Number(after, "width", -1)),
            Height = Number(now, "height", Number(after, "height", -1)),
            EventPointerCount = Elements(J.Get(packet, "eventPointers")).Length,
            NativePointerCount = Elements(J.Get(packet, "nativePointers")).Length,
            DirtyMatrixCount = Elements(J.Get(packet, "dirtyMatrices")).Length,
            ScalarFields = packet.EnumerateObject().Where(p => p.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                .ToDictionary(p => p.Name, p => Safe(p.Value))
        };
    }

    private static object ComparePngs(string oldDirectory, JsonElement oldPacket, string nextDirectory, JsonElement nextPacket)
    {
        static (ImageRgba Image, string Path) Load(string directory, JsonElement packet)
        {
            var manifest = J.Get(packet, "manifest");
            var descriptor = J.Get(manifest, "now");
            if (descriptor.ValueKind != JsonValueKind.Object) descriptor = J.Get(manifest, "after");
            string diffRoot = Path.Combine(directory, "diff");
            var image = LiveSessionReader.LoadSnapshot(diffRoot, descriptor, checked((int)Number(descriptor, "width")),
                checked((int)Number(descriptor, "height")), CancellationToken.None)
                ?? throw new InvalidDataException("The original cached PNG snapshot was absent.");
            string pngPath = Path.Combine(diffRoot, "snapshots", (J.Text(descriptor, "id") ?? "") + ".png");
            return (image, pngPath);
        }
        var old = Load(oldDirectory, oldPacket); var next = Load(nextDirectory, nextPacket);
        if (old.Image.Width != next.Image.Width || old.Image.Height != next.Image.Height)
            return new { Status = "Incomparable", OldWidth = old.Image.Width, OldHeight = old.Image.Height,
                NextWidth = next.Image.Width, NextHeight = next.Image.Height };
        long rgbaChanged = 0, rgbChanged = 0, alphaChanged = 0, hiddenRgbOnly = 0, visibleContributionChanged = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var samples = new List<object>();
        var a = old.Image.Bytes; var b = next.Image.Bytes;
        for (int n = 0; n < a.Length; n += 4)
        {
            bool rgb = a[n] != b[n] || a[n + 1] != b[n + 1] || a[n + 2] != b[n + 2];
            bool alpha = a[n + 3] != b[n + 3];
            if (!rgb && !alpha) continue;
            rgbaChanged++; if (rgb) rgbChanged++; if (alpha) alphaChanged++;
            bool hidden = a[n + 3] == 0 && b[n + 3] == 0;
            if (hidden && rgb) hiddenRgbOnly++;
            bool contribution = alpha || Enumerable.Range(0, 3).Any(c => a[n + c] * a[n + 3] != b[n + c] * b[n + 3]);
            if (contribution) visibleContributionChanged++;
            int x = n / 4 % old.Image.Width, y = n / 4 / old.Image.Width;
            if (contribution) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
            if (samples.Count < 16) samples.Add(new { X = x, Y = y, Before = a.AsSpan(n, 4).ToArray(),
                After = b.AsSpan(n, 4).ToArray(), HiddenRgbOnly = hidden, VisibleContributionChanged = contribution });
        }
        return new
        {
            Status = "Compared", OldPng = old.Path, NextPng = next.Path,
            OldPngHash = Hash(old.Path), NextPngHash = Hash(next.Path), old.Image.Width, old.Image.Height,
            RgbaChangedPixels = rgbaChanged, RgbChangedPixels = rgbChanged, AlphaChangedPixels = alphaChanged,
            HiddenRgbOnlyPixels = hiddenRgbOnly, VisibleAlphaOrColorContributionChangedPixels = visibleContributionChanged,
            VisibleDifferenceBounds = maxX < 0 ? null : (object)new { Left = minX, Top = minY, Right = maxX + 1, Bottom = maxY + 1 },
            Samples = samples,
            Decoder = "Production LiveSessionReader RGBA PNG decoding; read-only Format32bppArgb LockBits, no DrawImage or compositing.",
            Interpretation = "Both alpha=0 with different RGB is hidden pixel storage only. Nonzero alpha/color contribution or alpha changes are actual raster differences. These two snapshots cannot determine when or which operation caused a difference."
        };
    }

    private static object Position(JsonElement frame) => new
    {
        X = Safe(J.Get(frame, "x")), Y = Safe(J.Get(frame, "y")),
        ScreenX = Safe(J.Get(frame, "screenX")), ScreenY = Safe(J.Get(frame, "screenY")),
        Position = Safe(J.Get(frame, "position")), Location = Safe(J.Get(frame, "location")),
        Pressure = Safe(J.Get(frame, "pressure"))
    };

    private static IEnumerable<string> CommandNames(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in data.EnumerateObject())
        {
            if ((property.Name is "command" or "commandId" or "resolvedCommand") && property.Value.ValueKind == JsonValueKind.String)
                yield return property.Value.GetString()!;
            if (property.Value.ValueKind == JsonValueKind.Object)
                foreach (string command in CommandNames(property.Value)) yield return command;
            else if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    foreach (string command in CommandNames(item)) yield return command;
        }
    }

    private static object? Safe(JsonElement value) => value.ValueKind is JsonValueKind.Undefined ? null : value.Clone();
    private static JsonElement[] Elements(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(e => e.Clone()).ToArray() : [];
    private static long Number(JsonElement value, string field, long fallback = 0) => J.Get(value, field) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetInt64(out long result) ? result : fallback;
    private static ulong Unsigned(JsonElement value, string field) => J.Get(value, field) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetUInt64(out ulong result) ? result : 0;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
