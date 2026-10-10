using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

/// <summary>Bounded, real-file selection probe. It never injects input or invents brush captures.</summary>
internal static class ActualProjectProbeChecks
{
    internal static async Task RunAsync(string path, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        var report = new ProbeReport { Source = Path.GetFullPath(path) };
        try
        {
            report.SourceHash = FileHash(path);
            Console.WriteLine("Reading original sealed sessions and image assets for the bounded range probe...");
            var sources = await ProjectSessionReader.ReadProjectAsync(path,
                Path.Combine(reportDirectory, "cache-" + Guid.NewGuid().ToString("N")));
            var current = sources[^1];
            report.SessionCount = sources.Count;
            report.RawPenBegins = sources.Sum(s => s.Frames.Count(f => J.Text(f, "kind") == "penBegin"));
            var indexes = sources.ToDictionary(s => s.SessionId, EditIndexBuilder.Build);
            var index = ProjectEditIndex.Build(sources.Take(sources.Count - 1).ToArray(), current, indexes);
            report.IndexedStrokes = index.Strokes.Count;
            report.Images = index.Images.Count;
            var layer = index.Layers.Keys.Single(k => k.SessionId == current.SessionId);
            var view = current.Frames.Where(f => J.Text(f, "kind") == "coreStateUpdated")
                .Select(f => J.Get(f, "data"))
                .Last(d => J.Text(d, "module") == "canvasViewState" && J.Confirmed(d));
            var bound = JsonNode.Parse(view.GetRawText())!.AsObject();
            bound["sessionId"] = current.SessionId;
            var session = new EditSession(current, index, layer)
            {
                Sources = sources.ToDictionary(s => s.SessionId),
                CanvasViewEvidence = JsonSerializer.SerializeToElement(bound)
            };
            var interaction = new CanvasInteraction(session);
            var groups = index.Layers[layer].Packets.Where(p => !p.IsRecordingBaseline)
                .OrderBy(p => p.SessionOrder).ThenBy(p => p.TriggerTicks).ToArray();
            var imagePositions = groups.SelectMany((g, n) => g.Images.Select(i => (i.Key, n)))
                .ToDictionary(p => p.Key, p => p.n);
            var strokes = index.Strokes.Values.Where(s => s.Layer == layer && s.Images.Count > 0)
                .OrderByDescending(s => s.SessionOrder).ThenByDescending(s => s.BeginEventId)
                .Take(80).ToArray();
            report.Layers = index.Layers.Values.Select(l => new
            {
                l.Layer, l.ChainValid, Issues = Issues(l.Issues)
            }).ToArray();
            Console.WriteLine($"Recovered {report.IndexedStrokes} eligible contacts; testing up to {strokes.Length} recent contacts.");
            foreach (var stroke in strokes)
            {
                report.ContactsConsidered++;
                foreach (var imageKey in stroke.Images.OrderByDescending(k => imagePositions.GetValueOrDefault(k, -1)))
                {
                    if (!index.Images.TryGetValue(imageKey, out var image) || !imagePositions.TryGetValue(imageKey, out int g)) continue;
                    var later = groups.Skip(g + 1).SelectMany(p => p.Images).ToArray();
                    var point = VisiblePoint(image, stroke, later);
                    if (point is null) continue;
                    foreach (int extent in new[] { 40, 4 })
                    {
                        var (x, y) = point.Value;
                        var a = new PointF(Math.Max(0, x - extent / 2), Math.Max(0, y - extent / 2));
                        var b = new PointF(Math.Min(interaction.Transform.CanvasSize.Width, x + extent / 2),
                            Math.Min(interaction.Transform.CanvasSize.Height, y + extent / 2));
                        var attempt = new ProbeAttempt
                        {
                            Contact = stroke.Key, BeginEventId = stroke.BeginEventId, Image = imageKey,
                            CanvasRange = new { Left = a.X, Top = a.Y, Right = b.X, Bottom = b.Y },
                            VisiblePixel = new { X = x, Y = y }, Extent = extent
                        };
                        report.Attempts.Add(attempt);
                        try
                        {
                            interaction.SetRegion(CanvasSelectionGeometry.Rectangle(
                                interaction.Transform.CanvasToScreen(a), interaction.Transform.CanvasToScreen(b)));
                            var selection = interaction.RegionSelection!;
                            var candidate = interaction.Candidates.FirstOrDefault(c => c.Image.Key == imageKey);
                            attempt.CanSelect = candidate?.CanSelect ?? false;
                            attempt.CandidateReason = candidate?.Reason ?? "No intersecting candidate image.";
                            if (report.PreciseUndo is null)
                            {
                                var undo = PreciseUndoSelector.Select(index, layer, selection);
                                var request = new EditRequest(EditKind.PreciseUndo, layer, selection,
                                    undo.Selected.ToDictionary(s => s.Key, _ => new EventChange(Delete: true)));
                                var plan = ReconstructionPlanner.Build(index, request);
                                attempt.PreciseUndo = PlanSummary(plan, undo.IsValid, undo.Issues);
                                if (undo.IsValid && plan.IsValid)
                                    report.PreciseUndo = await ValidateLegalPlan(plan, session, attempt);
                            }
                            if (report.EventEditing is null)
                            {
                                if (candidate?.CanSelect == true)
                                {
                                    interaction.SelectImage(imageKey);
                                    interaction.SetSelectedEvents([stroke.Key]);
                                    var request = new EditRequest(EditKind.EventEditing, layer, selection,
                                        interaction.SelectedEvents.ToDictionary(k => k, _ => new EventChange(Delete: true)));
                                    var plan = ReconstructionPlanner.Build(index, request);
                                    attempt.EventEditing = PlanSummary(plan, true, []);
                                    if (plan.IsValid)
                                        report.EventEditing = await ValidateLegalPlan(plan, session, attempt);
                                }
                                else attempt.EventEditing = new { Status = "Blocked", Issues = new[] { attempt.CandidateReason } };
                            }
                        }
                        catch (Exception error)
                        {
                            attempt.Error = error.ToString();
                        }
                        if (report.Attempts.Count % 10 == 0)
                            Console.WriteLine($"Probe ranges: {report.Attempts.Count}; undo legal {report.PreciseUndo is not null}; event legal {report.EventEditing is not null}.");
                        if (report.PreciseUndo is not null && report.EventEditing is not null) break;
                    }
                    // One current visible point per contact keeps the actual-file probe bounded.
                    break;
                }
                if (report.PreciseUndo is not null && report.EventEditing is not null) break;
            }
        }
        catch (Exception error)
        {
            report.Error = error.ToString();
        }
        finally
        {
            try { report.SourceUnchanged = report.SourceHash is not null && report.SourceHash == FileHash(path); }
            catch (Exception error) { report.SourceVerificationError = error.Message; }
            string resultPath = Path.Combine(reportDirectory, "actual-project-probe-results.json");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Probe report: {resultPath}");
            Console.WriteLine($"Legal precise undo: {report.PreciseUndo is not null}; legal event editing: {report.EventEditing is not null}; source unchanged: {report.SourceUnchanged}.");
        }
    }

