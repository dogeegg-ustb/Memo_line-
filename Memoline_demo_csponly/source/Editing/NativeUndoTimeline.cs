using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;
using PacketReplay;

namespace MemolineDemo.Editing;

internal enum NativeUndoPenState { Active, Undone, Unknown }
internal enum NativeUndoCommandState { Resolved, NoRecordedTarget, Blocked, Unknown }

// Undo-unit evidence and replay eligibility are different. A confirmed eraser
// stroke can occupy one undo unit even when its recipe cannot be replayed.
internal sealed record NativeUndoPenProof(ulong BeginAppendId, ulong OperationId, LayerKey? Layer,
    bool KnownUndoUnit, bool ReplayEligible, string? BrushName = null);
// A stroke target is its penBegin; any other unit's target starts at its first claimed input.
internal sealed record NativeUndoTarget(ulong BeginAppendId, ulong OperationId, LayerKey? Layer,
    bool KnownUndoUnit, bool ReplayEligible, NativeUndoUnitKind Kind = NativeUndoUnitKind.Stroke, string? Description = null);
internal sealed record NativeUndoPenEntry(ulong BeginAppendId, ulong BeginEventId, ulong OperationId,
    LayerKey? Layer, ulong? EndAppendId, bool ParticipatesInHistory, bool KnownUndoUnit, bool ReplayEligible,
    NativeUndoPenState State, ulong LastStateInputAppendId, ulong CausalPrefixStartAppendId, string? Reason);
internal sealed record NativeUndoCommandResolution(string Command, ulong InputAppendId, ulong ResolutionAppendId,
    NativeUndoCommandState State, NativeUndoTarget? Target, ulong CausalPrefixStartAppendId,
    ulong CausalPrefixEndAppendId, bool RequiresBoundaryPixelValidation, string? Reason);
internal sealed record NativeUndoBarrier(ulong InputAppendId, ulong ResolutionAppendId, string Reason,
    IReadOnlyList<ulong> AffectedPenBeginAppendIds);
internal sealed record NativeUndoUnitEntry(NativeUndoUnitProof Proof, NativeUndoPenState State,
    ulong LastStateInputAppendId, string? Reason);
/// <summary>One reading of CSP's History palette and whether it could be matched to the recorded units.</summary>
internal sealed record NativeUndoHistoryReading(ulong AppendId, ulong AppliedAtAppendId, bool Accepted, string Reason,
    int RowCount, int UndoneRows, IReadOnlyList<ulong> AlignedUnits);
internal sealed record NativeUndoTimelineResult(string SessionId, ulong BoundaryAppendId,
    IReadOnlyDictionary<ulong, NativeUndoPenEntry> Pens, IReadOnlyList<NativeUndoCommandResolution> Commands,
    IReadOnlyList<NativeUndoBarrier> Barriers, bool InitialHistoryKnownEmpty, int? VerifiedUndoCapacity,
    IReadOnlyList<NativeUndoUnitEntry> Units, IReadOnlyList<NativeUndoHistoryReading>? HistoryReadings = null)
{
    // Hook delivery and a saved shortcut mapping are not a CSP application ack.
    // The caller must compare the resulting effective history with its immutable
    // full-layer boundary capture before using any inferred undo/redo target.
    internal bool RequiresBoundaryPixelValidation => Commands.Any(command => command.State == NativeUndoCommandState.Resolved);
    internal bool CapacityKnown => VerifiedUndoCapacity is not null;
}

/// <summary>
/// Derives a single session's document-wide undo history without changing native
/// bytes. The stack holds undo units: pen strokes, and the layer property changes and
/// macro operations that <see cref="INativeUndoUnitDetector"/>s prove. An unknown
/// interval discards stack knowledge; a later confirmed unit can establish a new local
/// suffix. It never imports another session's stack.
/// <para>
/// After undo/redo the recorder reads CSP's History palette (module historyState): one row per
/// undo unit, the undone ones dimmed. A reading is matched to the recorded units from the newest
/// row back. Row texts are tool paths that OCR may spell in another script than the brush state,
/// so only their pattern is compared: two rows read alike exactly when their strokes used the same
/// brush. Accepted readings set the units' states and the stacks, and give each undo/redo of the
/// burst the unit it moved, even after an unknown command made the stack unknown.
/// </para>
/// </summary>
internal static class NativeUndoTimeline
{
    internal static NativeUndoTimelineResult Build(SessionSnapshot session,
        IEnumerable<NativeUndoPenProof> penProofs, int? verifiedUndoCapacity = null,
        bool initialHistoryKnownEmpty = false, IReadOnlyList<INativeUndoUnitDetector>? detectors = null)
        => new Resolver(session, penProofs, verifiedUndoCapacity, initialHistoryKnownEmpty,
            detectors ?? NativeUndoUnitDetectors.Default).Resolve();

    private abstract class Unit(NativeUndoPenState state, ulong lastInput, string? reason)
    {
        internal NativeUndoPenState State = state;
        internal ulong LastInput = lastInput;
        internal string? Reason = reason;
        // Interval between inputs that may have added History rows nobody recorded.
        internal int Epoch;
        internal abstract bool Known { get; }
        internal abstract NativeUndoTarget Target { get; }
        // What its History row is compared by; null matches any row.
        internal abstract string? HistoryKey { get; }
        internal abstract ulong Id { get; }
    }

