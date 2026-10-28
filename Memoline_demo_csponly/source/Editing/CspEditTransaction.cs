using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using StrokeReplay;
using PacketReplay;

namespace MemolineDemo.Editing;

internal sealed record CspLayerEvidence(string Uuid, string Name, bool IsRaster, bool IsFolder, bool Visible, string? ParentUuid = null);
// Revision identifies the saved document's bytes. CSP skips writing an unmodified document, so an
// unchanged revision after a paste means CSP never applied the paste (null when the source cannot tell).
internal sealed record CspLayerInspection(string SessionId, long Generation, bool Saved, IReadOnlyList<CspLayerEvidence> Layers,
    string? Revision = null);
internal sealed record CspCurrentLayerEvidence(string Name, bool Confirmed, string? Uuid = null);
internal sealed record CspHideEvidence(bool UsedShortcut, bool ShortcutSemanticsConfirmed, bool SameFrame,
    bool RowSelected, string RowName, bool EyeClosed);
internal sealed record PhysicalInputEvidence(string TransactionId, string DeviceType, ulong AppendId);

internal interface ICspEditingActions
{
    event Action<PhysicalInputEvidence>? PhysicalInput;
    bool CaptureRequestsPaused { get; set; }
    Task<ulong> BeginNativeTransactionAsync(string id, CancellationToken token);
    Task EndNativeTransactionAsync(string id, string outcome, CancellationToken token);
    Task<IAsyncDisposable> AcquireCaptureCoordinatorAsync(CancellationToken token);
    Task<bool> VerifyShortcutsAsync(CancellationToken token);
    // refresh: save again even when no input was sent since the last inspection (CSP may have
    // been too busy to apply the previous action when that save ran). expectChange: the document was modified
    // since the last save, so CSP must write it; a file that stays unchanged means CSP has not saved yet.
    Task<CspLayerInspection> SaveAndInspectAsync(CancellationToken token, bool refresh = false, bool expectChange = false);
    // Panel-confirmed navigation (reads the layer panel after every step); the fallback when a path crosses folders.
    Task NavigateToLayerAsync(string name, CancellationToken token);
    // One press of CSP's select-upper/lower-layer shortcut, unconfirmed.
    Task SelectAdjacentLayerAsync(bool upper, CancellationToken token);
    // The layer the latest save names as selected (the layer panel when the save names none).
    Task<CspCurrentLayerEvidence> ReadCurrentLayerAsync(CancellationToken token);
    // The layer panel as it is now. CSP does not write the document for a selection change alone, so after
    // moving the selection only the panel shows where it is.
    Task<CspCurrentLayerEvidence> ReadPanelCurrentLayerAsync(CancellationToken token) => ReadCurrentLayerAsync(token);
    // The selected layer the first inspection names is CSP's actual selection: the layer panel showed the same
    // layer when the edit boundary was built. A save written before a later selection change still names the
    // old layer, so without this the transaction never deletes before it has seen where a paste lands.
    bool SelectionConfirmed => false;
    Task<CspHideEvidence> HideLayerAsync(CspLayerEvidence layer, CancellationToken token);
    // False when CSP has no show/hide-layer shortcut: the original stays visible under the reproduction.
    bool CanHideLayer => true;
    Task SetClipboardImageAsync(ImageRgba image, CancellationToken token);
    Task PasteAsync(CancellationToken token);
    Task<LayerExportEvidence> ExportLayerAsync(LayerKey layer, CancellationToken token);
    Task VerifyReferenceSceneAsync(LayerKey layer, IReadOnlyList<ReplayEvent> events, ReplayMode mode,
        IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token);
    Task ReplayAsync(CspLayerEvidence work, IReadOnlyList<ReplayEvent> events, ReplayMode mode,
        IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken token);
    Task DeleteLayerAsync(CancellationToken token);
    Task RegisterBaselineAsync(LayerExportEvidence capture, CancellationToken token);
    void ReleaseInputs();
    void Status(string text) { }
    // The reproduction now stands for the deleted original; reported with the native editing action.
    void LayerReplaced(string replacedUuid, string reproductionUuid) { }
    // Precise undo may work on a layer other than the user's: select the user's layer again before the end.
    Task ReturnToUserLayerAsync(CancellationToken token) => Task.CompletedTask;
}

/// <summary>Plans select-upper/lower-layer presses from a saved structure (top row first) without reading the panel.</summary>
internal static class LayerNavigation
{
    /// <summary>Signed presses from one layer to another, negative going up; null when a row on the way is a
    /// folder (expanded or collapsed changes what a press reaches) or lies in another folder.</summary>
    internal static int? Steps(IReadOnlyList<CspLayerEvidence> layers, string fromUuid, string toUuid)
    {
        int from = Index(layers, fromUuid), to = Index(layers, toUuid);
        if (from < 0 || to < 0) return null;
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            if (layers[i].IsFolder || layers[i].ParentUuid != layers[from].ParentUuid) return null;
        return to - from;
    }
    internal static int Index(IReadOnlyList<CspLayerEvidence> layers, string uuid)
    {
        for (int i = 0; i < layers.Count; i++) if (layers[i].Uuid == uuid) return i;
        return -1;
    }
}

internal sealed class CspEditingException(PlanIssue issue, Exception? inner = null,
    IReadOnlyList<Exception>? cleanupErrors = null) : Exception(issue.Message, inner)
{
    internal PlanIssue Issue { get; } = issue;
    internal IReadOnlyList<Exception> CleanupErrors { get; } = cleanupErrors ?? [];
}

