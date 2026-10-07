using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Storage.Memoline;

namespace BehaviorRecognizer.Capture;

public sealed partial class UnifiedInputCapture
{
    private sealed record PendingEditBoundary(string RequestId, CancellationToken Cancellation,
        TaskCompletionSource<MemolineEvent> Completion);
    private readonly List<PendingEditBoundary> _editBoundaries = [];
    private readonly Dictionary<string, MemolineEvent> _completedBoundaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemolineEvent> _completedTransactions = new(StringComparer.Ordinal);
    private string? _editTransactionId;
    private MemolineEvent? _transactionBegin;
    private RecorderInputControlStatus? _previousInterception;
    private IRecorderInputControl? _transactionControl;

    public bool EditTransactionActive { get { lock (_sync) return _editTransactionId is not null; } }

    private void EnqueueControl(Func<Task> action)
    {
        if (!_pending.Writer.TryWrite(new CaptureWork(null, 0, 0, 0, 0, 0, 0, false, false, false, Control: action)))
            throw new ObjectDisposedException(nameof(UnifiedInputCapture));
    }

    private Task<MemolineEvent> RunControlAsync(Func<Task<MemolineEvent>> action, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<MemolineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueControl(async () =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                completion.TrySetResult(await action());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellation); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        // Once a transaction command starts, return its definite outcome rather than
        // cancelling only the waiter while changing the recorder policy invisibly.
        return completion.Task;
    }

    public Task<MemolineEvent> PrepareEditBoundaryAsync(string requestId, CancellationToken cancellation = default)
    {
        ValidateEditIdentity(requestId);
        var completion = new TaskCompletionSource<MemolineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueControl(() =>
        {
            lock (_sync)
            {
                if (cancellation.IsCancellationRequested) completion.TrySetCanceled(cancellation);
                else if (_editTransactionId is not null)
                    completion.TrySetException(new InvalidOperationException("An edit transaction is active."));
                else if (_completedBoundaries.TryGetValue(requestId, out var previous)) completion.TrySetResult(previous);
                else _editBoundaries.Add(new(requestId, cancellation, completion));
            }
            return Task.CompletedTask;
        });
        return completion.Task.WaitAsync(cancellation);
    }

    private void CompleteEditBoundaries()
    {
        lock (_sync)
        {
            foreach (var pending in _editBoundaries.Where(p => p.Cancellation.IsCancellationRequested).ToArray())
            {
                pending.Completion.TrySetCanceled(pending.Cancellation);
                _editBoundaries.Remove(pending);
            }
            if (_penOperation is not null || _penPhysicalDown) return;
            foreach (var pending in _editBoundaries)
            {
                if (!_completedBoundaries.TryGetValue(pending.RequestId, out var boundary))
                {
                    boundary = _writer.AppendImmediateState("editBoundaryRequested", new { requestId = pending.RequestId });
                    _completedBoundaries.Add(pending.RequestId, boundary);
                }
                pending.Completion.TrySetResult(boundary);
            }
            _editBoundaries.Clear();
        }
    }

    private void CancelEditBoundaries()
    {
        lock (_sync)
        {
            foreach (var pending in _editBoundaries)
                pending.Completion.TrySetException(new ObjectDisposedException(nameof(UnifiedInputCapture)));
            _editBoundaries.Clear();
        }
    }