    private sealed class Pen(ulong append, ulong eventId, ulong operation, NativeUndoPenProof? proof,
        bool participates, bool navigation, bool layerPanel, ulong prefix)
        : Unit(participates ? NativeUndoPenState.Unknown : NativeUndoPenState.Active, append,
            participates ? "contactNotCompleted" : navigation ? "navigationContact" : "confirmedPanelContact")
    {
        internal readonly ulong Begin = append, EventId = eventId, Operation = operation;
        internal readonly NativeUndoPenProof? Proof = proof;
        internal readonly bool Participates = participates, Navigation = navigation, LayerPanel = layerPanel;
        internal readonly ulong Prefix = prefix;
        internal ulong? End;
        internal bool Invalid;
        internal override bool Known => End is not null && !Invalid && Participates && Proof?.KnownUndoUnit == true;
        internal override NativeUndoTarget Target => new(Begin, Operation, Proof?.Layer, Known, Known && Proof?.ReplayEligible == true);
        internal override string? HistoryKey => Proof?.BrushName is { Length: > 0 } brush ? "stroke:" + brush.Trim() : null;
        internal override ulong Id => Begin;
        internal NativeUndoPenEntry Freeze() => new(Begin, EventId, Operation, Proof?.Layer, End,
            Participates, Known, Known && Proof?.ReplayEligible == true, State, LastInput, Prefix, Reason);
    }

    // Proven by a detector, so always a known unit; nothing about it is replayable.
    private sealed class DocumentUnit(NativeUndoUnitProof proof)
        : Unit(NativeUndoPenState.Active, proof.LastInputAppendId, null)
    {
        internal readonly NativeUndoUnitProof Proof = proof;
        internal override bool Known => true;
        internal override NativeUndoTarget Target => new(Proof.FirstInputAppendId, 0, Proof.Layer, true, false, Proof.Kind, Proof.Description);
        internal override string? HistoryKey => $"unit:{Proof.Kind}:{Proof.Description}";
        internal override ulong Id => Proof.FirstInputAppendId;
        internal NativeUndoUnitEntry Freeze() => new(Proof, State, LastInput, Reason);
    }

    private sealed record Shortcut(JsonElement Result, JsonElement[] Matches)
    {
        internal ulong Append => U64(Result, "appendId");
    }

    private sealed class Resolver
    {
        private readonly SessionSnapshot _session;
        private readonly JsonElement[] _frames;
        private readonly Dictionary<ulong, NativeUndoPenProof> _proofs;
        private readonly IReadOnlyList<HistorySlot> _history;
        private readonly Dictionary<ulong, List<Shortcut>> _shortcuts = [];
        private readonly Dictionary<ulong, Shortcut> _orphanShortcuts = [];
        private readonly Dictionary<ulong, Pen> _pens = [], _contacts = [];
        private readonly List<Unit> _undo = [], _redo = [];
        private readonly Dictionary<ulong, DocumentUnit> _unitStarts = [];
        private readonly List<DocumentUnit> _units = [];
        private DocumentUnit? _claiming;
        private readonly List<NativeUndoCommandResolution> _commands = [];
        private readonly List<NativeUndoBarrier> _barriers = [];
        private readonly int? _capacity;
        private readonly bool _initialEmpty;
        private bool _unknownUndoBottom, _unknownRedoBottom;
        private ulong _prefix = 1;
        // Every unit CSP still lists, oldest first: pushed units minus the redo entries a new unit discarded.
        private readonly List<Unit> _chronology = [];
        private int _epoch;
        private ulong _lastPush;
        private readonly Dictionary<ulong, List<HistoryReading>> _readings = [];
        private readonly Dictionary<ulong, ulong> _eventAppends = [];
        private readonly List<NativeUndoHistoryReading> _historyReadings = [];
        private PanelRegionMap _regions = PanelRegionMap.Unavailable();

        internal Resolver(SessionSnapshot session, IEnumerable<NativeUndoPenProof> proofs, int? capacity, bool initialEmpty,
            IReadOnlyList<INativeUndoUnitDetector> detectors)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(proofs);
            if (capacity is <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _session = session; _capacity = capacity; _initialEmpty = initialEmpty;
            _unknownUndoBottom = _unknownRedoBottom = !initialEmpty;
            _frames = session.Frames.Where(frame => U64(frame, "appendId") is > 0 &&
                U64(frame, "appendId") <= session.BoundaryAppendId).OrderBy(frame => U64(frame, "appendId")).ToArray();
            if (_frames.GroupBy(frame => U64(frame, "appendId")).Any(group => group.Count() != 1))
                throw new InvalidDataException("Undo timeline contains duplicate native appendIds.");
            if (_frames.Any(frame => Text(frame, "sessionId") is { } id && id != session.SessionId))
                throw new InvalidDataException("Undo timeline cannot mix native sessions.");
            _history = StateHistory.Resolve(_frames);
            _proofs = proofs.ToDictionary(proof => proof.BeginAppendId);
            if (_proofs.Values.Any(proof => proof.BeginAppendId == 0 || proof.OperationId == 0 ||
                proof.Layer is { } layer && layer.SessionId != session.SessionId))
                throw new InvalidDataException("Undo-unit proof belongs to a different native session or identity.");
            PrepareShortcuts();
            PrepareUnits(detectors);
            PrepareHistoryReadings();
        }

        private sealed record HistoryReading(ulong Append, ulong After, (string Text, bool Undone)[] Rows, int Current, ulong[] Inputs);