/// <summary>
/// Evidence-checked request transaction; platform actions are injected and never inferred from names alone.
/// Every target layer is replaced in place: CSP deletes the layer standing for it (the original, or the work layer
/// of an earlier replay) and pastes the next composited image where it was, so the layer count never grows and
/// no layer has to be hidden. The last pasted layer becomes the reproduction.
/// </summary>
internal sealed class CspEditTransaction : IEditingTransaction
{
    private readonly ICspEditingActions _actions;
    private readonly EditJournal _journal;
    private readonly string _id;
    private readonly Dictionary<LayerKey, CanvasSize> _sizes;
    private readonly JsonElement _plan;
    private readonly IReadOnlyDictionary<LayerKey, IReadOnlyList<ReplayEvent>> _discrete;
    private readonly IReadOnlyDictionary<EditableStrokeKey, IReadOnlyDictionary<LayerKey, ImageRgba>> _discreteReferences;
    private readonly CancellationTokenSource _physical = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HashSet<LayerKey> _installed = [], _deleted = [];
    private readonly HashSet<LayerKey> _requiredFinal, _usedLayers = [];
    // The CSP layer that stands for each target now: its original until the first replacement, then the last
    // pasted work layer with the capture and checkpoint that authorize deleting it.
    private sealed record Standing(CspLayerEvidence Layer, bool Original, CapturedLayer? Capture = null, EditJournalCheckpoint? Checkpoint = null);
    private readonly Dictionary<LayerKey, Standing> _standing = [];
    private readonly Dictionary<LayerKey, CspLayerEvidence> _originals = [];
    // The UUID CSP is known to have selected (null when unknown). A save that writes the document records the
    // selection; a selection change alone is never written, so moves are confirmed on the layer panel.
    private string? _selected;
    private string? _lastRevision;
    private readonly ConcurrentQueue<Exception> _cleanupErrors = new();
    private IAsyncDisposable? _coordinator;
    private bool _begun, _finished, _nativeAttempted, _nativeEnded, _pauseChanged, _priorPause, _journalStarted, _subscribed, _disposed;
    private int _segment;
    internal CspEditingException? LastFailure { get; private set; }