    public Task<MemolineEvent> BeginEditTransactionAsync(string transactionId, IRecorderInputControl control,
        CancellationToken cancellation = default)
    {
        ValidateEditIdentity(transactionId);
        return RunControlAsync(async () =>
        {
            lock (_sync)
            {
                if (_editTransactionId == transactionId)
                {
                    return _transactionBegin!;
                }
                if (_editTransactionId is not null) throw new InvalidOperationException("Another edit transaction is active.");
                if (_completedTransactions.ContainsKey(transactionId)) throw new InvalidOperationException("Transaction identity has already ended.");
                if (_penOperation is not null || _penPhysicalDown)
                    throw new InvalidOperationException("Wait for the current pen contact to end before editing.");
            }
            var previous = control is LayerSaveGuard guard ? guard.ClaimEditProtection(transactionId) : control.GetInputControlStatus();
            try
            {
                var configured = control is LayerSaveGuard ownedGuard
                    ? await ownedGuard.ConfigureEditInterceptionAsync(transactionId, new() { Enabled = true }, cancellation)
                    : await control.ConfigureInputInterceptionAsync(new() { Enabled = true }, cancellation);
                if (!configured.Success || !configured.Enabled)
                    throw new IOException(configured.Error ?? "Unable to enable edit input protection.");
                lock (_sync)
                {
                    var began = _writer.AppendImmediateState("editTransactionBegan", new { transactionId });
                    _previousInterception = previous;
                    _transactionControl = control;
                    _editTransactionId = transactionId;
                    _transactionBegin = began;
                    return began;
                }
            }
            catch
            {
                try { await RestoreInterceptionAsync(control, previous, transactionId); }
                finally { if (control is LayerSaveGuard ownedGuard) ownedGuard.ReleaseEditProtection(transactionId); }
                throw;
            }
        }, cancellation);
    }

    public Task<MemolineEvent> EndEditTransactionAsync(string transactionId, string outcome, IRecorderInputControl control,
        CancellationToken cancellation = default)
    {
        ValidateEditIdentity(transactionId);
        if (string.IsNullOrWhiteSpace(outcome) || outcome.Length > 128) throw new ArgumentException("A nonempty outcome is required.");
        return RunControlAsync(async () =>
        {
            RecorderInputControlStatus previous;
            MemolineEvent ended;
            lock (_sync)
            {
                if (_completedTransactions.TryGetValue(transactionId, out var completed)) return completed;
                if (_editTransactionId != transactionId || !ReferenceEquals(control, _transactionControl))
                    throw new InvalidOperationException("Transaction identity does not match the active edit transaction.");
                previous = _previousInterception!;
            }
            // Restoration runs on the same input consumer. Keep the native interval
            // open on failure so intervening physical input cannot become editable.
            await RestoreInterceptionAsync(control, previous, transactionId);
            lock (_sync)
            {
                ended = _writer.AppendImmediateState("editTransactionEnded", new { transactionId, outcome });
                if (control is LayerSaveGuard ownedGuard) ownedGuard.ReleaseEditProtection(transactionId);
                _editTransactionId = null;
                _transactionBegin = null;
                _previousInterception = null;
                _transactionControl = null;
                _completedTransactions.Add(transactionId, ended);
            }
            return ended;
        }, cancellation);
    }

    private static async Task RestoreInterceptionAsync(IRecorderInputControl control, RecorderInputControlStatus previous, string owner)
    {
        var restored = control is LayerSaveGuard guard
            ? previous.Controlled
                ? await guard.ConfigureEditInterceptionAsync(owner, new() { Enabled = previous.Enabled }, CancellationToken.None)
                : guard.RestoreAutomaticEditProtection(owner)
            : previous.Controlled
            ? await control.ConfigureInputInterceptionAsync(new() { Enabled = previous.Enabled }, CancellationToken.None)
            : control.RestoreAutomaticInputProtection();
        if (!restored.Success || restored.Controlled != previous.Controlled || restored.Enabled != previous.Enabled)
            throw new IOException(restored.Error ?? "Unable to restore recorder input protection.");
    }

    private Task DrainPendingInputAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueControl(() => { drained.TrySetResult(); return Task.CompletedTask; });
        return drained.Task;
    }

    private Task EndActiveEditTransactionAsync()
    {
        lock (_sync)
            return _editTransactionId is { } identity && _transactionControl is { } control
                ? EndEditTransactionAsync(identity, "failed", control) : Task.CompletedTask;
    }

    private void ReportPhysicalInput(string deviceType, bool injected)
    {
        if (_editTransactionId is null || injected) return;
        _writer.AppendImmediateState("physicalInputDuringTransaction", new
        { transactionId = _editTransactionId, deviceType, appendId = _inputAppendId,
            inputRecorded = _inputAppendId != 0, latestAppendId = _writer.LastAppendId });
    }

    private static void ValidateEditIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 128)
            throw new ArgumentException("Editing identity must be nonempty and at most 128 characters.");
    }
}
