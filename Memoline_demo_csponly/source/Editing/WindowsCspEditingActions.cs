using System.Diagnostics;
using System.Text.Json;
using CanvasLayerWatcher;
using PacketReplay;
using StrokeReplay;
using DirtyMatrixPaster;

namespace MemolineDemo.Editing;

/// <summary>Real adapter for the guarded transaction, using the existing recorder, OCR and read-only CLIP bridge.</summary>
internal sealed class WindowsCspEditingActions : ICspEditingActions, IAsyncDisposable
{
    private readonly MainWindow _window;
    private readonly RecognizerMonitor _monitor;
    private readonly LivePacketFeed _feed;
    private readonly WindowsViewInput _input;
    private readonly RecorderControlClient _control;
    private readonly ClipLayerCaptureBackend _backend;
    private readonly EditSession _session;
    private readonly EditRequest _request;
    private readonly Action<string> _status;
    private SavedLayerDocument? _saved;
    private Dictionary<string,LayerReference> _layers = [];
    private string? _transactionId;
    internal WindowsCspEditingActions(MainWindow window, LivePacketFeed feed, EditSession session, EditRequest request, string bridge, Action<string> status)
    {
        _window=window; _monitor=window.EditingMonitor ?? throw new InvalidOperationException("录制已停止。");
        _feed=feed; _session=session; _request=request; _status=status; _bridge=bridge;
        _input=new WindowsViewInput();
        _control=new(_monitor.ControlPipe ?? throw new InvalidOperationException("控制接口未就绪。"));
        _backend=new(_monitor.ClipPath ?? throw new InvalidDataException("没有 CLIP 路径。"),bridge,_control);
        _monitor.MessageReceived+=Message;
    }
    public event Action<PhysicalInputEvidence>? PhysicalInput;
    private void Message(BehaviorRecognizer.Realtime.RecorderRealtimeEvent message)
    {
        if (message.Kind=="physicalInputDuringTransaction" && PacketReplay.J.Text(message.Data,"transactionId") is { } id)
            PhysicalInput?.Invoke(new(id,PacketReplay.J.Text(message.Data,"deviceType") ?? "unknown",message.AppendId));
    }
    public bool CaptureRequestsPaused { get=>_monitor.CaptureRequestsPaused; set=>_monitor.CaptureRequestsPaused=value; }
    public async Task<ulong> BeginNativeTransactionAsync(string id,CancellationToken token)
    {
        _transactionId=id;
        var result=await _control.BeginEditTransactionAsync(id,token);
        if (CanvasLayerWatcher.J.Text(result,"sessionId")!=_session.LiveView.SessionId || CanvasLayerWatcher.J.Text(result,"transactionId")!=id)
            throw new InvalidDataException("原生事务身份不匹配。");
        return PacketReplay.J.U64(result,"appendId");
    }
    public async Task EndNativeTransactionAsync(string id,string outcome,CancellationToken token)
    {
        var ended=await _control.EndEditTransactionAsync(id,outcome,token);
        if(CanvasLayerWatcher.J.Text(ended,"sessionId")!=_session.LiveView.SessionId || CanvasLayerWatcher.J.Text(ended,"transactionId")!=id)
            throw new InvalidDataException("原生事务结束回执身份不符。");
        if (outcome=="executed")
        {
            var action=await _control.SendAsync(new { command="recordEditingAction", transactionId=id,
                editKind=_request.Kind==EditKind.PreciseUndo?"preciseUndo":"eventEditing",
                changes=_request.Changes.Select(c=>new { sessionId=c.Key.SessionId,beginAppendId=c.Key.BeginAppendId,
                    delete=c.Value.Delete,brushName=c.Value.BrushName,numbers=c.Value.NumberValues }).ToArray() },CancellationToken.None);
            if(CanvasLayerWatcher.J.Text(action,"sessionId")!=_session.LiveView.SessionId || CanvasLayerWatcher.J.Text(action,"transactionId")!=id)
                throw new InvalidDataException("编辑记录回执身份不符。");
        }
        // Publish fresh post-replay state for the next physical drawing/undo.
        // These requests occur after native persistence has resumed.
        using var refresh=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _feed.RequestAsync(["brushState","colorState","currentLayerState","canvasViewState","clipState"],refresh.Token); }
        catch(Exception error) { _status("复现已结束，后续状态将在下一次操作时重新确认："+error.Message); }
    }
    public async Task<IAsyncDisposable> AcquireCaptureCoordinatorAsync(CancellationToken token)
        => new AsyncLease(await _window.CaptureCoordinator.EnterAsync(token));
    private sealed class AsyncLease(IDisposable lease):IAsyncDisposable
    { public ValueTask DisposeAsync(){lease.Dispose();return ValueTask.CompletedTask;} }
    public async Task<bool> VerifyShortcutsAsync(CancellationToken token)
    {
        await _feed.RequestAsync(["shortcuts"],token);
        var config=_feed.Shortcuts!;
        string[] commands=["paste","selectdeselect","layerdelete","layerselectupperlayer","layerselectlowerlayer"];
        bool Usable(string command)
        {
            try
            {
                var binding=config.ForCommand(command);
                return !config.Bindings.Any(b=>b.Shortcut==binding.Shortcut && b.Command!=binding.Command);
            }
            catch (InvalidOperationException){return false;}
        }
        // Hiding the original is optional until a show/hide-layer shortcut is provisioned.
        CanHideLayer=Usable("layervisiblelayer");
        if(!CanHideLayer)_status("提示：CSP 没有显示/隐藏图层快捷键，原图层保持显示，复现层放在其上方。");
        return commands.All(Usable);
    }
    public bool CanHideLayer { get; private set; }
    public async Task<CspLayerInspection> SaveAndInspectAsync(CancellationToken token)
    {
        _feed.EnsureConnected(); _input.EnsureTarget();
        if (_monitor.SessionId!=_session.LiveView.SessionId) throw new InvalidDataException("录制会话已改变。");
        if (_saved is not null) _backend.Cleanup(_saved);
        long ticks=Stopwatch.GetTimestamp()-_feed.View.ClockOriginTicks!.Value;
        _saved=await _backend.SaveSnapshotAsync(ticks,token);
        using var inspection=JsonDocument.Parse(await Bridge.RunAsync(_bridge,["inspect",_saved.SnapshotPath],token));
        var rows=CanvasLayerWatcher.J.Get(inspection.RootElement,"layers").EnumerateArray()
            .Where(r=>CanvasLayerWatcher.J.Get(r,"isRoot").ValueKind!=JsonValueKind.True)
            .OrderBy(r=>CanvasLayerWatcher.J.Tick(r,"stackOrder",long.MaxValue)).ToArray();
        _layers=rows.ToDictionary(r=>CanvasLayerWatcher.J.Text(r,"uuid") ?? throw new InvalidDataException("图层 UUID 缺失。"),
            r=>new LayerReference(CanvasLayerWatcher.J.Tick(r,"id",0),CanvasLayerWatcher.J.Text(r,"name") ?? "",CanvasLayerWatcher.J.Text(r,"uuid")));
        await _feed.RequestAsync(["clipState"],token);
        // The OCR layer name is only a fallback (see ReadCurrentLayerAsync); a hidden or unreadable
        // selected row must not stop the structural confirmation.
        try{await _feed.RequestAsync(["currentLayerState"],token);}catch(InvalidOperationException){}
        return new(_monitor.SessionId,_monitor.Generation,true,rows.Select(r=>new CspLayerEvidence(
            CanvasLayerWatcher.J.Text(r,"uuid")!,CanvasLayerWatcher.J.Text(r,"name") ?? "",
            CanvasLayerWatcher.J.Get(r,"isRaster").ValueKind==JsonValueKind.True,
            CanvasLayerWatcher.J.Get(r,"folder").ValueKind==JsonValueKind.True,
            CanvasLayerWatcher.J.Get(r,"visible").ValueKind==JsonValueKind.True,CanvasLayerWatcher.J.Text(r,"parentUuid"))).ToArray());
    }
    private string _bridge = "";
    public Task NavigateToLayerAsync(string name,CancellationToken token)=>new PacketStateRestorer(_feed,new PacketWindowsInput(_input),_status).RestoreLayerAsync(name,token);
    public async Task<CspCurrentLayerEvidence> ReadCurrentLayerAsync(CancellationToken token)
    {
        // Every confirmation follows a save. The saved document records the selected layer ID,
        // which does not depend on how the layer row looks (hidden, highlighted, scrolled).
        if(PacketReplay.J.Get(PacketReplay.J.Get(_feed.Layers,"canvas"),"current_layer_id") is {ValueKind:JsonValueKind.Number} id &&
           _saved is not null && _layers.Values.Where(l=>l.Id==id.GetInt64()).ToArray() is [var saved])
            return new(saved.Name,true,saved.Uuid);
        await _feed.RequestAsync(["currentLayerState"],token);
        string name=_feed.Layer!; var matches=_layers.Values.Where(l=>LayerMapping.SameName(l.Name,name)).ToArray();
        return new(name,matches.Length==1,matches.Length==1?matches[0].Uuid:null);
    }
    public async Task<CspHideEvidence> HideLayerAsync(CspLayerEvidence layer,CancellationToken token)
    {
        await ShortcutAsync("layervisiblelayer",token);
        var inspection=await SaveAndInspectAsync(token);
        var current=await ReadCurrentLayerAsync(token);
        bool hidden=inspection.Layers.Single(l=>l.Uuid==layer.Uuid).Visible==false && current.Uuid==layer.Uuid;
        return new(true,hidden,true,hidden,current.Name,hidden);
    }
    public Task SetClipboardImageAsync(ImageRgba image,CancellationToken token)
    { token.ThrowIfCancellationRequested(); image.Validate(); ClipboardImageCodec.SetImage(image.Width,image.Height,image.Bytes); return Task.CompletedTask; }
    public async Task PasteAsync(CancellationToken token)
    { await ShortcutAsync("selectdeselect",token); await ShortcutAsync("paste",token); }
    private Task ShortcutAsync(string command,CancellationToken token)
        =>_input.SendShortcutAsync(ShortcutConfiguration.Keys((_feed.Shortcuts ?? throw new InvalidOperationException("快捷键未就绪。")).ForCommand(command).Shortcut),token);
    public async Task<LayerExportEvidence> ExportLayerAsync(LayerKey layer,CancellationToken token)
    {
        if (_saved is null || !_layers.TryGetValue(layer.Uuid,out var reference)) throw new InvalidDataException("目标层不在最新保存结构中。");
        var size=_monitor.PixelSize ?? throw new InvalidDataException("完整画布尺寸不可用。");
        var result=await _backend.ExportAsync(_saved,reference,size,token);
        try
        {
            using var metadata=JsonDocument.Parse(result.Metadata);
            if (CanvasLayerWatcher.J.Text(metadata.RootElement,"layerUuid")!=layer.Uuid || CanvasLayerWatcher.J.Tick(metadata.RootElement,"layerId",-1)!=reference.Id)
                throw new InvalidDataException("导出 UUID 不符。");
            return new(layer,size.Width,size.Height,CanvasLayerWatcher.J.Get(metadata.RootElement,"fullCanvas").ValueKind==JsonValueKind.True,await File.ReadAllBytesAsync(result.PngPath,token));
        }
        finally { SaveCapture.TryDelete(result.PngPath); }
    }
    public async Task ReplayAsync(CspLayerEvidence work,IReadOnlyList<ReplayEvent> events,ReplayMode mode,
        IReadOnlyDictionary<LayerKey,ImageRgba> references,CancellationToken token)
    {
        // Each native recording has its own clock, event IDs and historical state.
        // Validate every source before preparing CSP, then replay project sessions
        // in order without manufacturing a gap between unrelated native clocks.
        var schedules=BuildSchedules(_session,_request,events,mode,work.Name,work.Uuid);
        if (events.Any(e=>e.ReadsReferenceLayers))
            await VerifyReferenceSceneAsync(events[0].Stroke.Layer,events,mode,references,token);
        await PacketReplayRunner.InitializeStatesAsync(_feed,false,true,token);
        var byKey=events.ToDictionary(e=>(e.Stroke.Key.SessionId,e.Stroke.Key.BeginAppendId));
        foreach(var schedule in schedules)
            await EventReplayRunner.RunAsync(schedule with {
                VerifyRestoredStateAsync=async (drawing,ct)=>
                {
                    var editing=byKey[drawing.EditableKey];
                    if(!editing.ReadsReferenceLayers)return;
                    await _feed.RequestAsync(["brushState"],ct);
                    VerifyReferenceControlsOff(ReferenceBrushEvidence(editing),_feed.BrushEvidence,
                        (drawing.OverrideBrush??drawing.State.Brush!).Name);
                }
            },new WindowsReplayEnvironment(_feed,_input),1,new(mode),_status,token);
    }
    internal static IReadOnlyList<EventReplaySchedule> BuildSchedules(EditSession session,EditRequest request,
        IReadOnlyList<ReplayEvent> events,ReplayMode mode,string workName,string workUuid)
    {
        var schedules=new List<EventReplaySchedule>();
        var sessionOrders=new Dictionary<int,string>();
        foreach(var group in events.OrderBy(e=>e.Stroke.SessionOrder).ThenBy(e=>e.Stroke.BeginEventId)
            .ThenBy(e=>e.Stroke.Key.BeginAppendId).GroupBy(e=>e.Stroke.Key.SessionId))
        {
            var snapshot=session.Source(group.Key);
            if(snapshot.SessionId!=group.Key || snapshot.Replay.SessionId!=group.Key ||
                snapshot.Replay.Frequency!=snapshot.Frequency)
                throw new InvalidDataException("历史重放来源的会话或时钟不匹配。");
            var ordered=group.ToArray();
            if(ordered.Select(e=>e.Stroke.SessionOrder).Distinct().Count()!=1)
                throw new InvalidDataException("同一原生会话包含冲突的工程顺序。");
            int sessionOrder=ordered[0].Stroke.SessionOrder;
            if(sessionOrders.TryGetValue(sessionOrder,out string? priorSession) && priorSession!=group.Key)
                throw new InvalidDataException("不同原生会话缺少明确的工程先后顺序。");
            sessionOrders[sessionOrder]=group.Key;
            var drawings=ordered.Select(e=>
            {
                var candidates=snapshot.Replay.Strokes.Where(s=>s.Samples.Count>0 &&
                    s.Samples[0].AppendId==e.Stroke.Key.BeginAppendId).ToArray();
                if(candidates.Length!=1 || candidates[0].Samples[0].EventId!=e.Stroke.BeginEventId ||
                    candidates[0].OperationId!=e.Stroke.OperationId)
                    throw new InvalidDataException("编辑笔画无法唯一匹配其原始会话的完整接触。");
                var historical=StateHistory.At(snapshot.History,e.Stroke.BeginEventId);
                return new ScheduledStroke((e.Stroke.Key.SessionId,e.Stroke.Key.BeginAppendId),candidates[0],
                    historical,BuildOverride(historical.Brush,e.Change));
            }).ToArray();
            var last=ordered[^1].Stroke;
            long? following=mode==ReplayMode.Discrete && request.Kind==EditKind.PreciseUndo
                ? request.Changes.Keys.Select(k=>session.Index.Strokes[k])
                    .Where(s=>s.Key.SessionId==group.Key && s.BeginEventId>last.BeginEventId)
                    .OrderBy(s=>s.BeginEventId).FirstOrDefault()?.StartTicks
                : null;
            var schedule=new EventReplaySchedule(snapshot.Frequency,drawings,workName,workUuid) {
                FollowingStrokeStartTicks=following
            };
            EventReplayRunner.Validate(schedule);
            schedules.Add(schedule);
        }
        return schedules;
    }
    public Task VerifyReferenceSceneAsync(LayerKey layer,IReadOnlyList<ReplayEvent> events,ReplayMode mode,
        IReadOnlyDictionary<LayerKey,ImageRgba> references,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!events.Any(e=>e.ReadsReferenceLayers)) return Task.CompletedTask;
        if(events.Where(e=>e.ReadsReferenceLayers).All(e=>ReferenceControlsAreOff(ReferenceBrushEvidence(e),TargetBrushName(e))))
            return Task.CompletedTask;
        if (mode==ReplayMode.Discrete)
            throw new ReplayStateException("RuntimeClosureViolation","精确撤销的历史参考场景尚不能在保留逐笔撤销记录的同时清理：最后一笔之后删除或隐藏参考层会增加撤销操作，已在修改 CSP 前停止。");
        // Pixels alone do not identify CSP's designated reference layers. The current
        // read-only CLIP bridge exposes UUID, order, parent and visibility, but no
        // reference-role evidence or a verified shortcut/readback for that role.
        // Do not turn ordinary pasted raster layers into guessed reference layers.
        throw new ReplayStateException("RuntimeClosureViolation","历史参考场景只有像素，缺少 CSP 指定参考图层标记及其设置读回证据；不能把普通粘贴层当作参考图层，已在隐藏或粘贴之前停止。");
    }
    internal static BrushSnapshot? BuildOverride(BrushSnapshot? historical,EventChange? change)
    {
        if(change is null)return null;
        if(historical is null)throw new ReplayStateException("BrushUnrestorable","参数修改事件缺少完整的历史笔刷快照。");
        var target=historical;
        if(change.BrushName is { } brushName && !BrushSnapshot.SameName(brushName,historical.Name))
        {
            target=BrushSnapshot.Parse(change.ReplacementBrushState) ??
                throw new ReplayStateException("UnsupportedOverride","更换子工具必须先主动确认该子工具的完整属性快照。");
            if(!BrushSnapshot.SameName(target.Name,brushName))
                throw new ReplayStateException("UnsupportedOverride","已确认的替换子工具名称与请求不一致。");
        }
        if(change.Delete || change.BrushName is { } name && string.IsNullOrWhiteSpace(name) ||
            change.NumberValues.Any(p=>!double.IsFinite(p.Value)||!target.Numbers.Any(n=>n.Key==p.Key)))
            throw new ReplayStateException("UnsupportedOverride","参数覆盖包含删除事件、空子工具名或未确认的历史数字属性。");
        return target with {Name=change.BrushName??historical.Name,
            Numbers=target.Numbers.Select(n=>change.NumberValues.TryGetValue(n.Key,out double value)?n with {Value=value}:n).ToArray()};
    }
    private static JsonElement ReferenceBrushEvidence(ReplayEvent drawing)
        =>drawing.BrushEvidence;
    private static string TargetBrushName(ReplayEvent drawing)=>drawing.Change?.BrushName??drawing.Stroke.BrushName??"";
    // This proves only the narrow case in which all known reference controls are
    // explicit unchecked checkboxes. Disabled numeric/enum rows do not prove off.
    // An active or unobserved reference scene still needs real layer-role evidence.
    internal static bool ReferenceControlsAreOff(JsonElement brushState,string expectedName)
        =>OffReferenceKeys(brushState,expectedName) is not null;
    private static HashSet<string>? OffReferenceKeys(JsonElement evidence,string expectedName)
        =>ReferenceReadEvidence.OffKeys(evidence,expectedName);
    internal static void VerifyReferenceControlsOff(JsonElement historical,JsonElement live,string expectedName)
    {
        var historicalKeys=OffReferenceKeys(historical,expectedName);
        var liveKeys=OffReferenceKeys(live,expectedName);
        if(historicalKeys is null||liveKeys is null||!historicalKeys.SetEquals(liveKeys))
            throw new ReplayStateException("BrushUnrestorable","历史及当前参考控制未明确确认关闭。");
    }
    public Task DeleteLayerAsync(CancellationToken token)=>ShortcutAsync("layerdelete",token);
    public async Task RegisterBaselineAsync(LayerExportEvidence capture,CancellationToken token)
    {
        if (!_layers.TryGetValue(capture.Layer.Uuid,out var layer)) throw new InvalidDataException("复现层身份缺失。");
        string png=Path.Combine(_window.EditingHistory!.Root,"baseline-install-"+Guid.NewGuid().ToString("N")+".png");
        try
        {
            await File.WriteAllBytesAsync(png,capture.Png,token);
            _window.EditingHistory.RegisterBaseline(layer,png,Stopwatch.GetTimestamp()-_feed.View.ClockOriginTicks!.Value);
        }
        finally { SaveCapture.TryDelete(png); }
    }
    public void ReleaseInputs()=>_input.ReleaseInputs();
    public ValueTask DisposeAsync()
    { _monitor.MessageReceived-=Message; if(_saved is not null)_backend.Cleanup(_saved); _input.ReleaseInputs(); return ValueTask.CompletedTask; }
}
