using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using StrokeReplay;

internal static class ParserChecks
{
    internal static void Run()
    {
        DelayedPackagesUseCausalAnchors();
        DeferredReservationsKeepOriginalOrder();
        TabletOnlyAndMappedPressure();
        InterruptedStrokeIsLifted();
        UnknownCanvasInvalidatesEarlierView();
        InvalidCanvasOperationsAreSkippedUntilConfirmed();
        PendingCanvasIsBlockedButOtherCoresAreIgnored();
        NavigationAndUiPenClicksAreSkipped();
        KeyboardNavigationStateExcludesSparsePenOperations();
        RawPressureNeedsRecordedDeviceLimit();
        MappedPressureUsesHardwareUnits();
        InvalidAndIncompletePenDataIsRejected();
        SharedContainerReaderHandlesCompressedSessionAsync().GetAwaiter().GetResult();
        Console.WriteLine("Memoline parser checks passed.");
    }

    private static void DelayedPackagesUseCausalAnchors()
    {
        var records = Initial();
        records.Add(Hardware("keyInput", 3, 1, 10, new { vk = 0x20 }));
        records.Add(Reserved("next", 4, 1, 10));
        records.Add(Hardware("penBegin", 5, 2, 20, Pen(300, 400, .2)));
        records.Add(Hardware("penEnd", 6, 3, 30, new { x = 310, y = 410 }, 2));
        records.Add(Result("next", 7, 1, Update(75, 90, 150, 160), new { module = "brushState", status = "unknown" }));
        // Physically later initial result still belongs before the first hardware event.
        records.Add(Result("initial", 8, 0, Update(25, 0, 5, 6)));
        var stroke = Read(records).Strokes.Single();
        Check(stroke.View.ScalePercent == 75 && stroke.View.RotationDegrees == 90 && stroke.View.OriginX == 150,
            "Append order must not override the state reserved after hardware event 1.");
        Check(stroke.StartTicks == 20 && stroke.EndTicks == 30, "Replay timing must retain session ticks.");

        // A state reserved after this penBegin affects later operations, never its first point.
        records.Insert(3, Reserved("afterBegin", 9, 2, 20));
        records.Add(Result("afterBegin", 10, 2, Update(125, 0, 0, 0)));
        Check(Read(records).Strokes.Single().View.ScalePercent == 75, "State after a penBegin cannot predate that input.");
    }

    private static void DeferredReservationsKeepOriginalOrder()
    {
        var records = ValidInitial();
        records.Add(Hardware("keyInput", 3, 1, 10, new { vk = 0x20 }, deviceType: "keyboard"));
        records.Add(Frame("statePackageReserved", 4, 0, 10,
            new { packageId = "second", afterEventId = 1, occurredTicks = 10, reservationOrder = 3 }));
        records.Add(Result("second", 5, 1, Update(75, 0, 100, 200)));
        records.Add(Frame("statePackageReserved", 6, 0, 10,
            new { packageId = "first", afterEventId = 1, occurredTicks = 10, reservationOrder = 2 }));
        records.Add(Result("first", 7, 1, Update(50, 0, 50, 100)));
        records.Add(Hardware("penBegin", 8, 2, 20, Pen(300, 400, .3)));
        records.Add(Hardware("penEnd", 9, 3, 30, new { x = 310, y = 410 }, 2));
        Check(Read(records).Strokes.Single().View.ScalePercent == 75,
            "Deferred successful reservations must follow their original order, even when results append in reverse order.");
    }

    private static void TabletOnlyAndMappedPressure()
    {
        var records = ValidInitial();
        records.Add(Hardware("mouseDown", 4, 1, 10, new { x = 700, y = 700 }, deviceType: "mouse"));
        records.Add(Hardware("mouseUp", 5, 2, 15, new { x = 800, y = 800 }, 1, "mouse"));
        records.Add(Hardware("keyInput", 6, 3, 16, new { vk = 0x43 }, deviceType: "keyboard"));
        records.Add(Hardware("penBegin", 7, 4, 20, new
        {
            x = 300, y = 400, screenX = 900, screenY = 950, tabletX = 5000, tabletY = 6000,
            pressure = 1500, mappedPressure = 500, normalizedPressure = .25, maxPressure = 2000, tiltX = -10, tiltY = 12,
            contactState = "Contact", heldKeys = Array.Empty<int>()
        }));
        records.Add(Hardware("penEnd", 8, 5, 30, new { x = 310, y = 410 }, 4));
        var document = Read(records);
        var sample = document.Strokes.Single().Samples[0];
        Check(document.Strokes.Count == 1 && sample.X == 300 && sample.Y == 400,
            "Replay must use only tablet operations and recorded Windows interaction pixels.");
        Check(sample.Pressure == .25 && sample.TiltX == -10 && sample.TiltY == 12,
            "Recorded normalized mapped pressure must be used without a second pressure curve.");
        Check(!document.Strokes[0].Samples[^1].InContact && document.Strokes[0].Samples[^1].Pressure == 0,
            "Pen end must be pressure-free.");
    }

