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
    public string RecycleRoot => RecycleBin.RootFor(_controller.ProjectRecording, _controller.WorkRoot);
    public EditSession? CurrentSession => _session;

    public async Task<EditSession> BeginEditSessionAsync(CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);
        _activeOperation=operation;
        try { return await BeginEditSessionCoreAsync(operation.Token); }
        finally { ReleaseEditingMemory();_activeOperation=null;_operationGate.Release(); }
    }

    private async Task<EditSession> BeginEditSessionCoreAsync(CancellationToken token)
    {
        var window = _window ?? throw new InvalidOperationException("录制窗口尚未建立。");
        if (!window.IsRecording) throw new InvalidOperationException("只可在录制中建立编辑边界。");
        var monitor = window.EditingMonitor ?? throw new InvalidOperationException("Recognizer 监听尚未就绪。");
        var mechanical = monitor.LiveFilePath ?? throw new InvalidDataException("没有可读取的机械流路径。");
        // Usually loaded since recording began; otherwise it loads while the boundary is saved and captured.
        var historyLoad = LoadHistory();
        LiveSessionReader.Prefetch(mechanical);
        var feed = await FeedAsync(token);
        var input = new WindowsViewInput();
        _progress?.Invoke("正在读取 CSP 当前画布与图层状态…");
        // Alt+M can suspend automatic observation. Refresh the same session before
        // its native boundary, so the watcher receives usable state before reserving captures.
        using (EditTiming.Start("boundary", "states"))
            await feed.RequestAsync(["clipState", "currentLayerState", "canvasViewState", "shortcuts"], token);
        input.EnsureTarget();
        // The boundary saves with Ctrl+S, which the recorder sends only to CSP in front. If anything takes the
        // foreground meanwhile, the recorder brings CSP back; Memoline (just clicked) lets it.
        CanvasLayerWatcher.Native.AllowRecorderForeground(monitor.ControlProcessId);
        PreparedEditingBoundary boundary;
        using (EditTiming.Start("boundary", "saveAndCaptureLayers"))
            try { boundary = await window.CaptureEditingBoundaryAsync("edit-" + Guid.NewGuid().ToString("N"), token); }
            catch (IOException error) when (error.Message.Contains("not foreground", StringComparison.OrdinalIgnoreCase)
                                             || error.Message.Contains("Could not activate", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("CSP 不在前台，无法保存当前文档建立编辑边界。请先点一下 CSP 窗口再试。", error); }
        var readMechanical = Task.Run(async () =>
        {
            using (EditTiming.Start("boundary", "readMechanical"))
                return await LiveSessionReader.ReadAsync(mechanical, _controller.DiffRoot, boundary.AppendId, token);
        }, token);
        // The boundary's save is the only structure that shows CSP as it is now: undo and redo save nothing, so any
        // earlier parse can predate them. Its parse comes after the boundary frame, outside the snapshot's frames.
        var (uuid,shownOnPanel)=await BoundaryCurrentLayerAsync(feed,token);
        // The brush panel read (about 1.8 s of OCR in the recorder) runs while this process reads and indexes.
        var brushRead = Task.Run(async () =>
        {
            using (EditTiming.Start("boundary", "brushState")) await feed.RequestAsync(["brushState"], token);
            return feed.BrushEvidence;
        }, token);
        SessionSnapshot snapshot;
        try { snapshot = await readMechanical; }
        catch { await Task.WhenAny(brushRead); throw; }
        if(PacketReplay.J.Array(PacketReplay.J.Get(feed.Layers,"layers")).Any())
            snapshot=snapshot with { BoundaryLayers=LayerLineage.Layers(feed.Layers) };
        if (historyLoad is { IsCompleted: false }) _progress?.Invoke("正在读取工程中以前录制的笔画和图层图片…");
        History? loaded;
        using (EditTiming.Start("boundary", "waitHistory"))
            loaded = historyLoad is null ? null : await historyLoad.WaitAsync(token);
        var history = loaded?.Sessions ?? [];
        // Replaced layers join their originals through the project index, even within this one session.
        EditIndex index;
        using (EditTiming.Start("boundary", "index"))
            index = await Task.Run(() => history.Count == 0 && !LayerLineage.Joins(snapshot) ? EditIndexBuilder.Build(snapshot)
                : ProjectEditIndex.Build(history, snapshot, loaded?.Indexes), token);
        _controller.RegisterEditReplaySources(history.Where(s=>s.Frames.Any(f=>PacketReplay.J.Text(f,"kind")=="penBegin"))
            .Select(s=>s.MechanicalPath));
        JsonElement liveBrush=default;
        try{liveBrush=await brushRead;}
        // Selection/deletion does not require a current numeric editor. An unreadable brush panel (CSP not in front
        // while the user selects on the overlay) must not discard the boundary that is already saved and captured;
        // a brush change confirms the brush again before it is used.
        catch(Exception error)when(error is InvalidOperationException or IOException){ }
        var current=index.Layers.Keys.Where(k=>k.Uuid==uuid && k.Generation==monitor.Generation).ToArray();
        var shortcuts=feed.Shortcuts!;
        bool Routable(string name)
        { try{shortcuts.RouteForBrush(name);return true;}catch(InvalidOperationException){return false;} }
        _session = new EditSession(snapshot, index, current.Length==1?current[0]:null)
        {
            Sources=history.Append(snapshot).ToDictionary(s=>s.SessionId),
            CanvasViewEvidence=feed.RequestedCanvasView,CurrentLayerShownOnPanel=shownOnPanel,
            LiveBrushEvidence=liveBrush,Shortcuts=shortcuts,
            LayerOrder=PacketReplay.J.Array(PacketReplay.J.Get(feed.Layers,"layers"))
                .Select(l=>(Uuid:PacketReplay.J.Text(l,"uuid")??"",Name:PacketReplay.J.Text(l,"name")??""))
                .Where(l=>l.Uuid.Length>0).ToArray(),
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
    /// <summary>The selected layer's UUID at the boundary. The boundary's save holds the selected layer's ID and
    /// nothing reached CSP since, so the .clip on disk names it — whatever the rows are called (duplicate names,
    /// a misread panel row). The layer panel's name is used only when the save names no selected layer.</summary>
    /// <returns>The UUID, and whether the layer panel read just before the boundary showed that same layer: only
    /// then is the saved selection known to be CSP's actual one (a selection change alone is never written).</returns>
    private async Task<(string? Uuid,bool ShownOnPanel)> BoundaryCurrentLayerAsync(LivePacketFeed feed,CancellationToken token)
    {
        string? shown=feed.Layer;
        // Bound to an ID of its own: the structure read before the boundary may predate its save.
        using(EditTiming.Start("boundary","savedStructure"))
            await feed.RequestAsync(["clipState"],token,"boundary-"+Guid.NewGuid().ToString("N"));
        var rows=PacketReplay.J.Array(PacketReplay.J.Get(feed.Layers,"layers")).ToArray();
        if(BoundaryLayers.Selected(feed.Layers) is { } saved)
        {
            string name=PacketReplay.J.Text(saved,"name") ?? "";
            bool agree=shown is not null&&LayerMapping.SameName(name,shown);
            if(shown is not null&&!agree)
                _progress?.Invoke($"图层面板显示「{shown}」，CSP 保存的当前图层是「{name}」，以保存为准。");
            feed.ConfirmLayer(name);
            return (PacketReplay.J.Text(saved,"uuid"),agree);
        }
        var named=rows.Where(l=>LayerMapping.SameName(PacketReplay.J.Text(l,"name") ?? "",shown ?? "")).ToArray();
        return (named.Length==1?PacketReplay.J.Text(named[0],"uuid"):null,false);
    }

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
                return new History(sources,ProjectEditIndex.BuildHistory(sources));
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
        ReconstructionResult? result=null;
        var executing=_session;
        try { return result=await ExecuteEditCoreAsync(request,undoLimitConfirmed,operation.Token); }
        finally
        {
            // The caller only reads the outcome; the reconstructed canvases stay readable but compressed.
            using(EditTiming.Start("execute","releaseMemory"))
            {
                if(result is not null)foreach(var image in result.Layers.Values)image.Park();
                ReleaseEditingMemory(executing,_session);
            }
            _activeOperation=null;_operationGate.Release();
        }
    }

    /// <summary>
    /// Compresses the project history again (an edit restores what it reads) and returns freed canvases to
    /// Windows. Without this the history alone holds two 139 MB canvases per layer and session until exit.
    /// </summary>
    /// <param name="sessions">Edit sessions an edit just used. The edit inflated their canvases and the editing panel
    /// still holds the session while this runs, so without parking them the compaction keeps them. Measured on the
    /// same precise undo (memolinetest10, 2026-10-24): the live app stayed at about 550 MB working set afterwards, the
    /// offline probe, which also parks the edited index, went back to about 110 MB.</param>
    private void ReleaseEditingMemory(params EditSession?[] sessions)
    {
        if(_history is { IsCompletedSuccessfully:true } history)
        {
            foreach(var source in history.Result.Sessions)EditingMemory.Park(source);
            foreach(var index in history.Result.Indexes.Values)EditingMemory.Park(index);
        }
        foreach(var session in sessions.OfType<EditSession>().Distinct())
        {EditingMemory.Park(session.LiveView);EditingMemory.Park(session.Index);}
        EditingMemory.Compact();
    }

    private async Task<ReconstructionResult> ExecuteEditCoreAsync(EditRequest request, bool undoLimitConfirmed, CancellationToken token)
    {
        var shown=_session ?? throw new InvalidOperationException("请先建立编辑边界。");
        // Without physical input recorded in CSP since the boundary nothing can have changed, so the whole second
        // boundary (save, captures, index) is skipped. With input (choosing a replacement brush in CSP does that) the
        // boundary is rebuilt and the edit goes on from it, even if CSP changed meanwhile (user, 2026-10-25: "不管它
        // 继续" — the pen grazing the canvas on the way to the toolbar left 14 px on memolinetest12 and every such
        // edit stopped); what CSP gained is indexed like any other user stroke. Only another recording stops it.
        var monitor=_window?.EditingMonitor ?? throw new InvalidOperationException("Recognizer 监听尚未就绪。");
        string mechanical=monitor.LiveFilePath ?? throw new InvalidDataException("没有可读取的机械流路径。");
        bool sameSession=monitor.SessionId==shown.LiveView.SessionId&&IsRecording;
        // Clean: nothing reached CSP since the boundary saved and read it, so its states and saved file still hold.
        bool clean=sameSession&&!await Task.Run(()=>EditingBoundaryCoordinator.HasPhysicalInput(
            BehaviorRecognizer.Storage.Memoline.MemolineReader.Read(mechanical),shown.LiveView.BoundaryAppendId,ulong.MaxValue),token);
        if(!clean)
        {
            if(!sameSession)
                return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                    [new(PlanIssueCode.CaptureMismatch,"选择期间录制已经结束或换成了新的录制，请重新选区并预览方案。")],new HashSet<DirtyImageKey>());
            await BeginEditSessionCoreAsync(token);
            if(!EditingBoundaryMatches(shown,_session!))
                _progress?.Invoke("选择期间 CSP 画布有改动，已按新的编辑边界继续。");
        }
        ReconstructionPlan plan;
        using (EditTiming.Start("execute", clean ? "plan (clean boundary)" : "plan (boundary rebuilt)"))
            plan = await PlanEditAsync(request, token);
        if (!plan.IsValid)
            return new(ReconstructionStatus.Failed, new Dictionary<LayerKey, ImageRgba>(), plan.Issues, new HashSet<DirtyImageKey>());
        var session=_session!;
        var discrete=new Dictionary<LayerKey,IReadOnlyList<ReplayEvent>>();
        var discreteReferences=new Dictionary<EditableStrokeKey,IReadOnlyDictionary<LayerKey,ImageRgba>>();
        if(request.Kind==EditKind.PreciseUndo)
        {
            var undo=session.SelectPreciseUndo(request.Selection);
            if(!undo.IsValid || undo.CurrentLayer!=request.CurrentLayer || !undo.Base.Select(s=>s.Key).ToHashSet().SetEquals(request.Changes.Keys) ||
                !undo.Closure.Select(s=>s.Key).ToHashSet().SetEquals(request.Closure) || request.Changes.Values.Any(c=>!c.Delete))
                return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                    [new(PlanIssueCode.NoSelection,"精确撤销闭包已改变或尚未确认扩展数量，请刷新并检查选择。")],new HashSet<DirtyImageKey>());
            var capacity=UndoCapacityPolicy.Check(undo.Selected.Count,session.VerifiedUndoCapacity,undoLimitConfirmed);
            if(!capacity.Allowed)return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                [new(PlanIssueCode.NoSelection,capacity.Message)],new HashSet<DirtyImageKey>());
            // A stroke an earlier event edit changed is drawn as that new version, so CSP's undo removes what is on the canvas.
            // Only the selection: the engine hands the closure over separately and it is replayed before these.
            discrete[request.CurrentLayer!.Value]=undo.Base.Select(s=>new ReplayEvent(s,MemolineEdits.Current(s))).ToArray();
            foreach(var stroke in undo.Selected.Where(s=>new ReplayEvent(s,MemolineEdits.Current(s)).RequiresReferenceScene))
                discreteReferences[stroke.Key]=HistoricalReferenceScene.BeforeStroke(session.Index,stroke);
        }
        var feed=await FeedAsync(token);
        _=new WindowsViewInput();
        bool needsReplay=plan.Layers.Any(l=>l.Segments.Any(s=>s.Kind==ReconstructionSegmentKind.Replay&&s.Events.Count>0) ||
            session.Index.Layers[l.Layer].UndoCheckpoints.Any(p=>p.Events.Count>0 && l.Groups.Any(g=>g.PacketId==p.PacketId)))||discrete.Count>0;
        if(clean)
        {
            // The boundary already read layers, current layer, view, shortcuts and brush with nothing changed since;
            // colours are read per stroke by the replay. Re-reading them all costs about 4 s of screen recognition.
            _progress?.Invoke("画布自编辑边界以来未变化，沿用边界时读取的 CSP 状态…");
            await feed.RequestAsync(["initializationConfiguration"],token);
        }
        else
        {
            using(EditTiming.Start("execute","states"))
                await feed.RequestAsync(["initializationConfiguration","shortcuts","canvasViewState","brushState","currentLayerState","colorState"],token);
        }
        // Colour is best effort: without a shortcut for CSP's screen colour picker, strokes replay in the current colour.
        if(needsReplay&&!EventReplayRunner.StrictColor&&ColorRestorer.MissingPicker(feed.Shortcuts) is { } missingPicker)
            _progress?.Invoke("提示："+missingPicker+"这次重放将使用当前颜色。");
        // A colour the user asked for is not best effort: stop before CSP is touched when it cannot be picked.
        if(request.Changes.Values.Any(c=>!c.Delete&&c.Rgb is not null))
        {
            string? noPicker=ColorRestorer.MissingPicker(feed.Shortcuts);
            if(noPicker is null)try{await new PanelStashSwatch().CheckAsync(token);}catch(InvalidOperationException ex){noPicker=ex.Message;}
            if(noPicker is not null)return new(ReconstructionStatus.Failed,new Dictionary<LayerKey,ImageRgba>(),
                [new(PlanIssueCode.ColorUnrestorable,"无法改颜色："+noPicker)],new HashSet<DirtyImageKey>());
        }
        string bridge=Path.Combine(AppContext.BaseDirectory,"clip-layer-bridge.exe");
        if(!File.Exists(bridge)) bridge=Path.Combine(DemoController.ResolveRuntimeDirectory(),"clip-layer-bridge.exe");
        await using var actions=new WindowsCspEditingActions(_window!,feed,session,request,bridge,text=>{_window!.EditingStatus(text);_progress?.Invoke(text);},
            documentSaved:clean);
        using var journal=EditJournal.Open(_controller.WorkRoot);
        bool readsReferences=plan.Layers.SelectMany(l=>l.Segments).SelectMany(s=>s.Events)
            .Any(k=>new ReplayEvent(session.Index.Strokes[k],request.Changes.GetValueOrDefault(k)??MemolineEdits.Current(session.Index.Strokes[k])).RequiresReferenceScene)||discreteReferences.Count>0;
        var involved=plan.Layers.Where(l=>l.Untrusted.Count>0||session.Index.Layers[l.Layer] is {ChainValid:true,Current:not null})
            .Select(l=>l.Layer).Concat(readsReferences?session.Index.Layers.Keys:[]).Distinct().ToArray();
        var dimensions=involved.ToDictionary(key=>key,key=>session.Index.Layers[key].Current is { } current
            ?new CanvasSize(current.Width,current.Height):throw new InvalidDataException("参考图层缺少编辑边界捕获："+key.Uuid));
        var frozen=FrozenEditPlan.Create(session,plan);
        // Every layer this edit may replace goes to the recycle bin first: the edit deletes it from CSP.
        var replaceable=request.Kind==EditKind.PreciseUndo?[request.CurrentLayer!.Value]:plan.Layers.Select(l=>l.Layer).Where(dimensions.ContainsKey).ToArray();
        var recycled=new List<(LayerKey Layer,RecycleEntry Entry,Task Image)>();
        foreach(var key in replaceable)
        {
            if(session.Index.Layers[key].Current is not { } pixels)continue;
            var properties=PacketReplay.J.Array(PacketReplay.J.Get(feed.Layers,"layers")).FirstOrDefault(l=>PacketReplay.J.Text(l,"uuid")==key.Uuid);
            string name=PacketReplay.J.Text(properties,"name")??key.Uuid;
            var (entry,image)=RecycleBin.Save(RecycleRoot,request.Kind,key,name,pixels,properties);
            recycled.Add((key,entry,image));
        }
        ReconstructionResult? result=null;
        try
        {
            using var transaction=EditTiming.Start("execute",request.Kind==EditKind.PreciseUndo?"transaction (precise undo)":"transaction (event editing)");
            result=await _coordinator.ExecuteAsync(plan,p=>new CspEditTransaction(actions,journal,"edit-txn-"+Guid.NewGuid().ToString("N"),dimensions,frozen,discrete,
                p.Layers.Where(l=>l.Untrusted.Count>0).Select(l=>l.Layer).ToHashSet(),discreteReferences),token);
        }
        finally
        {
            foreach(var (key,entry,image) in recycled)
            {
                try{await image;}
                catch(Exception error){_progress?.Invoke("回收箱图像保存失败："+error.Message);}
                bool replaced=result?.Layers.ContainsKey(key)==true;
                string outcome=result?.Status switch
                {
                    ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified=>"executed",
                    ReconstructionStatus.Cancelled or null=>"cancelled",_=>"failed"
                };
                // A layer the edit never touched was not deleted; it needs no recycled copy.
                if(outcome=="executed"&&!replaced)RecycleBin.Delete(entry);
                else RecycleBin.SetOutcome(entry,outcome);
            }
        }
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
        LiveSessionReader.ReleaseLiveFrames();
        ReleaseEditingMemory();
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
