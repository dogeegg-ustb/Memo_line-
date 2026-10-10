namespace MemolineDemo.Editing;

internal interface IEditingTransaction:ICspReconstructionExecutor,IAsyncDisposable
{
    Task BeginAsync(CancellationToken token);
    Task CompleteAsync(CancellationToken token);
    Task AbortAsync(PlanIssue issue);
}
internal enum EditingStatus { PlanUnconfirmed, Executing, Failed, Executed, PixelVerified, Cancelled }

internal sealed class EditingCoordinator
{
    private readonly SemaphoreSlim _serial=new(1,1);
    internal EditingStatus Status{get;private set;}=EditingStatus.PlanUnconfirmed;
    internal event Action<EditingStatus>? StatusChanged;
    private void Set(EditingStatus status){Status=status;StatusChanged?.Invoke(status);}
    internal async Task<ReconstructionResult> ExecuteAsync(ReconstructionPlan plan,
        Func<ReconstructionPlan,IEditingTransaction> transactionFactory,CancellationToken token)
    {
        await _serial.WaitAsync(token);
        try
        {
            if(!plan.IsValid)
            {
                Set(EditingStatus.PlanUnconfirmed);
                return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),plan.Issues,new HashSet<DirtyImageKey>());
            }
            Set(EditingStatus.Executing);
            await using var transaction=transactionFactory(plan);
            try
            {
                await transaction.BeginAsync(token);
                var result=await ReconstructionEngine.RunAsync(plan,transaction,token);
                if(result.Status is ReconstructionStatus.Failed or ReconstructionStatus.Cancelled)
                {
                    await transaction.AbortAsync(result.Issues.FirstOrDefault()??new(PlanIssueCode.CaptureMismatch,"编辑已取消，保留当前图层。"));
                    Set(result.Status==ReconstructionStatus.Cancelled?EditingStatus.Cancelled:EditingStatus.Failed);
                    return result;
                }
                await transaction.CompleteAsync(token);
                Set(result.Status==ReconstructionStatus.PixelVerified?EditingStatus.PixelVerified:EditingStatus.Executed);
                return result;
            }
            catch(CspEditingException error)
            {
                await transaction.AbortAsync(error.Issue);
                Set(EditingStatus.Failed);
                return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),[error.Issue],new HashSet<DirtyImageKey>());
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested)
            {
                await transaction.AbortAsync(new(PlanIssueCode.CaptureMismatch,"用户取消编辑，保留当前图层。"));
                Set(EditingStatus.Cancelled);
                return new(ReconstructionStatus.Cancelled,new Dictionary<LayerKey,ImageRgba>(),[],new HashSet<DirtyImageKey>());
            }
        }
        finally{_serial.Release();}
    }
}