    private static void InterruptedStrokeIsLifted()
    {
        var records = ValidInitial();
        records.Add(Hardware("penBegin", 4, 1, 10, Pen(100, 110, .3)));
        records.Add(Hardware("penSample", 5, 2, 20, Pen(120, 130, .4), 1));
        records.Add(Frame("penInterrupted", 6, 0, 25, new { reason = "cursorLeftCsp" }, refs: [1, 2]));
        var stroke = Read(records).Strokes.Single();
        Check(stroke.EndTicks == 25 && stroke.Samples.Count == 3, "Interrupted input must end at the recorded interruption time.");
        var last = stroke.Samples[^1];
        Check(!last.InContact && last.X == 120 && last.Y == 130 && last.Pressure == 0,
            "Interruption must release at the last measured position.");
    }

    private static void UnknownCanvasInvalidatesEarlierView()
    {
        var records = ValidInitial();
        records.Add(Hardware("keyInput", 4, 1, 10, new { vk = 0x5A }));
        records.Add(Reserved("bad", 5, 1, 10));
        records.Add(Result("bad", 6, 1, new { module = "canvasViewState", status = "unknown", state = (object?)null }));
        records.Add(Hardware("penBegin", 7, 2, 20, Pen(300, 400, .3)));
        records.Add(Hardware("penEnd", 8, 3, 30, new { x = 310, y = 410 }, 2));
        Reject(() => Read(records), "An unknown canvas update must invalidate the previous confirmed view.");
    }

    private static void InvalidCanvasOperationsAreSkippedUntilConfirmed()
    {
        foreach (string status in new[] { "unknown", "ambiguous", "error", "changed" })
        {
            var records = ValidInitial();
            records.Add(Hardware("penBegin", 3, 1, 5, Pen(100, 100, .2)));
            records.Add(Hardware("penEnd", 4, 2, 8, new { x = 100, y = 100 }, 1));
            records.Add(Reserved("invalid", 5, 2, 8));
            records.Add(Result("invalid", 6, 2, new {
                module = "canvasViewState", status, state = State(50, 0, 100, 200),
                evidence = new { causalAmbiguous = status == "changed" }
            }));
            // Ignored operations need no usable drawing samples, and must not inherit the old view.
            records.Add(Hardware("penBegin", 7, 3, 10, new { }));
            records.Add(Hardware("penEnd", 8, 4, 12, new { }, 3));
            records.Add(Hardware("penBegin", 9, 5, 14, new { }));
            records.Add(Hardware("penEnd", 10, 6, 16, new { }, 5));
            records.Add(Reserved("recovered", 11, 6, 16));
            // Later append order still applies the recovered view at its causal anchor.
            records.Add(Hardware("penBegin", 12, 7, 20, Pen(300, 300, .4)));
            records.Add(Hardware("penEnd", 13, 8, 25, new { x = 300, y = 300 }, 7));
            records.Add(Result("recovered", 14, 6, Update(75, 30, 200, 300)));
            var document = Read(records);
            Check(document.SkippedInvalidViewOperations == 2 &&
                  document.Strokes.Select(s => s.OperationId).SequenceEqual(new ulong[] { 1, 7 }),
                "Invalid views must skip whole operations until the next confirmed causal view.");
            Check(document.Strokes[1].View.ScalePercent == 75 && document.Strokes[1].StartTicks == 20,
                "Valid operations after recovery must retain the recovered view and recorded timing.");
        }
        var noInitialView = Initial();
        noInitialView.Add(Hardware("penBegin", 2, 1, 10, new { }));
        noInitialView.Add(Hardware("penEnd", 3, 2, 15, new { }, 1));
        noInitialView.Add(Reserved("ready", 4, 2, 15));
        noInitialView.Add(Result("ready", 5, 2, Update(100, 0, 10, 20)));
        noInitialView.Add(Hardware("penBegin", 6, 3, 20, Pen(300, 400, .3)));
        noInitialView.Add(Hardware("penEnd", 7, 4, 30, new { x = 310, y = 410 }, 3));
        var recovered = Read(noInitialView);
        Check(recovered.SkippedInvalidViewOperations == 1 && recovered.Strokes.Single().OperationId == 3,
            "An unresolved initial view must skip early operations and allow later confirmed ones.");
    }

