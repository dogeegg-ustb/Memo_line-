using System.Text.Json;
using DirtyMatrixPaster;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal static class CspEditTransactionChecks
{
    private static readonly LayerKey Original = new("transaction-fixture", 3, "original");
    private static ImageRgba Seed() => new(2, 1, new byte[] { 19, 83, 211, 0, 29, 79, 199, 1 });
    private static ReplayEvent Stroke(EventChange? change = null)
        => new(new(new(Original.SessionId, 8), 2, 10, 20,
            [new(20, 1, 0, 0, 0, 0, false, 10, 3), new(10, 0, 0, 1, 0, 0, true, 8, 2)],
            "Fixture pen", Original, false) { BeginEventId = 2 }, change);

    internal static async Task<int> RunAsync(string? root = null)
    {
        int n = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); n++; }
        var historicalBrush = new BrushSnapshot("Original pen", [new("brush_size", 8), new("opacity", 75)], 2);
        var replacementEvidence = JsonSerializer.SerializeToElement(new { status = "changed", state = new { name = "Replacement pen", properties = new[] {
            new { key = "brush_size", value = 30d, type = "number", status = "ok", enabled = "enabled" },
            new { key = "flow", value = 55d, type = "number", status = "ok", enabled = "enabled" } } } });
        var changedBrush = WindowsCspEditingActions.BuildOverride(historicalBrush,
            new EventChange(BrushName: "Replacement pen", Numbers: new Dictionary<string, double> { ["brush_size"] = 12 }) { ReplacementBrushState = replacementEvidence });
        Check(changedBrush is { Name: "Replacement pen" } && changedBrush.Numbers.Select(p => p.Value).SequenceEqual(new double[] { 12, 55 }) &&
              changedBrush.Numbers.Select(p => p.Key).SequenceEqual(new[] { "brush_size", "flow" }),
            "A subtool change uses the complete confirmed target snapshot, applies shared numeric overrides and keeps target-only properties: " + JsonSerializer.Serialize(changedBrush));
        Check(historicalBrush.Name == "Original pen" && historicalBrush.Numbers[0].Value == 8 &&
              WindowsCspEditingActions.BuildOverride(historicalBrush, null) is null,
            "Overrides never mutate historical parameters or leak into an unchanged following event.");
        try { WindowsCspEditingActions.BuildOverride(historicalBrush, new EventChange(BrushName: "Replacement pen"));
            throw new InvalidOperationException("An unconfirmed replacement brush was accepted."); }
        catch (ReplayStateException error) { Check(error.Code == "UnsupportedOverride", "A changed subtool requires its confirmed full target snapshot."); }
        JsonElement Reference(string value,string status="ok",string enabled="enabled",string name="Fixture pen",string envelope="changed",string type="checkbox")
            =>JsonSerializer.SerializeToElement(new {status=envelope,state=new {name,properties=new[]{new {key="anti_overflow.do_not_cross_lines_of_reference_layer",value,status,enabled,type}}}});
        var referenceOff=Reference("unchecked");
        var offDrawing=Stroke() with {Stroke=Stroke().Stroke with {BrushName="Fixture pen",BrushState=referenceOff,
            Classification=new(true,true,[],false,[]){ReadsReferenceLayers=true}}};
        Check(offDrawing.ReadsReferenceLayers&&!offDrawing.RequiresReferenceScene,
            "Explicit off evidence preserves causal classification while avoiding an unnecessary reference scene.");
        Check(WindowsCspEditingActions.ReferenceControlsAreOff(referenceOff,"Fixture pen"),
            "Explicitly unchecked reference controls are safe instead of being rejected merely for property presence.");
        Check(!WindowsCspEditingActions.ReferenceControlsAreOff(Reference("checked"),"Fixture pen"),"Checked reference controls need a real historical reference scene.");
        Check(!WindowsCspEditingActions.ReferenceControlsAreOff(Reference("unknown"),"Fixture pen")&&
              !WindowsCspEditingActions.ReferenceControlsAreOff(Reference("unchecked",status:"unknown"),"Fixture pen")&&
              !WindowsCspEditingActions.ReferenceControlsAreOff(Reference("unchecked",envelope:"unknown"),"Fixture pen"),
            "Unknown checkbox values, property status and state envelopes never prove reference controls off.");
        Check(!WindowsCspEditingActions.ReferenceControlsAreOff(Reference("checked",enabled:"disabled"),"Fixture pen")&&
              !WindowsCspEditingActions.ReferenceControlsAreOff(Reference("",enabled:"disabled"),"Fixture pen"),
            "A disabled property alone does not mean unchecked.");
        Check(!WindowsCspEditingActions.ReferenceControlsAreOff(Reference("unchecked",name:"Other pen"),"Fixture pen")&&
              !WindowsCspEditingActions.ReferenceControlsAreOff(Reference("0",type:"number"),"Fixture pen"),
            "Off evidence must describe the intended subtool and an actual checkbox, never a numeric companion.");
        WindowsCspEditingActions.VerifyReferenceControlsOff(referenceOff,referenceOff,"Fixture pen"); n++;
        try {WindowsCspEditingActions.VerifyReferenceControlsOff(referenceOff,Reference("checked"),"Fixture pen");throw new InvalidOperationException("Historical off / live on was accepted.");}
        catch(ReplayStateException error){Check(error.Code=="BrushUnrestorable","Historical off with current on fails before drawing.");}
        var twoControls=JsonSerializer.SerializeToElement(new {status="changed",state=new {name="Fixture pen",properties=new[]{
            new {key="anti_overflow.do_not_cross_lines_of_reference_layer",value="unchecked",status="ok",type="checkbox"},
            new {key="anti_overflow.fill_up_to_vector_path",value="unchecked",status="ok",type="checkbox"}}}});
        try {WindowsCspEditingActions.VerifyReferenceControlsOff(twoControls,referenceOff,"Fixture pen");throw new InvalidOperationException("A missing current control was accepted.");}
        catch(ReplayStateException error){Check(error.Code=="BrushUnrestorable","A partial current panel cannot drop a historical reference-control row.");}
        var targetOnlyOverride=WindowsCspEditingActions.BuildOverride(historicalBrush,
            new EventChange(BrushName:"Replacement pen",Numbers:new Dictionary<string,double>{["flow"]=61}){ReplacementBrushState=replacementEvidence});
        Check(targetOnlyOverride!.Numbers.Single(p=>p.Key=="flow").Value==61,"A confirmed changed subtool can override its own target-only numeric property.");
        string work = Path.Combine(root ?? Path.GetTempPath(), "csp-edit-transaction-" + Guid.NewGuid().ToString("N"));
        Fixture Create(string name, bool discrete = false, bool twoLayers = false)
            => new(Path.Combine(work, name), discrete, twoLayers);
        async Task<CspEditingException> Fails(Fixture f, PlanIssueCode expected, Func<Task> action)
        {
            try { await action(); }
            catch (CspEditingException error)
            {
                Check(error.Issue.Code == expected, "Failure code: " + error.Issue);
                Check(!f.Actions.Calls.Contains("delete"), "Failure must preserve all unknown or uncommitted layers.");
                Check(f.Actions.Calls.Contains("release") && f.Actions.Calls.Contains("end:failed"), "Failure must release inputs and end native transaction as failed.");
                Check(!f.Actions.CaptureRequestsPaused && f.Actions.Calls.Contains("coordinator:release"), "Failure must restore capture pause and release its coordinator.");
                return error;
            }
            throw new InvalidOperationException("Expected transaction failure " + expected);
        }

        await using (var f = Create("normal"))
        {
            await f.Transaction.BeginAsync(CancellationToken.None);
            var edited = Stroke(new(Numbers: new Dictionary<string, double> { ["opacity"] = .5 }));
            var actual = await f.Transaction.FlushAsync(Original, Seed(), [edited], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None);
            Check(actual.Bytes[0] == 90 && f.Actions.Layers.Count == 1 && !f.Actions.Layers[0].Visible,
                "Flush captures actual replay output, removes only checkpointed work layer and leaves original hidden.");
            Check(f.Actions.Calls.IndexOf("export:2") < f.Actions.Calls.IndexOf("delete") && f.Actions.DeletionHadDurableCheckpoint,
                "Durable recoverable checkpoint and captured PNG must exist before deleting T.");
            Check(f.Actions.Replays[0].Mode == ReplayMode.Concentrated && f.Actions.Replays[0].Events[0].Change == edited.Change &&
                  f.Actions.Replays[0].Events[0].Stroke.Samples.Select(s => s.AppendId).SequenceEqual(new ulong[] { 8, 10 }),
                "Flush passes concentrated replay overrides and full samples in appendId order.");
            var final = await f.Transaction.InstallReproductionLayerAsync(Original, actual, CancellationToken.None);
            await f.Transaction.CompleteAsync(CancellationToken.None);
            Check(final.Bytes.AsSpan().SequenceEqual(actual.Bytes) && f.Actions.Layers.Count == 2 && f.Actions.Baselines.Count == 1,
                "Final F is retained and its actual exported PNG is registered as a baseline.");
            var journal = EditJournal.Recover(f.Directory);
            Check(journal.ActiveTransactionId is null && journal.HasReplayed && journal.Records.Last().Data.GetProperty("outcome").GetString() == "Executed",
                "One transaction finishes with durable replay evidence and Executed outcome.");
            Check(journal.Records.FindKind("checkpoint") < journal.Records.FindKind("workLayerDeleted") &&
                  journal.Records.FindKind("reproductionInstalled") < journal.Records.FindKind("transactionEnded"),
                "Journal actions follow checkpoint/delete and final/end ordering.");
            Check(f.Actions.Calls.IndexOf("begin") < f.Actions.Calls.IndexOf("pause:True") &&
                  f.Actions.Calls.IndexOf("end:executed") < f.Actions.Calls.LastIndexOf("pause:False"),
                "Native begin/pause and end/restore ordering matches the transaction protocol.");
        }

        await using (var f = Create("discrete", discrete: true))
        {
            await f.Transaction.BeginAsync(CancellationToken.None);
            var final = await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None);
            await f.Transaction.CompleteAsync(CancellationToken.None);
            Check(f.Actions.Replays.Single().Mode == ReplayMode.Discrete && f.Actions.Replays.Single().Events.All(e => e.Change is null),
                "Precise undo replays original parameters discretely on final F.");
            Check(final.Bytes[0] == 90 && f.Actions.Calls.All(c => c != "delete") && f.Actions.Baselines.Single().Png.SequenceEqual(f.Actions.LastExport!.Png),
                "Discrete final capture, not the pre-replay seed, is retained and registered.");
        }
        await using (var f = Create("discrete-reverted-first", discrete: true))
        {
            // The reverted contact is newer than the undone stroke, yet it replays first: the undone strokes
            // must be the last replay so CSP's own undo steps back through exactly them.
            var reverted = Stroke() with { Stroke = Stroke().Stroke with { Key = new(Original.SessionId, 30), BeginEventId = 5 } };
            await f.Transaction.BeginAsync(CancellationToken.None);
            await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), [reverted], CancellationToken.None);
            await f.Transaction.CompleteAsync(CancellationToken.None);
            Check(f.Actions.Replays.Count == 2 && f.Actions.Replays[0].Mode == ReplayMode.Concentrated && f.Actions.Replays[1].Mode == ReplayMode.Discrete &&
                  f.Actions.Replays[0].Events.Single().Stroke.BeginEventId == 5 && f.Actions.Replays[1].Events.Single().Stroke.BeginEventId == 2,
                "Precise undo replays reverted contacts first (concentrated) and the undone strokes last (discrete).");
        }
        await using(var f=Create("discrete-reference-off"))
        {
            await using var transaction=new CspEditTransaction(f.Actions,f.Journal,"tx",f.Dimensions,
                JsonSerializer.SerializeToElement(new {kind="off-reference"}),
                new Dictionary<LayerKey,IReadOnlyList<ReplayEvent>>{[Original]=[offDrawing]},new HashSet<LayerKey>{Original});
            await transaction.BeginAsync(default);await transaction.InstallReproductionLayerAsync(Original,Seed(),default);await transaction.CompleteAsync(default);
            Check(f.Actions.Replays.Count==1&&f.Actions.ReferenceScenes.Single().Count==0,
                "Precise undo of explicitly off controls requires no fake or future reference scene.");
        }
        await using (var f = Create("discrete-reference-missing", discrete: true, twoLayers: true))
        {
            var drawing = Stroke() with { Stroke = Stroke().Stroke with { Classification = new(true, true, [], false, []) { ReadsReferenceLayers = true } } };
            await using var transaction = new CspEditTransaction(f.Actions, f.Journal, "tx", f.Dimensions,
                JsonSerializer.SerializeToElement(new { kind = "reference-fixture" }),
                new Dictionary<LayerKey, IReadOnlyList<ReplayEvent>> { [Original] = [drawing] }, new HashSet<LayerKey> { Original });
            try { await transaction.BeginAsync(CancellationToken.None); throw new InvalidOperationException("Missing references were accepted."); }
            catch (CspEditingException error) { Check(error.Issue.Code == PlanIssueCode.RuntimeClosureViolation,"Missing discrete reference scene reports its specific issue."); }
            Check(!f.Actions.Calls.Contains("begin") && !f.Actions.Calls.Contains("paste"),
                "Missing per-event historical reference pixels stop before the native transaction or CSP mutations.");
        }
        await using (var f = Create("discrete-reference-route", twoLayers: true))
        {
            var other = Original with { Uuid = "other" };
            var drawing = Stroke() with { Stroke = Stroke().Stroke with { Classification = new(true, true, [], false, []) { ReadsReferenceLayers = true } } };
            var scene = new Dictionary<LayerKey, ImageRgba> { [Original] = Seed(), [other] = Seed() };
            await using var transaction = new CspEditTransaction(f.Actions, f.Journal, "tx", f.Dimensions,
                JsonSerializer.SerializeToElement(new { kind = "reference-fixture" }),
                new Dictionary<LayerKey, IReadOnlyList<ReplayEvent>> { [Original] = [drawing] }, new HashSet<LayerKey> { Original },
                new Dictionary<EditableStrokeKey, IReadOnlyDictionary<LayerKey, ImageRgba>> { [drawing.Stroke.Key] = scene });
            await transaction.BeginAsync(CancellationToken.None);
            await transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None);
            await transaction.CompleteAsync(CancellationToken.None);
            Check(f.Actions.ReferenceScenes.Count == 1 && f.Actions.ReferenceScenes[0][other].Bytes.SequenceEqual(scene[other].Bytes),
                "Discrete replay receives the selected event's historical reference pixels instead of an empty or boundary scene.");
        }
        await using (var f = Create("multi-layer", twoLayers: true))
        {
            await f.Transaction.BeginAsync(CancellationToken.None);
            foreach (var layer in f.Dimensions.Keys) await f.Transaction.InstallReproductionLayerAsync(layer, Seed(), CancellationToken.None);
            await f.Transaction.CompleteAsync(CancellationToken.None);
            Check(f.Actions.Baselines.Count == 2 && f.Actions.Calls.Count(c => c == "begin") == 1 && f.Actions.Calls.Count(c => c == "end:executed") == 1,
                "A multi-layer request holds one native transaction and records both hidden original UUIDs.");
        }
        await using(var f=Create("multi-layer-incomplete",twoLayers:true))
        {
            await f.Transaction.BeginAsync(CancellationToken.None);
            await f.Transaction.InstallReproductionLayerAsync(Original,Seed(),CancellationToken.None);
            await Fails(f,PlanIssueCode.CaptureMismatch,()=>f.Transaction.CompleteAsync(CancellationToken.None));
            Check(!f.Actions.Calls.Contains("end:executed"),"Cannot complete while a required target layer has no final reproduction.");
        }

        await using (var f = Create("paste-tamper"))
        {
            f.Actions.TamperPaste = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.PasteNotExact, async () => await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None));
            Check(f.Actions.Replays.Count == 0 && f.Actions.Layers.Count == 2, "Tampered paste cannot replay and the uncommitted T remains.");
            int calls = f.Actions.Calls.Count;
            try { await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None); }
            catch (CspEditingException) { }
            Check(f.Actions.Calls.Count == calls, "A failed transaction cannot send more CSP actions on a later method call.");
        }
        await using (var f = Create("stale-save"))
        {
            f.Actions.StaleSaves = 2;
            await f.Transaction.BeginAsync(CancellationToken.None);
            var actual = await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None);
            await f.Transaction.InstallReproductionLayerAsync(Original, actual, CancellationToken.None);
            await f.Transaction.CompleteAsync(CancellationToken.None);
            Check(f.Actions.Layers.Count == 2 && f.Actions.Calls.Contains("end:executed"),
                "A save that still shows the document before a paste or delete is retried instead of failing the structure check.");
        }
        await using (var f = Create("paste-multiple"))
        {
            f.Actions.NewLayersOnPaste = 2;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.LayerIdentityUnconfirmed, async () => await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None));
            Check(!f.Actions.Calls.Any(c => c.StartsWith("export:", StringComparison.Ordinal)), "Ambiguous paste structure must stop before exporting an assumed T.");
        }
        await using (var f = Create("changed-current"))
        {
            f.Actions.ChangeCurrentAfterCheckpoint = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.LayerIdentityUnconfirmed, async () => await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None));
            Check(EditJournal.Recover(f.Directory).LastCheckpoint is not null && f.Actions.Layers.Count == 2,
                "Even a checkpointed T is retained when current UUID changes before deletion.");
        }
        await using (var f = Create("checkpoint-readback"))
        {
            f.Actions.CorruptAssetAfterCheckpoint = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.CaptureMismatch, async () => await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None));
            Check(f.Actions.CheckpointFaultTriggered && f.Actions.Layers.Count == 2, "A checkpoint whose asset fails readback cannot authorize deletion.");
        }
        await using (var f = Create("physical-input"))
        {
            f.Actions.PhysicalDuringReplay = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.PhysicalInputDuringTransaction, async () => await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None));
            Check(f.Actions.Calls.Count(c => c.StartsWith("export:", StringComparison.Ordinal)) == 1,
                "Physical input cancels replay before post-replay capture or delete.");
        }
        await using (var f = Create("replay-specific-error"))
        {
            f.Actions.DuringReplay = () => throw new ReplayStateException("BrushUnrestorable", "Replacement brush OCR did not confirm.");
            await f.Transaction.BeginAsync(CancellationToken.None);
            var failure = await Fails(f, PlanIssueCode.BrushUnrestorable, async () =>
                await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), CancellationToken.None));
            Check(failure.InnerException is ReplayStateException && f.Actions.Layers.Count == 2,
                "Replay restoration failures preserve their exact error code, primary exception and uncommitted work layer.");
        }
        await using (var f = Create("cleanup-failure"))
        {
            f.Actions.TamperPaste = true;
            f.Actions.ThrowEnd = true;
            f.Actions.ThrowRelease = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            var error = await Fails(f, PlanIssueCode.PasteNotExact, async () => await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None));
            Check(error.CleanupErrors.Count >= 2, "Cleanup faults are retained without replacing the primary PasteNotExact failure.");
        }
        await using (var f = Create("external-cancel"))
        {
            using var cancelled = new CancellationTokenSource();
            f.Actions.DuringReplay = cancelled.Cancel;
            await f.Transaction.BeginAsync(CancellationToken.None);
            try { await f.Transaction.FlushAsync(Original, Seed(), [Stroke()], new Dictionary<LayerKey, ImageRgba>(), cancelled.Token); }
            catch (OperationCanceledException)
            {
                Check(!f.Actions.Calls.Contains("delete") && f.Actions.Calls.Contains("end:failed") && !f.Actions.CaptureRequestsPaused,
                    "External cancellation releases the transaction, restores capture and preserves T.");
                Check(EditJournal.Recover(f.Directory).Records.Last().Data.GetProperty("outcome").GetString() == "Cancelled",
                    "External cancellation remains Cancelled in the journal.");
                goto externalCancelled;
            }
            throw new InvalidOperationException("External cancellation did not propagate.");
            externalCancelled:;
        }
        await using (var f = Create("baseline-failure"))
        {
            f.Actions.ThrowBaseline = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.CaptureMismatch, async () => await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None));
            Check(f.Actions.Layers.Count == 2, "A final layer survives baseline-registration failure.");
        }
        await using (var f = Create("shortcuts"))
        {
            f.Actions.ShortcutsAvailable = false;
            await Fails(f, PlanIssueCode.ShortcutsMissing, () => f.Transaction.BeginAsync(CancellationToken.None));
            Check(!f.Actions.Calls.Any(c => c.StartsWith("navigate:", StringComparison.Ordinal)), "Missing shortcuts stop before navigation or paste.");
        }
        await using (var f = Create("hide-evidence"))
        {
            f.Actions.BadEyeEvidence = true;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.LayerIdentityUnconfirmed, async () => await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None));
            Check(!f.Actions.Calls.Contains("paste"), "Eye fallback without same-frame selected-row evidence cannot paste.");
        }
        await using (var f = Create("partial-export"))
        {
            f.Actions.FullCanvas = false;
            await f.Transaction.BeginAsync(CancellationToken.None);
            await Fails(f, PlanIssueCode.CaptureMismatch, async () => await f.Transaction.InstallReproductionLayerAsync(Original, Seed(), CancellationToken.None));
        }
        var png = ClipboardImageCodec.EncodePng(2, 1, Seed().Bytes);
        var evidence = new LayerExportEvidence(Original, 2, 1, true, png);
        Check(LayerCapture.Read(evidence, Original, new(2, 1)).Pixels.Bytes.AsSpan().SequenceEqual(Seed().Bytes),
            "Capture PNG decoding preserves alpha zero hidden RGB and alpha one exactly.");
        try { LayerCapture.Read(evidence with { Layer = Original with { Generation = 4 } }, Original, new(2, 1)); }
        catch (CspEditingException error) when (error.Issue.Code == PlanIssueCode.CaptureMismatch) { n++; goto identityChecked; }
        throw new InvalidOperationException("Capture accepted a generation mismatch.");
        identityChecked:
        Console.WriteLine($"CSP edit transaction checks passed: {n}.");
        return n;
    }

    private static int FindKind(this IReadOnlyList<EditJournalRecord> records, string kind)
        => records.ToList().FindIndex(r => r.Kind == kind);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Directory;
        internal readonly EditJournal Journal;
        internal readonly Fake Actions;
        internal readonly CspEditTransaction Transaction;
        internal readonly Dictionary<LayerKey, CanvasSize> Dimensions = new() { [Original] = new(2, 1) };
        internal Fixture(string directory, bool discrete, bool twoLayers)
        {
            Directory = directory; Journal = EditJournal.Open(directory); Actions = new(Journal);
            if (twoLayers)
            {
                var other = Original with { Uuid = "other" };
                Dimensions[other] = new(2, 1);
                Actions.Layers.Add(new(other.Uuid, "Other", true, false, true));
            }
            Transaction = new(Actions, Journal, "tx", Dimensions, JsonSerializer.SerializeToElement(new { kind = "fixture", layers = Dimensions.Keys }),
                discrete ? new Dictionary<LayerKey, IReadOnlyList<ReplayEvent>> { [Original] = [Stroke(new(Delete: true))] } : null);
        }
        public async ValueTask DisposeAsync() { await Transaction.DisposeAsync(); Journal.Dispose(); }
    }

    private sealed class Fake(EditJournal journal) : ICspEditingActions
    {
        internal readonly List<string> Calls = [];
        internal readonly List<CspLayerEvidence> Layers = [new(Original.Uuid, "Ink", true, false, true)];
        internal readonly Dictionary<string, ImageRgba> Images = [];
        internal readonly List<(ReplayMode Mode, IReadOnlyList<ReplayEvent> Events)> Replays = [];
        internal readonly List<IReadOnlyDictionary<LayerKey, ImageRgba>> ReferenceScenes = [];
        internal readonly List<LayerExportEvidence> Baselines = [];
        private bool _paused;
        private string _current = Original.Uuid;
        private ImageRgba? _clipboard;
        private int _pastes, _exports;
        internal bool TamperPaste, ChangeCurrentAfterCheckpoint, CorruptAssetAfterCheckpoint, CheckpointFaultTriggered;
        internal bool PhysicalDuringReplay, ThrowEnd, ThrowRelease, BadEyeEvidence, ThrowBaseline;
        internal Action? DuringReplay;
        internal bool ShortcutsAvailable = true, FullCanvas = true, DeletionHadDurableCheckpoint;
        internal int NewLayersOnPaste = 1, StaleSaves;
        private CspLayerEvidence[]? _stale;
        private int _staleLeft;
        internal LayerExportEvidence? LastExport;
        public event Action<PhysicalInputEvidence>? PhysicalInput;
        public bool CaptureRequestsPaused { get => _paused; set { Calls.Add("pause:" + value); _paused = value; } }
        public Task<ulong> BeginNativeTransactionAsync(string id, CancellationToken token) { Calls.Add("begin"); return Task.FromResult(1UL); }
        public Task EndNativeTransactionAsync(string id, string outcome, CancellationToken token)
        { Calls.Add("end:" + outcome); if (ThrowEnd) throw new IOException("Native cleanup fault"); return Task.CompletedTask; }
        public Task<IAsyncDisposable> AcquireCaptureCoordinatorAsync(CancellationToken token)
        { Calls.Add("coordinator:acquire"); return Task.FromResult<IAsyncDisposable>(new Lease(Calls)); }
        public Task<bool> VerifyShortcutsAsync(CancellationToken token) { Calls.Add("shortcuts"); return Task.FromResult(ShortcutsAvailable); }
        public Task<CspLayerInspection> SaveAndInspectAsync(CancellationToken token)
        {
            Calls.Add("save");
            var layers = _staleLeft-- > 0 ? _stale! : Layers.ToArray();
            return Task.FromResult(new CspLayerInspection(Original.SessionId, Original.Generation, true, layers));
        }
        // CSP still busy when the save arrives: the next saves return the document before the last paste/delete.
        private void Mutating() { _stale = Layers.ToArray(); _staleLeft = StaleSaves; }
        public Task NavigateToLayerAsync(string name, CancellationToken token)
        { Calls.Add("navigate:" + name); _current = Layers.Single(l => l.Name == name).Uuid; return Task.CompletedTask; }
        public Task<CspCurrentLayerEvidence> ReadCurrentLayerAsync(CancellationToken token)
        {
            Calls.Add("current");
            if (!CheckpointFaultTriggered && (ChangeCurrentAfterCheckpoint || CorruptAssetAfterCheckpoint) && journal.Snapshot.LastCheckpoint is { } checkpoint)
            {
                CheckpointFaultTriggered = true;
                if (ChangeCurrentAfterCheckpoint) _current = Original.Uuid;
                if (CorruptAssetAfterCheckpoint) File.WriteAllBytes(Path.Combine(journal.WorkingDirectory, checkpoint.Asset.RelativePath), [1, 2, 3]);
            }
            var layer = Layers.Single(l => l.Uuid == _current);
            return Task.FromResult(new CspCurrentLayerEvidence(layer.Name, true, layer.Uuid));
        }
        public Task<CspHideEvidence> HideLayerAsync(CspLayerEvidence layer, CancellationToken token)
        { Calls.Add("hide"); int i = Layers.FindIndex(l => l.Uuid == layer.Uuid); Layers[i] = layer with { Visible = false }; return Task.FromResult(new CspHideEvidence(false, false, !BadEyeEvidence, true, layer.Name, true)); }
        public Task SetClipboardImageAsync(ImageRgba image, CancellationToken token) { Calls.Add("clipboard"); _clipboard = image.Copy(); return Task.CompletedTask; }
        public Task PasteAsync(CancellationToken token)
        {
            Calls.Add("paste"); Mutating(); int originalIndex = Layers.FindIndex(l => l.Uuid == _current);
            for (int n = 0; n < NewLayersOnPaste; n++)
            {
                string uuid = "new-" + ++_pastes; var layer = new CspLayerEvidence(uuid, "Work " + _pastes, true, false, true);
                Layers.Insert(originalIndex, layer); Images[uuid] = _clipboard!.Copy();
                if (TamperPaste) Images[uuid].Bytes[0] ^= 1;
                _current = uuid;
            }
            return Task.CompletedTask;
        }
        public Task<LayerExportEvidence> ExportLayerAsync(LayerKey layer, CancellationToken token)
        { Calls.Add("export:" + ++_exports); var image = Images[layer.Uuid]; LastExport = new(layer, image.Width, image.Height, FullCanvas, ClipboardImageCodec.EncodePng(image.Width, image.Height, image.Bytes)); return Task.FromResult(LastExport); }
        public Task VerifyReferenceSceneAsync(LayerKey layer, IReadOnlyList<ReplayEvent> events, ReplayMode mode,
            IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("verify-reference:" + mode); return Task.CompletedTask; }
        public Task ReplayAsync(CspLayerEvidence work, IReadOnlyList<ReplayEvent> events, ReplayMode mode,
            IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token)
        {
            Calls.Add("replay:" + mode); Replays.Add((mode, events)); ReferenceScenes.Add(references);
            if (PhysicalDuringReplay) PhysicalInput?.Invoke(new("tx", "pen", 8));
            DuringReplay?.Invoke();
            token.ThrowIfCancellationRequested(); Images[work.Uuid].Bytes[0] = 90; return Task.CompletedTask;
        }
        public Task DeleteLayerAsync(CancellationToken token)
        {
            Calls.Add("delete"); Mutating(); DeletionHadDurableCheckpoint = journal.Snapshot.LastCheckpoint?.WorkLayerUuid == _current;
            Layers.RemoveAll(l => l.Uuid == _current); _current = Original.Uuid; return Task.CompletedTask;
        }
        public Task RegisterBaselineAsync(LayerExportEvidence capture, CancellationToken token)
        { Calls.Add("baseline"); if (ThrowBaseline) throw new IOException("Baseline failed"); Baselines.Add(capture); return Task.CompletedTask; }
        public void ReleaseInputs() { Calls.Add("release"); if (ThrowRelease) throw new IOException("Release cleanup fault"); }
        private sealed class Lease(List<string> calls) : IAsyncDisposable
        { public ValueTask DisposeAsync() { calls.Add("coordinator:release"); return ValueTask.CompletedTask; } }
    }
}