    private static (int X, int Y)? VisiblePoint(DirtyImage image, EditableStroke stroke, DirtyImage[] later)
    {
        // Scan only the intersection of the exact changed image and this contact's write footprint.
        int left = Math.Max(image.Mask.Left, stroke.WriteMask.Left), top = Math.Max(image.Mask.Top, stroke.WriteMask.Top);
        int right = Math.Min(image.Mask.Left + image.Mask.Width, stroke.WriteMask.Left + stroke.WriteMask.Width);
        int bottom = Math.Min(image.Mask.Top + image.Mask.Height, stroke.WriteMask.Top + stroke.WriteMask.Height);
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (image.Mask.Contains(x, y) && stroke.WriteMask.Contains(x, y) && !later.Any(i => i.Mask.Contains(x, y)))
                    return (x, y);
        return null;
    }

    private static object PlanSummary(ReconstructionPlan plan, bool selectionValid, IReadOnlyList<PlanIssue> selectionIssues)
    {
        var contacts = ReplayContacts(plan);
        return new
        {
            Status = selectionValid && plan.IsValid ? contacts.Length == 0 ? "OfflineReconstructionAvailable" : "AwaitingCapture" : "Blocked",
            SelectionValid = selectionValid, PlanValid = plan.IsValid,
            SelectedContacts = plan.Request.Changes.Keys.ToArray(),
            SelectionIssues = Issues(selectionIssues), PlanIssues = Issues(plan.Issues),
            ReplayContactCount = contacts.Length, ReplayContacts = contacts,
            Segments = plan.Layers.SelectMany(l => l.Segments).Select(s => new
            { Kind = s.Kind.ToString(), s.Images, Contacts = s.Events, Reasons = s.Reasons.Select(r => r.Rule).ToArray() }).ToArray()
        };
    }

    private static EditableStrokeKey[] ReplayContacts(ReconstructionPlan plan) => plan.Layers.SelectMany(l => l.Segments)
        .Where(s => s.Kind == ReconstructionSegmentKind.Replay).SelectMany(s => s.Events).Distinct().ToArray();

    private static object[] Issues(IEnumerable<PlanIssue> issues) => issues.Select(i => (object)new
    { Code = i.Code.ToString(), i.Message, i.Image, i.Stroke }).ToArray();