        // A reading is applied right after the last input the recorder had seen when it captured the palette.
        private void PrepareHistoryReadings()
        {
            foreach (var frame in _frames.Where(frame => Text(frame, "path") == "hardware" && U64(frame, "eventId") > 0))
                _eventAppends.TryAdd(U64(frame, "eventId"), U64(frame, "appendId"));
            foreach (var frame in _frames.Where(frame => Text(frame, "kind") == "coreStateUpdated"))
            {
                var data = Get(frame, "data");
                if (Text(data, "module") != "historyState" || Text(data, "status") is not ("changed" or "unchanged")) continue;
                var state = Get(data, "state");
                var evidence = Get(data, "evidence");
                var rows = Array(Get(state, "rows")).Select(row => (Text(row, "text") ?? "", Get(row, "undone").ValueKind == JsonValueKind.True)).ToArray();
                if (rows.Length == 0 || Long(state, "currentIndex") is not { } current || current < 0 || current >= rows.Length) continue;
                if (Get(evidence, "observedAfterEventId").ValueKind != JsonValueKind.Number ||
                    !_eventAppends.TryGetValue(U64(evidence, "observedAfterEventId"), out ulong after)) continue;
                var inputs = Array(Get(evidence, "relatedEventIds")).Select(Unsigned)
                    .Select(id => _eventAppends.TryGetValue(id, out ulong append) ? append : 0).Where(append => append > 0).ToArray();
                if (!_readings.TryGetValue(after, out var list)) _readings.Add(after, list = []);
                list.Add(new(U64(frame, "appendId"), after, rows, (int)current, inputs));
            }
        }

        // An undo or redo moves the current row but adds none; every other barrier may have added one
        // (an interrupted contact is usually still committed by CSP).
        private static bool MayAddRows(string reason) => reason is not ("undoRedoDuringCanvasContact" or
            "undoRedoReachesUnrecordedHistory" or "undoRedoTargetsOpaqueUndoUnit" or "canvasContactNotCompletedAtBoundary");

        private void ApplyReadings(ulong append)
        {
            if (!_readings.TryGetValue(append, out var readings)) return;
            foreach (var reading in readings.OrderBy(reading => reading.Append)) ApplyReading(reading);
        }

        private void ApplyReading(HistoryReading reading)
        {
            var rows = reading.Rows.Skip(1).ToArray(); // the first row is the document's opening state
            int active = reading.Current, undone = rows.Length - active;
            void Report(bool accepted, string reason, IReadOnlyList<Unit> aligned)
                => _historyReadings.Add(new(reading.Append, reading.After, accepted, reason, rows.Length, undone,
                    aligned.Select(unit => unit.Id).ToArray()));
            if (rows.Skip(active).Any(row => !row.Undone) || rows.Take(active).Any(row => row.Undone))
            { Report(false, "rowsNotAnUndoSuffix", []); return; }
            if (_contacts.Values.Any(pen => pen.Participates)) { Report(false, "readDuringCanvasContact", []); return; }
            // Newest row against the newest unit, back while the units are of this epoch (nothing unrecorded
            // could have added a row among or after them) and the same-name pattern holds. Older rows (history
            // from before the recording, or before an unknown input) stay unmatched.
            var aligned = new List<(Unit Unit, (string Text, bool Undone) Row)>();
            for (int i = rows.Length - 1, j = _chronology.Count - 1; i >= 0 && j >= 0; i--, j--)
            {
                var unit = _chronology[j];
                if (unit.Epoch != _epoch) break;
                if (aligned.Any(pair => pair.Unit.HistoryKey is { } key && unit.HistoryKey is { } other &&
                    key == other != (pair.Row.Text == rows[i].Text))) break;
                aligned.Add((unit, rows[i]));
            }
            if (aligned.Count == 0) { Report(false, "newestRowNotRecorded", []); return; }
            aligned.Reverse();
            // Row-level stacks: unmatched rows are units nobody recorded (null).
            var slots = new Unit?[rows.Length];
            for (int k = 0; k < aligned.Count; k++) slots[rows.Length - aligned.Count + k] = aligned[k].Unit;
            foreach (var (unit, row) in aligned)
            {
                unit.State = row.Undone ? NativeUndoPenState.Undone : NativeUndoPenState.Active;
                unit.LastInput = reading.After; unit.Reason = null;
            }
            var window = aligned.Select(pair => pair.Unit).ToHashSet();
            _undo.Clear(); _redo.Clear();
            _undo.AddRange(slots.Take(active).OfType<Unit>());
            _unknownUndoBottom = slots.Take(active).Any(slot => slot is null);
            // The next redo takes the earliest undone row; if nobody recorded it, the redo stack is unknown.
            if (slots.Skip(active).Any(slot => slot is null)) _unknownRedoBottom = true;
            else { _redo.AddRange(slots.Skip(active).Reverse().OfType<Unit>()); _unknownRedoBottom = false; }
            ResolveBurst(reading, slots, active);
            Report(true, "matched", aligned.Select(pair => pair.Unit).ToArray());
        }