    private static void NavigationAndUiPenClicksAreSkipped()
    {
        var records = ValidInitial();
        records.Add(Frame("coreStateUpdated", 4, 0, 0, new
        {
            packageId = "initial", module = "canvasViewState", status = "changed", state = State(100, 0, 10, 20),
            rawResult = new { success = true, canvasWindowRoiScreenPx = new { left = 100, top = 100, right = 1000, bottom = 1000 } }
        }));
        records.Add(Hardware("penBegin", 5, 1, 10, new { x = 200, y = 200, heldKeys = new[] { 0x20 } }));
        records.Add(Hardware("penEnd", 6, 2, 20, new { x = 220, y = 220 }, 1));
        records.Add(Hardware("penBegin", 7, 3, 30, new { x = 50, y = 150 }));
        records.Add(Hardware("penEnd", 8, 4, 40, new { x = 50, y = 150 }, 3));
        records.Add(Hardware("penBegin", 9, 5, 50, Pen(300, 400, .3)));
        records.Add(Hardware("penEnd", 10, 6, 60, new { x = 310, y = 410 }, 5));
        var document = Read(records);
        Check(document.Strokes.Count == 1 && document.SkippedNavigationOperations == 1 && document.SkippedOutsideCanvasOperations == 1,
            "Space+pen navigation and penBegin outside a confirmed canvas ROI must not become ink.");
    }

    private static void KeyboardNavigationStateExcludesSparsePenOperations()
    {
        var records = ValidInitial();
        records.Add(Frame("keyboardStateChanged", 3, 0, 1, new { action = "keyDown", heldKeys = new[] { 32 } }));
        records.Add(Hardware("penBegin", 4, 1, 2, new { x = 200, y = 200 }));
        records.Add(Hardware("penEnd", 5, 2, 3, new { x = 200, y = 200 }, 1));
        records.Add(Frame("keyboardStateChanged", 6, 0, 4, new { action = "keyUp", heldKeys = Array.Empty<int>() }));
        records.Add(Hardware("penBegin", 7, 3, 5, new { x = 200, y = 200 }));
        records.Add(Frame("keyboardStateChanged", 8, 0, 6, new { action = "keyDown", heldKeys = new[] { 82 } }));
        records.Add(Frame("keyboardStateChanged", 9, 0, 7, new { action = "keyUp", heldKeys = Array.Empty<int>() }));
        records.Add(Hardware("penEnd", 10, 4, 8, new { x = 200, y = 200 }, 3));
        records.Add(Hardware("penBegin", 11, 5, 9, new { x = 200, y = 200, heldKeys = new[] { "R" } }));
        records.Add(Hardware("penEnd", 12, 6, 10, new { x = 200, y = 200, heldKeys = Array.Empty<string>() }, 5));
        records.Add(Hardware("penBegin", 13, 7, 11, new { x = 200, y = 200, normalizedPressure = .3 }));
        records.Add(Hardware("penEnd", 14, 8, 12, new { x = 200, y = 200 }, 7));
        var document = Read(records);
        Check(document.SkippedNavigationOperations == 3 && document.Strokes.Single().OperationId == 7,
            "Keyboard snapshots and mid-contact navigation keys must exclude whole operations without leaking after release.");
    }

