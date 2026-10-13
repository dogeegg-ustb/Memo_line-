using System.Text.Json;
using CanvasLayerWatcher;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal sealed class DemoEditingHost : IDemoEditingHost, IAsyncDisposable
{
    private readonly DemoController _controller;
    private MainWindow? _window;
    private EditSession? _session;
    private LivePacketFeed? _feed;
    private string? _endpoint;
    private readonly EditingCoordinator _coordinator = new();
    private readonly SemaphoreSlim _operationGate = new(1,1);
    private CancellationTokenSource? _activeOperation;
    private Action<string>? _progress;
    private sealed record History(IReadOnlyList<SessionSnapshot> Sessions, IReadOnlyDictionary<string, EditIndex> Indexes);
    private readonly object _historyGate = new();
    private string? _historyFingerprint;
    private Task<History>? _history;
    public void SetProgressObserver(Action<string>? observer)=>_progress=observer;
    public void ReportEditingError(string operation,Exception error)=>_window?.EditingStatus(operation+"："+error);
    internal DemoEditingHost(DemoController controller)
    {
        _controller = controller;
        controller.RecordingStarted += () =>
        {
            try { _ = LoadHistory(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The boundary reads it again. */ }
        };
    }
    internal void Attach(MainWindow window)
    { _window = window;window.BeforeRecordingStop=StopEditingAsync; }
    public bool IsRecording => _window?.IsRecording == true;
    public EditSession? CurrentSession => _session;

    public async Task<EditSession> BeginEditSessionAsync(CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);
        _activeOperation=operation;
        try { return await BeginEditSessionCoreAsync(operation.Token); }
        finally { _activeOperation=null;_operationGate.Release(); }
    }

    private async Task<EditSession> BeginEditSessionCoreAsync(CancellationToken token)
    {
        var window = _window ?? throw new InvalidOperationException("录制窗口尚未建立。");
        if (!window.IsRecording) throw new InvalidOperationException("只可在录制中建立编辑边界。");
        var monitor = window.EditingMonitor ?? throw new InvalidOperationException("Recognizer 监听尚未就绪。");
        var mechanical = monitor.LiveFilePath ?? throw new InvalidDataException("没有可读取的机械流路径。");
        // Usually loaded since recording began; otherwise it loads while the boundary is saved and captured.
        var historyLoad = LoadHistory();
        var feed = await FeedAsync(token);
        var input = new WindowsViewInput();
        _progress?.Invoke("正在读取 CSP 当前画布与图层状态…");
        // Alt+M can suspend automatic observation. Refresh the same session before
        // its native boundary, so the watcher receives usable state before reserving captures.
        await feed.RequestAsync(["clipState", "currentLayerState", "canvasViewState", "shortcuts"], token);
        input.EnsureTarget();
        var boundary = await window.CaptureEditingBoundaryAsync("edit-" + Guid.NewGuid().ToString("N"), token);
        var snapshot = await LiveSessionReader.ReadAsync(mechanical, _controller.DiffRoot, boundary.AppendId, token);
        if (historyLoad is { IsCompleted: false }) _progress?.Invoke("正在读取工程中以前录制的笔画和图层图片…");
        var loaded = historyLoad is null ? null : await historyLoad.WaitAsync(token);
        var history = loaded?.Sessions ?? [];
        var index = history.Count == 0 ? EditIndexBuilder.Build(snapshot) : ProjectEditIndex.Build(history, snapshot, loaded!.Indexes);
        _controller.RegisterEditReplaySources(history.Where(s=>s.Frames.Any(f=>PacketReplay.J.Text(f,"kind")=="penBegin"))
            .Select(s=>s.MechanicalPath));
        JsonElement liveBrush=default;
        try{await feed.RequestAsync(["brushState"],token);liveBrush=feed.BrushEvidence;}
        catch(InvalidOperationException){ /* Selection/deletion does not require a current numeric editor. */ }
        var layers = PacketReplay.J.Array(PacketReplay.J.Get(feed.Layers,"layers"))
            .Where(l=>LayerMapping.SameName(PacketReplay.J.Text(l,"name") ?? "",feed.Layer ?? "")).ToArray();
        string? uuid=layers.Length==1?PacketReplay.J.Text(layers[0],"uuid"):null;
        var current=index.Layers.Keys.Where(k=>k.Uuid==uuid && k.Generation==monitor.Generation).ToArray();
        var shortcuts=feed.Shortcuts!;
        bool Routable(string name)
        { try{shortcuts.RouteForBrush(name);return true;}catch(InvalidOperationException){return false;} }
        _session = new EditSession(snapshot, index, current.Length==1?current[0]:null)
        {
            Sources=history.Append(snapshot).ToDictionary(s=>s.SessionId),
            CanvasViewEvidence=feed.RequestedCanvasView,
            LiveBrushEvidence=liveBrush,Shortcuts=shortcuts,
            BrushChoices=shortcuts.Nodes.Concat(shortcuts.Bindings.SelectMany(b=>b.Subtools??[]))
                .Where(n=>n.Kind=="subtool"&&!n.Hidden).Select(n=>n.Name)
                .Concat(shortcuts.Bindings.Where(b=>b.Id.StartsWith("tool_")).Select(b=>b.Name))
                .Concat(index.Strokes.Values.Select(s=>s.BrushName).OfType<string>())
                .Distinct().Where(Routable).Order().ToArray()
        };
        return _session;
    }

    /// <summary>
    /// The project's sealed history and its per-session indexes, read once per project content. Sealing rewrites
    /// the project file, so its size and write time identify that content; it never changes while recording.
    /// Reading it takes about 11 s on 记艺线测试2, so it starts as soon as the recorder does (see the constructor)
    /// instead of when the user first freezes the canvas. A failed load is retried by the next call.
    /// </summary>
    private Task<History>? LoadHistory()
    {
        string? project=_controller.ProjectRecording;
        if(project is null||!File.Exists(project))return null;
        string fingerprint=Path.GetFullPath(project)+"/"+ProjectSessionReader.CacheStamp(project);
        lock(_historyGate)
        {
            if(_historyFingerprint==fingerprint&&_history is { IsFaulted:false,IsCanceled:false })return _history;
            _historyFingerprint=fingerprint;
            return _history=Task.Run(()=>
            {
                var sources=ProjectSessionReader.ReadProjectCached(project);
                return new History(sources,sources.ToDictionary(s=>s.SessionId,EditIndexBuilder.Build));
            });
        }
    }
    public async Task<JsonElement> ConfirmBrushForEditingAsync(string name,CancellationToken token)
    {
        var monitor=_window?.EditingMonitor??throw new InvalidOperationException("录制接口未就绪。");
        var control=new RecorderControlClient(monitor.ControlPipe??throw new IOException("没有录制控制管道。"));
        await _operationGate.WaitAsync(token);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);_activeOperation=operation;
        string id="edit-brush-inspect-"+Guid.NewGuid().ToString("N");bool begun=false,confirmed=false;
        LivePacketFeed? feed=null;BrushSnapshot? original=null;WindowsViewInput? input=null;
        void Physical(BehaviorRecognizer.Realtime.RecorderRealtimeEvent e)
        {if(e.Kind=="physicalInputDuringTransaction"&&PacketReplay.J.Text(e.Data,"transactionId")==id)operation.Cancel();}
        monitor.MessageReceived+=Physical;
        try
        {
            if(_session is null||!IsRecording)throw new InvalidOperationException("请先冻结当前录制画布。");
            feed=await FeedAsync(operation.Token);input=new WindowsViewInput();
            await feed.RequestAsync(["initializationConfiguration","shortcuts","brushState"],operation.Token);
            original=feed.Brush??throw new InvalidOperationException("当前笔刷快照未确认，无法安全恢复。");
            feed.Shortcuts!.RouteForBrush(name);
            await control.BeginEditTransactionAsync(id,operation.Token);begun=true;
            var restorer=new PacketStateRestorer(feed,new PacketWindowsInput(input),text=>_progress?.Invoke(text));
            await restorer.RestoreBrushAsync(new(name,[],0),operation.Token);
            await feed.RequestAsync(["brushState"],operation.Token);
            var result=feed.BrushEvidence.Clone();
            if(new BrushClassifier().Classify(result,ToolCatalog.FromJson(feed.Shortcuts.Raw)) is not {Eligible:true})
                throw new InvalidOperationException("目标子工具不是可在画布上重放的笔刷工具。");
            confirmed=true;
            return result;
        }
        finally
        {
            try
            {
                if(begun&&feed is not null&&input is not null)
                {
                    using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    bool restored=false;
                    try
                    {
                        // Physical input transfers control back to the user immediately.
                        // Never send a restoration shortcut after that cancellation.
                        if(original is not null&&!operation.IsCancellationRequested)
                        {await new PacketStateRestorer(feed,new PacketWindowsInput(input),text=>_progress?.Invoke(text)).RestoreBrushAsync(original,operation.Token);restored=true;}
                    }
                    finally{input.ReleaseInputs();await control.EndEditTransactionAsync(id,confirmed&&restored?"executed":"failed",cleanup.Token);}
                }
            }
            finally{monitor.MessageReceived-=Physical;_activeOperation=null;_operationGate.Release();}
        }
    }

    public async Task<JsonElement> CaptureBrushPanelAsync(Rectangle region,CancellationToken token)
    {
        if(region.Width<8||region.Height<8||!SystemInformation.VirtualScreen.Contains(region))
            throw new InvalidDataException("请在截图中框选完整的笔刷属性面板。");
        await _operationGate.WaitAsync(token);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);_activeOperation=operation;
        try
        {
            if(_session is null||!IsRecording)throw new InvalidOperationException("当前编辑会话已结束，请重新选择。");
            var feed=await FeedAsync(operation.Token);_ = new WindowsViewInput();
            await feed.RequestAsync(["shortcuts"],operation.Token);
            await feed.RequestBrushPanelAsync(region,operation.Token);
            var result=feed.BrushEvidence.Clone();var brush=feed.Brush??throw new InvalidOperationException("所选截图未识别到完整笔刷。");
            var roi=PacketReplay.J.Get(PacketReplay.J.Get(result,"evidence"),"panelRoi");
            if(roi.ValueKind!=JsonValueKind.Array||!roi.EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(new[]{region.X,region.Y,region.Width,region.Height}))
                throw new InvalidDataException("笔刷识别结果不属于本次截图选区。");
            feed.Shortcuts!.RouteForBrush(brush.Name);
            if(new BrushClassifier().Classify(result,ToolCatalog.FromJson(feed.Shortcuts.Raw)) is not {Eligible:true})
                throw new InvalidOperationException("所选属性面板对应的工具无法重放为画布笔接触。");
            return result;
        }
        finally{_activeOperation=null;_operationGate.Release();}
    }

    public Task<ReconstructionPlan> PlanEditAsync(EditRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = _session ?? throw new InvalidOperationException("请先建立编辑边界。");
        return Task.FromResult(ReconstructionPlanner.Build(session.Index, request));
    }

    public async Task<ReconstructionResult> ExecuteEditAsync(EditRequest request, bool undoLimitConfirmed, CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);
        _activeOperation=operation;
        try { return await ExecuteEditCoreAsync(request,undoLimitConfirmed,operation.Token); }
        finally { _activeOperation=null;_operationGate.Release(); }
    }

    private async Task<ReconstructionResult> ExecuteEditCoreAsync(EditRequest request, bool undoLimitConfirmed, CancellationToken token)
    {
        var shown=_session ?? throw new InvalidOperationException("请先建立编辑边界。");
        // A changed boundary must be reviewed again; never silently execute a different plan. The canvas stayed
        // frozen since the boundary, so without physical input recorded in CSP nothing can have changed and the
        // whole second boundary (save, captures, index) is skipped. With input (choosing a replacement brush in CSP
        // does that) the boundary is rebuilt and compared as before.
        var monitor=_window?.EditingMonitor ?? throw new InvalidOperationException("Recognizer 监听尚未就绪。");
        string mechanical=monitor.LiveFilePath ?? throw new InvalidDataException("没有可读取的机械流路径。");
        bool sameSession=monitor.SessionId==shown.LiveView.SessionId&&IsRecording;
        if(!sameSession||await Task.Run(()=>EditingBoundaryCoordinator.HasPhysicalInput(
               BehaviorRecognizer.Storage.Memoline.MemolineReader.Read(mechanical),shown.LiveView.BoundaryAppendId,ulong.MaxValue),token))
        {
            if(sameSession)await BeginEditSessionCoreAsync(token);
            if(!sameSession||!EditingBoundaryMatches(shown,_session!))
                return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                    [new(PlanIssueCode.CaptureMismatch,"选择期间录制或画布内容发生变化，请重新选区并预览方案。")],new HashSet<DirtyImageKey>());
        }
        var plan = await PlanEditAsync(request, token);
        if (!plan.IsValid)
            return new(ReconstructionStatus.Failed, new Dictionary<LayerKey, ImageRgba>(), plan.Issues, new HashSet<DirtyImageKey>());
        var session=_session!;
        var discrete=new Dictionary<LayerKey,IReadOnlyList<ReplayEvent>>();
        var discreteReferences=new Dictionary<EditableStrokeKey,IReadOnlyDictionary<LayerKey,ImageRgba>>();
        if(request.Kind==EditKind.PreciseUndo)
        {
            var undo=PreciseUndoSelector.Select(session.Index,session.CurrentLayer,request.Selection);
            if(!undo.IsValid || !undo.Selected.Select(s=>s.Key).ToHashSet().SetEquals(request.Changes.Keys) ||
                request.Changes.Values.Any(c=>!c.Delete))
                return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                    [new(PlanIssueCode.NoSelection,"精确撤销闭包已改变或尚未确认扩展数量，请刷新并检查选择。")],new HashSet<DirtyImageKey>());
            var capacity=UndoCapacityPolicy.Check(undo.Selected.Count,session.VerifiedUndoCapacity,undoLimitConfirmed);
            if(!capacity.Allowed)return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                [new(PlanIssueCode.NoSelection,capacity.Message)],new HashSet<DirtyImageKey>());
            discrete[session.CurrentLayer!.Value]=undo.Selected.Select(s=>new ReplayEvent(s,null)).ToArray();
            foreach(var stroke in undo.Selected.Where(s=>new ReplayEvent(s,null).RequiresReferenceScene))
                discreteReferences[stroke.Key]=HistoricalReferenceScene.BeforeStroke(session.Index,stroke);
        }
        var feed=await FeedAsync(token);
        _=new WindowsViewInput();
        bool needsReplay=plan.Layers.Any(l=>l.Segments.Any(s=>s.Kind==ReconstructionSegmentKind.Replay&&s.Events.Count>0) ||
            session.Index.Layers[l.Layer].UndoCheckpoints.Any(p=>p.Events.Count>0 && l.Groups.Any(g=>g.PacketId==p.PacketId)))||discrete.Count>0;
        // Colour is best effort: without the RGB slider page, strokes replay in the current colour.
        if(needsReplay&&!EventReplayRunner.StrictColor)
        {
            try{await feed.RequestAsync(["colorInputState"],token);}catch(InvalidOperationException){}
            if(RgbInputSnapshot.Parse(feed.ColorInput) is not { Slot:"main" })
                _progress?.Invoke("提示：未检测到色彩滑块 RGB 页，重放将使用当前颜色。");
        }
        await feed.RequestAsync(["initializationConfiguration","shortcuts","canvasViewState","brushState","currentLayerState","colorState"],token);
        string bridge=Path.Combine(AppContext.BaseDirectory,"clip-layer-bridge.exe");
        if(!File.Exists(bridge)) bridge=Path.Combine(DemoController.ResolveRuntimeDirectory(),"clip-layer-bridge.exe");
        await using var actions=new WindowsCspEditingActions(_window!,feed,session,request,bridge,text=>{_window!.EditingStatus(text);_progress?.Invoke(text);});
        using var journal=EditJournal.Open(_controller.WorkRoot);
        bool readsReferences=plan.Layers.SelectMany(l=>l.Segments).SelectMany(s=>s.Events)
            .Any(k=>new ReplayEvent(session.Index.Strokes[k],request.Changes.GetValueOrDefault(k)).RequiresReferenceScene)||discreteReferences.Count>0;
        var involved=plan.Layers.Where(l=>l.Untrusted.Count>0||session.Index.Layers[l.Layer] is {ChainValid:true,Current:not null})
            .Select(l=>l.Layer).Concat(readsReferences?session.Index.Layers.Keys:[]).Distinct().ToArray();
        var dimensions=involved.ToDictionary(key=>key,key=>session.Index.Layers[key].Current is { } current
            ?new CanvasSize(current.Width,current.Height):throw new InvalidDataException("参考图层缺少编辑边界捕获："+key.Uuid));
        var frozen=FrozenEditPlan.Create(session,plan);
        var result=await _coordinator.ExecuteAsync(plan,p=>new CspEditTransaction(actions,journal,"edit-txn-"+Guid.NewGuid().ToString("N"),dimensions,frozen,discrete,
            p.Layers.Where(l=>l.Untrusted.Count>0).Select(l=>l.Layer).ToHashSet(),discreteReferences),token);
        _session=null;
        return result;
    }

    /// <summary>The view to freeze the screen with at once: the latest automatic observation when there is one. The
    /// boundary built afterwards reads the view again, and the selection only attaches if both agree.</summary>
    public async Task<JsonElement> FreezeCanvasViewAsync(CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        try
        {
            var feed=await FeedAsync(token);
            static long Captured(JsonElement view)=>PacketReplay.J.Get(PacketReplay.J.Get(view,"evidence"),"capturedTicks") is {ValueKind:JsonValueKind.Number} n&&n.TryGetInt64(out long t)?t:-1;
            foreach(var known in new[]{feed.ObservedCanvasView,feed.RequestedCanvasView}.Where(v=>v.ValueKind==JsonValueKind.Object).OrderByDescending(Captured))
                try{_=new CanvasAffineTransform(known);return known;}catch(InvalidDataException){}
            await feed.RequestAsync(["canvasViewState"],token);
            return feed.RequestedCanvasView;
        }
        finally{_operationGate.Release();}
    }
    public async Task<JsonElement> ReadCurrentCanvasViewAsync(CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);
        _activeOperation=operation;
        try
        {
            var feed=await FeedAsync(operation.Token);
            await feed.RequestAsync(["canvasViewState"],operation.Token);
            return feed.RequestedCanvasView;
        }
        finally { _activeOperation=null;_operationGate.Release(); }
    }
    internal static bool EditingBoundaryMatches(EditSession before,EditSession after)
    {
        if(before.LiveView.SessionId!=after.LiveView.SessionId || before.CurrentLayer!=after.CurrentLayer ||
            !before.Index.Strokes.Keys.ToHashSet().SetEquals(after.Index.Strokes.Keys))return false;
        if (before.Index.Strokes.Any(pair => pair.Value.IsUndone != after.Index.Strokes[pair.Key].IsUndone)) return false;
        foreach(var (key,layer) in before.Index.Layers)
        {
            if(!after.Index.Layers.TryGetValue(key,out var updated))return false;
            if (!layer.UndoRedo.SequenceEqual(updated.UndoRedo) || layer.UndoCheckpoints.Count != updated.UndoCheckpoints.Count ||
                layer.UndoCheckpoints.Where((p, i) => p.PacketId != updated.UndoCheckpoints[i].PacketId ||
                    !p.Events.SequenceEqual(updated.UndoCheckpoints[i].Events)).Any()) return false;
            if(layer.Current is { } old && (updated.Current is not { } now || old.Width!=now.Width || old.Height!=now.Height ||
                !old.Bytes.AsSpan().SequenceEqual(now.Bytes)))return false;
        }
        return true;
    }
    internal async Task WaitForIdleAsync()
    {
        await _operationGate.WaitAsync();
        _operationGate.Release();
    }
    private async Task StopEditingAsync()
    {
        _activeOperation?.Cancel();
        await WaitForIdleAsync();
        _session=null;
    }
    private async Task<LivePacketFeed> FeedAsync(CancellationToken token)
    {
        string endpoint=_controller.LiveEndpoint ?? throw new InvalidOperationException("录制接口未就绪。");
        if(_endpoint!=endpoint || _feed is null)
        {
            if(_feed is not null) await _feed.DisposeAsync();
            _feed=new LivePacketFeed(endpoint); _endpoint=endpoint;
            _=_feed.View.RunAsync();
        }
        await _feed.View.WaitForConnectionAsync(token); return _feed;
    }
    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync();
        try { if(_feed is not null)await _feed.DisposeAsync(); }
        finally { _operationGate.Release(); }
    }
}
