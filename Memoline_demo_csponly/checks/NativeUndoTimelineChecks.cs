using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using MemolineDemo.Editing;
using StrokeReplay;

namespace MemolineDemo;

internal static class NativeUndoTimelineChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string message)
        { count++; if (!value) throw new InvalidOperationException(message); }
        static NativeUndoPenState State(NativeUndoTimelineResult result, ulong begin) => result.Pens[begin].State;

        var global = new Fixture();
        ulong a = global.Pen("A"), b = global.Pen("B"); global.Command("undo");
        var result = global.Resolve();
        Check(State(result, a) == NativeUndoPenState.Active && State(result, b) == NativeUndoPenState.Undone,
            "Undo is a document stack: the newest B stroke is undone even when a caller selects A.");
        Check(result.Commands.Single().Target is { } target && target.BeginAppendId == b && target.Layer?.Uuid == "B",
            "The target preserves the original native stroke and layer, rather than a current-layer guess.");
        Check(result.RequiresBoundaryPixelValidation && !result.CapacityKnown,
            "An inferred target with unverified retention must be checked against immutable boundary pixels.");

        var eraser = new Fixture();
        ulong ink = eraser.Pen("A"), erase = eraser.Pen("B", replayEligible: false);
        eraser.Command("undo"); eraser.Command("undo"); result = eraser.Resolve();
        Check(State(result, ink) == NativeUndoPenState.Undone && State(result, erase) == NativeUndoPenState.Undone,
            "A known complete eraser undo unit participates even if it lacks a replay recipe.");
        Check(result.Commands[0].Target is { KnownUndoUnit: true, ReplayEligible: false } erased && erased.BeginAppendId == erase,
            "KnownUndoUnit is independent from ReplayEligible and identifies the eraser's own unit.");

        var opaque = new Fixture();
        ulong previous = opaque.Pen("A"), unknownPen = opaque.Pen("B", known: false);
        opaque.Command("undo"); result = opaque.Resolve();
        Check(result.Commands.Single().State == NativeUndoCommandState.Unknown &&
            result.Commands.Single().Target?.BeginAppendId == unknownPen,
            "An unknown pen tool occupies an opaque stack position instead of vanishing.");
        Check(State(result, previous) == NativeUndoPenState.Unknown && State(result, previous) != NativeUndoPenState.Undone,
            "Undo against an opaque unit never incorrectly pops the preceding eligible stroke.");

        var ignored = new Fixture();
        ulong drawing = ignored.Pen("A");
        ulong panel = ignored.Pen("B", region: "brushProperties");
        ulong navigation = ignored.Pen("B", heldKeys: [32]); ignored.Command("undo"); result = ignored.Resolve();
        Check(!result.Pens[panel].ParticipatesInHistory && !result.Pens[navigation].ParticipatesInHistory,
            "Confirmed panel and navigation pen contacts do not occupy canvas undo units.");
        Check(result.Commands.Single().Target?.BeginAppendId == drawing && State(result, drawing) == NativeUndoPenState.Undone,
            "Undo still targets the preceding drawing after non-document contacts.");

        var delayed = new Fixture();
        ulong first = delayed.Pen("A"); var delayedInput = delayed.Key();
        ulong later = delayed.Pen("B"); delayed.ResolveShortcut(delayedInput, ["undo"]); result = delayed.Resolve();
        Check(State(result, first) == NativeUndoPenState.Undone && State(result, later) == NativeUndoPenState.Active,
            "A delayed shortcut resolution is applied at its referenced input, before the later pen.");
        Check(result.Commands.Single().InputAppendId < later && result.Commands.Single().ResolutionAppendId > later,
            "Both input cause and delayed resolution proof remain available in command metadata.");

        var redo = new Fixture(); ulong redoPen = redo.Pen("A"); redo.Command("undo"); redo.Command("redo"); result = redo.Resolve();
        Check(State(result, redoPen) == NativeUndoPenState.Active && result.Commands.All(command => command.Target?.BeginAppendId == redoPen),
            "Redo restores the exact unit that Undo removed.");
        var branch = new Fixture(); ulong discarded = branch.Pen("A"); branch.Command("undo"); ulong fresh = branch.Pen("B");
        branch.Command("redo"); result = branch.Resolve();
        Check(State(result, discarded) == NativeUndoPenState.Undone && State(result, fresh) == NativeUndoPenState.Active &&
            result.Commands[^1].State == NativeUndoCommandState.NoRecordedTarget,
            "New drawing clears redo without reviving discarded content.");

        var mutation = new Fixture(); ulong old = mutation.Pen("A"); mutation.Command("layermergeall");
        ulong suffix = mutation.Pen("B"); mutation.Command("undo"); result = mutation.Resolve();
        Check(State(result, old) == NativeUndoPenState.Unknown && State(result, suffix) == NativeUndoPenState.Undone,
            "An unknown document mutation invalidates the old stack but permits a new confirmed suffix.");
        Check(result.Commands[^1].State == NativeUndoCommandState.Resolved && result.Commands[^1].Target?.BeginAppendId == suffix,
            "A later Undo cannot be reassigned to a stroke before the unknown interval.");

        var ambiguous = new Fixture(); ulong ambiguousPen = ambiguous.Pen("A");
        var ambiguousKey = ambiguous.Key(); ambiguous.ResolveShortcut(ambiguousKey, ["undo", "toolselectpen"]);
        result = ambiguous.Resolve();
        Check(State(result, ambiguousPen) == NativeUndoPenState.Unknown && result.Commands.Single().Reason == "shortcutMappingAmbiguous",
            "Multiple shortcut mappings are not treated as proof of an executed Undo.");
        var orphan = new Fixture(); ulong orphanPen = orphan.Pen("A");
        orphan.Add("shortcutResolved", new { matches = new[] { new { command = "undo" } } }); result = orphan.Resolve();
        Check(State(result, orphanPen) == NativeUndoPenState.Unknown && result.Commands.Single().InputAppendId == 0,
            "A shortcut without a native input reference creates an explicit barrier.");

        foreach ((long Hook, bool Guard) blocked in new[] { (1L, false), (0L, true) })
        {
            var blockedInput = new Fixture(); ulong blockedPen = blockedInput.Pen("A");
            blockedInput.Command("undo", hookResult: blocked.Hook, guard: blocked.Guard); result = blockedInput.Resolve();
            Check(State(result, blockedPen) == NativeUndoPenState.Active && result.Commands.Single().State == NativeUndoCommandState.Blocked,
                "A blocked or intercepted hook input is not applied to the document undo stack.");
        }
        var missingDelivery = new Fixture(); missingDelivery.Pen("A"); missingDelivery.Command("undo", hookResult: null); result = missingDelivery.Resolve();
        Check(result.Commands.Single().Reason == "nativeHookDeliveryUnknown", "Absent delivery metadata is not invented as a CSP acknowledgment.");
        var injected = new Fixture(); ulong injectedPen = injected.Pen("A"); injected.Command("undo", injected: true); result = injected.Resolve();
        Check(State(result, injectedPen) == NativeUndoPenState.Undone,
            "Driver-injected keys with no RecorderInputTag remain real native undo candidates.");
        var tagged = new Fixture(); ulong taggedPen = tagged.Pen("A"); tagged.Command("undo", tagged: true); result = tagged.Resolve();
        Check(State(result, taggedPen) == NativeUndoPenState.Active && result.Commands.Count == 0,
            "The Recorder's save/replay input tag is excluded even if a legacy stream contains it.");

        var midContact = new Fixture(); ulong beforeContact = midContact.Pen("A");
        var open = midContact.Begin("B"); midContact.Command("undo"); midContact.End(open); result = midContact.Resolve();
        Check(State(result, beforeContact) == NativeUndoPenState.Unknown && result.Commands.Single().Reason == "undoRedoDuringCanvasContact",
            "Undo during an unfinished drawing is rejected rather than popping a previous complete stroke.");
        Check(State(result, open.Begin) == NativeUndoPenState.Unknown,
            "Completing a contact that crossed an unknown command does not restore its known-step status.");
        var incomplete = new Fixture(); var unclosed = incomplete.Begin("A"); result = incomplete.Resolve();
        Check(State(result, unclosed.Begin) == NativeUndoPenState.Unknown && result.Barriers.Any(barrier => barrier.Reason == "canvasContactNotCompletedAtBoundary"),
            "A boundary cannot silently promote an unfinished contact to a known undo unit.");
        var unknownLayout = new Fixture(); ulong layoutPen = unknownLayout.Pen("A", status: "layoutUnavailable", region: "other");
        unknownLayout.Command("undo"); result = unknownLayout.Resolve();
        Check(!result.Pens[layoutPen].KnownUndoUnit && result.Commands.Single().State == NativeUndoCommandState.Unknown,
            "A caller proof cannot override the native contact's unconfirmed canvas location.");

        var capacity = new Fixture(); ulong retainedPixels = capacity.Pen("A"), retainedUndo = capacity.Pen("B");
        capacity.Command("undo"); capacity.Command("undo"); result = capacity.Resolve(capacity: 1);
        Check(State(result, retainedPixels) == NativeUndoPenState.Active && State(result, retainedUndo) == NativeUndoPenState.Undone &&
            result.Commands[^1].State == NativeUndoCommandState.NoRecordedTarget,
            "A verified one-unit capacity cannot Undo an older retained drawing after its history entry was discarded.");
        var priorHistory = new Fixture(); priorHistory.Command("undo"); result = priorHistory.Resolve();
        Check(result.Commands.Single().Reason == "undoRedoReachesUnrecordedHistory" && result.Commands.Single().Target is null,
            "A session boundary does not imply that CSP's earlier document undo stack was empty.");
        result = priorHistory.Resolve(initialEmpty: true);
        Check(result.Commands.Single().State == NativeUndoCommandState.NoRecordedTarget,
            "Only an explicit verified empty initial history permits a harmless empty Undo.");

        var mouse = new Fixture(); ulong mousePrior = mouse.Pen("A"); mouse.Add("mouseDown", new { x = 20, y = 20, button = "left" }, hardware: true);
        mouse.Command("undo"); result = mouse.Resolve();
        Check(State(result, mousePrior) == NativeUndoPenState.Unknown && result.Barriers.Any(barrier => barrier.Reason == "mouseDocumentHistoryUnknown"),
            "Mouse drawing and unclassified document clicks do not disappear from stack causality.");
        var unresolved = new Fixture(); ulong unknownInputPen = unresolved.Pen("A"); unresolved.Key(); result = unresolved.Resolve();
        Check(State(result, unknownInputPen) == NativeUndoPenState.Unknown,
            "A raw key without shortcut resolution cannot be guessed as a harmless or Undo command.");
        var safeTool = new Fixture(); ulong safePen = safeTool.Pen("A"); safeTool.Command("toolselectpen"); safeTool.Command("undo"); result = safeTool.Resolve();
        Check(State(result, safePen) == NativeUndoPenState.Undone && result.Barriers.Count == 0,
            "A confirmed tool-selection shortcut changes future settings without creating a document undo unit.");

        var safeChoices = new Fixture(); ulong choicePen = safeChoices.Pen("A");
        safeChoices.Command("toolselectpen"); var choicesKey = safeChoices.Key();
        safeChoices.ResolveShortcut(choicesKey, ["toolselectpen", "subtoolselectpencil"]);
        safeChoices.Command("undo"); result = safeChoices.Resolve();
        Check(State(result, choicePen) == NativeUndoPenState.Undone && result.Barriers.Count == 0,
            "Several safe tool choices do not become an unknown document-history operation.");

        var navigationKey = new Fixture(); ulong navInk = navigationKey.Pen("A");
        navigationKey.UnmappedKey(32, [32]); navigationKey.Pen("A", heldKeys: [32]);
        navigationKey.Command("undo"); result = navigationKey.Resolve();
        Check(State(result, navInk) == NativeUndoPenState.Undone && result.Barriers.Count == 0,
            "An explicitly unmapped Space key with a complete held-Space navigation contact preserves the canvas undo stack.");
        var canvasSpace = new Fixture(); canvasSpace.CanvasContext(); ulong canvasInk = canvasSpace.Pen("A");
        canvasSpace.UnmappedKey(32, [32]); canvasSpace.Command("undo"); result = canvasSpace.Resolve();
        Check(State(result, canvasInk) == NativeUndoPenState.Undone && result.Barriers.Count == 0,
            "A confirmed canvas-view context classifies an unmapped bare Space press even without a subsequent pan contact.");
        var unknownSpace = new Fixture(); ulong unknownSpaceInk = unknownSpace.Pen("A");
        unknownSpace.UnmappedKey(32, [32]); result = unknownSpace.Resolve();
        Check(State(result, unknownSpaceInk) == NativeUndoPenState.Unknown,
            "Space without a confirmed view or navigation contact is not presumed harmless.");
        var futureView = new Fixture(); futureView.CanvasContext(capturedTicks: 1000); ulong futureInk = futureView.Pen("A");
        futureView.UnmappedKey(32, [32]); result = futureView.Resolve();
        Check(State(result, futureInk) == NativeUndoPenState.Unknown,
            "A future screenshot does not retroactively classify an earlier Space key.");
        var rotateKey = new Fixture(); ulong rotateInk = rotateKey.Pen("A");
        rotateKey.UnmappedKey(82, [82]); rotateKey.Pen("A", heldKeys: [82]); rotateKey.Command("undo"); result = rotateKey.Resolve();
        Check(State(result, rotateInk) == NativeUndoPenState.Undone && result.Barriers.Count == 0,
            "A complete held-R canvas navigation contact proves temporary rotation instead of a drawing unit.");
        var unknownRotate = new Fixture(); unknownRotate.CanvasContext(); ulong unknownRotateInk = unknownRotate.Pen("A");
        unknownRotate.UnmappedKey(82, [82]); result = unknownRotate.Resolve();
        Check(State(result, unknownRotateInk) == NativeUndoPenState.Unknown,
            "A bare unmapped R requires actual held-R navigation evidence.");
        var releasedRotate = new Fixture(); ulong releasedInk = releasedRotate.Pen("A");
        releasedRotate.UnmappedKey(82, [82]); releasedRotate.Add("keyboardStateChanged", new { heldKeys = Array.Empty<int>() });
        releasedRotate.Pen("A", heldKeys: [82]); result = releasedRotate.Resolve();
        Check(State(result, releasedInk) == NativeUndoPenState.Unknown,
            "A navigation contact after the key's release cannot classify its previous held interval.");
        var spaceMapping = new Fixture(); ulong mappedSpaceInk = spaceMapping.Pen("A");
        var spaceUndo = spaceMapping.RawKey(32, [32]); spaceMapping.ResolveShortcut(spaceUndo, ["undo"]); result = spaceMapping.Resolve();
        Check(State(result, mappedSpaceInk) == NativeUndoPenState.Undone && result.Commands.Single().Command == "undo",
            "A real document mapping on Space takes precedence over default navigation.");
        var ctrlSpace = new Fixture(); ctrlSpace.CanvasContext(); ulong ctrlSpaceInk = ctrlSpace.Pen("A");
        ctrlSpace.UnmappedKey(32, [32, 162], ["Ctrl"]); result = ctrlSpace.Resolve();
        Check(State(result, ctrlSpaceInk) == NativeUndoPenState.Unknown,
            "The bare-Space navigation rule does not exempt an unknown modified Space combination.");

        var panelKey = new Fixture(); ulong panelInk = panelKey.Pen("A"); panelKey.Command("undo");
        panelKey.UnmappedKey(77, [77, 164], ["Alt"]); panelKey.Add("editBoundaryRequested", new { requestId = "edit-panel-fixture" });
        result = panelKey.Resolve();
        Check(State(result, panelInk) == NativeUndoPenState.Undone && result.Barriers.Count == 0 && result.Commands.Count == 1,
            "The host's unmapped Alt+M followed by its editing boundary preserves already undone native contacts.");
        var unprovedPanel = new Fixture(); ulong unprovedInk = unprovedPanel.Pen("A");
        unprovedPanel.UnmappedKey(77, [77, 164], ["Alt"]); result = unprovedPanel.Resolve();
        Check(State(result, unprovedInk) == NativeUndoPenState.Unknown,
            "Alt+M without host evidence remains an unknown input rather than a blanket key exemption.");
        var interveningPanel = new Fixture(); ulong interveningInk = interveningPanel.Pen("A");
        interveningPanel.UnmappedKey(77, [77, 164], ["Alt"]); interveningPanel.Pen("B");
        interveningPanel.Add("editBoundaryRequested", new { requestId = "edit-unrelated" }); result = interveningPanel.Resolve();
        Check(State(result, interveningInk) == NativeUndoPenState.Unknown,
            "A later editing boundary after another document input cannot acknowledge the earlier Alt+M.");
        var panelMapping = new Fixture(); ulong mappedPanelInk = panelMapping.Pen("A");
        var panelUndo = panelMapping.RawKey(77, [77, 164], ["Alt"]); panelMapping.ResolveShortcut(panelUndo, ["undo"]);
        panelMapping.Add("editBoundaryRequested", new { requestId = "edit-remapped" }); result = panelMapping.Resolve();
        Check(State(result, mappedPanelInk) == NativeUndoPenState.Undone && result.Commands.Single().Command == "undo",
            "A configured Undo on Alt+M is not suppressed by a later host panel marker.");

        var wheel = new Fixture(); ulong wheelInk = wheel.Pen("A"); wheel.CanvasWheel(); wheel.Command("undo"); result = wheel.Resolve();
        Check(State(result, wheelInk) == NativeUndoPenState.Undone && result.Barriers.Count == 0,
            "A uniquely resolved global canvas-view wheel mapping preserves the document undo stack.");
        var contextWheel = new Fixture(); ulong contextWheelInk = contextWheel.Pen("A");
        contextWheel.CanvasWheel(contextDependent: true); result = contextWheel.Resolve();
        Check(State(result, contextWheelInk) == NativeUndoPenState.Unknown,
            "A context-dependent wheel operation has no blanket navigation exemption.");
        var documentWheel = new Fixture(); ulong documentWheelInk = documentWheel.Pen("A");
        documentWheel.CanvasWheel(targets: ["画布视口", "图层"]); result = documentWheel.Resolve();
        Check(State(result, documentWheelInk) == NativeUndoPenState.Unknown,
            "A wheel mapping routed to document/layer state cannot be treated as pure canvas navigation.");
        var unmappedWheel = new Fixture(); ulong unmappedWheelInk = unmappedWheel.Pen("A");
        unmappedWheel.Add("mouseWheel", new { x = 20, y = 20, axis = "vertical", delta = 120, heldKeys = Array.Empty<int>(),
            heldButtons = Array.Empty<string>(), penDownLocation = new PenDownLocation("canvasViewport", "canvas", 20, 20, "windowsCursor", "matched") }, hardware: true);
        result = unmappedWheel.Resolve();
        Check(State(result, unmappedWheelInk) == NativeUndoPenState.Unknown,
            "Canvas coordinates alone do not prove an unclassified wheel operation's history effect.");

        var immutable = new Fixture(); immutable.Pen("A"); immutable.Command("undo");
        string before = string.Join("\n", immutable.Frames.Select(frame => frame.GetRawText())); immutable.Resolve();
        Check(before == string.Join("\n", immutable.Frames.Select(frame => frame.GetRawText())), "Timeline derivation never edits the original native frames.");
        var fixedPrefix = immutable.Snapshot(); ulong boundary = fixedPrefix.BoundaryAppendId;
        immutable.Command("redo");
        result = NativeUndoTimeline.Build(fixedPrefix with { Frames = immutable.Frames }, immutable.Proofs);
        Check(result.Commands.Count == 1 && result.Commands[0].Command == "undo" && result.BoundaryAppendId == boundary,
            "Inputs and late results beyond the immutable editing boundary do not enter this timeline.");
        Console.WriteLine($"Native undo timeline: {count} checks passed.");
        return count;
    }

    private sealed class Fixture
    {
        internal readonly List<JsonElement> Frames = [];
        internal readonly List<NativeUndoPenProof> Proofs = [];
        private ulong _append, _event;
        internal JsonElement Add(string kind, object data, bool hardware = false, ulong? operation = null, ulong[]? refs = null)
        {
            var frame = JsonSerializer.SerializeToElement(new { sessionId = "undo-fixture", appendId = ++_append,
                eventId = hardware ? ++_event : 0, ticks = (long)_append, kind, path = hardware ? "hardware" : "state",
                operationId = operation, relatedEventIds = refs ?? [], data });
            Frames.Add(frame); return frame;
        }
        internal (ulong Begin, ulong Operation) Begin(string layer, bool known = true, bool replayEligible = true,
            string region = "canvasViewport", string status = "matched", int[]? heldKeys = null)
        {
            var frame = Add("penBegin", new { inCsp = true, injected = false, heldKeys = heldKeys ?? [],
                penDownLocation = new PenDownLocation(region, region, 20, 20, "windowsCursor", status) }, hardware: true);
            ulong begin = frame.GetProperty("appendId").GetUInt64(), operation = frame.GetProperty("eventId").GetUInt64();
            Proofs.Add(new(begin, operation, new("undo-fixture", layer == "A" ? 1 : 2, layer), known, replayEligible));
            return (begin, operation);
        }
        internal void End((ulong Begin, ulong Operation) contact)
            => Add("penEnd", new { inCsp = true, injected = false, reason = "penUp" }, hardware: true, operation: contact.Operation);
        internal ulong Pen(string layer, bool known = true, bool replayEligible = true, string region = "canvasViewport",
            string status = "matched", int[]? heldKeys = null)
        {
            var contact = Begin(layer, known, replayEligible, region, status, heldKeys); End(contact); return contact.Begin;
        }
        internal JsonElement Key(long? hookResult = 0, bool guard = false, bool injected = false, bool tagged = false)
        {
            var data = new Dictionary<string, object?> { ["vk"] = 90, ["heldKeys"] = new[] { 17, 90 },
                ["guardIntercepted"] = guard, ["injected"] = injected, ["extraInfo"] = tagged ? "0x4D4C5243" : "0x0" };
            if (hookResult is not null) data.Add("nextHookResult", hookResult);
            return Add("keyInput", data, hardware: true);
        }
        internal void ResolveShortcut(JsonElement key, string[] commands)
            => Add("shortcutResolved", new { resolution = "matched", matches = commands.Select(command => new { command }).ToArray() },
                refs: [key.GetProperty("eventId").GetUInt64()]);
        internal JsonElement RawKey(int vk, int[] heldKeys, string[]? modifiers = null)
            => Add("keyInput", new { vk, heldKeys, modifiers = modifiers ?? [], inCsp = true,
                guardIntercepted = false, nextHookResult = 0, injected = false, extraInfo = "0x0" }, hardware: true);
        internal void UnmappedKey(int vk, int[] heldKeys, string[]? modifiers = null)
        {
            var key = RawKey(vk, heldKeys, modifiers);
            Add("shortcutResolved", new { resolution = "unmapped", matches = Array.Empty<object>(), shortcutMatchPending = false },
                refs: [key.GetProperty("eventId").GetUInt64()]);
        }
        internal void CanvasContext(long capturedTicks = 0)
            => Add("coreStateUpdated", new { module = "canvasViewState", status = "changed",
                state = new { canvasOriginScreenPx = new { x = 0, y = 0 }, ocrScalePercent = 100, ocrRotationDegrees = 0 },
                evidence = new { observedAfterEventId = 0, causalAmbiguous = false, capturedTicks } });
        internal void CanvasWheel(bool contextDependent = false, string[]? targets = null)
        {
            var input = Add("mouseWheel", new { x = 20, y = 20, axis = "vertical", delta = 120, heldKeys = Array.Empty<int>(),
                heldButtons = Array.Empty<string>(), penDownLocation = new PenDownLocation("canvasViewport", "canvas", 20, 20, "windowsCursor", "matched") }, hardware: true);
            Add("shortcutResolved", new { resolution = "matched", inputType = "mouseWheel", shortcutMatchPending = false,
                matches = new[] { new { outputType = 4, operationCode = 1, mask = 4096, axis = "vertical", contextDependent,
                    context = new[] { 0, 0, 0 }, targets = targets ?? ["画布视口", "导航器"] } } },
                refs: [input.GetProperty("eventId").GetUInt64()]);
        }
        internal void Command(string command, long? hookResult = 0, bool guard = false, bool injected = false, bool tagged = false)
            => ResolveShortcut(Key(hookResult, guard, injected, tagged), [command]);
        internal SessionSnapshot Snapshot() => new("fixture.part", "undo-fixture", 1000, Frames.ToArray(),
            new MemolineReplayDocument("fixture.part", "undo-fixture", 1000, []), [], _append, (long)_append);
        internal NativeUndoTimelineResult Resolve(int? capacity = null, bool initialEmpty = false)
            => NativeUndoTimeline.Build(Snapshot(), Proofs, capacity, initialEmpty);
    }
}