    private static void PendingCanvasIsBlockedButOtherCoresAreIgnored()
    {
        var records = Initial();
        records.Add(Frame("coreStateUpdated", 2, 0, 0, new
        {
            packageId = "initial", module = "canvasViewState", status = "changed", state = State(100, 0, 10, 20),
            rawResult = new { success = true }
        }));
        records.Add(Hardware("penBegin", 4, 1, 10, Pen(200, 300, .3)));
        records.Add(Hardware("penEnd", 5, 2, 20, new { x = 210, y = 310 }, 1));
        Check(Read(records).Strokes.Count == 1,
            "A confirmed canvas core must remain usable while unrelated initial cores are unresolved.");
        records = ValidInitial();
        records.Add(Hardware("keyInput", 4, 1, 10, new { vk = 0x5A }));
        records.Add(Reserved("pending", 5, 1, 10));
        records.Add(Frame("panelUpdateRequested", 6, 0, 10, new { modules = new[] { "canvasViewState" } }, refs: [1]));
        records.Add(Hardware("penBegin", 7, 2, 20, Pen(200, 300, .3)));
        records.Add(Hardware("penEnd", 8, 3, 30, new { x = 210, y = 310 }, 2));
        records.Add(Reserved("ready", 9, 3, 20));
        records.Add(Result("ready", 10, 3, Update(75, 0, 100, 200)));
        records.Add(Hardware("penBegin", 11, 4, 30, Pen(300, 400, .3)));
        records.Add(Hardware("penEnd", 12, 5, 40, new { x = 310, y = 410 }, 4));
        var document = Read(records);
        Check(document.SkippedInvalidViewOperations == 1 && document.Strokes.Single().OperationId == 4,
            "A pending requested canvas update must skip operations until a confirmed view arrives.");
    }

    private static void RawPressureNeedsRecordedDeviceLimit()
    {
        var records = ValidInitial();
        records.Add(Frame("tabletDeviceChanged", 4, 0, 0, new { deviceId = "test-pen", maxPressure = 2000 }));
        records.Add(Hardware("penBegin", 5, 1, 10, new { screenX = 200, screenY = 300, pressure = 500 }));
        records.Add(Hardware("penEnd", 6, 2, 20, new { screenX = 210, screenY = 310 }, 1));
        Check(Read(records).Strokes.Single().Samples[0].Pressure == .25,
            "Raw pressure must use the preceding recorded device maximum.");
        records.RemoveAt(3);
        Reject(() => Read(records), "Raw pressure without a device maximum must not guess an upper bound.");
    }

    private static void MappedPressureUsesHardwareUnits()
    {
        var records = ValidInitial();
        records.Add(Frame("tabletDeviceChanged", 3, 0, 0, new { deviceId = "test-pen", maxPressure = 8191 }));
        records.Add(Hardware("penBegin", 4, 1, 10, new { x = 200, y = 300, pressure = 1000, mappedPressure = 4095.5 }));
        records.Add(Hardware("penEnd", 5, 2, 20, new { x = 210, y = 310 }, 1));
        Check(Read(records).Strokes.Single().Samples[0].Pressure == .5,
            "Mapped hardware-range pressure must be divided by the recorded device maximum.");
        records[4] = Hardware("penBegin", 4, 1, 10, new {
            x = 200, y = 300, pressure = 1000, mappedPressure = .5, maxPressure = 2000
        });
        Check(Read(records).Strokes.Single().Samples[0].Pressure == .00025,
            "Even sub-unit mapped pressure uses hardware units and must not be guessed as normalized.");
        records.RemoveAt(3);
        records[3] = Hardware("penBegin", 4, 1, 10, new { x = 200, y = 300, mappedPressure = 8191, maxPressure = 8191 });
        Check(Read(records).Strokes.Single().Samples[0].Pressure == 1,
            "Mapped pressure can be normalized from sample metadata without a raw pressure field.");
        records[3] = Hardware("penBegin", 4, 1, 10, new { x = 200, y = 300, mappedPressure = 4095 });
        Reject(() => Read(records), "Hardware-range mapped pressure without a recorded maximum must not guess its units.");
    }

    private static void InvalidAndIncompletePenDataIsRejected()
    {
        var records = ValidInitial();
        records.Add(Hardware("penBegin", 4, 1, 10, new { x = 200, y = 300, pressure = (double?)null, source = "windowsPenCompatibility" }));
        records.Add(Hardware("penEnd", 5, 2, 20, new { x = 210, y = 310 }, 1));
        Reject(() => Read(records), "Passive pen must not receive invented pressure.");
        records[3] = Hardware("penBegin", 4, 1, 10, Pen(200, 300, 1.1));
        Reject(() => Read(records), "Out-of-range normalized pressure must be rejected.");
        records[3] = Hardware("penBegin", 4, 1, 10, new { tabletX = 1000, tabletY = 2000, normalizedPressure = .5 });
        Reject(() => Read(records), "Tablet units must not be interpreted as Windows pixels.");
        records[3] = Hardware("penBegin", 4, 1, 10, Pen(200, 300, .5));
        records.RemoveAt(4);
        Reject(() => Read(records), "An active incomplete stroke cannot be silently truncated.");
        Reject(() => Read(ValidInitial()), "A file without drawing operations must report the absence clearly.");
    }

