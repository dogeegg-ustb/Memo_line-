using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using MemolineDemo.Editing;
using PacketReplay;

namespace MemolineDemo;

internal interface IDemoEditingHost
{
    bool IsRecording { get; }
    EditSession? CurrentSession { get; }
    Task<EditSession> BeginEditSessionAsync(CancellationToken token);
    Task<ReconstructionPlan> PlanEditAsync(EditRequest request,CancellationToken token);
    Task<ReconstructionResult> ExecuteEditAsync(EditRequest request,bool undoLimitConfirmed,CancellationToken token);
    Task<JsonElement> ReadCurrentCanvasViewAsync(CancellationToken token);
    Task<JsonElement> FreezeCanvasViewAsync(CancellationToken token)=>ReadCurrentCanvasViewAsync(token);
    Task<JsonElement> ConfirmBrushForEditingAsync(string name,CancellationToken token);
    Task<JsonElement> CaptureBrushPanelAsync(Rectangle region,CancellationToken token)
        => throw new NotSupportedException("识别器尚不支持截图笔刷属性选区。");
    void SetProgressObserver(Action<string>? observer) { }
    void ReportEditingError(string operation,Exception error) { }
}

internal sealed class EditingPanel : Form
{
    private const int WmMouseActivate=0x0021,MaNoActivate=3,WsExNoActivate=0x08000000,WsExToolWindow=0x80;
    private readonly IDemoEditingHost _host;
    private readonly Func<CanvasInteraction,EventSelectionOverlay> _overlayFactory;
    private EventSelectionOverlay? _overlay;
    private CanvasInteraction? _interaction;
    private EditSession? _shown;
    private EditRequest? _pending;
    private CancellationTokenSource? _operation;
    private long _requestVersion;
    private bool _busy;
    private bool _layingOut;
    private EditKind? _mode;
    private bool _choosingBrush;
    // The overlay is frozen and selectable while the editing boundary is still being built behind it.
    private bool _preparing;
    private int _beginAttempt;
    private Rectangle _selectionBounds;
    private JsonElement _liveBrushEvidence;
    private BrushParameterAvailability? _parameterAvailability;
    private string? _preciseSummary;
    private readonly TextBox _status=new(){Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,
        AutoSize=false,MaxLength=0,BackColor=SystemColors.Control,HideSelection=false,AccessibleName="操作状态与错误详情"};
    private readonly Label _summary=new(){Height=52,Padding=new(8)};
    private readonly Button _refresh=new(){Text="重新冻结",AutoSize=true};
    private readonly Button _close=new(){Text="返回作画",AutoSize=true};
    private readonly Button _eventPlan=new(){Text="预览编辑",AutoSize=true};
    private readonly Button _undoPlan=new(){Text="预览精确撤销",AutoSize=true};
    private readonly Button _execute=new(){Text="执行",AutoSize=true,Enabled=false};
    private readonly Button _cancel=new(){Text="取消复现",AutoSize=true,Enabled=false};
    private readonly RadioButton _rectangle=new(){Text="框选",AutoSize=true,Checked=true};
    private readonly RadioButton _lasso=new(){Text="圈选",AutoSize=true};
    private readonly Button _newRegion=new(){Text="重选范围",AutoSize=true};
    private readonly Button _clear=new(){Text="清空笔画",AutoSize=true};
    private readonly ListView _images=new(){Dock=DockStyle.Fill,View=View.LargeIcon,MultiSelect=false,ShowItemToolTips=true};
    private readonly ListView _events=new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,CheckBoxes=false};
    private readonly ImageList _thumbnails=new(){ImageSize=new(128,128),ColorDepth=ColorDepth.Depth32Bit};
    private readonly TreeView _details=new(){Dock=DockStyle.Fill,ShowNodeToolTips=true};
    private readonly TabControl _tabs=new(){Dock=DockStyle.Fill};
    private readonly CheckBox _delete=new(){Text="让所选事件不再发生",Checked=true,AutoSize=true};
    private readonly CheckBox _numberEnabled=new(){Text="调整数字参数",AutoSize=true};
    private readonly ComboBox _property=new(){Width=150,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly NumericUpDown _value=new(){Minimum=-100000,Maximum=100000,DecimalPlaces=3,Width=100};
    private readonly CheckBox _changeBrush=new(){Text="使用选取的笔刷属性",AutoSize=true};
    private readonly ComboBox _brush=new(){Width=220,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly Button _confirmBrush=new(){Text="更换笔刷 / 属性",AutoSize=true};
    private readonly PictureBox _brushPreview=new(){Width=340,Height=180,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(45,45,45),Visible=false};
    private readonly Label _help=new(){Dock=DockStyle.Top,Height=105,Padding=new(8)};
    private readonly FlowLayoutPanel _modeBar=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6)};
    private readonly Button _chooseEdit=new(){Text="事件编辑",AutoSize=true};
    private readonly Button _chooseUndo=new(){Text="精确撤销",AutoSize=true};
    private readonly FlowLayoutPanel _brushCaptureBar=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6),Visible=false};
    private readonly Button _captureBrush=new(){Text="截取并框选属性面板",AutoSize=true};
    private readonly Button _cancelBrush=new(){Text="返回画布选择",AutoSize=true};
    private readonly TabPage _imagePage=new("2 · 脏矩阵图片"),_eventPage=new("3 · 整笔"),_modifyPage=new("修改"),_planPage=new("执行方案");
    private readonly CheckBox _undoCapacity=new(){Text="预览撤销方案后确认 CSP 撤销容量",Height=64,Enabled=false};
    private readonly FlowLayoutPanel _tools=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6)};
    private readonly FlowLayoutPanel _options=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6),FlowDirection=FlowDirection.TopDown,WrapContents=false};
    private readonly FlowLayoutPanel _actions=new(){AutoSize=true,Padding=new(6)};

    internal EditingPanel(IDemoEditingHost host,Func<CanvasInteraction,EventSelectionOverlay>? overlayFactory=null)
    {
        _host=host;_overlayFactory=overlayFactory??(model=>new EventSelectionOverlay(model));
        _host.SetProgressObserver(text=>{if(!IsDisposed&&IsHandleCreated)BeginInvoke(()=>
        {if(_busy)_status.Text=text;else if(_preparing)_status.Text="画布已冻结，可以直接选范围。后台："+text+"（再次按 Alt+M 可取消）";});});
        Text="Memoline · 画布事件编辑";Size=new(470,850);MinimumSize=new(420,720);
        StartPosition=FormStartPosition.Manual;TopMost=true;KeyPreview=true;ShowInTaskbar=false;
        KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){if(_busy)_operation?.Cancel();else if(_choosingBrush)FinishBrushChoice();else CloseSelection();e.Handled=true;}};
        _tools.Controls.AddRange([_rectangle,_lasso,_newRegion,_clear]);
        _images.LargeImageList=_thumbnails;
        _events.Columns.Add("所选",50);_events.Columns.Add("整笔事件",85);_events.Columns.Add("子工具",200);
        foreach(var (page,control) in new (TabPage,Control)[]{(_imagePage,_images),(_eventPage,_events),(_modifyPage,_options),(_planPage,_details)})
        {page.AutoScroll=true;page.Controls.Add(control);_tabs.TabPages.Add(page);}
        var numberRow=new FlowLayoutPanel{AutoSize=true};numberRow.Controls.AddRange([_numberEnabled,_property,_value]);
        var brushRow=new FlowLayoutPanel{AutoSize=true};brushRow.Controls.AddRange([_changeBrush,_brush,_confirmBrush]);
        _options.Controls.AddRange([_delete,numberRow,brushRow,_brushPreview]);
        _undoCapacity.Visible=false;_details.Dock=DockStyle.None;_planPage.Controls.Add(_undoCapacity);
        void LayoutPlan(){int width=_planPage.ClientSize.Width,height=_planPage.ClientSize.Height,confirmation=_undoCapacity.Visible?_undoCapacity.Height:0;
            _details.Bounds=new(0,0,width,Math.Max(1,height-confirmation));_undoCapacity.Bounds=new(0,Math.Max(0,height-confirmation),width,64);}
        _planPage.Layout+=(_,_)=>LayoutPlan();_undoCapacity.VisibleChanged+=(_,_)=>LayoutPlan();
        _actions.Controls.AddRange([_refresh,_eventPlan,_undoPlan,_execute,_cancel,_close]);
        _modeBar.Controls.AddRange([_chooseUndo,_chooseEdit]);_brushCaptureBar.Controls.AddRange([_captureBrush,_cancelBrush]);
        foreach(var control in new Control[]{_tabs,_summary,_tools,_brushCaptureBar,_modeBar,_help,_status,_actions}){control.Dock=DockStyle.None;Controls.Add(control);}
        _status.TextChanged+=(_,_)=>PerformLayout();_status.FontChanged+=(_,_)=>PerformLayout();
        _chooseEdit.Click+=async(_,_)=>await SelectModeAsync(EditKind.EventEditing);_chooseUndo.Click+=async(_,_)=>await SelectModeAsync(EditKind.PreciseUndo);
        _captureBrush.Click+=async(_,_)=>await ConfirmBrushAsync();_cancelBrush.Click+=(_,_)=>FinishBrushChoice();
        _refresh.Click+=async(_,_)=>await BeginAsync();_close.Click+=(_,_)=>CloseSelection();
        _eventPlan.Click+=async(_,_)=>await PlanAsync(false);_undoPlan.Click+=async(_,_)=>await PlanAsync(true);
        _execute.Click+=async(_,_)=>await ExecuteAsync();_cancel.Click+=(_,_)=>_operation?.Cancel();
        _images.SelectedIndexChanged+=(_,_)=>ChooseImage();_newRegion.Click+=(_,_)=>BeginRegion();
        _rectangle.CheckedChanged+=(_,_)=>{if(_rectangle.Checked)BeginRegion();};
        _lasso.CheckedChanged+=(_,_)=>{if(_lasso.Checked)BeginRegion();};
        _clear.Click+=(_,_)=>_interaction?.SetSelectedEvents([]);
        _delete.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateOptions();};
        _numberEnabled.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateOptions();};
        _changeBrush.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateParameters();UpdateOptions();};
        _property.SelectedIndexChanged+=(_,_)=>{InvalidatePlan();if(_parameterAvailability?.Numbers.FirstOrDefault(n=>n.Key==_property.Text) is { } number)
            _value.Value=(decimal)Math.Clamp(number.RecordedValue,(double)_value.Minimum,(double)_value.Maximum);};_value.ValueChanged+=(_,_)=>InvalidatePlan();
        _brush.SelectedIndexChanged+=(_,_)=>{InvalidatePlan();UpdateParameters();};
        _confirmBrush.Click+=(_,_)=>BeginBrushChoice();_undoCapacity.CheckedChanged+=(_,_)=>UpdateExecutionAvailability();
        FormClosing+=(_,e)=>{e.Cancel=true;if(_busy)_operation?.Cancel();else CloseSelection();};
        ShowWelcome();UpdateOptions();UpdateSummary();
    }
    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);if(_layingOut||_tabs?.Parent!=this)return;_layingOut=true;
        try
        {
            int width=ClientSize.Width,top=0,bottom=ClientSize.Height;
            int HeightFor(Control control)=>control is FlowLayoutPanel?control.GetPreferredSize(new Size(width,0)).Height:control.Height;
            foreach(var control in new Control[]{_help,_modeBar,_brushCaptureBar,_tools,_summary})if(control.Visible)
            {int height=HeightFor(control);control.Bounds=new(0,top,width,height);top+=height;}
            if(_actions.Visible){int height=HeightFor(_actions);bottom-=height;_actions.Bounds=new(0,bottom,width,height);}
            if(_status.Visible)
            {
                // Grow ordinary messages, while keeping long diagnostics scrollable and the plan usable.
                int available=Math.Max(1,bottom-top),padding=Math.Min(8,(available-1)/2);
                int minimum=Math.Min(available-2*padding,_status.Font.Height*2+8);
                int reservedTabs=_tabs.Visible?Math.Min(170,Math.Max(0,available-2*padding-minimum)):0;
                int maximum=Math.Max(1,Math.Min(180,available-2*padding-reservedTabs));
                int textWidth=Math.Max(1,width-16-SystemInformation.VerticalScrollBarWidth-8);
                int measured=TextRenderer.MeasureText(_status.Text,_status.Font,new Size(textWidth,int.MaxValue),
                    TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPrefix).Height+8;
                int height=Math.Clamp(measured,Math.Min(minimum,maximum),maximum);
                bottom-=height+2*padding;_status.Bounds=new(8,bottom+padding,Math.Max(1,width-16),height);
            }
            _tabs.Bounds=new(0,top,width,Math.Max(1,bottom-top));
        }
        finally{_layingOut=false;}
    }
    // Keyboard focus belongs to the editor only while a frozen selection exists.
    // Failure/cancellation leaves the selected mode set, but no frozen surface.
    private bool KeepCspForeground=>_overlay is null||_busy||_choosingBrush||_preparing;
    protected override bool ShowWithoutActivation=>KeepCspForeground;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters=base.CreateParams;parameters.ExStyle|=WsExToolWindow;
            if(KeepCspForeground)parameters.ExStyle|=WsExNoActivate;
            return parameters;
        }
    }
    protected override void WndProc(ref Message message)
    {
        // Preserve the foreground CSP window while still delivering entry/cancel clicks.
        if(message.Msg==WmMouseActivate&&KeepCspForeground){message.Result=new(MaNoActivate);return;}
        base.WndProc(ref message);
    }
    internal Task OpenAsync()
    {
        if(_busy||_preparing){_operation?.Cancel();return Task.CompletedTask;}
        if(_overlay is null&&!_choosingBrush)ShowWelcome();
        if(!_choosingBrush)_overlay?.Show();Show();return Task.CompletedTask;
    }
    private void ShowWelcome()
    {
        _mode=null;UpdateStyles();MinimumSize=new(420,360);Size=new(470,420);
        _help.Text="选择本次要使用的功能。\r\n精确撤销：在当前图层选区，按历史路径恢复。\r\n事件编辑：选脏矩阵图片，再扫选整笔修改。";
        _tabs.Visible=_tools.Visible=_summary.Visible=false;_modeBar.Visible=true;
        _refresh.Visible=_eventPlan.Visible=_undoPlan.Visible=_execute.Visible=false;
        _cancel.Visible=false;_chooseEdit.Enabled=_chooseUndo.Enabled=_host.IsRecording;
        _status.Text=_host.IsRecording?"按下功能按钮后冻结 CSP 画布。":"当前没有正在录制的会话。";
    }
    private async Task SelectModeAsync(EditKind mode)
    {
        _mode=mode;ConfigureWorkflow();
        // Switching function on a frozen canvas keeps its boundary; only a fresh entry (or 重新冻结) freezes again.
        if(_overlay is not null&&_interaction is not null&&!_busy){InvalidatePlan();BeginRegion();UpdateSummary();return;}
        await BeginAsync();
    }
    private void ConfigureWorkflow()
    {
        bool undo=_mode==EditKind.PreciseUndo;
        _help.Height=undo?85:125;_summary.Height=undo?70:52;_modeBar.Visible=true;
        MinimumSize=new(420,undo?680:720);Size=new(470,850);_tabs.TabPages.Clear();
        if(!undo)_tabs.TabPages.AddRange([_imagePage,_eventPage,_modifyPage]);_tabs.TabPages.Add(_planPage);
        _help.Text=undo?"在冻结的 CSP 画布上框选或圈选范围。\r\n只处理当前用户所在图层。\r\n预览按路径计算的笔画及闭包，再确认撤销容量。":
            "1 · 在冻结的 CSP 画布上框选或圈选范围。\r\n2 · 从跨层图片列表选择一张脏矩阵。\r\n3 · 沿附近白线扫选整笔。\r\n    更换笔刷，或让这些事件不再发生。";
        _tabs.Visible=_tools.Visible=_summary.Visible=true;_refresh.Visible=_execute.Visible=true;
        _eventPlan.Visible=!undo;_undoPlan.Visible=undo;_clear.Visible=!undo;
    }
    private async Task BeginAsync()
    {
        if(!_host.IsRecording){_status.Text="当前没有正在录制的会话。";Show();return;}
        CloseOverlay();Hide();_operation?.Cancel();_operation?.Dispose();_operation=new();var token=_operation.Token;int attempt=++_beginAttempt;
        _shown=null;_interaction=null;InvalidatePlan();_images.Items.Clear();_events.Items.Clear();_thumbnails.Images.Clear();
        Preparing(true);
        try
        {
            // Freeze at once so the user can select; the boundary (save, layer captures, history) follows in the
            // background and the selection is evaluated as soon as it is ready.
            var view=await _host.FreezeCanvasViewAsync(token);
            if(IsDisposed||attempt!=_beginAttempt)return;
            var model=new CanvasInteraction(new CanvasAffineTransform(view));
            BindOverlay(_overlayFactory(model));
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;Bounds=new(area.Right-Width,area.Top,Width,Math.Min(850,area.Height));
            // The boundary asks the Recognizer for state it reads from the screen: the panel and the frozen overlay
            // may only be shown over CSP while they are invisible to screen capture. Otherwise wait, as before.
            bool selectEarly=UpdateCaptureExclusion();
            if(selectEarly)
            {
                _overlay!.Show();Owner=_overlay;BeginRegion();Show();
                _status.Text="画布已冻结，可以直接选范围。后台正在建立编辑边界（保存并读取历史）…再次按 Alt+M 可取消。";UpdateSummary();
            }
            var session=await _host.BeginEditSessionAsync(token);
            if(IsDisposed||attempt!=_beginAttempt)return;
            if(!selectEarly){_overlay!.Show();Owner=_overlay;BeginRegion();}
            BindSession(session);
            try{model.Attach(session);}catch{_shown=null;throw;}
            if(model.RegionSelection is null)_status.Text="编辑边界已建立。在冻结画布上选范围。";
        }
        catch(OperationCanceledException){if(attempt==_beginAttempt){_status.Text="建立编辑边界已取消。";CloseOverlay();_interaction=null;}}
        catch(Exception error)
        {
            if(attempt!=_beginAttempt)return;
            _host.ReportEditingError("无法进入画布选择",error);_status.Text="无法进入画布选择："+error.Message;CloseOverlay();_interaction=null;_shown=null;
        }
        finally{if(!IsDisposed&&attempt==_beginAttempt){Preparing(false);UpdateSummary();Show();if(!KeepCspForeground)Activate();}}
    }
    private void Preparing(bool preparing)
    {
        _preparing=preparing;if(_overlay is not null)_overlay.KeepTargetForeground=preparing;UpdateStyles();UpdateCaptureExclusion();
        _eventPlan.Enabled=_undoPlan.Enabled=_images.Enabled=!_busy&&!preparing;UpdateOptions();UpdateExecutionAvailability();
    }
    /// <summary>While Memoline waits on the Recognizer (boundary, execution) its windows are hidden from screen
    /// capture; otherwise visible, so the user's own screenshots still show them. False if Windows refused.</summary>
    private bool UpdateCaptureExclusion()
    {
        bool exclude=_busy||_preparing;
        bool done=CspSnapshotNative.ExcludeFromCapture(this,exclude);
        if(_overlay is { IsDisposed: false } overlay)done&=CspSnapshotNative.ExcludeFromCapture(overlay,exclude);
        return done;
    }
    private void BindOverlay(EventSelectionOverlay overlay)
    {
        _mode??=EditKind.EventEditing;ConfigureWorkflow();
        _interaction=overlay.Model;_overlay=overlay;overlay.KeepTargetForeground=_preparing;_preciseSummary=null;
        _interaction.RegionChanged+=(_,_)=>RefreshCandidates();_interaction.SelectionChanged+=(_,_)=>SelectionChanged();
        _overlay.TargetInvalidated+=(_,_)=>{if(_preparing)_operation?.Cancel();InvalidatePlan();CloseOverlay();_interaction=null;_shown=null;_status.Text="CSP 窗口已移动或关闭，请重新冻结画布后选择。";};
        _overlay.EscapeRequested+=(_,_)=>CloseSelection();
    }
    private void BindSession(EditSession session)
    {
        _shown=session;_liveBrushEvidence=session.LiveBrushEvidence;
        _preciseSummary=null;_delete.Checked=true;_changeBrush.Checked=false;
        _brushPreview.Image?.Dispose();_brushPreview.Image=null;_brushPreview.Visible=false;
        _brush.Items.Clear();foreach(string name in session.BrushChoices)_brush.Items.Add(name);
        if(_brush.Items.Count>0)_brush.SelectedIndex=0;
    }
    internal void BindSelection(EditSession session,EventSelectionOverlay overlay){BindOverlay(overlay);BindSession(session);}
    private void BeginRegion()
    {
        if(_overlay is null)return;
        InvalidatePlan();_overlay.ClearSelection();_overlay.Mode=_lasso.Checked?EventSelectionMode.RegionLasso:EventSelectionMode.RegionRectangle;
        _tabs.SelectedIndex=0;_status.Text=_mode==EditKind.PreciseUndo?"在冻结画布上选范围，然后预览当前图层的精确撤销方案。":"在冻结画布上选范围，然后从跨层图片列表选择一张脏矩阵。";
    }
    private void RefreshCandidates()
    {
        InvalidatePlan();_images.Items.Clear();_events.Items.Clear();_thumbnails.Images.Clear();
        if(_interaction is null)return;
        if(_mode==EditKind.PreciseUndo){UpdateSummary();return;}
        foreach(var candidate in _interaction.Candidates)
        {
            var image=candidate.Image;
            using var stream=new MemoryStream(DirtyMatrixPaster.ClipboardImageCodec.EncodePng(image.Now.Width,image.Now.Height,image.Now.Bytes));
            using var decoded=new Bitmap(stream);using var thumbnail=new Bitmap(128,128);using(var graphics=Graphics.FromImage(thumbnail))
            {
                for(int y=0;y<128;y+=8)for(int x=0;x<128;x+=8)
                {using var square=new SolidBrush(((x+y)/8)%2==0?Color.White:Color.FromArgb(225,225,225));graphics.FillRectangle(square,x,y,8,8);}
                float scale=Math.Min(128f/decoded.Width,128f/decoded.Height);float w=decoded.Width*scale,h=decoded.Height*scale;
                graphics.DrawImage(decoded,(128-w)/2,(128-h)/2,w,h);
            }
            _thumbnails.Images.Add(thumbnail);
            double seconds=(double)candidate.TriggerTicks/_shown!.Source(candidate.SourceSessionId??_shown.LiveView.SessionId).Frequency;
            _images.Items.Add(new ListViewItem($"{candidate.LayerName} · 第 {candidate.SessionOrder+1} 次录制 {seconds:0.00}s\r\n{image.Events.Count} 笔 · {(candidate.CanSelect?"可扫选":"不可编辑")}")
                {Tag=image.Key,ImageIndex=_thumbnails.Images.Count-1,ToolTipText=$"{image.Key.PacketId}/{image.Key.ImageId}\r\n{candidate.Reason}",ForeColor=candidate.CanSelect?SystemColors.WindowText:Color.DimGray});
        }
        _tabs.SelectedTab=_imagePage;UpdateSummary();
    }
    private void ChooseImage()
    {
        if(_images.SelectedItems.Count!=1||_interaction is null||_overlay is null)return;
        try
        {
            _overlay.SelectImage((DirtyImageKey)_images.SelectedItems[0].Tag!);
            _tabs.SelectedTab=_eventPage;_status.Text="在画布上移动光标显示附近白线；按住左键像矢量橡皮一样扫选整笔。同一次拖动只切换一次。";SelectionChanged();
        }
        catch(Exception error){_status.Text=error.Message;}
    }
    private void SelectionChanged()
    {
        InvalidatePlan();_events.Items.Clear();_property.Items.Clear();if(_interaction is null)return;
        if(_mode==EditKind.PreciseUndo)
        {
            _preciseSummary=null;
            if(_interaction.RegionSelection is { } region&&_shown is { } session)
            {
                var undo=PreciseUndoSelector.Select(session.Index,session.CurrentLayer,region);
                if(undo.IsValid)
                {
                    var layer=session.Index.Layers[session.CurrentLayer!.Value];
                    string name=CanvasAffineTransform.Text(CanvasAffineTransform.Get(layer.Packets.LastOrDefault()?.NowDescriptor??default,"layer"),"name")??layer.Layer.Uuid;
                    _preciseSummary=$"当前层「{name}」\r\n基础 {undo.BaseCount} 笔 + 闭包 {undo.ClosureCount} 笔，共 {undo.Selected.Count} 笔。";
                    _overlay?.ShowPreciseUndo(undo.Selected.Select(s=>s.Key));
                }
                else _preciseSummary=string.Join("；",undo.Issues.Select(issue=>issue.Message));
            }
            UpdateSummary();return;
        }
        foreach(var stroke in _interaction.EligibleStrokes.OrderByDescending(s=>s.SessionOrder).ThenByDescending(s=>s.BeginEventId))
            _events.Items.Add(new ListViewItem([_interaction.SelectedEvents.Contains(stroke.Key)?"●":"",stroke.BeginEventId.ToString(),stroke.BrushName??""])
                {Tag=stroke.Key,ForeColor=_interaction.SelectedEvents.Contains(stroke.Key)?Color.RoyalBlue:SystemColors.WindowText});
        UpdateParameters();
        UpdateSummary();
    }
    private void UpdateSummary()=>_summary.Text=_interaction?.RegionSelection is null?"等待画布选区。":
        !_interaction.IsReady?"已记录选区。编辑边界建立后立即计算。":
        _mode==EditKind.PreciseUndo?_preciseSummary??"已选画布范围。精确撤销只作用于当前图层，可直接预览方案。":
        _interaction.ChosenImage is null?$"范围内共 {_interaction.Candidates.Count} 张脏矩阵图片。请选择一张。":
        $"已选择单张脏矩阵，扫选 {_interaction.SelectedEvents.Count} 笔。";
    private void InvalidatePlan()
    {_requestVersion++;_pending=null;_execute.Enabled=false;_details.Nodes.Clear();_undoCapacity.Checked=false;_undoCapacity.Enabled=false;_undoCapacity.Visible=false;}
    private void UpdateOptions()
    {
        _numberEnabled.Enabled=_changeBrush.Enabled=!_busy&&!_delete.Checked;
        _property.Enabled=_value.Enabled=!_busy&&!_delete.Checked&&_numberEnabled.Checked;
        _brush.Enabled=false;
        _confirmBrush.Enabled=!_busy&&!_preparing&&_interaction?.SelectedEvents.Count>0;
    }
    private void UpdateParameters()
    {
        _property.Items.Clear();if(_shown is null||_interaction is null)return;
        var historical=_interaction.SelectedEvents.Select(key=>
        {
            var recorded=StateHistory.At(_shown.Source(key.SessionId).History,_shown.Index.Strokes[key].BeginEventId).Brush;
            var state=_shown.Index.Strokes[key].BrushState;
            return recorded??(state.ValueKind==JsonValueKind.Object?BrushSnapshot.Parse(JsonSerializer.SerializeToElement(new{status="changed",state})):null);
        }).ToArray();
        _parameterAvailability=BrushParameterContract.Evaluate(historical,BrushSnapshot.Parse(_liveBrushEvidence,live:true),
            _changeBrush.Checked?_brush.Text:null,_shown.Shortcuts);
        foreach(var option in _parameterAvailability.Numbers)_property.Items.Add(option.Key);
        if(_property.Items.Count>0)
        {_property.SelectedIndex=0;_value.Value=(decimal)Math.Clamp(_parameterAvailability.Numbers[0].RecordedValue,(double)_value.Minimum,(double)_value.Maximum);}
    }
    private void BeginBrushChoice()
    {
        if(_interaction?.SelectedEvents.Count is not >0)return;
        InvalidatePlan();_selectionBounds=Bounds;_choosingBrush=true;Owner=null;_overlay?.Hide();
        _modeBar.Visible=false;_tabs.Visible=_tools.Visible=_summary.Visible=false;_brushCaptureBar.Visible=true;
        _refresh.Visible=_eventPlan.Visible=_undoPlan.Visible=_execute.Visible=false;
        MinimumSize=new(420,280);Size=new(620,350);_help.Height=110;
        _help.Text="所选笔画已保留。\r\n在 CSP 中选择目标笔刷，并调整需要的属性。\r\n准备好后点击「截取并框选属性面板」，在冻结截图中框住完整笔刷属性面板。";
        _status.Text="仅切换笔刷和属性，完成截图选择前请保持画布和图层不变。";
        UpdateStyles();
    }
    private void FinishBrushChoice()
    {
        _choosingBrush=false;_brushCaptureBar.Visible=false;_modeBar.Visible=true;ConfigureWorkflow();Bounds=_selectionBounds;
        if(_overlay is not null){_overlay.Show();Owner=_overlay;}UpdateStyles();UpdateOptions();Show();if(!KeepCspForeground)Activate();
    }
    private async Task ConfirmBrushAsync()
    {
        if(_interaction is not { } model)return;
        try
        {
            Hide();using var picker=_overlayFactory(model);picker.Mode=EventSelectionMode.BrushPanelRectangle;
            Rectangle region=default;Bitmap? screenshot=null;
            picker.BrushPanelSelected+=rectangle=>{region=rectangle;screenshot=picker.CropScreenRegion(rectangle);picker.DialogResult=DialogResult.OK;picker.Close();};
            picker.EscapeRequested+=(_,_)=>{picker.DialogResult=DialogResult.Cancel;picker.Close();};
            picker.TargetInvalidated+=(_,_)=>{picker.DialogResult=DialogResult.Cancel;picker.Close();};
            if(picker.ShowDialog()==DialogResult.OK&&screenshot is not null)
            {using(screenshot)await AcceptBrushPanelAsync(region,screenshot);}
            else{_status.Text="已取消笔刷属性截图选择。";FinishBrushChoice();}
        }
        catch(Exception error){_status.Text="无法截取笔刷属性面板："+error.Message;FinishBrushChoice();}
    }
    private async Task AcceptBrushPanelAsync(Rectangle region,Bitmap screenshot)
    {
        InvalidatePlan();Busy(true);_operation?.Dispose();_operation=new();
        try
        {
            Owner=null;_overlay?.Hide();_status.Text="正在识别所选截图范围内的笔刷属性…";
            var evidence=await _host.CaptureBrushPanelAsync(region,_operation.Token);
            var current=await _host.ReadCurrentCanvasViewAsync(_operation.Token);
            var transform=new CanvasAffineTransform(current,_shown!.LiveView.SessionId,_interaction!.Transform.CanvasSize);
            if(new PointF[]{new(0,0),new(transform.CanvasSize.Width,0),new(0,transform.CanvasSize.Height)}.Any(point=>
                {var a=transform.CanvasToScreen(point);var b=_interaction.Transform.CanvasToScreen(point);return Math.Abs(a.X-b.X)>.25||Math.Abs(a.Y-b.Y)>.25;}))
            {CloseOverlay();_interaction=null;_shown=null;throw new InvalidDataException("选择笔刷期间画布视图发生变化，请重新冻结画布并选择。");}
            var brush=BrushSnapshot.Parse(evidence,live:true)??throw new InvalidDataException("截图内未确认完整笔刷属性。");
            _liveBrushEvidence=evidence;_delete.Checked=false;
            if(!_brush.Items.Contains(brush.Name))_brush.Items.Add(brush.Name);_brush.SelectedItem=brush.Name;_changeBrush.Checked=true;
            _brushPreview.Image?.Dispose();_brushPreview.Image=new Bitmap(screenshot);_brushPreview.Visible=true;
            UpdateParameters();_status.Text=$"已从所选属性截图确认「{brush.Name}」。执行时仍会逐项读回校验。";
        }
        catch(Exception error){_status.Text="无法采用笔刷属性："+error.Message;}
        finally{if(!IsDisposed){Busy(false);if(_choosingBrush)FinishBrushChoice();else{if(_overlay is not null){_overlay.Show();Owner=_overlay;}Show();if(!KeepCspForeground)Activate();}}}
    }
    private void UpdateExecutionAvailability()=>_execute.Enabled=!_busy&&!_preparing&&_pending is { } request &&
        (request.Kind!=EditKind.PreciseUndo||UndoCapacityPolicy.Check(request.Changes.Count,_shown?.VerifiedUndoCapacity,_undoCapacity.Checked).Allowed);
    private void Busy(bool busy)
    {
        _busy=busy;UpdateStyles();UpdateCaptureExclusion();_refresh.Enabled=_tools.Enabled=_delete.Enabled=!busy;_eventPlan.Enabled=_undoPlan.Enabled=_images.Enabled=!busy&&!_preparing;
        _chooseEdit.Enabled=_chooseUndo.Enabled=_captureBrush.Enabled=_cancelBrush.Enabled=!busy;
        _cancel.Enabled=_cancel.Visible=busy;_close.Enabled=!busy;if(_overlay is not null)_overlay.InteractionEnabled=!busy;
        UpdateOptions();UpdateExecutionAvailability();
    }
    private async Task PlanAsync(bool preciseUndo)
    {
        try
        {
            InvalidatePlan();long version=_requestVersion;
            if(_preparing)throw new InvalidOperationException("后台仍在建立编辑边界，请稍候。");
            var session=_shown??throw new InvalidOperationException("请重新冻结画布。");
            if(_mode!=(preciseUndo?EditKind.PreciseUndo:EditKind.EventEditing))throw new InvalidOperationException("请先在面板选择对应功能。");
            var model=_interaction??throw new InvalidOperationException("画布选择尚未就绪。");
            var selection=model.RegionSelection?.Clone()??throw new InvalidOperationException("请先在 CSP 画布上选范围。");
            IReadOnlyDictionary<EditableStrokeKey,EventChange> changes;string detail;
            if(preciseUndo)
            {
                var undo=PreciseUndoSelector.Select(session.Index,session.CurrentLayer,selection);
                if(!undo.IsValid)throw new InvalidOperationException(string.Join("；",undo.Issues.Select(i=>i.Message)));
                changes=undo.Selected.ToDictionary(s=>s.Key,_=>new EventChange(Delete:true));
                var current=session.Index.Layers[session.CurrentLayer!.Value];
                string name=CanvasAffineTransform.Text(CanvasAffineTransform.Get(current.Packets.LastOrDefault()?.NowDescriptor??default,"layer"),"name")??current.Layer.Uuid;
                detail=$"当前层「{name}」撤销：基础 {undo.BaseCount} 笔 + 叠放闭包 {undo.ClosureCount} 笔 = {undo.Selected.Count} 笔。";
                _overlay!.ShowPreciseUndo(undo.Selected.Select(s=>s.Key));
            }
            else
            {
                _overlay!.ShowEventSelection();
                if(model.ChosenImage is null||model.SelectedEvents.Count==0)throw new InvalidOperationException("请选择单张脏矩阵，然后在画布上扫选整笔。");
                if(!_delete.Checked&&!_numberEnabled.Checked&&!_changeBrush.Checked)throw new InvalidOperationException("请选择数字参数修改或更换子工具。");
                if(!_delete.Checked&&_numberEnabled.Checked&&_property.SelectedItem is null)throw new InvalidOperationException("所选笔画没有共同可修改的数字参数。");
                if(!_delete.Checked&&_changeBrush.Checked&&_brush.SelectedItem is null)throw new InvalidOperationException("没有可用的子工具快捷键路由。");
                if(!_delete.Checked&&_changeBrush.Checked&&!BrushSnapshot.SameName(BrushSnapshot.Parse(_liveBrushEvidence)?.Name??"",_brush.Text))
                    throw new InvalidOperationException("请通过「更换笔刷 / 属性」框选目标笔刷属性面板。");
                var numbers=new Dictionary<string,double>();
                if(!_delete.Checked&&_changeBrush.Checked)
                {
                    var target=BrushSnapshot.Parse(_liveBrushEvidence)!;
                    var sameTool=model.SelectedEvents.Select(key=>session.Index.Strokes[key])
                        .Where(stroke=>BrushSnapshot.SameName(stroke.BrushName??"",target.Name)).ToArray();
                    foreach(var number in target.Numbers.Where(number=>sameTool.All(stroke=>stroke.Numbers.ContainsKey(number.Key))))numbers[number.Key]=number.Value;
                }
                if(!_delete.Checked&&_numberEnabled.Checked)numbers[_property.Text]=(double)_value.Value;
                var change=_delete.Checked?new EventChange(Delete:true):new EventChange(BrushName:_changeBrush.Checked?_brush.Text:null,Numbers:numbers)
                    {ReplacementBrushState=_changeBrush.Checked?_liveBrushEvidence:default};
                changes=model.SelectedEvents.ToDictionary(k=>k,_=>change);detail=$"修改 {changes.Count} 个完整笔接触："+(_delete.Checked?"删除":"参数 / 子工具覆盖")+"。";
            }
            var request=new EditRequest(preciseUndo?EditKind.PreciseUndo:EditKind.EventEditing,session.CurrentLayer,selection,changes);
            var plan=await _host.PlanEditAsync(request,CancellationToken.None);if(IsDisposed||version!=_requestVersion)return;
            if(!plan.IsValid)throw new InvalidOperationException(string.Join("；",plan.Issues.Select(i=>i.Message)));
            _pending=request;RenderPlan(plan);_tabs.SelectedTab=_planPage;
            if(preciseUndo)
            {
                var capacity=UndoCapacityPolicy.Check(changes.Count,session.VerifiedUndoCapacity,false);
                _undoCapacity.Text=$"我已确认 CSP 撤销容量至少为 {capacity.Required}（{changes.Count} 笔 + 5）";
                _undoCapacity.Enabled=session.VerifiedUndoCapacity is null;_undoCapacity.Visible=true;detail+=" "+capacity.Message;
            }
            UpdateExecutionAvailability();_status.Text=detail;
        }
        catch(Exception error){_status.Text="方案不可执行："+error.Message;}
    }
    private void RenderPlan(ReconstructionPlan plan)
    {
        _details.Nodes.Clear();foreach(var layer in plan.Layers)
        {
            var node=_details.Nodes.Add($"图层 {layer.Layer.Uuid} · 种子 {layer.SeedPacketId}");
            foreach(var segment in layer.Segments)
            {
                var part=node.Nodes.Add($"{(segment.Kind==ReconstructionSegmentKind.Compose?"合成":"重放")} · {segment.Images.Count} 张矩阵 / {segment.Events.Count} 笔");
                foreach(var image in segment.Images)part.Nodes.Add($"矩阵 {image.PacketId}/{image.ImageId}");
                foreach(var stroke in segment.Events)part.Nodes.Add($"完整笔接触 {stroke.BeginAppendId}");
                foreach(var reason in segment.Reasons)part.Nodes.Add("原因："+reason.Rule);
            }
        }
        _details.ExpandAll();
    }
    private async Task ExecuteAsync()
    {
        if(_pending is not { } request||!_execute.Enabled)return;
        bool capacityConfirmed=_undoCapacity.Checked;Busy(true);_operation?.Dispose();_operation=new();var bounds=Bounds;var minimum=MinimumSize;
        try
        {
            Owner=null;_overlay?.Hide();_tabs.Visible=_tools.Visible=_summary.Visible=_options.Visible=false;
            _modeBar.Visible=false;_help.Height=42;_help.Text="正在复现所选事件。";
            MinimumSize=new(340,140);Size=new(620,230);var area=Screen.FromPoint(Cursor.Position).WorkingArea;Location=new(area.Left,area.Bottom-Height);Show();BringToFront();
            _status.Text="正在复现。可随时点击「取消复现」。";
            var result=await _host.ExecuteEditAsync(request,capacityConfirmed,_operation.Token);
            _status.Text=result.Status is ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified?
                "已完成，正常录制已恢复。下一次编辑请重新冻结画布。":"复现已停止："+string.Join("；",result.Issues.Select(i=>i.Message));
        }
        catch(OperationCanceledException){_status.Text="已取消复现，正常录制已恢复。";}
        catch(Exception error){_status.Text="编辑失败："+error.Message;}
        finally
        {
            CloseOverlay();_shown=null;_interaction=null;InvalidatePlan();
            if(!IsDisposed){MinimumSize=minimum;ConfigureWorkflow();Bounds=bounds;_options.Visible=true;Busy(false);Show();if(!KeepCspForeground)Activate();}
        }
    }
    private void CloseOverlay(){Owner=null;_overlay?.Dispose();_overlay=null;UpdateStyles();}
    private void CloseSelection(){if(_preparing)_operation?.Cancel();CloseOverlay();_shown=null;_interaction=null;InvalidatePlan();Hide();}
    internal void RenderSmoke(string directory)
    {
        Directory.CreateDirectory(directory);ShowInTaskbar=false;Opacity=0;Show();
        void Render(string name){PerformLayout();using var image=new Bitmap(Width,Height);DrawToBitmap(image,new(Point.Empty,Size));image.Save(Path.Combine(directory,name));
            File.WriteAllText(Path.Combine(directory,name+".layout.json"),JsonSerializer.Serialize(new{Tools=new{_tools.Visible,_tools.Bounds},Summary=new{_summary.Visible,_summary.Bounds,_summary.Text},Tabs=_tabs.Bounds,
                Status=new{_status.Bounds,_status.Text,_status.ReadOnly,_status.ScrollBars}}));}
        Render("editing-entry.png");_mode=EditKind.EventEditing;ConfigureWorkflow();
        for(int i=0;i<2;i++)
        {
            using var icon=new Bitmap(128,128);using var graphics=Graphics.FromImage(icon);graphics.Clear(Color.FromArgb(240,240,240));
            using var pen=new Pen(i==0?Color.FromArgb(35,35,35):Color.FromArgb(70,130,210),i==0?4:18);
            graphics.DrawBezier(pen,new Point(12,95),new Point(25,8),new Point(90,10),new Point(110,90));
            _thumbnails.Images.Add(icon);_images.Items.Add(new ListViewItem(i==0?"线稿 · 3.50s\r\n可扫选":"上色 · 4.10s\r\n可扫选"){ImageIndex=i});
        }
        _summary.Text="范围内共 2 张脏矩阵。请选择一张。";_status.Text="画布已冻结。在 CSP 原画面框选范围，再选择单张脏矩阵并扫选整笔。";
        Render("editing.png");Size=MinimumSize;Render("editing-minimum.png");
        _tabs.SelectedTab=_modifyPage;Render("editing-options.png");
        _tabs.SelectedTab=_planPage;
        _status.Text="方案不可执行：\r\n"+string.Join("\r\n",Enumerable.Range(1,12).Select(i=>
            $"原因 {i}：图层 4cd6b862-a094-49be-aac1-55f15c3b308d 的历史撤销命令缺少可验证的作用笔画，无法建立精确撤销路径。"))+"\r\n最终原因：该笔的历史归属尚未确认。";
        Render("editing-long-error-minimum.png");_status.Select(_status.TextLength,0);_status.ScrollToCaret();Render("editing-long-error-end.png");
        _mode=EditKind.PreciseUndo;ConfigureWorkflow();_summary.Text="当前图层：线稿\r\n基础 50 笔 + 闭包 3 笔，共 53 笔。";
        _status.Text="当前层的所选笔画已显示白线。预览方案后，确认撤销容量再执行。";
        _details.Nodes.Add("当前层：线稿 · 精确撤销 53 笔").Nodes.Add("重建删除所选笔画后的图层，再按原序逐笔重放。");
        _undoCapacity.Text="我已确认 CSP 撤销容量至少为 58（53 笔 + 5）";_undoCapacity.Enabled=_undoCapacity.Visible=true;
        Render("editing-undo.png");
        var minimum=MinimumSize;MinimumSize=new(340,140);Size=new(620,230);_tabs.Visible=_tools.Visible=_summary.Visible=_modeBar.Visible=false;
        _help.Height=42;_help.Text="正在复现所选事件。";
        _cancel.Enabled=_cancel.Visible=true;_status.Text="正在复现，按「取消复现」可停止当前操作。";Render("editing-progress.png");MinimumSize=minimum;Hide();
    }
    protected override void Dispose(bool disposing)
    {if(disposing){_host.SetProgressObserver(null);_operation?.Cancel();_operation?.Dispose();CloseOverlay();_thumbnails.Dispose();_brushPreview.Image?.Dispose();}base.Dispose(disposing);}
}