        // Each undo/redo of the read burst moved one row; replaying the burst backwards from the read rows
        // names it. Only when no unit was added since the burst began.
        private void ResolveBurst(HistoryReading reading, Unit?[] slots, int active)
        {
            var inputs = reading.Inputs.ToHashSet();
            var burst = _commands.Select((command, index) => (command, index))
                .Where(item => inputs.Contains(item.command.InputAppendId) && item.command.Command is "undo" or "redo"
                    && item.command.State != NativeUndoCommandState.Blocked).ToArray();
            if (burst.Length == 0 || _lastPush > burst.Min(item => item.command.InputAppendId)) return;
            var undo = slots.Take(active).ToList();
            var redo = slots.Skip(active).Reverse().ToList();
            foreach (var (command, index) in burst.OrderByDescending(item => item.command.InputAppendId))
            {
                var (from, to) = command.Command == "undo" ? (redo, undo) : (undo, redo);
                if (from.Count == 0)
                {
                    // The panel lists every row down to the opening state: there was nothing to move.
                    _commands[index] = command with { State = NativeUndoCommandState.NoRecordedTarget, Target = null,
                        Reason = "historyPanelNothingToMove" };
                    continue;
                }
                var unit = from[^1]; from.RemoveAt(from.Count - 1); to.Add(unit);
                _commands[index] = unit is null
                    ? command with { State = NativeUndoCommandState.Unknown, Target = null, Reason = "historyPanelRowNotRecorded" }
                    : command with { State = NativeUndoCommandState.Resolved, Target = unit.Target,
                        RequiresBoundaryPixelValidation = true, Reason = "historyPanel" };
            }
        }


        // A unit's claim must start and end on recorded hardware inputs, must not overlap another
        // claim and must not swallow an undo or redo; otherwise its inputs keep their own meaning.
        private void PrepareUnits(IReadOnlyList<INativeUndoUnitDetector> detectors)
        {
            var hardware = _frames.Where(frame => Text(frame, "path") == "hardware" && !RecorderTagged(Get(frame, "data")))
                .Select(frame => U64(frame, "appendId")).ToHashSet();
            ulong claimedUntil = 0;
            foreach (var proof in detectors.SelectMany(detector => detector.Detect(_session, _frames, _history))
                .OrderBy(proof => proof.FirstInputAppendId).ThenBy(proof => proof.LastInputAppendId))
            {
                if (proof.Kind == NativeUndoUnitKind.Stroke || proof.FirstInputAppendId > proof.LastInputAppendId ||
                    !hardware.Contains(proof.FirstInputAppendId) || !hardware.Contains(proof.LastInputAppendId) ||
                    proof.FirstInputAppendId <= claimedUntil || proof.Layer is { } layer && layer.SessionId != _session.SessionId ||
                    _shortcuts.Any(item => item.Key >= proof.FirstInputAppendId && item.Key <= proof.LastInputAppendId &&
                        item.Value.Any(shortcut => shortcut.Matches.Any(match => Text(match, "command") is "undo" or "redo")))) continue;
                _unitStarts.Add(proof.FirstInputAppendId, new DocumentUnit(proof));
                claimedUntil = proof.LastInputAppendId;
            }
        }

        private void PrepareShortcuts()
        {
            var hardware = _frames.Where(frame => Text(frame, "path") == "hardware" && U64(frame, "eventId") > 0)
                .GroupBy(frame => U64(frame, "eventId")).ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var frame in _frames.Where(frame => Text(frame, "kind") == "shortcutResolved"))
            {
                var shortcut = new Shortcut(frame, Array(Get(Get(frame, "data"), "matches")));
                var refs = Array(Get(frame, "relatedEventIds")).Select(Unsigned).ToArray();
                if (refs.Length != 1 || refs[0] == 0 || !hardware.TryGetValue(refs[0], out var inputs) || inputs.Length != 1 ||
                    U64(inputs[0], "appendId") >= shortcut.Append)
                {
                    if (shortcut.Matches.Any(match => Text(match, "command") is "undo" or "redo") ||
                        shortcut.Matches.Any(match => !NonMutatingMatch(match))) _orphanShortcuts.Add(shortcut.Append, shortcut);
                    continue;
                }
                ulong append = U64(inputs[0], "appendId");
                if (!_shortcuts.TryGetValue(append, out var resolutions)) _shortcuts.Add(append, resolutions = []);
                resolutions.Add(shortcut);
            }
        }

