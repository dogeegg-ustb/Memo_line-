using BehaviorRecognizer.Storage.Memoline;

namespace CanvasLayerWatcher;

internal sealed partial class MainWindow
{
    internal bool HideOnClose;
    internal Func<Task>? BeforeRecordingStop;
    internal bool IsRecording=>_stopSource is {IsCancellationRequested:false}&&_monitor is not null&&!_stopping;
    internal RecognizerMonitor? EditingMonitor=>_monitor;
    internal SnapshotHistory? EditingHistory=>_history;
    internal void EditingStatus(string text)=>Status(text);
    internal Task StopRecordingAsync()=>StopAsync();
    private Task[] PendingCaptureTasks(){lock(_captureTasks)return _captureTasks.ToArray();}
    internal Task<PreparedEditingBoundary> CaptureEditingBoundaryAsync(string requestId,CancellationToken token)
    {
        if(!IsRecording||_monitor is not { } monitor||_history is not { } history)
            throw new InvalidOperationException("只可在录制中建立编辑边界。");
        return EditingBoundaryCoordinator.RunAsync(new BoundaryActions(this,monitor,history),requestId,token);
    }
    /// <summary>
    /// Seals a Memoline-operation packet per replaced layer, at the native editingActionCompleted frame
    /// (<paramref name="appendId"/>, <paramref name="ticks"/>): the replaced layer's last snapshot against its
    /// reproduction's installed baseline. Returns how many were committed; a layer without both snapshots is skipped.
    /// </summary>
    internal async Task<int> CommitMemolineOperationAsync(string transactionId,string editKind,ulong appendId,long ticks,
        IReadOnlyList<(string Replaced,string Reproduction)> replacements,CancellationToken token)
    {
        if(_monitor is not { } monitor||_history is not { } history)throw new InvalidOperationException("录制已停止。");
        string session=monitor.SessionId;long generation=monitor.Generation;
        _host?.MemolineOperationRecorded(transactionId,appendId,ticks);
        int committed=0;
        foreach(var (replaced,reproduction) in replacements)
        {
            if(history.Latest(replaced,session,generation) is not { } before||history.Latest(reproduction,session,generation) is not { } after)
            {WriteLog($"记艺线操作包未生成：{replaced} → {reproduction} 缺少被替换图层或复现层的快照");continue;}
            var request=new CaptureRequest(generation,ticks,after.Layer.Name,new(0,0,0,0),[],after.Layer,TriggerKind:"memolineOperation")
                {BoundaryRequestId=transactionId};
            var operation=new{transactionId,editKind,appendId,replacedLayerUuid=replaced,reproductionLayerUuid=reproduction};
            _host?.CaptureQueued(request);
            var update=await Task.Run(()=>history.RecordOperation(before,after,request,monitor.Frequency,operation,token),token);
            if(_host is not null)await _host.PacketCommittedAsync(request,update,token);
            WriteLog($"记艺线操作包已提交：{before.Layer.Name} → {after.Layer.Name}，{update.DiffImages} 张差异图片");
            committed++;
        }
        return committed;
    }
    private sealed class BoundaryActions(MainWindow owner,RecognizerMonitor monitor,SnapshotHistory history):IEditingBoundaryActions
    {
        private IReadOnlyList<CaptureRequest> _requests=[];
        public string SessionId=>monitor.SessionId;
        public bool CaptureRequestsPaused{get=>monitor.CaptureRequestsPaused;set=>monitor.CaptureRequestsPaused=value;}
        public async Task DrainPendingAsync(CancellationToken token)=>await Task.WhenAll(owner.PendingCaptureTasks()).WaitAsync(token);
        public Task<IDisposable> EnterCaptureAsync(CancellationToken token)=>owner.CaptureCoordinator.EnterAsync(token);
        public void Status(string text)=>owner.Status(text);
        private readonly HashSet<long> _completed=[];
        public async Task<PreparedEditingBoundary> PrepareAsync(string requestId,CancellationToken token)
        {
            _requests=await monitor.PrepareEditBoundaryAsync(requestId,token);_completed.Clear();
            try
            {
                if(_requests.Count==0)throw new InvalidDataException("边界没有已确认 UUID 的图层。");
                return monitor.GetPreparedEditBoundary(requestId);
            }
            catch{Abandon();throw;}
        }
        // The reservation blocks every later boundary until each of its layers is completed, so any
        // failure after PrepareAsync (cancel, timeout, save/export error) must release it.
        public void Abandon()
        {
            foreach(var request in _requests.Where(r=>!_completed.Contains(r.Layer!.Id)))
            {owner._host?.CaptureFailed(request,"编辑边界捕获未完成。");monitor.Complete(request,false);}
            _requests=[];
        }
        public async Task CaptureAndCommitAsync(PreparedEditingBoundary boundary,CancellationToken token)
        {
            var completed=_completed;
            bool Current()=>monitor.SessionId==boundary.SessionId&&_requests.Where(r=>!completed.Contains(r.Layer!.Id)).All(monitor.Current)&&owner.IsRecording;
            IReadOnlyList<BoundaryLayerCapture> captures=[];
            try
            {
                var context=_requests[0].Context??throw new InvalidDataException("边界缺少固定的捕获上下文。");
                var control=new RecorderControlClient(context.ControlPipe,owner.WriteLog);
                captures=await SaveCapture.CaptureBoundaryAsync(owner._clip.Text,Path.Combine(AppContext.BaseDirectory,"clip-layer-bridge.exe"),
                    control,context.PixelSize,_requests,Current,()=>monitor.InContact,owner.Status,token,
                    documentModified:monitor.EditedSinceWrite(owner._clip.Text),requireWrite:true);
                // A second native queue barrier makes input during save/export observable before
                // any boundary packet is committed. Its marker never replaces the original limit.
                string checkId=boundary.RequestId+"-validate";
                var checkedBoundary=BaselineBarrier.FromResponse(await control.PrepareEditBoundaryAsync(checkId,token),checkId);
                if(checkedBoundary.SessionId!=boundary.SessionId||!Current())
                    throw new InvalidDataException("建立边界期间录制会话或图层状态已改变；请重新打开编辑面板。");
                if(EditingBoundaryCoordinator.HasPhysicalInput(MemolineReader.Read(monitor.LiveFilePath??throw new InvalidDataException("没有机械录制路径。")),
                        boundary.AppendId,checkedBoundary.AppendId))
                    throw new EditingBoundaryInputException();
                foreach(var capture in captures)
                {
                    owner._host?.CaptureQueued(capture.Request);
                    var update=await Task.Run(()=>history.Advance(capture.Result,capture.Request,boundary.SessionId,context.Evidence.Snapshot,Current,token),token);
                    if(owner._host is not null)await owner._host.PacketCommittedAsync(capture.Request,update,token);
                    completed.Add(capture.Request.Layer!.Id);
                    monitor.Complete(capture.Request,true,history.EvidenceRetentionTicks);
                }
                // A layer reserved by name that turned out to be one captured by UUID has nothing of its own to commit.
                foreach(var request in _requests.Where(r=>!completed.Contains(r.Layer!.Id)).ToArray())
                {completed.Add(request.Layer!.Id);monitor.Complete(request,true,history.EvidenceRetentionTicks);}
                owner.Status("编辑边界已固定，图层捕获与差异包已提交。");
            }
            catch{Abandon();throw;}
            finally{foreach(var capture in captures)SaveCapture.TryDelete(capture.Result.PngPath);}
        }
    }
}