    internal CspEditTransaction(ICspEditingActions actions, EditJournal journal, string transactionId,
        IReadOnlyDictionary<LayerKey, CanvasSize> dimensions, JsonElement frozenPlan,
        IReadOnlyDictionary<LayerKey, IReadOnlyList<ReplayEvent>>? discreteReplay = null,
        IReadOnlySet<LayerKey>? requiredFinalLayers = null,
        IReadOnlyDictionary<EditableStrokeKey, IReadOnlyDictionary<LayerKey, ImageRgba>>? discreteReferenceLayers = null)
    {
        ArgumentNullException.ThrowIfNull(actions); ArgumentNullException.ThrowIfNull(journal);
        if (string.IsNullOrWhiteSpace(transactionId) || dimensions.Count == 0 || dimensions.Any(kv =>
            string.IsNullOrWhiteSpace(kv.Key.SessionId) || string.IsNullOrWhiteSpace(kv.Key.Uuid) || kv.Value.Width <= 0 || kv.Value.Height <= 0))
            throw new ArgumentException("事务身份、图层或画布尺寸无效。");
        _actions = actions; _journal = journal; _id = transactionId; _sizes = dimensions.ToDictionary(); _plan = frozenPlan.Clone();
        _discrete = discreteReplay?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ReplayEvent>)kv.Value.ToArray())
            ?? new Dictionary<LayerKey, IReadOnlyList<ReplayEvent>>();
        _discreteReferences = discreteReferenceLayers?.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyDictionary<LayerKey, ImageRgba>)kv.Value.ToDictionary(p => p.Key, p => p.Value.Copy()))
            ?? new Dictionary<EditableStrokeKey, IReadOnlyDictionary<LayerKey, ImageRgba>>();
        _requiredFinal=(requiredFinalLayers??_sizes.Keys.ToHashSet()).ToHashSet();
        if(_requiredFinal.Count==0||_requiredFinal.Any(layer=>!_sizes.ContainsKey(layer)))throw new ArgumentException("必须安装的复现层不属于事务目标。");
    }

    internal Task BeginAsync(CancellationToken token) => Guard(async ct =>
    {
        if (_begun || _finished) throw Error(PlanIssueCode.CaptureMismatch, "事务已开始或已结束。");
        foreach (var (layer, events) in _discrete)
            foreach (var drawing in Ordered(events.Select(e => e with { Change = null }).ToArray(), layer))
                if (ReadsReferences(drawing))
                {
                    var references = ReferencesFor(drawing);
                    ValidateReferencePixels(layer, [drawing], references);
                    await _actions.VerifyReferenceSceneAsync(layer, [drawing], ReplayMode.Discrete, references, ct);
                }
        _actions.PhysicalInput += PhysicalInput; _subscribed = true;
        _priorPause = _actions.CaptureRequestsPaused;
        _nativeAttempted = true;
        if (await _actions.BeginNativeTransactionAsync(_id, ct) == 0) throw Error(PlanIssueCode.CaptureMismatch, "原生事务没有有效 appendId 确认。");
        _pauseChanged = true; _actions.CaptureRequestsPaused = true;
        _coordinator = await _actions.AcquireCaptureCoordinatorAsync(ct);
        _journal.Start(_id, _sizes.Keys.ToArray()); _journalStarted = true;
        _journal.FreezePlan(_plan);
        if (!await _actions.VerifyShortcutsAsync(ct)) throw Error(PlanIssueCode.ShortcutsMissing, "CSP 编辑快捷键缺失或有冲突，请在设置页检查。");
        // One inspection identifies every target; nothing is selected yet, each replacement selects its own layer.
        var inspection = await InspectAsync(_sizes.Keys.First(), ct);
        foreach (var layer in _sizes.Keys)
        {
            var original = RequireLayer(inspection, layer.Uuid);
            _originals[layer] = original; _standing[layer] = new(original, true);
        }
        var current = await _actions.ReadCurrentLayerAsync(ct);
        _selected = _actions.SelectionConfirmed && current is { Confirmed: true, Uuid: { } uuid } ? uuid : null;
        _begun = true; return true;
    }, token);

    public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
        IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token) => Guard(async ct =>
    {
        Active(); ValidatePixels(layer, seed);
        if (_installed.Contains(layer)) throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "复现层安装后不能再重放该图层。");
        var replay = Ordered(events, layer);
        ValidateReferencePixels(layer, replay, referenceLayers);
        if (replay.Any(ReadsReferences))
            await _actions.VerifyReferenceSceneAsync(layer, replay, ReplayMode.Concentrated, referenceLayers, ct);
        var (work, pasted) = await ReplaceAsync(layer, seed, ct);
        if (replay.Count > 0) await ReplayAsync(work, replay, ReplayMode.Concentrated, referenceLayers, ct);
        var captured = replay.Count > 0 ? await CaptureAsync(layer, work, ct) : pasted;
        EditJournalCheckpoint checkpoint;
        try
        {
            var asset = JournalCapture(captured);
            checkpoint = _journal.CheckpointFlushed(work.Uuid, asset, replay.Select(e => e.Stroke.Key).ToArray(), Overrides(replay));
        }
        catch (Exception error) { throw Error(PlanIssueCode.CaptureMismatch, "工作层检查点刷盘或读回失败。", error); }
        // The work layer now stands for the target; the next replacement deletes it under this checkpoint.
        _standing[layer] = new(work, false, captured, checkpoint);
        _usedLayers.Add(layer);
        return captured.Pixels;
    }, token);

    public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected, CancellationToken token)
        => InstallReproductionLayerAsync(layer, expected, [], token);

    public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer, ImageRgba expected,
        IReadOnlyList<ReplayEvent> finalReplay, CancellationToken token) => Guard(async ct =>
    {
        Active(); ValidatePixels(layer, expected);
        if (_installed.Contains(layer)) throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "同一原图层不能重复安装复现层。");
        // Event replay is the last step, after the pasted state. The contacts the snapshot reverted come first; the
        // undone strokes come last, so CSP's own undo steps back through exactly them. Both are discrete: each
        // stroke stays its own CSP undo step, as it was when the user drew it.
        var undone = _discrete.TryGetValue(layer, out var discrete) ? discrete.Select(e => e with { Change = null }).ToArray() : [];
        var undoneKeys = undone.Select(e => e.Stroke.Key).ToHashSet();
        var restored = finalReplay.Where(e => !undoneKeys.Contains(e.Stroke.Key)).DistinctBy(e => e.Stroke.Key).ToArray();
        CspLayerEvidence final;
        CapturedLayer captured;
        EditJournalAsset asset;
        if (_standing[layer] is { Original: false, Capture: { } replayed, Checkpoint: { } checkpoint } standing &&
            undone.Length == 0 && restored.Length == 0 && replayed.Pixels.SamePixels(expected))
        {
            // The last replay already produced the final pixels on its work layer: it becomes the reproduction.
            final = standing.Layer; captured = replayed; asset = checkpoint.Asset;
        }
        else
        {
            var (work, pasted) = await ReplaceAsync(layer, expected, ct);
            final = work;
            int replays = 0;
            foreach (var (batch, mode) in new[] { (restored, ReplayMode.Discrete), (undone, ReplayMode.Discrete) }.Where(b => b.Item1.Length > 0))
            {
                var ordered = Ordered(batch, layer);
                replays++;
                if (ordered.Any(ReadsReferences))
                    foreach (var drawing in ordered)
                        await ReplayAsync(final, [drawing], mode,
                            ReadsReferences(drawing) && _discreteReferences.ContainsKey(drawing.Stroke.Key) ? ReferencesFor(drawing) :
                            new Dictionary<LayerKey, ImageRgba>(), ct);
                else
                    await ReplayAsync(final, ordered, mode, new Dictionary<LayerKey, ImageRgba>(), ct);
            }
            // Without a replay the paste's own verified export is the final capture.
            captured = replays > 0 ? await CaptureAsync(layer, final, ct) : pasted;
            asset = JournalCapture(captured);
        }
        _journal.ReproductionInstalled(final.Uuid, asset);
        _actions.LayerReplaced(_originals[layer].Uuid, final.Uuid);
        await _actions.RegisterBaselineAsync(captured.Evidence, ct);
        _standing[layer] = new(final, false, captured);
        _installed.Add(layer);
        return captured.Pixels;
    }, token);

    internal Task CompleteAsync(CancellationToken token) => Guard(async ct =>
    {
        Active();
        if (_installed.Count == 0 || _requiredFinal.Concat(_usedLayers).Any(layer=>!_installed.Contains(layer)))
            throw Error(PlanIssueCode.CaptureMismatch, "尚未安装全部受影响目标图层的最终复现层。");
        await _actions.ReturnToUserLayerAsync(ct);
        _actions.ReleaseInputs();
        await _actions.EndNativeTransactionAsync(_id, "executed", ct); _nativeEnded = true;
        await RestoreAsync();
        if (_cleanupErrors.Count > 0) throw Error(PlanIssueCode.CaptureMismatch, "事务结束时恢复捕获失败。");
        _journal.Finish(EditJournalOutcome.Executed); _finished = true;
        return true;
    }, token);

    internal async Task AbortAsync(PlanIssue issue) => await AbortCore(issue, null, cancelled: false);

    Task IEditingTransaction.BeginAsync(CancellationToken token)=>BeginAsync(token);
    Task IEditingTransaction.CompleteAsync(CancellationToken token)=>CompleteAsync(token);
    Task IEditingTransaction.AbortAsync(PlanIssue issue)=>AbortAsync(issue);

    internal const int PasteAttempts = 2;

    /// <summary>
    /// Replaces the layer standing for <paramref name="layer"/> with a pasted copy of <paramref name="image"/> at the
    /// same place, and returns the pasted layer with its verified export. CSP pastes above the selected layer and,
    /// after deleting the selected layer, selects the row below it; so when that row is another plain layer of the
    /// same folder the standing layer is deleted first and the paste lands exactly where it was, proven by one
    /// save. On the bottom row of a folder the image is pasted above it first and the standing layer is deleted
    /// only after that save showed where the paste went. Either way the standing layer must be known to be
    /// selected first: from this transaction's own actions, or confirmed on the layer panel.
    /// </summary>
    private async Task<(CspLayerEvidence Work, CapturedLayer Pasted)> ReplaceAsync(LayerKey layer, ImageRgba image, CancellationToken ct)
    {
        var standing = _standing[layer];
        var before = await InspectAsync(layer, ct);
        var target = RequireLayer(before, standing.Layer.Uuid, standing.Layer.Name);
        if (_selected != target.Uuid) await SelectAsync(before, target, ct);
        int index = LayerNavigation.Index(before.Layers, target.Uuid);
        bool deleteFirst = index + 1 < before.Layers.Count &&
            LayerNavigation.Steps(before.Layers, target.Uuid, before.Layers[index + 1].Uuid) == 1;
        var (work, after) = deleteFirst ? await DeleteThenPasteAsync(layer, standing, target, before, image, ct)
            : await PasteThenDeleteAsync(layer, standing, target, before, image, ct);
        var key = layer with { Uuid = work.Uuid };
        var pasted = LayerCapture.Read(await _actions.ExportLayerAsync(key, ct), key, _sizes[layer]);
        if (!pasted.Pixels.Bytes.AsSpan().SequenceEqual(image.Bytes))
            throw Error(PlanIssueCode.PasteNotExact, "新层导出 RGBA 与粘贴的完整画布逐字节不相等。");
        _standing[layer] = new(work, false);
        return (work, pasted);
    }

    // Selects a layer that is not known to be selected: planned presses from a known selection, panel-confirmed
    // navigation otherwise. Either way the layer panel, not a save, proves where the selection is.
    private async Task SelectAsync(CspLayerInspection inspection, CspLayerEvidence target, CancellationToken ct)
    {
        string? from = _selected;
        if (from is null && await _actions.ReadPanelCurrentLayerAsync(ct) is { Confirmed: true, Uuid: { } shown }) from = shown;
        if (from == target.Uuid) { _selected = from; return; }
        if (from is not null && LayerNavigation.Steps(inspection.Layers, from, target.Uuid) is { } steps)
        {
            for (int i = 0; i < Math.Abs(steps); i++) await _actions.SelectAdjacentLayerAsync(upper: steps < 0, ct);
            if (await SettledPanelLayerAsync(target.Uuid, ct) is { Confirmed: true } moved && moved.Uuid == target.Uuid)
            { _selected = target.Uuid; return; }
        }
        _selected = null;
        await _actions.NavigateToLayerAsync(target.Name, ct);
        var current = await _actions.ReadPanelCurrentLayerAsync(ct);
        if (!current.Confirmed || current.Uuid is { } uuid && uuid != target.Uuid || !SameName(current.Name, target.Name))
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"未能在图层面板确认已选中「{target.Name}」，已停止，未删除任何图层。");
        _selected = target.Uuid;
    }

    // The panel can show a selection after the presses that made it (CSP busy after a save, 2026-10-24: it still showed
    // the first of two presses, and stepping on from that reading overshot). Another reading follows until one shows
    // the target or two in a row agree.
    private async Task<CspCurrentLayerEvidence> SettledPanelLayerAsync(string expected, CancellationToken ct)
    {
        var reading = await _actions.ReadPanelCurrentLayerAsync(ct);
        for (int attempt = 0; attempt < 3 && reading.Uuid != expected; attempt++)
        {
            var again = await _actions.ReadPanelCurrentLayerAsync(ct);
            if (again.Uuid == reading.Uuid && SameName(again.Name, reading.Name)) return again;
            reading = again;
        }
        return reading;
    }

    private async Task<(CspLayerEvidence Work, CspLayerInspection After)> DeleteThenPasteAsync(LayerKey layer, Standing standing,
        CspLayerEvidence target, CspLayerInspection before, ImageRgba image, CancellationToken ct)
    {
        int index = LayerNavigation.Index(before.Layers, target.Uuid);
        // The clipboard is written before anything is deleted: a clipboard failure leaves CSP untouched.
        await _actions.SetClipboardImageAsync(image, ct);
        ct.ThrowIfCancellationRequested();
        RecordDeletion(standing, null);
        await _actions.DeleteLayerAsync(ct);
        if (standing.Original) _deleted.Add(layer);
        _selected = before.Layers[index + 1].Uuid;
        var remaining = before.Layers.Where(l => l.Uuid != target.Uuid).ToArray();
        string Order(CspLayerInspection inspection) => string.Join(" / ", inspection.Layers.Select(l => l.Name));
        CspLayerInspection after;
        for (int attempt = 1; ; attempt++)
        {
            await _actions.PasteAsync(ct);
            after = await InspectChangedAsync(layer, before, ct);
            if (after.Layers.SequenceEqual(before.Layers))
                // Nothing changed over every save: CSP ignored both the delete and the paste (a dialog, a focused field).
                throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "CSP 没有执行删除和粘贴：保存后的图层结构没有任何变化。请关闭 CSP 中打开的对话框、让画布获得焦点后重试；" +
                    "如果 CSP 当前打开的不是 Memoline 记录的那个 .clip 文件，保存也写不到它，请先在 Memoline 中选对文件。");
            // Deleted, nothing pasted: CSP dropped the paste. The clipboard is written again (another program may
            // have replaced it) and pasted once more; the deletion already happened and is not repeated.
            if (after.Layers.SequenceEqual(remaining) && attempt < PasteAttempts)
            {
                _actions.Status($"CSP 未执行粘贴，重新写入剪贴板后再次粘贴（{attempt + 1}/{PasteAttempts}）…");
                await _actions.SetClipboardImageAsync(image, ct);
                continue;
            }
            break;
        }
        var gone = before.Layers.Where(l => after.Layers.All(a => a.Uuid != l.Uuid)).ToArray();
        var added = after.Layers.Where(l => before.Layers.All(b => b.Uuid != l.Uuid)).ToArray();
        if (gone is not [var deleted] || deleted.Uuid != target.Uuid)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"删除的不是「{target.Name}」，而是「{string.Join("、", gone.Select(l => l.Name))}」。" +
                $"请在 CSP 中按 Ctrl+Z 撤销这次粘贴和删除。删除前：{Order(before)}；之后：{Order(after)}。");
        if (added.Length == 0)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"已删除「{target.Name}」，但 CSP 连续 {PasteAttempts} 次没有执行粘贴。" +
                "该图层已存入回收箱，可在 CSP 中按 Ctrl+Z 恢复。");
        if (added.Length != 1)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴后新增了 {added.Length} 个图层（应为 1 个）。之前：{Order(before)}；之后：{Order(after)}。");
        if (!added[0].IsRaster || added[0].IsFolder)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴新增的「{added[0].Name}」不是普通栅格层。");
        if (added[0].ParentUuid != target.ParentUuid || LayerNavigation.Index(after.Layers, added[0].Uuid) != index)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴新增的「{added[0].Name}」没有落在已删除的「{target.Name}」原来的位置。" +
                $"请在 CSP 中按两次 Ctrl+Z 撤销这次粘贴和删除。之后：{Order(after)}。");
        if (!after.Layers.Where(l => l.Uuid != added[0].Uuid).SequenceEqual(remaining))
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"替换期间其他图层的结构或显示状态发生了变化。之前：{Order(before)}；之后：{Order(after)}。");
        var work = RequireLayer(after, added[0].Uuid, added[0].Name);
        await ConfirmAsync(after, work, ct);
        _selected = work.Uuid;
        return (work, after);
    }

    private async Task<(CspLayerEvidence Work, CspLayerInspection After)> PasteThenDeleteAsync(LayerKey layer, Standing standing,
        CspLayerEvidence target, CspLayerInspection before, ImageRgba image, CancellationToken ct)
    {
        CspLayerInspection pasted;
        for (int attempt = 1; ; attempt++)
        {
            // The clipboard is written again on a retry: another program may have replaced it.
            await _actions.SetClipboardImageAsync(image, ct);
            ct.ThrowIfCancellationRequested(); await _actions.PasteAsync(ct);
            pasted = await InspectChangedAsync(layer, before, ct);
            if (!pasted.Layers.SequenceEqual(before.Layers)) break;
            // Nothing changed over every save of InspectChangedAsync: CSP ignored the paste (a dialog, a
            // focused panel field). The document is untouched, so pasting again cannot duplicate a layer.
            bool unwritten = pasted.Revision is not null && pasted.Revision == before.Revision;
            if (attempt < PasteAttempts) { _actions.Status($"CSP 未执行粘贴{(unwritten ? "（文档未被修改）" : "")}，重新激活 CSP 后再次粘贴（{attempt + 1}/{PasteAttempts}）…"); continue; }
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"CSP 连续 {PasteAttempts} 次没有执行粘贴：保存后的图层结构没有任何变化" +
                (unwritten ? "，文档也未被写入" : "") + "。请关闭 CSP 中打开的对话框、让画布获得焦点后重试。");
        }
        var added = pasted.Layers.Where(l => before.Layers.All(b => b.Uuid != l.Uuid)).ToArray();
        string Order(CspLayerInspection inspection) => string.Join(" / ", inspection.Layers.Select(l => l.Name));
        if (added.Length != 1)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴后新增了 {added.Length} 个图层（应为 1 个）。粘贴前：{Order(before)}；粘贴后：{Order(pasted)}。");
        if (!added[0].IsRaster || added[0].IsFolder)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴新增的「{added[0].Name}」不是普通栅格层。");
        if (added[0].ParentUuid != target.ParentUuid || LayerNavigation.Index(pasted.Layers, added[0].Uuid) != LayerNavigation.Index(before.Layers, target.Uuid))
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴新增的「{added[0].Name}」没有紧贴在「{target.Name}」之上（粘贴时 CSP 当前层不是它）。粘贴后：{Order(pasted)}。");
        if (!pasted.Layers.Where(l => l.Uuid != added[0].Uuid).SequenceEqual(before.Layers))
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"粘贴期间其他图层的结构或显示状态发生了变化。粘贴前：{Order(before)}；粘贴后：{Order(pasted)}。");
        var work = RequireLayer(pasted, added[0].Uuid, added[0].Name);
        RequireLayer(pasted, target.Uuid, target.Name);
        await ConfirmAsync(pasted, work, ct);
        _selected = work.Uuid;
        // The paste is proven exact before anything is deleted.
        var key = layer with { Uuid = work.Uuid };
        if (!LayerCapture.Read(await _actions.ExportLayerAsync(key, ct), key, _sizes[layer]).Pixels.Bytes.AsSpan().SequenceEqual(image.Bytes))
            throw Error(PlanIssueCode.PasteNotExact, "新层导出 RGBA 与粘贴的完整画布逐字节不相等。");
        return (work, await DeleteBelowAsync(layer, standing, target, work, pasted, ct));
    }

    /// <summary>
    /// Runs on the structure alone, then proves it with one save. The paste's save showed the selected
    /// work layer directly above the target in the same folder, so one "select lower layer" reaches it.
    /// After the delete CSP selects the row that takes the deleted one (the layer below), or the one
    /// above when the target was the bottom row; one "select upper layer" from that row is planned too. A
    /// wrong prediction is only a selection: it is corrected on the layer panel before anything is drawn.
    /// </summary>
    private async Task<CspLayerInspection> DeleteBelowAsync(LayerKey layer, Standing standing, CspLayerEvidence target, CspLayerEvidence work,
        CspLayerInspection pasted, CancellationToken ct)
    {
        int index = LayerNavigation.Index(pasted.Layers, target.Uuid);
        if (LayerNavigation.Steps(pasted.Layers, work.Uuid, target.Uuid) != 1)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"新层「{work.Name}」不在「{target.Name}」正上方，已停止，未删除任何图层。");
        await _actions.SelectAdjacentLayerAsync(upper: false, ct);
        ct.ThrowIfCancellationRequested();
        RecordDeletion(standing, work.Uuid);
        await _actions.DeleteLayerAsync(ct);
        if (standing.Original) _deleted.Add(layer);
        var remaining = pasted.Layers.Where(l => l.Uuid != target.Uuid).ToArray();
        _selected = null;
        if (index + 1 < pasted.Layers.Count && LayerNavigation.Steps(remaining, pasted.Layers[index + 1].Uuid, work.Uuid) == -1)
            await _actions.SelectAdjacentLayerAsync(upper: true, ct);
        var after = await InspectChangedAsync(layer, pasted, ct);
        string Order(CspLayerInspection inspection) => string.Join(" / ", inspection.Layers.Select(l => l.Name));
        var missing = pasted.Layers.Where(l => after.Layers.All(a => a.Uuid != l.Uuid)).ToArray();
        if (missing.Length == 0)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"已删除的「{target.Name}」仍在 CSP 中。");
        if (missing is not [var gone] || gone.Uuid != target.Uuid)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"删除的不是「{target.Name}」，而是「{string.Join("、", missing.Select(l => l.Name))}」：" +
                $"选择它的快捷键没有按预期生效。请在 CSP 中按一次 Ctrl+Z 撤销这次删除。删除后：{Order(after)}。");
        if (!after.Layers.SequenceEqual(remaining))
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"删除期间其他图层的结构或显示状态发生了变化。删除前：{Order(pasted)}；删除后：{Order(after)}。");
        RequireLayer(after, work.Uuid, work.Name);
        // The delete was written, so this save names CSP's actual selection.
        var current = await _actions.ReadCurrentLayerAsync(ct);
        if (current is { Confirmed: true } && current.Uuid == work.Uuid) _selected = work.Uuid;
        else
        {
            _actions.Status($"删除后当前层是「{current.Name}」，按图层结构切回新层「{work.Name}」…");
            _selected = current is { Confirmed: true, Uuid: { } uuid } ? uuid : null;
            await SelectAsync(after, work, ct);
        }
        return after;
    }

    private void RecordDeletion(Standing standing, string? replacement)
    {
        if (standing.Original) { _journal.OriginalDeleted(standing.Layer.Uuid, replacement); return; }
        var checkpoint = standing.Checkpoint ?? throw Error(PlanIssueCode.CaptureMismatch, "工作层缺少可恢复的检查点，不能删除。");
        try
        {
            if (_journal.Snapshot.LastCheckpoint != checkpoint) throw new InvalidDataException("检查点回执改变。");
        }
        catch (Exception error) { throw Error(PlanIssueCode.CaptureMismatch, "删除前检查点及资产读回失败。", error); }
        _journal.WorkLayerDeleted(checkpoint);
    }

    private async Task<CapturedLayer> CaptureAsync(LayerKey layer, CspLayerEvidence work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The replay drew into the document, so CSP writes it.
        var inspection = await InspectAsync(layer, ct, expectChange: true);
        var replaced = _originals[layer];
        // CSP may still be busy with the delete when the save arrives (see InspectChangedAsync).
        for (int attempt = 1; _deleted.Contains(layer) && inspection.Layers.Any(l => l.Uuid == replaced.Uuid); attempt++)
        {
            if (attempt > 5) throw Error(PlanIssueCode.LayerIdentityUnconfirmed, $"已删除的原层「{replaced.Name}」仍在 CSP 中。");
            await Task.Delay(300, ct); inspection = await InspectAsync(layer, ct, refresh: true);
        }
        await ConfirmAsync(inspection, RequireLayer(inspection, work.Uuid, work.Name), ct);
        var key = layer with { Uuid = work.Uuid };
        return LayerCapture.Read(await _actions.ExportLayerAsync(key, ct), key, _sizes[layer]);
    }

    private async Task ReplayAsync(CspLayerEvidence work, IReadOnlyList<ReplayEvent> events, ReplayMode mode,
        IReadOnlyDictionary<LayerKey, ImageRgba> references, CancellationToken ct)
    {
        string segment = "replay-" + ++_segment;
        _journal.StartSegment(segment, EditJournalOperation.Replay);
        await _actions.ReplayAsync(work, events, mode, references, ct);
        ct.ThrowIfCancellationRequested();
        _journal.Replay(segment, events.Select(e => e.Stroke.Key).ToArray(), Overrides(events));
    }

    private EditJournalAsset JournalCapture(CapturedLayer capture)
    {
        string segment = "capture-" + ++_segment;
        _journal.StartSegment(segment, EditJournalOperation.Capture);
        return _journal.Capture(segment, capture.Evidence.Layer.Uuid, capture.Evidence.Png, capture.Pixels.Width, capture.Pixels.Height);
    }

    private async Task<CspLayerInspection> InspectAsync(LayerKey layer, CancellationToken ct, bool refresh = false, bool expectChange = false)
    {
        ct.ThrowIfCancellationRequested(); var inspection = await _actions.SaveAndInspectAsync(ct, refresh, expectChange);
        if (!inspection.Saved || inspection.SessionId != layer.SessionId || inspection.Generation != layer.Generation ||
            inspection.Layers.Any(l => string.IsNullOrWhiteSpace(l.Uuid) || string.IsNullOrWhiteSpace(l.Name)) ||
            inspection.Layers.Select(l => l.Uuid).Distinct(StringComparer.Ordinal).Count() != inspection.Layers.Count)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "保存后的图层结构身份未确认。");
        // A save that wrote the document records CSP's selection as it is now.
        if (inspection.Revision is { } revision && revision != _lastRevision)
        {
            if (_lastRevision is not null && await _actions.ReadCurrentLayerAsync(ct) is { Confirmed: true, Uuid: { } selected }) _selected = selected;
            _lastRevision = revision;
        }
        return inspection;
    }

    // CSP may still be busy with the paste/delete when the save shortcut arrives; the save then leaves the
    // file untouched and the snapshot is the old document. Save again while nothing has changed at all.
    private async Task<CspLayerInspection> InspectChangedAsync(LayerKey layer, CspLayerInspection before, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            // Each save waits for CSP's write (expectChange), so a document still unchanged after three saves was
            // not modified at all; the old six attempts only covered saves copied before CSP had written.
            var after = await InspectAsync(layer, ct, refresh: attempt > 0, expectChange: true);
            if (attempt >= 2 || !after.Layers.SequenceEqual(before.Layers)) return after;
            await Task.Delay(300, ct);
        }
    }

    private static CspLayerEvidence RequireLayer(CspLayerInspection inspection, string uuid, string? name = null, bool hidden = false)
    {
        var matches = inspection.Layers.Where(l => l.Uuid == uuid).ToArray();
        if (matches.Length != 1 || !matches[0].IsRaster || matches[0].IsFolder || name is not null && !SameName(matches[0].Name, name) ||
            inspection.Layers.Count(l => SameName(l.Name, matches[0].Name)) != 1 || hidden && matches[0].Visible)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "目标 UUID、唯一图层名称、栅格类型或隐藏状态无法确认。");
        return matches[0];
    }

    private async Task ConfirmAsync(CspLayerInspection inspection, CspLayerEvidence expected, CancellationToken ct)
    {
        var current = await _actions.ReadCurrentLayerAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!current.Confirmed || !SameName(current.Name, expected.Name) || current.Uuid is not null && current.Uuid != expected.Uuid ||
            inspection.Layers.Count(l => SameName(l.Name, current.Name)) != 1)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "当前层 OCR 名称及唯一 UUID 与目标层不一致。");
    }

    private void ValidatePixels(LayerKey layer, ImageRgba pixels)
    {
        pixels.Validate();
        if (!_sizes.TryGetValue(layer, out var size) || size.Width != pixels.Width || size.Height != pixels.Height)
            throw Error(PlanIssueCode.CaptureMismatch, "粘贴图像与冻结的画布尺寸不符。");
    }
    private static bool ReadsReferences(ReplayEvent drawing) => drawing.RequiresReferenceScene;
    private IReadOnlyDictionary<LayerKey, ImageRgba> ReferencesFor(ReplayEvent drawing)
        => _discreteReferences.TryGetValue(drawing.Stroke.Key, out var references) ? references :
            throw Error(PlanIssueCode.RuntimeClosureViolation, "精确撤销缺少该笔历史时点的参考图层场景，不能使用空场景或边界未来像素。");
    private void ValidateReferencePixels(LayerKey layer, IReadOnlyList<ReplayEvent> events,
        IReadOnlyDictionary<LayerKey, ImageRgba> references)
    {
        if (!events.Any(ReadsReferences)) return;
        if (!references.ContainsKey(layer))
            throw Error(PlanIssueCode.RuntimeClosureViolation, "历史参考场景缺少当前工作层的时点，无法确认引用身份。");
        foreach (var (key, image) in references)
        {
            if (key.SessionId != layer.SessionId || key.Generation != layer.Generation || !_sizes.ContainsKey(key))
                throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "历史参考图层不属于同一会话、画布 generation 或冻结的事务图层。");
            ValidatePixels(key, image);
        }
    }
    private void Active() { if (!_begun || _finished) throw Error(PlanIssueCode.CaptureMismatch, "编辑事务未开始或已经结束。"); }
    private static bool SameName(string left, string right) => string.Equals(left.Trim().Normalize(NormalizationForm.FormKC), right.Trim().Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase);
    private static CspEditingException Error(PlanIssueCode code, string message, Exception? inner = null) => new(new(code, message), inner);
    private static EditJournalOverride[] Overrides(IReadOnlyList<ReplayEvent> events)
        => events.Where(e => e.Change is not null).Select(e => new EditJournalOverride(e.Stroke.Key, e.Change!)).ToArray();
    private static IReadOnlyList<ReplayEvent> Ordered(IReadOnlyList<ReplayEvent> events, LayerKey layer)
    {
        if (events.Any(e => !e.Stroke.Eligible || e.Stroke.Layer != layer || e.Change?.Delete == true) || events.Select(e => e.Stroke.Key).Distinct().Count() != events.Count)
            throw Error(PlanIssueCode.UnreplayableEventInMatrix, "重放段必须包含该原层的完整、合格且未删除的唯一事件。");
        return events.OrderBy(e => e.Stroke.SessionOrder).ThenBy(e => e.Stroke.BeginEventId).ThenBy(e => e.Stroke.Key.BeginAppendId)
            .Select(e => e with { Stroke = e.Stroke with { Samples = e.Stroke.Samples.OrderBy(s => s.AppendId).ToArray() } }).ToArray();
    }

    private void PhysicalInput(PhysicalInputEvidence input)
    {
        if (_finished || input.TransactionId != _id) return;
        _physical.Cancel();
        try { _actions.ReleaseInputs(); } catch (Exception error) { _cleanupErrors.Enqueue(error); }
    }

    private async Task<T> Guard<T>(Func<CancellationToken, Task<T>> action, CancellationToken caller)
    {
        bool held = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _physical.Token, _shutdown.Token);
        try
        {
            await _gate.WaitAsync(linked.Token); held = true;
            var result = await action(linked.Token); linked.Token.ThrowIfCancellationRequested(); return result;
        }
        catch (Exception error)
        {
            var issue = _physical.IsCancellationRequested ? new PlanIssue(PlanIssueCode.PhysicalInputDuringTransaction, "事务检测到物理输入，已停止并保留图层。") :
                (error as CspEditingException)?.Issue ??
                (error is ReplayStateException replay && Enum.TryParse<PlanIssueCode>(replay.Code,out var replayCode)
                    ? new(replayCode,replay.Message) : new(PlanIssueCode.CaptureMismatch, "CSP 编辑事务失败：" + error.Message));
            bool externalCancelled = error is OperationCanceledException && caller.IsCancellationRequested && !_physical.IsCancellationRequested;
            if (!_finished) await AbortCore(issue, error, externalCancelled);
            if (externalCancelled) throw;
            throw LastFailure ?? new CspEditingException(issue, error, _cleanupErrors.ToArray());
        }
        finally { if (held) _gate.Release(); }
    }

    private async Task AbortCore(PlanIssue issue, Exception? primary, bool cancelled)
    {
        if (_finished) return;
        _finished = true;
        _shutdown.Cancel();
        try { _actions.ReleaseInputs(); } catch (Exception error) { _cleanupErrors.Enqueue(error); }
        if (_nativeAttempted && !_nativeEnded)
            try { await _actions.EndNativeTransactionAsync(_id, "failed", CancellationToken.None); _nativeEnded = true; }
            catch (Exception error) { _cleanupErrors.Enqueue(error); }
        await RestoreAsync();
        // An original deleted before the failure is not restored by Memoline: CSP's undo and the recycle bin hold it.
        if (_deleted.Any(layer => !_installed.Contains(layer)))
            try
            {
                issue = issue with { Message = issue.Message + "（原图层已在替换时删除并存入回收箱：可在 CSP 中按 Ctrl+Z 撤回本次修改，或从回收箱粘贴回原图层。）" };
                _actions.Status(issue.Message);
            }
            catch (Exception error) { _cleanupErrors.Enqueue(error); }
        if (_journalStarted)
        {
            try { _journal.Fail(issue.Code.ToString(), issue.Message); } catch (Exception error) { _cleanupErrors.Enqueue(error); }
            try { _journal.Finish(cancelled ? EditJournalOutcome.Cancelled : EditJournalOutcome.Failed); } catch (Exception error) { _cleanupErrors.Enqueue(error); }
        }
        LastFailure = new(issue, primary, _cleanupErrors.ToArray());
    }

    private async Task RestoreAsync()
    {
        if (_subscribed) { _actions.PhysicalInput -= PhysicalInput; _subscribed = false; }
        if (_pauseChanged)
        {
            try { _actions.CaptureRequestsPaused = _priorPause; _pauseChanged = false; }
            catch (Exception error) { _cleanupErrors.Enqueue(error); }
        }
        if (_coordinator is { } lease)
        {
            _coordinator = null;
            try { await lease.DisposeAsync(); } catch (Exception error) { _cleanupErrors.Enqueue(error); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_finished) await AbortCore(new(PlanIssueCode.CaptureMismatch, "编辑事务未完成即被释放。"), null, cancelled: false);
        _physical.Dispose(); _shutdown.Dispose(); _gate.Dispose();
    }
}