        internal NativeUndoTimelineResult Resolve()
        {
            foreach (var frame in _frames)
            {
                ulong append = U64(frame, "appendId");
                string? kind = Text(frame, "kind");
                var data = Get(frame, "data");
                if (kind == "workspaceStatus") _regions = PanelRegionMap.FromWorkspaceStatus(data, append, Long(frame, "ticks"));
                if (_orphanShortcuts.TryGetValue(append, out var orphan))
                {
                    string command = Text(orphan.Matches.FirstOrDefault(), "command") ?? "unknown";
                    ulong prefix = _prefix;
                    Barrier(0, append, "shortcutNativeReferenceUnknown");
                    _commands.Add(new(command, 0, append, NativeUndoCommandState.Unknown, null,
                        prefix, append, false, "shortcutNativeReferenceUnknown"));
                }
                if (RecorderTagged(data) || Text(frame, "path") == "program") { ApplyReadings(append); continue; }
                if (Text(frame, "path") == "hardware" && Claim(append)) { ApplyReadings(append); continue; }
                if (Text(frame, "path") == "hardware")
                {
                    switch (kind)
                    {
                        case "penBegin": BeginPen(frame); break;
                        case "penSample":
                            if (!_contacts.ContainsKey(Operation(frame))) Barrier(append, append, "penSampleWithoutNativeBegin");
                            break;
                        case "penEnd": EndPen(frame); break;
                        case "keyInput": case "keyDown": Key(frame); break;
                        case "mouseDown": case "mouseDrag": Mouse(frame); break;
                        case "mouseWheel":
                            if (!Navigation(data, Location(data)) && !SafeWheelResolution(frame)) Barrier(append, append, "unclassifiedMouseWheel");
                            break;
                    }
                }
                else if (kind == "penInterrupted") InterruptPen(frame);
                else if (kind is "editingActionCompleted" or "documentMutation" or "documentChanged")
                    Barrier(append, append, "documentMutationOutsideKnownUnits");
                // A failed or cancelled transaction records no editing action, yet may have left
                // CSP operations (a new layer, a paste) on the native stack.
                else if (kind == "editTransactionEnded" && Text(data, "outcome") != "executed")
                    Barrier(append, append, "editTransactionLeftUnknownChanges");
                else if (kind == "saveGuardResult" && Get(data, "released").ValueKind == JsonValueKind.False)
                    Barrier(append, append, "guardedInputNotReleased");
                ApplyReadings(append);
            }
            foreach (var pen in _contacts.Values.Where(pen => pen.Participates).ToArray())
            {
                pen.Invalid = true;
                Barrier(pen.Begin, _session.BoundaryAppendId, "canvasContactNotCompletedAtBoundary");
            }
            return new(_session.SessionId, _session.BoundaryAppendId, _pens.ToDictionary(item => item.Key, item => item.Value.Freeze()),
                _commands.ToArray(), _barriers.ToArray(), _initialEmpty, _capacity, _units.Select(unit => unit.Freeze()).ToArray(),
                _historyReadings.ToArray());
        }

        // Inputs inside a proven unit's claim are that unit; it joins the stack at its last input (a
        // view-state claim joins nothing).
        // A claim reached during an open canvas contact is not trusted and its inputs are read normally.
        private bool Claim(ulong append)
        {
            if (_claiming is null && _unitStarts.TryGetValue(append, out var starting) &&
                !_contacts.Values.Any(pen => pen.Participates)) _claiming = starting;
            if (_claiming is not { } unit) return false;
            if (append == unit.Proof.LastInputAppendId)
            {
                _claiming = null;
                if (unit.Proof.Kind == NativeUndoUnitKind.ViewState) return true;
                _units.Add(unit);
                PushNew(unit, append);
            }
            return true;
        }

        private void BeginPen(JsonElement frame)
        {
            ulong append = U64(frame, "appendId"), eventId = U64(frame, "eventId"), operation = Operation(frame);
            var data = Get(frame, "data");
            var location = PanelRegionMap.ReadLocation(data);
            bool outside = Get(data, "inCsp").ValueKind == JsonValueKind.False ||
                location is { Status: "matched", Region: not "canvasViewport" } || location?.Status == "outsideRegions";
            bool navigation = !outside && Navigation(data, location);
            _proofs.TryGetValue(append, out var proof);
            if (proof is not null && proof.OperationId != operation)
                throw new InvalidDataException("Undo-unit proof operationId differs from its native penBegin.");
            bool matchedCanvas = location is { Status: "matched", Region: "canvasViewport" };
            if (!matchedCanvas && proof is not null) proof = proof with { KnownUndoUnit = false, ReplayEligible = false };
            var pen = new Pen(append, eventId, operation, proof, !outside && !navigation, navigation,
                location is { Status: "matched", Region: "layers" }, _prefix);
            if (_contacts.Values.Any(contact => contact.Participates) || _contacts.ContainsKey(operation))
            {
                pen.Invalid = true;
                Barrier(append, append, "overlappingNativePenContacts");
            }
            _pens.Add(append, pen); _contacts[operation] = pen;
        }

        private void EndPen(JsonElement frame)
        {
            ulong append = U64(frame, "appendId");
            if (!_contacts.Remove(Operation(frame), out var pen)) { Barrier(append, append, "penEndWithoutNativeBegin"); return; }
            pen.End = append;
            // A layer panel tap can change a layer (properties, creation, deletion), which CSP stacks
            // like a stroke. Unless a detector proved and claimed it, the stack is no longer known.
            if (pen.LayerPanel) { Barrier(pen.Begin, append, "layerPanelContactUnproven"); return; }
            if (!pen.Participates) return;
            // CSP still lists it, but it is on no recorded stack: the rows after it can no longer be matched across it.
            if (pen.Invalid) { pen.State = NativeUndoPenState.Unknown; pen.Reason ??= "contactCrossesUnknownInterval"; _epoch++; return; }
            pen.State = NativeUndoPenState.Active; pen.LastInput = append;
            pen.Reason = pen.Known ? null : "opaqueCanvasUndoUnit";
            // Every possible drawing contact occupies a stack position. It must
            // never disappear merely because it failed replay qualification.
            PushNew(pen, append);
        }

        private void InterruptPen(JsonElement frame)
        {
            var refs = Array(Get(frame, "relatedEventIds")).Select(Unsigned).ToHashSet();
            var interrupted = _contacts.Values.Where(pen => refs.Count == 0 || refs.Contains(pen.EventId)).ToArray();
            foreach (var pen in interrupted)
            {
                _contacts.Remove(pen.Operation); pen.Invalid = true;
                if (pen.Participates) Barrier(pen.Begin, U64(frame, "appendId"), "nativePenContactInterrupted");
            }
        }

