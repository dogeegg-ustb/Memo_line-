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
    // True while the .clip on disk is known to hold the current document: the boundary saved it and no input
    // has reached CSP since. The first inspection then reads that file instead of saving again.
    private bool _documentSaved;
    private readonly long _initialInputs=WindowsViewInput.SentInputs;
    private readonly List<(string Replaced,string Reproduction)> _replacements=[];
    // Every recorded contact drawn again in CSP during this transaction: Memoline's own events (see recordEditingAction).
    private readonly HashSet<EditableStrokeKey> _replayed=[];
    private IDisposable? _shield;
    internal WindowsCspEditingActions(MainWindow window, LivePacketFeed feed, EditSession session, EditRequest request, string bridge, Action<string> status,
        bool documentSaved=false)
    {
        _documentSaved=documentSaved;
        _window=window; _monitor=window.EditingMonitor ?? throw new InvalidOperationException("录制已停止。");
        _feed=feed; _session=session; _request=request; _status=status; _bridge=bridge;
        _input=new WindowsViewInput();
        _control=new(_monitor.ControlPipe ?? throw new InvalidOperationException("控制接口未就绪。"));
        // Saves inside the transaction bring CSP back to the front too, like every injected input.
        _backend=new(_monitor.ClipPath ?? throw new InvalidDataException("没有 CLIP 路径。"),bridge,_control,activateCsp:true);
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
        // From here until the end no real pen or mouse reaches CSP: a hovering pen merged the replay's strokes with its
        // own position (rays) and into one undo step. Injected input goes through holes (see InputShield).
        _shield??=InputShield.Raise();
        return PacketReplay.J.U64(result,"appendId");
    }
    public async Task EndNativeTransactionAsync(string id,string outcome,CancellationToken token)
    {
        LowerShield();
        var ended=await _control.EndEditTransactionAsync(id,outcome,token);
        if(CanvasLayerWatcher.J.Text(ended,"sessionId")!=_session.LiveView.SessionId || CanvasLayerWatcher.J.Text(ended,"transactionId")!=id)
            throw new InvalidDataException("原生事务结束回执身份不符。");
        if (outcome=="executed")
        {
            using var recordStep=EditTiming.Start(id,"recordEditingAction+operationPacket");
            var action=await _control.SendAsync(new { command="recordEditingAction", transactionId=id,
                editKind=_request.Kind==EditKind.PreciseUndo?"preciseUndo":"eventEditing",
                // A subtool change carries the confirmed target snapshot: a later edit of this new version replays with it.
                changes=_request.Changes.Select(c=>new { sessionId=c.Key.SessionId,beginAppendId=c.Key.BeginAppendId,
                    delete=c.Value.Delete,brushName=c.Value.BrushName,numbers=c.Value.NumberValues,
                    replacementBrushState=c.Value.ReplacementBrushState.ValueKind==JsonValueKind.Object?(object)c.Value.ReplacementBrushState:null,
                    rgb=c.Value.Rgb }).ToArray(),
                layerReplacements=_replacements.Select(r=>new { replaced=r.Replaced,reproduction=r.Reproduction }).ToArray(),
                replayed=_replayed.OrderBy(k=>k.SessionId,StringComparer.Ordinal).ThenBy(k=>k.BeginAppendId)
                    .Select(k=>new { sessionId=k.SessionId,beginAppendId=k.BeginAppendId }).ToArray() },CancellationToken.None);
            if(CanvasLayerWatcher.J.Text(action,"sessionId")!=_session.LiveView.SessionId || CanvasLayerWatcher.J.Text(action,"transactionId")!=id)
                throw new InvalidDataException("编辑记录回执身份不符。");
            // The Memoline-operation packet: sealed at that native record, the new version of what the edit rebuilt.
            // The edit is in CSP already; without the packet the index still versions it from the record alone.
            try
            {
                int sealedPackets=await _window.CommitMemolineOperationAsync(id,_request.Kind==EditKind.PreciseUndo?"preciseUndo":"eventEditing",
                    PacketReplay.J.U64(action,"appendId"),PacketReplay.J.I64(action,"ticks",-1),_replacements,CancellationToken.None);
                if(sealedPackets<_replacements.Count)_status("提示：部分图层的记艺线操作包未能生成，编辑索引将只按原生编辑记录区分版本。");
            }
            catch(Exception error)when(error is IOException or InvalidDataException or InvalidOperationException)
            {_status("提示：记艺线操作包未能生成："+error.Message);}
        }
        // Publish fresh post-replay state for the next physical drawing/undo, after native persistence has
        // resumed. Nothing in the transaction waits for it, so it runs on its own; and as a background request,
        // because the user picks the pen up right now: run side by side, its OCR took about six cores for 3 s
        // (usage.log 2026-10-24 15:11:45 and 15:25:00) and starved CSP of memory bandwidth, priority or not.
        _=RefreshAfterEndAsync(id,_expectedFinalLayer);
    }
    private async Task RefreshAfterEndAsync(string id,string? expected)
    {
        using var step=EditTiming.Start(id,"stateRefreshAfterEnd");
        // The screenshots are taken at once; only their analysis waits while the user draws.
        using var refresh=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await _feed.RequestInBackgroundAsync(["brushState","colorState","currentLayerState","canvasViewState","clipState"],refresh.Token); }
        catch(Exception error) { _status("复现已结束，后续状态将在下一次操作时重新确认："+error.Message); return; }
        if(expected is not null)
            _status(_feed.Layer is { } shown && LayerMapping.SameName(expected,shown)
                ? $"已回到原来所在的图层「{expected}」。"
                : $"提示：结束时确认的当前图层是「{_feed.Layer}」，不是原来所在的图层「{expected}」，请在 CSP 中手动切回。");
    }
    public async Task<IAsyncDisposable> AcquireCaptureCoordinatorAsync(CancellationToken token)
        => new AsyncLease(await _window.CaptureCoordinator.EnterAsync(token));
    private sealed class AsyncLease(IDisposable lease):IAsyncDisposable
    { public ValueTask DisposeAsync(){lease.Dispose();return ValueTask.CompletedTask;} }
    public async Task<bool> VerifyShortcutsAsync(CancellationToken token)
    {
        using(EditTiming.Start(_transactionId,"shortcuts"))
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
        // The transaction replaces layers in place and never hides one; the shortcut is only reported.
        CanHideLayer=Usable("layervisiblelayer");
        return commands.All(Usable);
    }
    public bool CanHideLayer { get; private set; }
    private CspLayerInspection? _inspection;
    private long _inspectedInputs=-1;
    private int _penReplays, _inspectedPenReplays=-1;
    public async Task<CspLayerInspection> SaveAndInspectAsync(CancellationToken token,bool refresh=false,bool expectChange=false)
    {
        _feed.EnsureConnected(); _input.EnsureTarget();
        if (_monitor.SessionId!=_session.LiveView.SessionId) throw new InvalidDataException("录制会话已改变。");
        // Each save costs a CLIP write plus a clipState request (~2–3 s). Without any input or
        // pen replay from Memoline since the last one, the document is still the one inspected: physical input
        // aborts the transaction. The saved snapshot that exports read from stays the same as well.
        long inputs=WindowsViewInput.SentInputs;
        if (!refresh && _inspection is { } known && _saved is not null && inputs==_inspectedInputs && _penReplays==_inspectedPenReplays &&
            known.SessionId==_monitor.SessionId && known.Generation==_monitor.Generation)
            return known;
        _inspection=null;
        using var total=EditTiming.Start(_transactionId,"saveAndInspect");
        if (_saved is not null) _backend.Cleanup(_saved);
        long ticks=Stopwatch.GetTimestamp()-_feed.View.ClockOriginTicks!.Value;
        string saveId;
        if(_documentSaved&&inputs==_initialInputs&&_penReplays==0)
        {
            _documentSaved=false;
            (_saved,saveId)=await CopySavedDocumentAsync(token);
        }
        else
        {
            _documentSaved=false;
            // Ctrl+S, CSP writing the .clip, and the quiet period that proves the write finished.
            using(EditTiming.Start(_transactionId,expectChange?"  saveSettled (modified)":"  saveSettled"))
                _saved=await _backend.SaveSnapshotAsync(ticks,token,expectChange);
            saveId=CanvasLayerWatcher.J.Text(_saved.ControlResponse,"requestId") ?? throw new InvalidDataException("保存回执缺少 requestId。");
        }
        var inspecting=EditTiming.Start(_transactionId,"  bridgeInspect");
        using var inspection=JsonDocument.Parse(await Bridge.RunAsync(_bridge,["inspect",_saved.SnapshotPath],token));
        inspecting.Dispose();
        var rows=CanvasLayerWatcher.J.Get(inspection.RootElement,"layers").EnumerateArray()
            .Where(r=>CanvasLayerWatcher.J.Get(r,"isRoot").ValueKind!=JsonValueKind.True)
            .OrderBy(r=>CanvasLayerWatcher.J.Tick(r,"stackOrder",long.MaxValue)).ToArray();
        _layers=rows.ToDictionary(r=>CanvasLayerWatcher.J.Text(r,"uuid") ?? throw new InvalidDataException("图层 UUID 缺失。"),
            r=>new LayerReference(CanvasLayerWatcher.J.Tick(r,"id",0),CanvasLayerWatcher.J.Text(r,"name") ?? "",CanvasLayerWatcher.J.Text(r,"uuid")));
        // Bind the structure request to this save: an unbound clipState may describe an earlier document.
        using(EditTiming.Start(_transactionId,"  clipState"))
            await _feed.RequestAsync(["clipState"],token,saveId);
        if(PacketReplay.J.Text(_feed.LayersEvidence,"saveId")!=saveId)
            throw new InvalidDataException("主动请求的图层结构不属于本次保存。");
        // The replay and the panel-confirmed navigation read the current layer from the feed. The saved
        // selection is that layer, so the feed takes it from the save; panel OCR older than this save is
        // stale and ignored. The layer panel is read only when the save names no selected layer.
        if(SavedCurrentLayer() is { } selected) _feed.ConfirmLayer(selected.Name);
        else try{await _feed.RequestAsync(["currentLayerState"],token);}catch(InvalidOperationException){}
        _inspection=new(_monitor.SessionId,_monitor.Generation,true,rows.Select(r=>new CspLayerEvidence(
            CanvasLayerWatcher.J.Text(r,"uuid")!,CanvasLayerWatcher.J.Text(r,"name") ?? "",
            CanvasLayerWatcher.J.Get(r,"isRaster").ValueKind==JsonValueKind.True,
            CanvasLayerWatcher.J.Get(r,"folder").ValueKind==JsonValueKind.True,
            CanvasLayerWatcher.J.Get(r,"visible").ValueKind==JsonValueKind.True,CanvasLayerWatcher.J.Text(r,"parentUuid"))).ToArray(),
            Revision(_saved.SnapshotPath));
        _inspectedInputs=inputs; _inspectedPenReplays=_penReplays;
        return _inspection;
    }
    private string _bridge = "";
    /// <summary>Copies the already saved document without a CSP save; the Recognizer parses the same file under
    /// a request ID of its own, so the structure stays bound to this copy.</summary>
    private async Task<(SavedLayerDocument Document,string SaveId)> CopySavedDocumentAsync(CancellationToken token)
    {
        string clip=_monitor.ClipPath ?? throw new InvalidDataException("没有 CLIP 路径。");
        string directory=Path.Combine(AppContext.BaseDirectory,"snapshots");Directory.CreateDirectory(directory);
        string snapshot=Path.Combine(directory,"edit-inspect-"+Guid.NewGuid().ToString("N")+".clip");
        for(int attempt=0;;attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var before=SaveCapture.Stat(clip);
                await using(var input=new FileStream(clip,FileMode.Open,FileAccess.Read,FileShare.Read))
                await using(var output=new FileStream(snapshot,FileMode.Create,FileAccess.Write,FileShare.None))
                    await input.CopyToAsync(output,token);
                if(SaveCapture.Stat(clip)==before)
                {
                    string id="inspect-"+Guid.NewGuid().ToString("N");
                    return (new(snapshot,-1,JsonSerializer.SerializeToElement(new{requestId=id})),id);
                }
            }
            catch(IOException) when(attempt<10){}
            SaveCapture.TryDelete(snapshot);
            if(attempt>=10)throw new IOException("CLIP 文件持续被写入，无法读取已保存的文档。");
            await Task.Delay(100,token);
        }
    }
    public void LayerReplaced(string replacedUuid,string reproductionUuid)=>_replacements.Add((replacedUuid,reproductionUuid));
    // Set when the return to the user's layer was planned from the structure; the state refresh at the
    // end of the native transaction reads the panel once and confirms it.
    private string? _expectedFinalLayer;
    public async Task ReturnToUserLayerAsync(CancellationToken token)
    {
        if(_session.CurrentLayer is not { } user||_request.CurrentLayer==user)return;
        // The user's own layer was not replaced: its UUID still names it in the saved structure.
        if(!_layers.TryGetValue(user.Uuid,out var layer)){_status("提示：未在 CSP 中找到原来所在的图层，保持在复现层。");return;}
        try
        {
            // The capture save after the replay is the latest structure and names the selected layer.
            if(_inspection is { } inspection && SavedCurrentLayer()?.Uuid is { } from &&
               LayerNavigation.Steps(inspection.Layers,from,user.Uuid) is { } steps)
            {
                for(int i=0;i<Math.Abs(steps);i++) await SelectAdjacentLayerAsync(steps<0,token);
                _expectedFinalLayer=layer.Name;
            }
            else{await NavigateToLayerAsync(layer.Name,token);_status($"已回到原来所在的图层「{layer.Name}」。");}
        }
        catch(Exception error)when(error is not OperationCanceledException)
        {_status($"提示：未能切回图层「{layer.Name}」：{error.Message}");}
    }
    public Task SelectAdjacentLayerAsync(bool upper,CancellationToken token)
        =>ShortcutAsync(upper?"layerselectupperlayer":"layerselectlowerlayer",token);
    private static string Revision(string path)
    {
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
    public void Status(string text)=>_status(text);
    public Task NavigateToLayerAsync(string name,CancellationToken token)=>new PacketStateRestorer(_feed,new PacketWindowsInput(_input),_status).RestoreLayerAsync(name,token);
    public async Task<CspCurrentLayerEvidence> ReadCurrentLayerAsync(CancellationToken token)
    {
        // Every confirmation follows a save. The saved document records the selected layer ID,
        // which does not depend on how the layer row looks (hidden, highlighted, scrolled).
        if(SavedCurrentLayer() is { } saved) return new(saved.Name,true,saved.Uuid);
        return await ReadPanelCurrentLayerAsync(token);
    }
    public async Task<CspCurrentLayerEvidence> ReadPanelCurrentLayerAsync(CancellationToken token)
    {
        using(EditTiming.Start(_transactionId,"currentLayerOcr"))
            await _feed.RequestAsync(["currentLayerState"],token);
        string name=_feed.Layer!; var matches=_layers.Values.Where(l=>LayerMapping.SameName(l.Name,name)).ToArray();
        return new(name,matches.Length==1,matches.Length==1?matches[0].Uuid:null);
    }
    public bool SelectionConfirmed=>_session.CurrentLayerShownOnPanel;
    private LayerReference? SavedCurrentLayer()
        => PacketReplay.J.Get(PacketReplay.J.Get(_feed.Layers,"canvas"),"current_layer_id") is {ValueKind:JsonValueKind.Number} id &&
           _saved is not null && _layers.Values.Where(l=>l.Id==id.GetInt64()).ToArray() is [var saved] ? saved : null;
    public async Task<CspHideEvidence> HideLayerAsync(CspLayerEvidence layer,CancellationToken token)
    {
        await ShortcutAsync("layervisiblelayer",token);
        var inspection=await SaveAndInspectAsync(token);
        var current=await ReadCurrentLayerAsync(token);
        bool hidden=inspection.Layers.Single(l=>l.Uuid==layer.Uuid).Visible==false && current.Uuid==layer.Uuid;
        return new(true,hidden,true,hidden,current.Name,hidden);
    }
    private ImageRgba? _clipboardImage;
    /// <summary>
    /// CSP pastes the clipboard's PNG (checked live: byte-identical to the export, with or without CF_DIBV5), so the
    /// first write is the PNG alone: encoded off the UI thread in ~80 ms, published without the 139 MB bitmap copy.
    /// Writing the same image again is a retry after CSP ignored a paste; it adds CF_DIBV5 as before.
    /// </summary>
    public async Task SetClipboardImageAsync(ImageRgba image,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); image.Validate();
        bool retry=ReferenceEquals(image,_clipboardImage); _clipboardImage=image;
        using var step=EditTiming.Start(_transactionId,retry?"clipboard (PNG+DIB)":"clipboard (PNG)");
        var (png,dib)=await Task.Run<(byte[] Png,byte[]? Dib)>(()=>retry?ClipboardImageCodec.Encode(image.Width,image.Height,image.Bytes)
            :(ClipboardImageCodec.EncodePng(image.Width,image.Height,image.Bytes),null),token);
        ClipboardImageCodec.Publish(png,dib);
    }
    public async Task PasteAsync(CancellationToken token)
    { await ShortcutAsync("selectdeselect",token); await ShortcutAsync("paste",token); }
    private Task ShortcutAsync(string command,CancellationToken token)
        =>_input.SendShortcutAsync(ShortcutConfiguration.Keys((_feed.Shortcuts ?? throw new InvalidOperationException("快捷键未就绪。")).ForCommand(command).Shortcut),token);
    public async Task<LayerExportEvidence> ExportLayerAsync(LayerKey layer,CancellationToken token)
    {
        if (_saved is null || !_layers.TryGetValue(layer.Uuid,out var reference)) throw new InvalidDataException("目标层不在最新保存结构中。");
        var size=_monitor.PixelSize ?? throw new InvalidDataException("完整画布尺寸不可用。");
        using var step=EditTiming.Start(_transactionId,"exportLayer");
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
        using var step=EditTiming.Start(_transactionId,$"replay {events.Count} {mode}");
        var schedules=BuildSchedules(_session,_request,events,mode,work.Name,work.Uuid);
        _penReplays++; // Injected pen input does not pass through WindowsViewInput.SentInputs.
        foreach(var drawing in events)_replayed.Add(drawing.Stroke.Key);
        if (events.Any(e=>e.ReadsReferenceLayers))
            await VerifyReferenceSceneAsync(events[0].Stroke.Layer,events,mode,references,token);
        await PacketReplayRunner.InitializeStatesAsync(_feed,false,true,token);
        var byKey=events.ToDictionary(e=>(e.Stroke.Key.SessionId,e.Stroke.Key.BeginAppendId));
        var options=ReplayTiming.Options(mode);
        var pauses=new PauseMeter();
        // The injection loop waits on a high-resolution timer between samples; on the UI thread every wait and
        // continuation competed with the editing panel and the progress frame (about 250 ms between strokes).
        try
        {
            await Task.Run(async()=>
            {
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
                    },new WindowsReplayEnvironment(_feed,_input){WrapInjector=pauses.Wrap},1,options,_status,token);
            },token);
        }
        finally
        {
            if(pauses.Count>0)EditTiming.Note(_transactionId,$"pen-up→pen-down {mode}",pauses.Summary);
        }
    }
    /// <summary>Times each pause between replayed strokes, from injecting a pen-up to injecting the next pen-down: the
    /// fixed discrete gap plus CSP committing the stroke. CSP keeps strokes with too short a pause as one undo step.</summary>
    private sealed class PauseMeter
    {
        private readonly List<double> _pauses=[];
        private long _up;
        internal int Count=>_pauses.Count;
        internal string Summary=>$"n={_pauses.Count} min {_pauses.Min():F1} avg {_pauses.Average():F1} max {_pauses.Max():F1} ms";
        internal PenReplayEngine.IPenInjector Wrap(PenReplayEngine.IPenInjector inner)=>new Measured(inner,this);
        private sealed class Measured(PenReplayEngine.IPenInjector inner,PauseMeter meter):PenReplayEngine.IPenInjector
        {
            public void Send(PenReplayEngine.MappedPoint point,PenReplayEngine.PointerTransition transition)
            {
                if(transition==PenReplayEngine.PointerTransition.Down&&meter._up!=0)meter._pauses.Add(Stopwatch.GetElapsedTime(meter._up).TotalMilliseconds);
                inner.Send(point,transition);
                if(transition is PenReplayEngine.PointerTransition.Up or PenReplayEngine.PointerTransition.UpAndLeave)meter._up=Stopwatch.GetTimestamp();
            }
            public void Dispose()=>inner.Dispose();
        }
    }
    /// <summary>
    /// How fast an edit replays: discrete contacts (precise undo) keep their recorded timing, like the early discrete
    /// mode, and follow 200 ms apart like packetreplayer's discrete mode (counted after CSP has handled the pen-up); a
    /// concentrated contact (event editing) may take as little as 20 ms. Every sample still waits
    /// until CSP has handled the previous one (back-pressure on its UI thread). Shorter pauses let CSP fold strokes into
    /// one undo step: with 15 ms a precise undo's ten replayed strokes became one (2026-10-22). On 2026-10-26 50 ms (the
    /// earlier spec) still folded them: a real precise undo's 38 strokes (pen-up→pen-down 85–114 ms) were one step, and
    /// on a scratch canvas after a full-canvas paste 38 strokes became 24 steps, and 2 while the user's real pen hovered
    /// over the tablet (the input shield stops its rays, not this); 100 ms (no hover) and 200 ms (with and without
    /// hover) kept all 38. The user sets the pause in the editing panel (0–1000 ms, kept in editing-settings.json beside
    /// Memoline.exe). Measured in CSP on 16 recorded contacts (1572 samples, recorded 4.2 s): injected within 20 ms each,
    /// Windows merged the updates CSP had not read and the strokes kept 33% of their ink, most collapsing into thin
    /// straight lines; with back-pressure they kept 100% and looked like a replay at the recorded speed (CSP draws
    /// about one sample per 1.9 ms with a G-pen, so that took 3.7 s). MEMOLINE_REPLAY_STROKE_MS /
    /// MEMOLINE_REPLAY_GAP_MS / MEMOLINE_REPLAY_SYNC override these for a fidelity check (stroke 0 replays the
    /// recorded timing, sync 0 drops the back-pressure).
    /// </summary>
    internal static class ReplayTiming
    {
        internal static double? StrokeSeconds => Milliseconds("MEMOLINE_REPLAY_STROKE_MS",20) is var ms && ms>0?ms/1000:null;
        internal const double DefaultGapMilliseconds=200,MaximumGapMilliseconds=1000;
        private static readonly string SettingsPath=Path.Combine(AppContext.BaseDirectory,"editing-settings.json");
        private static double? s_gap;
        /// <summary>The pause after each discrete contact; set in the editing panel and kept across runs.</summary>
        internal static double GapMilliseconds
        {
            get=>s_gap??=ReadGap();
            set
            {
                s_gap=Math.Clamp(value,0,MaximumGapMilliseconds);
                try{File.WriteAllText(SettingsPath,JsonSerializer.Serialize(new{discreteGapMs=s_gap}));}
                catch(Exception error) when (error is IOException or UnauthorizedAccessException){}
            }
        }
        private static double ReadGap()
        {
            try
            {
                using var settings=JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if(settings.RootElement.TryGetProperty("discreteGapMs",out var gap)&&gap.TryGetDouble(out double ms)&&double.IsFinite(ms))
                    return Math.Clamp(ms,0,MaximumGapMilliseconds);
            }
            catch(Exception error) when (error is IOException or UnauthorizedAccessException or JsonException){}
            return DefaultGapMilliseconds;
        }
        internal static double GapSeconds => Math.Clamp(Milliseconds("MEMOLINE_REPLAY_GAP_MS",GapMilliseconds),0,MaximumGapMilliseconds)/1000;
        internal static int SyncRounds => (int)Math.Clamp(Milliseconds("MEMOLINE_REPLAY_SYNC",1),0,4);
        internal static ReplayOptions Options(ReplayMode mode)
            =>mode==ReplayMode.Discrete?new(mode,GapSeconds,FixedGap:true,SyncRounds:SyncRounds)
                :new(mode,StrokeSeconds:StrokeSeconds,SyncRounds:SyncRounds);
        private static double Milliseconds(string variable,double fallback)
            =>double.TryParse(Environment.GetEnvironmentVariable(variable),System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,out double value)&&double.IsFinite(value)&&value>=0?value:fallback;
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
                    historical,BuildOverride(historical.Brush,e.Change))
                    {OverrideColor=e.Change?.Rgb is { } rgb?ColorRestorer.State(rgb):default};
            }).ToArray();
            var last=ordered[^1].Stroke;
            // Precise undo replays its closure, then its selection, each in order: the next stroke is one of the same batch.
            var batch=request.Changes.ContainsKey(last.Key)?request.Changes.Keys:(IEnumerable<EditableStrokeKey>)request.Closure;
            long? following=mode==ReplayMode.Discrete && request.Kind==EditKind.PreciseUndo
                ? batch.Select(k=>session.Index.Strokes[k])
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
        using var step=EditTiming.Start(_transactionId,"registerBaseline");
        string png=Path.Combine(_window.EditingHistory!.Root,"baseline-install-"+Guid.NewGuid().ToString("N")+".png");
        try
        {
            await File.WriteAllBytesAsync(png,capture.Png,token);
            _window.EditingHistory.RegisterBaseline(layer,png,Stopwatch.GetTimestamp()-_feed.View.ClockOriginTicks!.Value);
        }
        finally { SaveCapture.TryDelete(png); }
    }
    public void ReleaseInputs()=>_input.ReleaseInputs();
    private void LowerShield(){_shield?.Dispose();_shield=null;}
    public ValueTask DisposeAsync()
    { LowerShield(); _monitor.MessageReceived-=Message; if(_saved is not null)_backend.Cleanup(_saved); _input.ReleaseInputs(); return ValueTask.CompletedTask; }
}
