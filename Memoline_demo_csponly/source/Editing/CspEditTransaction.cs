using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using StrokeReplay;
using PacketReplay;

namespace MemolineDemo.Editing;

internal sealed record CspLayerEvidence(string Uuid, string Name, bool IsRaster, bool IsFolder, bool Visible, string? ParentUuid = null);
internal sealed record CspLayerInspection(string SessionId, long Generation, bool Saved, IReadOnlyList<CspLayerEvidence> Layers);
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
    // been too busy to apply the previous action when that save ran).
    Task<CspLayerInspection> SaveAndInspectAsync(CancellationToken token, bool refresh = false);
    Task NavigateToLayerAsync(string name, CancellationToken token);
    Task<CspCurrentLayerEvidence> ReadCurrentLayerAsync(CancellationToken token);
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
}

internal sealed class CspEditingException(PlanIssue issue, Exception? inner = null,
    IReadOnlyList<Exception>? cleanupErrors = null) : Exception(issue.Message, inner)
{
    internal PlanIssue Issue { get; } = issue;
    internal IReadOnlyList<Exception> CleanupErrors { get; } = cleanupErrors ?? [];
}

/// <summary>Evidence-checked request transaction; platform actions are injected and never inferred from names alone.</summary>
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
    private readonly HashSet<LayerKey> _hidden = [], _installed = [];
    private bool _skipHide;
    private readonly HashSet<LayerKey> _requiredFinal, _usedLayers = [];
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
        _skipHide = !_actions.CanHideLayer;
        foreach (var layer in _sizes.Keys) await OriginalAsync(layer, hide: false, ct);
        _begun = true; return true;
    }, token);

    public Task<ImageRgba> FlushAsync(LayerKey layer, ImageRgba seed, IReadOnlyList<ReplayEvent> events,
        IReadOnlyDictionary<LayerKey, ImageRgba> referenceLayers, CancellationToken token) => Guard(async ct =>
    {
        Active(); ValidatePixels(layer, seed);
        var replay = Ordered(events, layer);
        ValidateReferencePixels(layer, replay, referenceLayers);
        if (replay.Any(ReadsReferences))
            await _actions.VerifyReferenceSceneAsync(layer, replay, ReplayMode.Concentrated, referenceLayers, ct);
        var (original, before) = await OriginalAsync(layer, hide: true, ct);
        var work = await PasteAsync(layer, original, before, seed, ct);
        if (replay.Count > 0) await ReplayAsync(work, replay, ReplayMode.Concentrated, referenceLayers, ct);
        var captured = await CaptureAsync(layer, original, work, ct);
        EditJournalCheckpoint checkpoint;
        try
        {
            var asset = JournalCapture(captured);
            checkpoint = _journal.CheckpointFlushed(work.Uuid, asset, replay.Select(e => e.Stroke.Key).ToArray(), Overrides(replay));
        }
        catch (Exception error) { throw Error(PlanIssueCode.CaptureMismatch, "工作层检查点刷盘或读回失败。", error); }
        var proof = await InspectAsync(layer, ct);
        RequireLayer(proof, original.Uuid, original.Name, hidden: !_skipHide);
        var confirmed = RequireLayer(proof, work.Uuid, work.Name);
        await ConfirmAsync(proof, confirmed, ct);
        try
        {
            if (_journal.Snapshot.LastCheckpoint != checkpoint) throw new InvalidDataException("检查点回执改变。");
        }
        catch (Exception error) { throw Error(PlanIssueCode.CaptureMismatch, "删除前检查点及资产读回失败。", error); }
        ct.ThrowIfCancellationRequested();
        await _actions.DeleteLayerAsync(ct);
        var after = await InspectChangedAsync(layer, proof, ct);
        if (after.Layers.Any(l => l.Uuid == work.Uuid)) throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "删除后工作层 UUID 仍存在。");
        RequireLayer(after, original.Uuid, original.Name, hidden: !_skipHide);
        _journal.WorkLayerDeleted(checkpoint);
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
        var (original, before) = await OriginalAsync(layer, hide: true, ct);
        var final = await PasteAsync(layer, original, before, expected, ct);
        // Event replay is the last step, after the pasted state. The contacts the snapshot reverted come first,
        // concentrated (they only rebuild pixels); the undone strokes come last, discrete, so CSP's own undo
        // steps back through exactly them.
        var undone = _discrete.TryGetValue(layer, out var discrete) ? discrete.Select(e => e with { Change = null }).ToArray() : [];
        var undoneKeys = undone.Select(e => e.Stroke.Key).ToHashSet();
        var restored = finalReplay.Where(e => !undoneKeys.Contains(e.Stroke.Key)).DistinctBy(e => e.Stroke.Key).ToArray();
        foreach (var (batch, mode) in new[] { (restored, ReplayMode.Concentrated), (undone, ReplayMode.Discrete) }.Where(b => b.Item1.Length > 0))
        {
            var ordered = Ordered(batch, layer);
            if (ordered.Any(ReadsReferences))
                foreach (var drawing in ordered)
                    await ReplayAsync(final, [drawing], mode,
                        ReadsReferences(drawing) && _discreteReferences.ContainsKey(drawing.Stroke.Key) ? ReferencesFor(drawing) :
                        new Dictionary<LayerKey, ImageRgba>(), ct);
            else
                await ReplayAsync(final, ordered, mode, new Dictionary<LayerKey, ImageRgba>(), ct);
        }
        var captured = await CaptureAsync(layer, original, final, ct);
        var asset = JournalCapture(captured);
        _journal.ReproductionInstalled(final.Uuid, asset);
        await _actions.RegisterBaselineAsync(captured.Evidence, ct);
        _installed.Add(layer);
        return captured.Pixels;
    }, token);

    internal Task CompleteAsync(CancellationToken token) => Guard(async ct =>
    {
        Active();
        if (_installed.Count == 0 || _requiredFinal.Concat(_usedLayers).Any(layer=>!_installed.Contains(layer)))
            throw Error(PlanIssueCode.CaptureMismatch, "尚未安装全部受影响目标图层的最终复现层。");
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

    private async Task<(CspLayerEvidence Original, CspLayerInspection Inspection)> OriginalAsync(LayerKey layer, bool hide, CancellationToken ct)
    {
        if (!_sizes.ContainsKey(layer)) throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "图层不属于冻结的请求。");
        var inspection = await InspectAsync(layer, ct);
        var original = RequireLayer(inspection, layer.Uuid);
        await _actions.NavigateToLayerAsync(original.Name, ct);
        inspection = await InspectAsync(layer, ct); original = RequireLayer(inspection, original.Uuid, original.Name);
        await ConfirmAsync(inspection, original, ct);
        if (hide && !_skipHide && !_hidden.Contains(layer))
        {
            if (original.Visible)
            {
                var evidence = await _actions.HideLayerAsync(original, ct);
                if (evidence.UsedShortcut ? !evidence.ShortcutSemanticsConfirmed :
                    !evidence.SameFrame || !evidence.RowSelected || !SameName(evidence.RowName, original.Name) || !evidence.EyeClosed)
                    throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "隐藏图层没有已确认的快捷键语义或同帧眼睛行证据。");
            }
            inspection = await InspectAsync(layer, ct); original = RequireLayer(inspection, original.Uuid, original.Name, hidden: !_skipHide);
            await ConfirmAsync(inspection, original, ct);
            _journal.LayerHidden(original.Uuid); _hidden.Add(layer);
        }
        else if (hide) original = RequireLayer(inspection, original.Uuid, original.Name, hidden: !_skipHide);
        return (original, inspection);
    }

    private async Task<CspLayerEvidence> PasteAsync(LayerKey layer, CspLayerEvidence original, CspLayerInspection before,
        ImageRgba image, CancellationToken ct)
    {
        await _actions.SetClipboardImageAsync(image.Copy(), ct);
        ct.ThrowIfCancellationRequested(); await _actions.PasteAsync(ct);
        var after = await InspectChangedAsync(layer, before, ct);
        var oldIds = before.Layers.Select(l => l.Uuid).ToHashSet(StringComparer.Ordinal);
        var added = after.Layers.Where(l => !oldIds.Contains(l.Uuid)).ToArray();
        if (added.Length != 1 || !added[0].IsRaster || added[0].IsFolder ||
            added[0].ParentUuid != original.ParentUuid ||
            !after.Layers.Where(l => l.Uuid != added[0].Uuid).SequenceEqual(before.Layers) ||
            after.Layers.ToList().IndexOf(added[0]) != before.Layers.ToList().IndexOf(original))
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "粘贴前后必须恰好新增一个位于原层之上的非文件夹栅格层，其余结构保持一致。");
        var work = RequireLayer(after, added[0].Uuid, added[0].Name);
        RequireLayer(after, original.Uuid, original.Name, hidden: !_skipHide);
        await ConfirmAsync(after, work, ct);
        var captured = LayerCapture.Read(await _actions.ExportLayerAsync(layer with { Uuid = work.Uuid }, ct),
            layer with { Uuid = work.Uuid }, _sizes[layer]);
        if (!captured.Pixels.Bytes.AsSpan().SequenceEqual(image.Bytes)) throw Error(PlanIssueCode.PasteNotExact, "新层导出 RGBA 与粘贴的完整画布逐字节不相等。");
        return work;
    }

    private async Task<CapturedLayer> CaptureAsync(LayerKey layer, CspLayerEvidence original, CspLayerEvidence work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inspection = await InspectAsync(layer, ct);
        RequireLayer(inspection, original.Uuid, original.Name, hidden: !_skipHide);
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

    private async Task<CspLayerInspection> InspectAsync(LayerKey layer, CancellationToken ct, bool refresh = false)
    {
        ct.ThrowIfCancellationRequested(); var inspection = await _actions.SaveAndInspectAsync(ct, refresh);
        if (!inspection.Saved || inspection.SessionId != layer.SessionId || inspection.Generation != layer.Generation ||
            inspection.Layers.Any(l => string.IsNullOrWhiteSpace(l.Uuid) || string.IsNullOrWhiteSpace(l.Name)) ||
            inspection.Layers.Select(l => l.Uuid).Distinct(StringComparer.Ordinal).Count() != inspection.Layers.Count)
            throw Error(PlanIssueCode.LayerIdentityUnconfirmed, "保存后的图层结构身份未确认。");
        return inspection;
    }

    // CSP may still be busy with the paste/delete when the save shortcut arrives; the save then leaves the
    // file untouched and the snapshot is the old document. Save again while nothing has changed at all.
    private async Task<CspLayerInspection> InspectChangedAsync(LayerKey layer, CspLayerInspection before, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var after = await InspectAsync(layer, ct, refresh: attempt > 0);
            if (attempt >= 5 || !after.Layers.SequenceEqual(before.Layers)) return after;
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