        private void Key(JsonElement frame)
        {
            ulong append = U64(frame, "appendId");
            var data = Get(frame, "data");
            if (Get(data, "inCsp").ValueKind == JsonValueKind.False || IsModifier(data)) return;
            _shortcuts.TryGetValue(append, out var results);
            var result = results?.FirstOrDefault();
            var matches = result?.Matches ?? [];
            string command = matches.Length == 1 ? Text(matches[0], "command") ?? "unknown" : "ambiguous";
            ulong resolutionAppend = result?.Append ?? append;
            // The save guard releases an intercepted key to CSP right after its save; the replay carries
            // the Recorder tag and is not recorded again, so the key counts at its recorded position. A
            // cancelled release is a barrier of its own (saveGuardResult). Only another hook blocks.
            if (Long(data, "nextHookResult") is not null and not 0)
            {
                if (matches.Any(match => Text(match, "command") is "undo" or "redo"))
                    _commands.Add(new(command, append, resolutionAppend, NativeUndoCommandState.Blocked, null,
                        _prefix, append, false, "nativeHookDidNotDeliverCommand"));
                return;
            }
            bool duplicateConflict = results is not null && results.Any(other => !SameMatches(matches, other.Matches));
            // Several possible tool/settings choices still prove that no document
            // undo unit was created. Only their history effect is relevant here;
            // subsequent brush evidence decides which recipe actually became active.
            if (result is not null && Long(data, "nextHookResult") == 0 && results!.All(other =>
                other.Matches.Length > 0 && other.Matches.All(NonMutatingMatch) &&
                Text(Get(other.Result, "data"), "resolution") == "matched" &&
                Get(Get(other.Result, "data"), "shortcutMatchPending").ValueKind != JsonValueKind.True)) return;
            // A delivered key with an explicit unmapped result can still be the
            // canvas's temporary navigation modifier or the host's panel hotkey.
            // A configured document command always takes precedence over these
            // contextual rules, including when that mapping conflicts with another.
            bool unmapped = result is not null && matches.Length == 0 && !duplicateConflict &&
                results!.All(other => Text(Get(other.Result, "data"), "resolution") == "unmapped" &&
                    Get(Get(other.Result, "data"), "shortcutMatchPending").ValueKind != JsonValueKind.True);
            if (unmapped && Long(data, "nextHookResult") == 0 &&
                (ConfirmedNavigationKey(frame) || ConfirmedHostPanelHotkey(frame))) return;
            if (result is null || matches.Length != 1 || duplicateConflict || Long(data, "nextHookResult") is null)
            {
                UnknownCommand(command, append, resolutionAppend,
                    result is null ? "nativeShortcutUnresolved" : matches.Length != 1 || duplicateConflict
                        ? "shortcutMappingAmbiguous" : "nativeHookDeliveryUnknown");
                return;
            }
            if (command is "undo" or "redo") ApplyCommand(command, append, resolutionAppend);
            else if (!NonMutatingMatch(matches[0])) UnknownCommand(command, append, resolutionAppend, "commandOutsideKnownPenHistory");
        }

        private void ApplyCommand(string command, ulong input, ulong resolution)
        {
            if (_contacts.Values.Any(pen => pen.Participates))
            { UnknownCommand(command, input, resolution, "undoRedoDuringCanvasContact"); return; }
            var stack = command == "undo" ? _undo : _redo;
            if (stack.Count == 0)
            {
                if (command == "undo" ? _unknownUndoBottom : _unknownRedoBottom)
                { UnknownCommand(command, input, resolution, "undoRedoReachesUnrecordedHistory"); return; }
                _commands.Add(new(command, input, resolution, NativeUndoCommandState.NoRecordedTarget, null,
                    _prefix, input, false, "verifiedRecordedStackEmpty"));
                return;
            }
            var unit = stack[^1];
            if (!unit.Known)
            { UnknownCommand(command, input, resolution, "undoRedoTargetsOpaqueUndoUnit", unit.Target); return; }
            stack.RemoveAt(stack.Count - 1);
            unit.State = command == "undo" ? NativeUndoPenState.Undone : NativeUndoPenState.Active;
            unit.LastInput = input; unit.Reason = null;
            if (command == "undo") _redo.Add(unit); else PushUndo(unit);
            _commands.Add(new(command, input, resolution, NativeUndoCommandState.Resolved, unit.Target,
                _prefix, input, true, _capacity is null ? "undoCapacityUnverifiedRequiresBoundaryPixels" : null));
        }

        // A new unit discards the redo entries: CSP drops their History rows too.
        private void PushNew(Unit unit, ulong append)
        {
            foreach (var discarded in _redo) _chronology.Remove(discarded);
            unit.Epoch = _epoch; _chronology.Add(unit); _lastPush = append;
            PushUndo(unit); _redo.Clear(); _unknownRedoBottom = false;
        }

        private void PushUndo(Unit unit)
        {
            _undo.Add(unit);
            if (_capacity is { } capacity)
            {
                if (_undo.Count > capacity) _undo.RemoveRange(0, _undo.Count - capacity);
                if (_undo.Count == capacity) _unknownUndoBottom = false;
            }
        }