    private static async Task<object> ValidateLegalPlan(ReconstructionPlan plan, EditSession session, ProbeAttempt attempt)
    {
        var replay = ReplayContacts(plan);
        var concentrated = WindowsCspEditingActions.BuildSchedules(session, plan.Request,
            replay.Select(k => new ReplayEvent(plan.Index.Strokes[k], plan.Request.Changes.GetValueOrDefault(k))).ToArray(),
            ReplayMode.Concentrated, "offline-probe-layer", "offline-probe-uuid");
        IReadOnlyList<EventReplaySchedule> discrete = plan.Request.Kind == EditKind.PreciseUndo
            ? WindowsCspEditingActions.BuildSchedules(session, plan.Request,
                plan.Request.Changes.Keys.Select(k => new ReplayEvent(plan.Index.Strokes[k], null)).ToArray(),
                ReplayMode.Discrete, "offline-probe-layer", "offline-probe-uuid")
            : [];
        object? reconstruction = null;
        string status = "AwaitingCapture";
        if (replay.Length == 0)
        {
            var executor = new NoCaptureExecutor();
            var result = await ReconstructionEngine.RunAsync(plan, executor, CancellationToken.None);
            status = result.Status is ReconstructionStatus.Failed or ReconstructionStatus.Cancelled ? "Blocked" : "OfflineReconstructed";
            reconstruction = new
            {
                Status = result.Status.ToString(), executor.CaptureCalls, executor.InstallCalls,
                Issues = Issues(result.Issues),
                Pixels = result.Layers.Select(l => new
                {
                    Layer = l.Key, ChangedPixels = ImageCompositor.Difference(l.Value, plan.Index.Layers[l.Key].Current!).Count,
                    RgbaSha256 = Convert.ToHexString(SHA256.HashData(l.Value.Bytes))
                }).ToArray()
            };
        }
        return new
        {
            Status = status, attempt.Contact, attempt.Image, attempt.CanvasRange, attempt.VisiblePixel,
            Plan = PlanSummary(plan, true, []),
            ConcentratedSchedules = concentrated.Select(ScheduleSummary).ToArray(),
            DiscreteSchedules = discrete.Select(ScheduleSummary).ToArray(),
            Reconstruction = reconstruction,
            Limitation = replay.Length == 0
                ? "Recorded patches supplied the offline reconstruction. The offline installer only copies its result. No CSP paste, pen injection, or undo stack is claimed."
                : "Selection, dependency plan and original native replay schedules are valid. A changed seed requires a new CSP brush capture; no capture or CSP execution has been simulated."
        };
    }

    private static object ScheduleSummary(EventReplaySchedule schedule) => new
    {
        schedule.Frequency, Contacts = schedule.Strokes.Select(s => new
        { s.EditableKey.SessionId, s.EditableKey.BeginAppendId, s.Stroke.OperationId, Samples = s.Stroke.Samples.Count }).ToArray()
    };

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class NoCaptureExecutor : ICspReconstructionExecutor
    {
        internal int CaptureCalls, InstallCalls;
        public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token)
        {
            CaptureCalls++;
            throw new InvalidOperationException("Offline probe refuses to synthesize a fresh CSP brush capture.");
        }
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
        { InstallCalls++; return Task.FromResult(expected.Copy()); }
    }

    private sealed class ProbeReport
    {
        public string Source { get; init; } = "";
        public string? SourceHash { get; set; }
        public bool SourceUnchanged { get; set; }
        public string? SourceVerificationError { get; set; }
        public int SessionCount { get; set; }
        public int RawPenBegins { get; set; }
        public int IndexedStrokes { get; set; }
        public int Images { get; set; }
        public int ContactsConsidered { get; set; }
        public object[] Layers { get; set; } = [];
        public List<ProbeAttempt> Attempts { get; } = [];
        public object? PreciseUndo { get; set; }
        public object? EventEditing { get; set; }
        public string? Error { get; set; }
        public string Scope { get; } = "Supplied immutable recording, exact visible point selection, real planner and native source validation. Reconstruction runs only when no fresh brush capture is needed. No CSP UI, paste or undo-stack interaction.";
    }

    private sealed class ProbeAttempt
    {
        public EditableStrokeKey Contact { get; init; }
        public ulong BeginEventId { get; init; }
        public DirtyImageKey Image { get; init; }
        public object? CanvasRange { get; init; }
        public object? VisiblePixel { get; init; }
        public int Extent { get; init; }
        public bool CanSelect { get; set; }
        public string? CandidateReason { get; set; }
        public object? PreciseUndo { get; set; }
        public object? EventEditing { get; set; }
        public string? Error { get; set; }
    }
}
