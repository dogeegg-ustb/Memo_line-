using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

/// <summary>Real-file evidence for native undo inference and effective-history checkpoints.</summary>
internal static class ActualUndoHistoryChecks
{
    internal static async Task RunAsync(string path, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        var report = new Dictionary<string, object?>
        {
            ["source"] = Path.GetFullPath(path),
            ["scope"] = "Immutable real-file read, production native undo timeline/index, selection/planning and native schedule validation. No CSP input, new brush capture, paste or undo-stack readback."
        };
        string? sourceHash = null;
        try
        {
            report["sourceHash"] = sourceHash = Hash(path);
            Console.WriteLine("Reading real sealed sessions for native undo-history validation...");
            var sources = await ProjectSessionReader.ReadProjectAsync(path,
                Path.Combine(reportDirectory, "cache-" + Guid.NewGuid().ToString("N")));
            var indexes = sources.ToDictionary(s => s.SessionId, EditIndexBuilder.Build);
            report["nativeSessions"] = sources.Select(s => NativeSummary(s, indexes[s.SessionId])).ToArray();
            // Save the native diagnosis immediately so a costly selection probe never hides it.
            WriteReport(reportDirectory, report);
            foreach (var source in sources)
            {
                var timeline = indexes[source.SessionId].UndoTimeline;
                Console.WriteLine($"Session {source.SessionId}: commands {timeline?.Commands.Count}, barriers {timeline?.Barriers.Count}, " +
                    $"checkpoints {indexes[source.SessionId].Layers.Values.Sum(l => l.UndoCheckpoints.Count)}.");
                if (timeline is not null)
                    foreach (var barrier in timeline.Barriers)
                        Console.WriteLine($"Barrier input {barrier.InputAppendId}, result {barrier.ResolutionAppendId}: {barrier.Reason}; affected {barrier.AffectedPenBeginAppendIds.Count}.");
            }
            var current = sources[^1];
            var index = ProjectEditIndex.Build(sources.Take(sources.Count - 1).ToArray(), current, indexes);
            report["projectLayers"] = index.Layers.Values.Select(l => new
            {
                l.Layer, l.ChainValid, Issues = Issues(l.Issues),
                Checkpoints = l.UndoCheckpoints.Select(p => new { p.PacketId, Contacts = p.Events.Count, p.Events,
                    Width = p.Seed.Width, Height = p.Seed.Height }).ToArray()
            }).ToArray();
            report["projectStrokes"] = new
            {
                Total = index.Strokes.Count, Active = index.Strokes.Values.Count(s => !s.IsUndone),
                Undone = index.Strokes.Values.Count(s => s.IsUndone),
                UndoneKeys = index.Strokes.Values.Where(s => s.IsUndone).Select(s => s.Key).ToArray()
            };
            var layer = index.Layers.Keys.Single(k => k.SessionId == current.SessionId);
            var view = current.Frames.Where(f => J.Text(f, "kind") == "coreStateUpdated").Select(f => J.Get(f, "data"))
                .Last(d => J.Text(d, "module") == "canvasViewState" && J.Confirmed(d));
            var bound = JsonNode.Parse(view.GetRawText())!.AsObject(); bound["sessionId"] = current.SessionId;
            var session = new EditSession(current, index, layer)
            { Sources = sources.ToDictionary(s => s.SessionId), CanvasViewEvidence = JsonSerializer.SerializeToElement(bound) };
            var interaction = new CanvasInteraction(session);
            var attempts = new List<object>();
            report["selectionAttempts"] = attempts;
            ulong? firstUndoInput = indexes[current.SessionId].UndoTimeline?.Commands
                .Where(c => c.Command == "undo" && c.State == NativeUndoCommandState.Resolved)
                .OrderBy(c => c.InputAppendId).FirstOrDefault()?.InputAppendId;
            report["firstResolvedUndoInputAppendId"] = firstUndoInput;
            var eligibleCandidates = index.Strokes.Values.Where(s => s.Layer == layer && !s.IsUndone && s.Images.Count > 0).ToArray();
            var oldCandidates = eligibleCandidates.Where(s => firstUndoInput is { } first && s.Key.SessionId == current.SessionId && s.Key.BeginAppendId < first)
                .OrderBy(s => s.Key.BeginAppendId).ToArray();
            // Exercise surviving ink on both ends of the prefix before the first Undo.
            // Recent ink is supplementary; it cannot establish regression coverage.
            var chosenOld = oldCandidates.Reverse().Take(4).Concat(oldCandidates.Take(4)).DistinctBy(s => s.Key).Take(8).ToArray();
            var chosenRecent = eligibleCandidates.Where(s => firstUndoInput is null || s.Key.SessionId != current.SessionId || s.Key.BeginAppendId >= firstUndoInput.Value)
                .OrderByDescending(s => s.SessionOrder).ThenByDescending(s => s.BeginEventId).Take(8).ToArray();
            report["candidateCounts"] = new { OldBeforeFirstUndo = oldCandidates.Length, ChosenOld = chosenOld.Length, ChosenRecent = chosenRecent.Length };
            object? undoSuccess = null, eventSuccess = null;
            bool undoCrosses = false, eventCrosses = false;
            foreach (var stroke in chosenOld.Concat(chosenRecent).DistinctBy(s => s.Key))
            {
                var currentPixels = index.Layers[layer].Current!;
                var point = stroke.WriteMask.Pixels().FirstOrDefault(p => currentPixels.Bytes[(p.Y * currentPixels.Width + p.X) * 4 + 3] > 0);
                if (!stroke.WriteMask.Contains(point.X, point.Y)) continue;
                var a = new PointF(Math.Max(0, point.X - 2), Math.Max(0, point.Y - 2));
                var b = new PointF(Math.Min(currentPixels.Width, point.X + 2), Math.Min(currentPixels.Height, point.Y + 2));
                var range = new { Left = a.X, Top = a.Y, Right = b.X, Bottom = b.Y };
                interaction.SetRegion(CanvasSelectionGeometry.Rectangle(interaction.Transform.CanvasToScreen(a), interaction.Transform.CanvasToScreen(b)));
                var selection = interaction.RegionSelection!;
                var undo = PreciseUndoSelector.Select(index, layer, selection);
                var undoPlan = ReconstructionPlanner.Build(index, undo.Request(selection));
                var candidate = interaction.Candidates.FirstOrDefault(c => stroke.Images.Contains(c.Image.Key) && c.CanSelect);
                ReconstructionPlan? eventPlan = null;
                if (candidate is not null)
                {
                    interaction.SelectImage(candidate.Image.Key); interaction.SetSelectedEvents([stroke.Key]);
                    eventPlan = ReconstructionPlanner.Build(index, new(EditKind.EventEditing, layer, selection,
                        interaction.SelectedEvents.ToDictionary(k => k, _ => new EventChange(Delete: true))));
                }
                attempts.Add(new
                {
                    stroke.Key, stroke.BeginEventId, Point = new { point.X, point.Y },
                    Range = range,
                    ProbeContactBeforeFirstUndo = firstUndoInput is { } first && stroke.Key.SessionId == current.SessionId && stroke.Key.BeginAppendId < first,
                    PreciseUndoChangesBeforeFirstUndo = CrossesUndo(undoPlan, current.SessionId, firstUndoInput),
                    EventEditingChangesBeforeFirstUndo = eventPlan is not null && CrossesUndo(eventPlan, current.SessionId, firstUndoInput),
                    Candidates = interaction.Candidates.Select(c => new { c.Image.Key, c.CanSelect, c.Reason }).ToArray(),
                    UndoSelectionValid = undo.IsValid, UndoSelected = undo.Selected.Count,
                    UndoSelectionIssues = Issues(undo.Issues), UndoPlan = PlanSummary(undoPlan),
                    EventPlan = eventPlan is null ? null : PlanSummary(eventPlan)
                });
                bool undoPlanCrosses = CrossesUndo(undoPlan, current.SessionId, firstUndoInput);
                bool eventPlanCrosses = eventPlan is not null && CrossesUndo(eventPlan, current.SessionId, firstUndoInput);
                if ((undoSuccess is null || !undoCrosses && undoPlanCrosses) && undo.IsValid && undoPlan.IsValid)
                {
                    undoSuccess = ValidateLegalPlan(session, undoPlan, stroke.Key, range, firstUndoInput);
                    undoCrosses |= undoPlanCrosses;
                }
                if ((eventSuccess is null || !eventCrosses && eventPlanCrosses) && eventPlan?.IsValid == true)
                {
                    eventSuccess = ValidateLegalPlan(session, eventPlan, stroke.Key, range, firstUndoInput);
                    eventCrosses |= eventPlanCrosses;
                }
                if (undoSuccess is not null && eventSuccess is not null && undoCrosses && eventCrosses) break;
            }
            report["selectionAttempts"] = attempts;
            report["preciseUndo"] = undoSuccess;
            report["eventEditing"] = eventSuccess;
            report["undoRegressionCoverage"] = new
            {
                FirstUndoInputAppendId = firstUndoInput,
                PreciseUndoPlanChangesBeforeFirstUndo = undoCrosses,
                EventEditingPlanChangesBeforeFirstUndo = eventCrosses,
                BothModesCoverSurvivingInkBeforeUndo = undoCrosses && eventCrosses,
                Note = "A legal plan on a newly drawn stroke after Undo does not establish this regression's coverage. Coverage requires actual changed contacts from before the first recorded Undo, with valid original checkpoint schedules."
            };
            Console.WriteLine($"Actual-file selection: legal precise undo {undoSuccess is not null}; legal single-event delete {eventSuccess is not null}.");
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        finally
        {
            report["sourceUnchanged"] = sourceHash is not null && sourceHash == Hash(path);
            WriteReport(reportDirectory, report);
        }
    }

    private static object NativeSummary(SessionSnapshot snapshot, EditIndex index)
    {
        var timeline = index.UndoTimeline;
        return new
        {
            snapshot.SessionId, snapshot.Frequency, snapshot.BoundaryAppendId,
            PenBegins = snapshot.Frames.Count(f => J.Text(f, "kind") == "penBegin"),
            PenEnds = snapshot.Frames.Count(f => J.Text(f, "kind") == "penEnd"),
            Timeline = timeline is null ? null : new
            {
                timeline.Commands, timeline.Barriers, timeline.InitialHistoryKnownEmpty, timeline.VerifiedUndoCapacity,
                timeline.RequiresBoundaryPixelValidation,
                PenStates = timeline.Pens.Values.GroupBy(p => p.State).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                ParticipatingPenStates = timeline.Pens.Values.Where(p => p.ParticipatesInHistory).GroupBy(p => p.State)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                Pens = timeline.Pens.Values.ToArray()
            },
            IndexedStrokes = index.Strokes.Count,
            Layers = index.Layers.Values.Select(layer => CheckpointSummary(snapshot, layer, index, timeline)).ToArray()
        };
    }

    private static object CheckpointSummary(SessionSnapshot snapshot, LayerIndex layer, EditIndex index, NativeUndoTimelineResult? timeline)
    {
        var resolved = timeline?.Commands.Where(c => c.State == NativeUndoCommandState.Resolved && c.Target?.Layer == layer.Layer).ToArray() ?? [];
        var active = timeline?.Pens.Values.Where(p => p.ParticipatesInHistory && p.Layer == layer.Layer && p.State == NativeUndoPenState.Active)
            .OrderBy(p => p.BeginAppendId).ToArray() ?? [];
        var packets = layer.Packets.OrderBy(p => p.TriggerTicks).ToArray();
        var last = packets.LastOrDefault(p => p.Current is not null);
        var first = packets.FirstOrDefault(p => !p.IsRecordingBaseline);
        var baseline = packets.Select(p => p.Before).FirstOrDefault(p => p is not null) ??
            packets.Where(p => p.IsRecordingBaseline).Select(p => p.Current).FirstOrDefault(p => p is not null);
        var gates = new List<object>();
        void Gate(string code, object evidence) => gates.Add(new { Code = code, Evidence = evidence });
        if (resolved.Length == 0) Gate("NoResolvedUndoTargetsForLayer", timeline?.Commands.ToArray() ?? []);
        if (timeline?.Commands.Any(c => c.State is NativeUndoCommandState.Unknown or NativeUndoCommandState.NoRecordedTarget) == true)
            Gate("CommandHistoryUnknown", timeline.Commands.Where(c => c.State is NativeUndoCommandState.Unknown or NativeUndoCommandState.NoRecordedTarget).ToArray());
        if (timeline?.Pens.Values.Any(p => p.ParticipatesInHistory && p.State == NativeUndoPenState.Unknown) == true)
            Gate("ParticipatingPensUnknown", timeline.Pens.Values.Where(p => p.ParticipatesInHistory && p.State == NativeUndoPenState.Unknown).ToArray());
        if (!layer.ChainValid) Gate("LayerChainInvalid", Issues(layer.Issues));
        if (first is null) Gate("NoRecordedPacket", layer.Layer);
        if (last?.Current is null) Gate("NoFullBoundaryCapture", layer.Layer);
        long lastCommandTicks = snapshot.Frames.Where(f => resolved.Any(c => c.ResolutionAppendId == J.U64(f, "appendId")))
            .Select(f => J.I64(f, "ticks")).DefaultIfEmpty(long.MaxValue).Max();
        if (last is not null && last.TriggerTicks < lastCommandTicks) Gate("CapturePrecedesLastResolvedCommand", new { last.TriggerTicks, LastCommandTicks = lastCommandTicks });
        if (baseline is null) Gate("NoBaselinePixels", layer.Layer);
        if (baseline is not null && last?.Current is { } current && (baseline.Width != current.Width || baseline.Height != current.Height))
            Gate("BaselineSizeMismatch", new { baseline.Width, baseline.Height, CurrentWidth = current.Width, CurrentHeight = current.Height });
        var opaque = active.Where(p => !p.KnownUndoUnit || !p.ReplayEligible).ToArray();
        if (opaque.Length > 0) Gate("ActivePensNotReplayable", opaque);
        var missing = active.Where(p => !index.Strokes.TryGetValue(new(snapshot.SessionId, p.BeginAppendId), out var stroke) || !stroke.Eligible).ToArray();
        if (missing.Length > 0) Gate("ActivePensMissingEligibleStroke", missing);
        var references = active.Where(p => index.Strokes.TryGetValue(new(snapshot.SessionId, p.BeginAppendId), out var stroke) && new ReplayEvent(stroke, null).RequiresReferenceScene).ToArray();
        if (references.Length > 0) Gate("ActivePensNeedHistoricalReferenceScene", references);
        return new
        {
            layer.Layer, layer.ChainValid, Issues = Issues(layer.Issues),
            Checkpoints = layer.UndoCheckpoints.Select(p => new { p.PacketId, p.Events, Contacts = p.Events.Count }).ToArray(),
            ResolvedCommandsForLayer = resolved.Length, ActivePens = active.Length, Gates = gates
        };
    }

    private static object PlanSummary(ReconstructionPlan plan) => new
    {
        plan.IsValid, Issues = Issues(plan.Issues), ChangedContacts = plan.Request.Changes.Keys.ToArray(),
        ReplayContacts = ReplayKeys(plan)
    };

    private static bool CrossesUndo(ReconstructionPlan plan, string currentSession, ulong? firstUndo)
        => firstUndo is { } first && plan.Request.Changes.Keys.Any(k => k.SessionId == currentSession && k.BeginAppendId < first);

    private static object ValidateLegalPlan(EditSession session, ReconstructionPlan plan, EditableStrokeKey probeContact, object range, ulong? firstUndo)
    {
        // Pixel validation replays the original effective checkpoint before applying
        // edits, so its contacts include those the request later deletes.
        var originalValidation = plan.Index.Layers.Values.SelectMany(layer => layer.UndoCheckpoints.Select(checkpoint => new
        {
            checkpoint.PacketId,
            Contacts = checkpoint.Events,
            Schedules = WindowsCspEditingActions.BuildSchedules(session, plan.Request,
                checkpoint.Events.Select(k => new ReplayEvent(plan.Index.Strokes[k], null)).ToArray(),
                ReplayMode.Concentrated, "offline-original-undo-validation", "offline-original-undo-validation-uuid")
        })).ToArray();
        var replayKeys = ReplayKeys(plan);
        var concentrated = WindowsCspEditingActions.BuildSchedules(session, plan.Request,
            replayKeys.Select(k => new ReplayEvent(plan.Index.Strokes[k], plan.Request.Changes.GetValueOrDefault(k))).ToArray(),
            ReplayMode.Concentrated, "offline-undo-audit", "offline-undo-audit-uuid");
        IReadOnlyList<EventReplaySchedule> discrete = plan.Request.Kind == EditKind.PreciseUndo
            ? WindowsCspEditingActions.BuildSchedules(session, plan.Request,
                plan.Request.Changes.Keys.Select(k => new ReplayEvent(plan.Index.Strokes[k], null)).ToArray(),
                ReplayMode.Discrete, "offline-undo-audit", "offline-undo-audit-uuid") : [];
        return new
        {
            Status = "AwaitingCapture", Plan = PlanSummary(plan),
            ProbeContact = probeContact, Range = range, FirstUndoInputAppendId = firstUndo,
            ChangesBeforeFirstRecordedUndo = CrossesUndo(plan, session.LiveView.SessionId, firstUndo),
            OriginalUndoValidationSchedules = originalValidation.Select(proof => new
            {
                proof.PacketId, proof.Contacts,
                Schedules = proof.Schedules.Select(s => new { s.Frequency,
                    Contacts = s.Strokes.Select(p => new { p.EditableKey.SessionId, p.EditableKey.BeginAppendId }).ToArray() }).ToArray()
            }).ToArray(),
            Concentrated = concentrated.Select(s => new { s.Frequency, Contacts = s.Strokes.Select(p => new { p.EditableKey.SessionId, p.EditableKey.BeginAppendId }).ToArray() }).ToArray(),
            Discrete = discrete.Select(s => new { s.Frequency, Contacts = s.Strokes.Select(p => new { p.EditableKey.SessionId, p.EditableKey.BeginAppendId }).ToArray() }).ToArray(),
            Limitation = "A valid dependency plan and original native schedule still need CSP brush capture and checkpoint pixel validation. No execution or pixel verification has been invented."
        };
    }

    private static EditableStrokeKey[] ReplayKeys(ReconstructionPlan plan) => plan.Layers.SelectMany(l => l.Segments)
        .Where(s => s.Kind == ReconstructionSegmentKind.Replay).SelectMany(s => s.Events).Distinct().ToArray();
    private static object[] Issues(IEnumerable<PlanIssue> issues) => issues.Select(i => (object)new { Code = i.Code.ToString(), i.Message, i.Image, i.Stroke }).ToArray();
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void WriteReport(string directory, Dictionary<string, object?> report)
    {
        var options = new JsonSerializerOptions { WriteIndented = true }; options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(Path.Combine(directory, "actual-undo-history-results.json"), JsonSerializer.Serialize(report, options));
    }
}