        private void Mouse(JsonElement frame)
        {
            var data = Get(frame, "data");
            if (Get(data, "inCsp").ValueKind == JsonValueKind.False) return;
            var location = Location(data);
            if (Navigation(data, location) || location is { Status: "matched", Region: "brushProperties" or "brushSelection" or "navigator" }
                || location?.Status == "outsideRegions") return;
            Barrier(U64(frame, "appendId"), U64(frame, "appendId"), "mouseDocumentHistoryUnknown");
        }

        private PenDownLocation? Location(JsonElement data)
            => PanelRegionMap.ReadLocation(data) ?? (_regions.HasRegions && Number(data, "x") is { } x && Number(data, "y") is { } y
                ? _regions.Classify(x, y) : null);

        private bool SafeWheelResolution(JsonElement frame)
        {
            if (!_shortcuts.TryGetValue(U64(frame, "appendId"), out var shortcuts) || shortcuts.Count == 0) return false;
            var first = shortcuts[0];
            if (first.Matches.Length != 1 || shortcuts.Any(other => !SameMatches(first.Matches, other.Matches))) return false;
            var match = first.Matches[0];
            if (NonMutatingMatch(match)) return true;
            var data = Get(frame, "data");
            // recorder_integration/catalog.py exposes global wheel output type 4
            // as a canvas-view operation, keeping its original numeric code.
            // Context-dependent or differently routed wheel mappings remain opaque.
            var targets = Array(Get(match, "targets"));
            return Text(match, "command") is null && Long(match, "outputType") == 4 && Long(match, "operationCode") is > 0 &&
                Get(match, "contextDependent").ValueKind == JsonValueKind.False &&
                Array(Get(match, "context")) is { Length: 3 } context && context.All(value => value.TryGetInt32(out int n) && n == 0) &&
                Long(match, "mask") is { } mask && (mask & 0x1000) != 0 && (mask & ~0x100F) == 0 &&
                targets.Length > 0 && targets.All(value => value.ValueKind == JsonValueKind.String && value.GetString() is "画布视口" or "导航器") &&
                Text(data, "axis") == "vertical" && Text(match, "axis") == "vertical" && Array(Get(data, "heldButtons")).Length == 0 &&
                Location(data) is { Status: "matched", Region: "canvasViewport" or "navigator" };
        }

        private bool ConfirmedNavigationKey(JsonElement frame)
        {
            var data = Get(frame, "data");
            if (Long(data, "vk") is not (32 or 82) || Array(Get(data, "modifiers")).Length != 0) return false;
            int vk = (int)Long(data, "vk")!;
            var held = Array(Get(data, "heldKeys"));
            if (held.Length == 0 || held.Any(value => !value.TryGetInt32(out int key) || key != vk)) return false;
            // Space has CSP's temporary hand meaning in a confirmed canvas context.
            // An unmapped R additionally needs an actual held-R navigation contact;
            // a bare letter elsewhere is not assigned a guessed tool action.
            return vk == 32 && ConfirmedCanvasContext(frame) || ConfirmedNavigationContact(frame, vk);
        }

        private bool ConfirmedCanvasContext(JsonElement frame)
        {
            ulong eventId = U64(frame, "eventId");
            var view = _history.LastOrDefault(slot => slot.Module == "canvasViewState" && slot.Anchor < eventId);
            if (view is null || StateHistory.At(_history, eventId).View is null) return false;
            // A later screenshot cannot retroactively prove the key's input context.
            var captured = Long(Get(view.Data, "evidence"), "capturedTicks");
            return captured is { } ticks && Long(frame, "ticks") is { } input && ticks < input;
        }

        private bool ConfirmedNavigationContact(JsonElement key, int vk)
        {
            ulong keyAppend = U64(key, "appendId");
            foreach (var begin in _frames.Where(frame => Text(frame, "path") == "hardware" && Text(frame, "kind") == "penBegin"))
            {
                var data = Get(begin, "data");
                if (Get(data, "inCsp").ValueKind == JsonValueKind.False || RecorderTagged(data) ||
                    PanelRegionMap.ReadLocation(data) is not { Status: "matched", Region: "canvasViewport" or "navigator" } ||
                    !Array(Get(data, "heldKeys")).Any(value => value.TryGetInt32(out int held) && held == vk)) continue;
                ulong beginAppend = U64(begin, "appendId"), operation = Operation(begin);
                if (!_frames.Any(frame => Text(frame, "kind") == "penEnd" && Text(frame, "path") == "hardware" &&
                    Operation(frame) == operation && U64(frame, "appendId") > beginAppend)) continue;
                ulong from = Math.Min(keyAppend, beginAppend), to = Math.Max(keyAppend, beginAppend);
                // The same held-key interval is evidence; unrelated earlier navigation
                // or a future press after release cannot classify this key.
                if (_frames.Any(frame => U64(frame, "appendId") > from && U64(frame, "appendId") < to &&
                    (Text(frame, "kind") == "keyboardStateChanged" &&
                        !Array(Get(Get(frame, "data"), "heldKeys")).Any(value => value.TryGetInt32(out int held) && held == vk) ||
                     Text(frame, "kind") == "shortcutConfiguration"))) continue;
                return true;
            }
            return false;
        }