    private static async Task SharedContainerReaderHandlesCompressedSessionAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "replay-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path;
            await using (var writer = new MemolineWriter(directory, new { }))
            {
                path = writer.FilePath;
                string package = writer.ReserveStatePackage(0, 0, "initialState");
                writer.AppendPackageResult(package, new { status = "changed", updates = new[] { Update(100, 0, 10, 20) } });
                var source = new HardwareDeviceSource("pen", "OpenTabletDriver.HID", "test-pen", "deviceId");
                var begin = writer.AppendHardware("penBegin", Pen(300, 400, .35), source);
                writer.AppendHardware("penEnd", new { x = 310, y = 410 }, source, begin.EventId);
                await writer.FlushAsync();
                Check(MemolineReplayReader.Read(writer.LiveFilePath).Strokes.Count == 1,
                    "The shared reader must parse a complete operation before session close.");
            }
            var document = MemolineReplayReader.Read(path + ".part");
            Check(document.Strokes.Count == 1 && document.Frequency > 0,
                "Shared container parser must handle Brotli/CRC frames and .part rename alias.");
            byte[] bytes = File.ReadAllBytes(path);
            string prefix = Path.Combine(directory, "truncated.memoline.part");
            File.WriteAllBytes(prefix, bytes[..^10]);
            Check(MemolineReplayReader.Read(prefix).Strokes.Count == 1,
                "A partial footer must not hide earlier complete pen operations.");
            bytes[^1] ^= 0x10;
            string corrupt = Path.Combine(directory, "corrupt.memoline");
            File.WriteAllBytes(corrupt, bytes);
            Reject(() => MemolineReplayReader.Read(corrupt), "The shared reader must reject CRC-corrupt records.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static List<JsonElement> Initial() =>
    [
        JsonSerializer.SerializeToElement(new { kind = "header", sessionId = "test-session", frequency = 1000L }),
        Reserved("initial", 1, 0, 0)
    ];
    private static List<JsonElement> ValidInitial()
    {
        var records = Initial();
        records.Add(Result("initial", 2, 0, Update(100, 0, 10, 20)));
        return records;
    }
    private static MemolineReplayDocument Read(IEnumerable<JsonElement> records) => MemolineReplayReader.Parse("test.memoline", records);
    private static object State(double scale, double rotation, double x, double y) => new
    {
        ocrScalePercent = scale, ocrRotationDegrees = rotation, canvasOriginScreenPx = new { x, y },
        transform = new { canvasPixelWidth = 2000, canvasPixelHeight = 1500 }
    };
    private static object Update(double scale, double rotation, double x, double y) => new
    { module = "canvasViewState", status = "changed", state = State(scale, rotation, x, y), evidence = new { causalAmbiguous = false } };
    private static object Pen(double x, double y, double pressure) => new
    { x, y, normalizedPressure = pressure, contactState = "Contact", heldKeys = Array.Empty<int>() };
    private static JsonElement Reserved(string id, ulong appendId, ulong anchor, long ticks) =>
        Frame("statePackageReserved", appendId, 0, ticks, new { packageId = id, afterEventId = anchor, occurredTicks = ticks });
    private static JsonElement Result(string id, ulong appendId, ulong anchor, params object[] updates) =>
        Frame("statePackageResult", appendId, 0, 0,
            new { packageId = id, afterEventId = anchor, result = new { status = "changed", updates } });
    private static JsonElement Hardware(string kind, ulong appendId, ulong eventId, long ticks, object data,
        ulong? operationId = null, string deviceType = "pen") => Frame(kind, appendId, eventId, ticks, data,
            operationId ?? eventId, deviceType: deviceType, path: "hardware");
    private static JsonElement Frame(string kind, ulong appendId, ulong eventId, long ticks, object data,
        ulong? operationId = null, ulong[]? refs = null, string deviceType = "pen", string path = "state") =>
        JsonSerializer.SerializeToElement(new
        {
            kind, appendId, eventId, ticks, data, operationId, relatedEventIds = refs,
            path, deviceSource = new { deviceType, deviceId = "test-pen", captureApi = "test" }
        });
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }
}