        private bool ConfirmedHostPanelHotkey(JsonElement key)
        {
            var data = Get(key, "data");
            var held = Array(Get(data, "heldKeys"));
            var modifiers = Array(Get(data, "modifiers"));
            if (Long(data, "vk") != 77 || modifiers.Length != 1 || modifiers[0].ValueKind != JsonValueKind.String ||
                modifiers[0].GetString() != "Alt" || !held.Any(value => value.TryGetInt32(out int n) && n == 77) ||
                !held.Any(value => value.TryGetInt32(out int n) && n is 18 or 164 or 165) ||
                held.Any(value => !value.TryGetInt32(out int n) || n is not (77 or 18 or 164 or 165))) return false;
            if (Text(data, "source") == "hostGlobalHotkey") return true;
            // GlobalHotkey registers Alt+M for the demo's editing panel. Legacy
            // recordings lack an explicit host tag, but the panel's edit-* marker
            // proves that path when no intervening document input is recorded.
            if (_session.Frequency <= 0 || Long(key, "ticks") is not { } inputTicks) return false;
            foreach (var frame in _frames.Where(frame => U64(frame, "appendId") > U64(key, "appendId")))
            {
                if (Text(frame, "kind") == "editBoundaryRequested" &&
                    Text(Get(frame, "data"), "requestId")?.StartsWith("edit-", StringComparison.Ordinal) == true &&
                    Long(frame, "ticks") is { } boundaryTicks && boundaryTicks >= inputTicks &&
                    ((double)boundaryTicks - inputTicks) / _session.Frequency <= 30) return true;
                if (Text(frame, "path") == "hardware" &&
                    (Text(frame, "kind") is "penBegin" or "mouseDown" or "mouseDrag" or "mouseWheel" ||
                     Text(frame, "kind") is "keyInput" or "keyDown" && !IsModifier(Get(frame, "data")))) return false;
            }
            return false;
        }

        private void UnknownCommand(string command, ulong input, ulong resolution, string reason, NativeUndoTarget? target = null)
        {
            ulong prefix = _prefix;
            Barrier(input, resolution, reason);
            _commands.Add(new(command, input, resolution, NativeUndoCommandState.Unknown, target, prefix, input, false, reason));
        }

        private void Barrier(ulong input, ulong resolution, string reason)
        {
            var affected = _pens.Values.Where(pen => pen.Participates).Select(pen => pen.Begin).Order().ToArray();
            foreach (var pen in _pens.Values.Where(pen => pen.Participates))
            {
                pen.State = NativeUndoPenState.Unknown; pen.LastInput = input; pen.Reason = reason;
                if (pen.End is null) pen.Invalid = true;
            }
            foreach (var unit in _units) { unit.State = NativeUndoPenState.Unknown; unit.LastInput = input; unit.Reason = reason; }
            _barriers.Add(new(input, resolution, reason, affected));
            _undo.Clear(); _redo.Clear(); _unknownUndoBottom = _unknownRedoBottom = true;
            if (MayAddRows(reason)) _epoch++;
            // A delayed resolution proves an earlier input. Its publication must
            // not move that input's causal boundary past intervening strokes.
            _prefix = (input > 0 ? input : resolution) + 1;
        }
    }

    private static bool NonMutatingMatch(JsonElement match)
    {
        string? command = Text(match, "command");
        if (command is not null)
            return command == "filesave" || command is "applicationchangecurrentcolor" or "applicationchangecolortransparent" ||
                command.StartsWith("view", StringComparison.Ordinal) || command.StartsWith("layerselect", StringComparison.Ordinal) ||
                command.StartsWith("tool", StringComparison.Ordinal) || command.StartsWith("subtool", StringComparison.Ordinal) ||
                command.StartsWith("windowshow", StringComparison.Ordinal) || command.StartsWith("windowhide", StringComparison.Ordinal) ||
                command.StartsWith("windowbubble", StringComparison.Ordinal);
        // Catalog tool entries carry their original identities rather than a
        // menu-command string. Merely selecting that tool is not an undo unit.
        return Text(match, "id") is not null && (Get(match, "tool").ValueKind == JsonValueKind.Object ||
            Get(match, "pointerTargets").ValueKind == JsonValueKind.Array);
    }

    private static bool Navigation(JsonElement data, PenDownLocation? location)
        => location is { Status: "matched", Region: "canvasViewport" or "navigator" } &&
            Array(Get(data, "heldKeys")).Any(key => key.TryGetInt32(out int vk) && vk is 32 or 82);
    private static bool IsModifier(JsonElement data) => Long(data, "vk") is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
    private static bool RecorderTagged(JsonElement data) => Text(data, "extraInfo")?.Equals("0x4D4C5243", StringComparison.OrdinalIgnoreCase) == true
        || U64(data, "extraInfo") == 0x4D4C5243;
    private static bool SameMatches(JsonElement[] left, JsonElement[] right)
        => left.Length == right.Length && left.Select(match => match.GetRawText()).SequenceEqual(right.Select(match => match.GetRawText()));
    private static ulong Operation(JsonElement frame)
    {
        ulong operation = U64(frame, "operationId");
        return operation > 0 ? operation : U64(frame, "eventId");
    }
    private static JsonElement Get(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? item : default;
    private static string? Text(JsonElement value, string key)
        => Get(value, key) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;
    private static ulong U64(JsonElement value, string key) => Unsigned(Get(value, key));
    private static ulong Unsigned(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong result) ? result : 0;
    private static long? Long(JsonElement value, string key)
        => Get(value, key) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt64(out long result) ? result : null;
    private static double? Number(JsonElement value, string key)
        => Get(value, key) is { ValueKind: JsonValueKind.Number } item && item.TryGetDouble(out double result) && double.IsFinite(result) ? result : null;
    private static JsonElement[] Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
}
