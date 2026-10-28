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
    string? RecycleRoot => null;
    void ReportEditingError(string operation,Exception error) { }
}

internal sealed class EditingPanel : Form
{
    private const int WmMouseActivate=0x0021,MaNoActivate=3,WsExNoActivate=0x08000000,WsExToolWindow=0x80,WsExLayered=0x80000,WsExTransparent=0x20;
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
    // A session's dirty images never change; each region change only picks among them.
    private readonly Dictionary<DirtyImageKey,Bitmap> _thumbnailCache=[];
    private readonly TreeView _details=new(){Dock=DockStyle.Fill,ShowNodeToolTips=true};
    private readonly TabControl _tabs=new(){Dock=DockStyle.Fill};
    private readonly CheckBox _delete=new(){Text="让所选事件不再发生",Checked=true,AutoSize=true};
    // Images of layers the user deleted ("已被删除") are hidden unless asked for; they are never editable.
    private readonly CheckBox _showDeleted=new(){Text="显示已被删除图层的脏矩阵",AutoSize=true,Dock=DockStyle.Top};
    // Images a Memoline-operation packet replaced ("旧版本") are hidden unless asked for; they are never editable.
    private readonly CheckBox _showSuperseded=new(){Text="显示被记艺线操作包取代的旧版本",AutoSize=true,Dock=DockStyle.Top};
    private readonly CheckBox _numberEnabled=new(){Text="调整数字参数",AutoSize=true};
    private readonly ComboBox _property=new(){Width=150,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly NumericUpDown _value=new(){Minimum=-100000,Maximum=100000,DecimalPlaces=3,Width=100};
    private readonly CheckBox _changeBrush=new(){Text="使用选取的笔刷属性",AutoSize=true};
    private readonly ComboBox _brush=new(){Width=220,DropDownStyle=ComboBoxStyle.DropDownList};
    // Replay sets it with CSP's screen colour picker from a swatch on the virtual screen.
    private readonly CheckBox _changeColor=new(){Text="改为颜色",AutoSize=true};
    private readonly Button _color=new(){Width=110,Height=27,FlatStyle=FlatStyle.Flat,Text="选择…"};
    private int[]? _chosenRgb;
    // Direct: CSP already shows the target brush, so the panel is framed on a fresh screenshot at once.
    private readonly Button _confirmBrush=new(){Text="截图笔刷属性面板…",AutoSize=true};
    // The frozen canvas covers CSP; switching brushes first needs it out of the way.
    private readonly Button _switchBrush=new(){Text="先去 CSP 换笔刷…",AutoSize=true};
    private readonly PictureBox _brushPreview=new(){Width=340,Height=180,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(45,45,45),Visible=false};
    private readonly Label _help=new(){Dock=DockStyle.Top,Height=105,Padding=new(8)};
    private readonly FlowLayoutPanel _modeBar=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6)};
    private readonly Button _chooseEdit=new(){Text="事件编辑",AutoSize=true};
    private readonly Button _chooseUndo=new(){Text="精确撤销",AutoSize=true};
    private readonly FlowLayoutPanel _brushCaptureBar=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6),Visible=false};
    private readonly Button _captureBrush=new(){Text="截取并框选属性面板",AutoSize=true};
    private readonly Button _cancelBrush=new(){Text="返回画布选择",AutoSize=true};
    private readonly TabPage _imagePage=new("2 · 脏矩阵图片"),_eventPage=new("3 · 整笔"),_modifyPage=new("修改"),_planPage=new("执行方案"),_recyclePage=new("回收箱");
    private readonly ListView _recycleList=new(){Dock=DockStyle.Fill,View=View.LargeIcon,MultiSelect=false,ShowItemToolTips=true};
    private readonly ImageList _recycleThumbs=new(){ImageSize=new(96,96),ColorDepth=ColorDepth.Depth32Bit};
    private readonly ListView _recycleProperties=new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,HeaderStyle=ColumnHeaderStyle.Nonclickable};
    private readonly Button _recycleCopy=new(){Text="复制图像到剪贴板",AutoSize=true,Enabled=false};
    private readonly Button _recycleOpen=new(){Text="打开文件夹",AutoSize=true,Enabled=false};
    private readonly Button _recycleRefresh=new(){Text="刷新",AutoSize=true};
    private readonly Button _chooseRecycle=new(){Text="回收箱",AutoSize=true};
    // The pause after each stroke precise undo replays: shorter lets CSP fold neighbouring strokes into one undo step.
    private readonly NumericUpDown _gap=new(){Minimum=0,Maximum=(decimal)WindowsCspEditingActions.ReplayTiming.MaximumGapMilliseconds,
        Increment=10,Width=70,Margin=new(0,5,0,3)};
    private SelectionProgressFrame? _progressFrame;
    // Precise undo starts by itself once a region exists; set while that region waits for the boundary.
    private bool _autoUndoPending;
    private readonly CheckBox _undoCapacity=new(){Text="预览撤销方案后确认 CSP 撤销容量",Height=64,Enabled=false};
    private readonly FlowLayoutPanel _tools=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6)};
    private readonly FlowLayoutPanel _options=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(6),FlowDirection=FlowDirection.TopDown,WrapContents=false};
    private readonly FlowLayoutPanel _actions=new(){AutoSize=true,Padding=new(6)};

    internal EditingPanel(IDemoEditingHost host,Func<CanvasInteraction,EventSelectionOverlay>? overlayFactory=null)
    {
        _host=host;_overlayFactory=overlayFactory??(model=>new EventSelectionOverlay(model));
        _host.SetProgressObserver(text=>{if(!IsDisposed&&IsHandleCreated)BeginInvoke(()=>
        {
            ReportProgress(text);
            if(_busy)_status.Text=text;else if(_preparing)_status.Text=(_autoUndoPending?"选区已固定，编辑边界就绪后自动精确撤销。后台：":"画布已冻结，可以直接选范围。后台：")+text+"（再次按 Alt+M 可取消）";
        });});
        Text="Memoline · 画布事件编辑";Size=new(470,850);MinimumSize=new(420,720);
        StartPosition=FormStartPosition.Manual;TopMost=true;KeyPreview=true;ShowInTaskbar=false;
        KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){if(_busy)_operation?.Cancel();else if(_choosingBrush)FinishBrushChoice();else CloseSelection();e.Handled=true;}};
        _tools.Controls.AddRange([_rectangle,_lasso,_newRegion,_clear]);
        _images.LargeImageList=_thumbnails;
        _events.Columns.Add("所选",50);_events.Columns.Add("整笔事件",85);_events.Columns.Add("子工具",200);
        foreach(var (page,control) in new (TabPage,Control)[]{(_imagePage,_images),(_eventPage,_events),(_modifyPage,_options),(_planPage,_details)})
        {page.AutoScroll=true;page.Controls.Add(control);_tabs.TabPages.Add(page);}
        _imagePage.Controls.Add(_showDeleted);_showDeleted.CheckedChanged+=(_,_)=>RefreshCandidates();
        _imagePage.Controls.Add(_showSuperseded);_showSuperseded.CheckedChanged+=(_,_)=>RefreshCandidates();
        var numberRow=new FlowLayoutPanel{AutoSize=true};numberRow.Controls.AddRange([_numberEnabled,_property,_value]);
        var colorRow=new FlowLayoutPanel{AutoSize=true};colorRow.Controls.AddRange([_changeColor,_color]);
        var brushRow=new FlowLayoutPanel{AutoSize=true};brushRow.Controls.AddRange([_changeBrush,_brush]);
        var brushCaptureRow=new FlowLayoutPanel{AutoSize=true};brushCaptureRow.Controls.AddRange([_confirmBrush,_switchBrush]);
        _options.Controls.AddRange([_delete,numberRow,colorRow,brushRow,brushCaptureRow,_brushPreview]);
        _recycleList.LargeImageList=_recycleThumbs;
        _recycleProperties.Columns.Add("属性",130);_recycleProperties.Columns.Add("值",260);
        var recycleButtons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true};recycleButtons.Controls.AddRange([_recycleCopy,_recycleOpen,_recycleRefresh]);
        var recycleSplit=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal};
        recycleSplit.Panel1.Controls.Add(_recycleList);recycleSplit.Panel2.Controls.Add(_recycleProperties);
        _recyclePage.Controls.Add(recycleSplit);_recyclePage.Controls.Add(recycleButtons);
        _recycleList.SelectedIndexChanged+=(_,_)=>ShowRecycleEntry();_recycleRefresh.Click+=(_,_)=>RefreshRecycle();
        _recycleCopy.Click+=(_,_)=>CopyRecycledImage();
        _recycleOpen.Click+=(_,_)=>{if(SelectedRecycleEntry() is { } entry)System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",entry.Directory){UseShellExecute=true});};
        _tabs.Selected+=(_,e)=>
        {
            if(e.TabPage==_recyclePage)RefreshRecycle();
            // Choosing what to change no longer needs the frozen canvas; CSP must be usable to pick a brush.
            else if(e.TabPage==_modifyPage)Unfreeze();
        };
        _undoCapacity.Visible=false;_details.Dock=DockStyle.None;_planPage.Controls.Add(_undoCapacity);
        void LayoutPlan(){int width=_planPage.ClientSize.Width,height=_planPage.ClientSize.Height,confirmation=_undoCapacity.Visible?_undoCapacity.Height:0;
            _details.Bounds=new(0,0,width,Math.Max(1,height-confirmation));_undoCapacity.Bounds=new(0,Math.Max(0,height-confirmation),width,64);}
        _planPage.Layout+=(_,_)=>LayoutPlan();_undoCapacity.VisibleChanged+=(_,_)=>LayoutPlan();
        _actions.Controls.AddRange([_refresh,_eventPlan,_undoPlan,_execute,_cancel,_close]);
        _gap.Value=(decimal)WindowsCspEditingActions.ReplayTiming.GapMilliseconds;
        _gap.ValueChanged+=(_,_)=>WindowsCspEditingActions.ReplayTiming.GapMilliseconds=(double)_gap.Value;
        var gapRow=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=new(3,0,3,0)};
        gapRow.Controls.AddRange([new Label{Text="精确撤销笔间隔",AutoSize=true,Margin=new(3,9,3,3)},_gap,
            new Label{Text="ms",AutoSize=true,Margin=new(3,9,3,3)}]);
        new ToolTip().SetToolTip(_gap,$"精确撤销重放每一笔后等待的时间（0–{WindowsCspEditingActions.ReplayTiming.MaximumGapMilliseconds:0} ms，" +
            $"默认 {WindowsCspEditingActions.ReplayTiming.DefaultGapMilliseconds:0}）。太短时 CSP 会把相邻几笔记成一个撤销步。");
        _modeBar.Controls.AddRange([_chooseUndo,_chooseEdit,_chooseRecycle,gapRow]);_chooseRecycle.Click+=(_,_)=>ShowRecycleOnly();_brushCaptureBar.Controls.AddRange([_captureBrush,_cancelBrush]);
        foreach(var control in new Control[]{_tabs,_summary,_tools,_brushCaptureBar,_modeBar,_help,_status,_actions}){control.Dock=DockStyle.None;Controls.Add(control);}
        _status.TextChanged+=(_,_)=>PerformLayout();_status.FontChanged+=(_,_)=>PerformLayout();
        _chooseEdit.Click+=async(_,_)=>await SelectModeAsync(EditKind.EventEditing);_chooseUndo.Click+=async(_,_)=>await SelectModeAsync(EditKind.PreciseUndo);
        _captureBrush.Click+=async(_,_)=>await ConfirmBrushAsync();_cancelBrush.Click+=(_,_)=>FinishBrushChoice();
        _refresh.Click+=async(_,_)=>await BeginAsync();_close.Click+=(_,_)=>CloseSelection();
        _eventPlan.Click+=async(_,_)=>await PlanAsync(false);_undoPlan.Click+=async(_,_)=>await PlanAsync(true);
        _execute.Click+=async(_,_)=>await ExecuteAsync();_cancel.Click+=(_,_)=>_operation?.Cancel();
        _images.SelectedIndexChanged+=(_,_)=>ChooseImage();
        _newRegion.Click+=async(_,_)=>{if(_overlay is null&&!_busy&&!_preparing)await BeginAsync();else BeginRegion();};
        _rectangle.CheckedChanged+=(_,_)=>{if(_rectangle.Checked)BeginRegion();};
        _lasso.CheckedChanged+=(_,_)=>{if(_lasso.Checked)BeginRegion();};
        _clear.Click+=(_,_)=>_interaction?.SetSelectedEvents([]);
        _delete.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateOptions();};
        _numberEnabled.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateOptions();};
        _changeBrush.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateParameters();UpdateOptions();};
        _changeColor.CheckedChanged+=(_,_)=>{InvalidatePlan();UpdateOptions();};_color.Click+=(_,_)=>ChooseColor();
        _property.SelectedIndexChanged+=(_,_)=>{InvalidatePlan();if(_parameterAvailability?.Numbers.FirstOrDefault(n=>n.Key==_property.Text) is { } number)
            _value.Value=(decimal)Math.Clamp(number.RecordedValue,(double)_value.Minimum,(double)_value.Maximum);};_value.ValueChanged+=(_,_)=>InvalidatePlan();
        _brush.SelectedIndexChanged+=(_,_)=>{InvalidatePlan();UpdateParameters();};
        _confirmBrush.Click+=async(_,_)=>await ConfirmBrushAsync();_switchBrush.Click+=(_,_)=>BeginBrushChoice();_undoCapacity.CheckedChanged+=(_,_)=>UpdateExecutionAvailability();
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
            // While executing, CSP receives injected pen strokes and panel clicks, each checked with WindowFromPoint:
            // the progress panel must not occlude any of them, so it is layered and click-through (cancel: Alt+M).
            if(_busy)parameters.ExStyle|=WsExLayered|WsExTransparent;
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
        _mode=null;ApplyStyles();MinimumSize=new(420,360);Size=new(470,420);
        _help.Text="选择本次要使用的功能。\r\n精确撤销：在当前图层选区，按历史路径恢复。\r\n事件编辑：选脏矩阵图片，再扫选整笔修改。";
        _tabs.Visible=_tools.Visible=_summary.Visible=false;_modeBar.Visible=true;
        _refresh.Visible=_eventPlan.Visible=_undoPlan.Visible=_execute.Visible=false;
        _cancel.Visible=false;_chooseEdit.Enabled=_chooseUndo.Enabled=_host.IsRecording;_chooseRecycle.Enabled=_host.RecycleRoot is not null;
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
        if(!undo)_tabs.TabPages.AddRange([_imagePage,_recyclePage,_eventPage,_modifyPage,_planPage]);else _tabs.TabPages.AddRange([_planPage,_recyclePage]);
        _help.Text=undo?"在冻结的 CSP 画布上框选或圈选范围。\r\n只处理当前用户所在图层，松手即自动精确撤销。\r\n原图层存入「回收箱」。":
            "1 · 在冻结的 CSP 画布上框选或圈选范围。\r\n2 · 从跨层图片列表选择一张脏矩阵。\r\n3 · 沿附近白线扫选整笔。\r\n    更换笔刷，或让这些事件不再发生。";
        _tabs.Visible=_tools.Visible=_summary.Visible=true;_refresh.Visible=_execute.Visible=true;
        _eventPlan.Visible=!undo;_undoPlan.Visible=false;_execute.Visible=!undo;_clear.Visible=!undo;
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
            _status.Text=model.RegionSelection is null?"编辑边界已建立。在冻结画布上选范围。":
                _mode==EditKind.PreciseUndo?"编辑边界已建立，选区已计算。可以预览精确撤销方案。":"编辑边界已建立。从跨层图片列表选择一张脏矩阵。";
        }
        catch(OperationCanceledException){if(attempt==_beginAttempt){_status.Text="建立编辑边界已取消。";CloseOverlay();_interaction=null;}}
        catch(Exception error)
        {
            if(attempt!=_beginAttempt)return;
            _host.ReportEditingError("无法进入画布选择",error);_status.Text="无法进入画布选择："+error.Message;CloseOverlay();_interaction=null;_shown=null;
        }
        finally{if(!IsDisposed&&attempt==_beginAttempt){Preparing(false);UpdateSummary();Show();if(!KeepCspForeground)Activate();}}
        if(!IsDisposed&&attempt==_beginAttempt&&_autoUndoPending)
        {
            _autoUndoPending=false;
            if(_shown is not null&&_interaction?.RegionSelection is not null)await AutoPreciseUndoAsync();
            else CloseProgressFrame();
        }
    }
    private void Preparing(bool preparing)
    {
        _preparing=preparing;if(_overlay is not null)_overlay.KeepTargetForeground=preparing;ApplyStyles();UpdateCaptureExclusion();
        _eventPlan.Enabled=_undoPlan.Enabled=_images.Enabled=!_busy&&!preparing;UpdateOptions();UpdateExecutionAvailability();
    }
    /// <summary>While Memoline waits on the Recognizer (boundary, execution) its windows are hidden from screen
    /// capture; otherwise visible, so the user's own screenshots still show them. False if Windows refused.</summary>
    private void ApplyStyles()
    {
        UpdateStyles();
        // A layered window without attributes is not drawn at all.
        if(_busy&&IsHandleCreated)CspSnapshotNative.SetLayeredWindowAttributes(Handle,0,235,2);
    }
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
        _interaction.RegionChanged+=(_,_)=>{RefreshCandidates();RequestAutoUndo();};_interaction.SelectionChanged+=(_,_)=>SelectionChanged();
        _overlay.TargetInvalidated+=(_,_)=>{if(_preparing)_operation?.Cancel();InvalidatePlan();CloseOverlay();_interaction=null;_shown=null;_status.Text="CSP 窗口已移动或关闭，请重新冻结画布后选择。";};
        _overlay.EscapeRequested+=(_,_)=>CloseSelection();
    }
    private void BindSession(EditSession session)
    {
        _shown=session;_liveBrushEvidence=session.LiveBrushEvidence;ClearThumbnailCache();
        _preciseSummary=null;_delete.Checked=true;_changeBrush.Checked=false;_changeColor.Checked=false;ShowChosenColor(null);
        _brushPreview.Image?.Dispose();_brushPreview.Image=null;_brushPreview.Visible=false;
        _brush.Items.Clear();foreach(string name in session.BrushChoices)_brush.Items.Add(name);
        if(_brush.Items.Count>0)_brush.SelectedIndex=0;
    }
    internal void BindSelection(EditSession session,EventSelectionOverlay overlay){BindOverlay(overlay);BindSession(session);}
    private void BeginRegion()
    {
        if(_overlay is null)return;
        InvalidatePlan();_overlay.ClearSelection();_overlay.Mode=_lasso.Checked?EventSelectionMode.RegionLasso:EventSelectionMode.RegionRectangle;
        _tabs.SelectedIndex=0;_status.Text=_mode==EditKind.PreciseUndo?"在冻结画布上选范围，松手后立即对当前图层精确撤销。":"在冻结画布上选范围，然后从跨层图片列表选择一张脏矩阵。";
    }
    private void RefreshCandidates()
    {
        InvalidatePlan();_images.Items.Clear();_events.Items.Clear();_thumbnails.Images.Clear();
        if(_interaction is null)return;
        if(_mode==EditKind.PreciseUndo){UpdateSummary();return;}
        foreach(var candidate in _interaction.Candidates.Where(c=>(_showDeleted.Checked||!c.Deleted)&&(_showSuperseded.Checked||!c.Superseded)))
        {
            var image=candidate.Image;
            if(!_thumbnailCache.TryGetValue(image.Key,out var thumbnail))_thumbnailCache[image.Key]=thumbnail=Thumbnail(image.Now);
            _thumbnails.Images.Add(thumbnail);
            double seconds=(double)candidate.TriggerTicks/_shown!.Source(candidate.SourceSessionId??_shown.LiveView.SessionId).Frequency;
            string origin=image.Operation is { } operation?(operation.PreciseUndo?"记艺线操作 · 精确撤销":"记艺线操作 · 新版本")+" · ":"";
            _images.Items.Add(new ListViewItem($"{candidate.LayerName} · 第 {candidate.SessionOrder+1} 次录制 {seconds:0.00}s\r\n{origin}{candidate.Strokes} 笔 · "+
                (candidate.Deleted?"已被删除":candidate.Superseded?"旧版本":candidate.CanSelect?"可扫选":"不可编辑"))
                {Tag=image.Key,ImageIndex=_thumbnails.Images.Count-1,ToolTipText=$"{image.Key.PacketId}/{image.Key.ImageId}\r\n{candidate.Reason}",ForeColor=candidate.CanSelect?SystemColors.WindowText:Color.DimGray});
        }
        _tabs.SelectedTab=_imagePage;UpdateSummary();
    }
    private static Bitmap Thumbnail(ImageRgba image)
    {
        // The same 32bpp ARGB bitmap the PNG codec would build from these RGBA bytes, without encoding and decoding it.
        using var full=new Bitmap(image.Width,image.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits=full.LockBits(new(0,0,image.Width,image.Height),System.Drawing.Imaging.ImageLockMode.WriteOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row=new byte[image.Width*4];
            for(int y=0;y<image.Height;y++)
            {
                image.Bytes.AsSpan(y*row.Length,row.Length).CopyTo(row);
                for(int x=0;x<row.Length;x+=4)(row[x],row[x+2])=(row[x+2],row[x]);
                System.Runtime.InteropServices.Marshal.Copy(row,0,bits.Scan0+y*bits.Stride,row.Length);
            }
        }
        finally{full.UnlockBits(bits);}
        var thumbnail=new Bitmap(128,128);
        using(var graphics=Graphics.FromImage(thumbnail))
        {
            for(int y=0;y<128;y+=8)for(int x=0;x<128;x+=8)
            {using var square=new SolidBrush(((x+y)/8)%2==0?Color.White:Color.FromArgb(225,225,225));graphics.FillRectangle(square,x,y,8,8);}
            float scale=Math.Min(128f/full.Width,128f/full.Height);float w=full.Width*scale,h=full.Height*scale;
            graphics.DrawImage(full,(128-w)/2,(128-h)/2,w,h);
        }
        return thumbnail;
    }
    private void ClearThumbnailCache(){foreach(var thumbnail in _thumbnailCache.Values)thumbnail.Dispose();_thumbnailCache.Clear();}
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
                var undo=session.SelectPreciseUndo(region);
                if(undo.IsValid)
                {
                    string name=session.LayerName(undo.CurrentLayer!.Value);
                    _preciseSummary=(undo.CurrentLayer==session.CurrentLayer?$"当前层「{name}」":$"当前层在选区内没有笔画，改为最近的「{name}」")+
                        $"\r\n基础 {undo.BaseCount} 笔 + 闭包 {undo.ClosureCount} 笔，共 {undo.Selected.Count} 笔。";
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
        if(!_changeColor.Checked)ShowChosenColor(_interaction.SelectedEvents.Select(StrokeRgb).FirstOrDefault(rgb=>rgb is not null));
        UpdateSummary();
    }
    // The colour a stroke is drawn in now: the one an earlier edit gave it, else the recorded one.
    private int[]? StrokeRgb(EditableStrokeKey key)
    {
        if(_shown is null||!_shown.Index.Strokes.TryGetValue(key,out var stroke))return null;
        if(MemolineEdits.Current(stroke)?.Rgb is { } rgb)return [..rgb];
        return ColorRestorer.Rgb(StateHistory.At(_shown.Source(key.SessionId).History,stroke.BeginEventId).Color);
    }
    private void ChooseColor()
    {
        using var dialog=new ColorDialog{FullOpen=true,AnyColor=true,SolidColorOnly=true,
            Color=_chosenRgb is { } rgb?Color.FromArgb(rgb[0],rgb[1],rgb[2]):Color.Black};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        ShowChosenColor([dialog.Color.R,dialog.Color.G,dialog.Color.B]);
        _delete.Checked=false;_changeColor.Checked=true;InvalidatePlan();UpdateOptions();
    }
    private void ShowChosenColor(int[]? rgb)
    {
        _chosenRgb=rgb;
        _color.BackColor=rgb is null?SystemColors.Control:Color.FromArgb(rgb[0],rgb[1],rgb[2]);
        _color.ForeColor=rgb is null||rgb[0]*299+rgb[1]*587+rgb[2]*114>128000?Color.Black:Color.White;
        _color.Text=rgb is null?"选择…":$"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";
    }
    private void UpdateSummary()=>_summary.Text=_interaction?.RegionSelection is null?"等待画布选区。":
        !_interaction.IsReady?"已记录选区。编辑边界建立后立即计算。":
        _mode==EditKind.PreciseUndo?_preciseSummary??"已选画布范围。精确撤销只作用于当前图层，可直接预览方案。":
        _interaction.ChosenImage is null?$"范围内共 {_interaction.Candidates.Count(c=>!c.Deleted&&!c.Superseded)} 张脏矩阵图片"+
            (_interaction.Candidates.Count(c=>c.Deleted) is > 0 and var gone?$"（另有 {gone} 张属于已被删除的图层{(_showDeleted.Checked?"":"，默认不显示")}）":"")+
            (_interaction.Candidates.Count(c=>c.Superseded&&!c.Deleted) is > 0 and var old?$"（另有 {old} 张旧版本{(_showSuperseded.Checked?"":"，默认不显示")}）":"")+"。请选择一张。":
        $"已选择单张脏矩阵，扫选 {_interaction.SelectedEvents.Count} 笔。";
    private void InvalidatePlan()
    {_requestVersion++;_pending=null;_execute.Enabled=false;_details.Nodes.Clear();_undoCapacity.Checked=false;_undoCapacity.Enabled=false;_undoCapacity.Visible=false;}
    private void UpdateOptions()
    {
        _numberEnabled.Enabled=_changeBrush.Enabled=_changeColor.Enabled=!_busy&&!_delete.Checked;
        _color.Enabled=!_busy;
        _property.Enabled=_value.Enabled=!_busy&&!_delete.Checked&&_numberEnabled.Checked;
        _brush.Enabled=false;
        _confirmBrush.Enabled=_switchBrush.Enabled=!_busy&&!_preparing&&_interaction?.SelectedEvents.Count>0;
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
        ApplyStyles();
    }
    private void FinishBrushChoice()
    {
        _choosingBrush=false;_brushCaptureBar.Visible=false;_modeBar.Visible=true;ConfigureWorkflow();Bounds=_selectionBounds;
        if(_overlay is not null){_overlay.Show();Owner=_overlay;}ApplyStyles();UpdateOptions();Show();if(!KeepCspForeground)Activate();
    }
    private async Task ConfirmBrushAsync()
    {
        if(_interaction is not { } model)return;
        try
        {
            if(model.SelectedEvents.Count==0){_status.Text="请先扫选要更换笔刷的整笔事件。";return;}
            // The new picker freezes the screen again: the frozen canvas must not cover CSP's panels.
            Hide();Owner=null;_overlay?.Hide();
            await Task.Delay(150); // Let the compositor drop both windows before the screen is frozen.
            using var picker=_overlayFactory(model);picker.Mode=EventSelectionMode.BrushPanelRectangle;
            Rectangle region=default;Bitmap? screenshot=null;
            picker.BrushPanelSelected+=rectangle=>{region=rectangle;screenshot=picker.CropScreenRegion(rectangle);picker.DialogResult=DialogResult.OK;picker.Close();};
            picker.EscapeRequested+=(_,_)=>{picker.DialogResult=DialogResult.Cancel;picker.Close();};
            picker.TargetInvalidated+=(_,_)=>{picker.DialogResult=DialogResult.Cancel;picker.Close();};
            if(picker.ShowDialog()==DialogResult.OK&&screenshot is not null)
            {using(screenshot)await AcceptBrushPanelAsync(region,screenshot);}
            else{_status.Text="已取消笔刷属性截图选择。";EndBrushPick();}
        }
        catch(Exception error){_status.Text="无法截取笔刷属性面板："+error.Message;EndBrushPick();}
    }
    // The direct path never entered the brush-choice layout, so it must not restore that layout's saved bounds.
    private void EndBrushPick()
    {
        if(IsDisposed)return;
        if(_choosingBrush)FinishBrushChoice();
        else{if(_overlay is not null){_overlay.Show();Owner=_overlay;}Show();if(!KeepCspForeground)Activate();}
    }
    private async Task AcceptBrushPanelAsync(Rectangle region,Bitmap screenshot)
    {
        InvalidatePlan();Busy(true);_operation?.Dispose();_operation=new();
        try
        {
            Owner=null;_overlay?.Hide();_status.Text="正在识别所选截图范围内的笔刷属性…";
            var evidence=await _host.CaptureBrushPanelAsync(region,_operation.Token);
            // A frozen selection must still match CSP's view; once unfrozen the canvas-space selection does not depend on it.
            if(_overlay is not null&&await _host.ReadCurrentCanvasViewAsync(_operation.Token) is var current&&
               new CanvasAffineTransform(current,_shown!.LiveView.SessionId,_interaction!.Transform.CanvasSize) is var transform&&new PointF[]{new(0,0),new(transform.CanvasSize.Width,0),new(0,transform.CanvasSize.Height)}.Any(point=>
                {var a=transform.CanvasToScreen(point);var b=_interaction.Transform.CanvasToScreen(point);return Math.Abs(a.X-b.X)>.25||Math.Abs(a.Y-b.Y)>.25;}))
            {CloseOverlay();_interaction=null;_shown=null;throw new InvalidDataException("选择笔刷期间画布视图发生变化，请重新冻结画布并选择。");}
            var brush=BrushSnapshot.Parse(evidence,live:true)??throw new InvalidDataException("截图内未确认完整笔刷属性。");
            _liveBrushEvidence=evidence;_delete.Checked=false;
            if(!_brush.Items.Contains(brush.Name))_brush.Items.Add(brush.Name);_brush.SelectedItem=brush.Name;_changeBrush.Checked=true;
            _brushPreview.Image?.Dispose();_brushPreview.Image=new Bitmap(screenshot);_brushPreview.Visible=true;
            UpdateParameters();_status.Text=$"已从所选属性截图确认「{brush.Name}」。执行时仍会逐项读回校验。";
        }
        catch(Exception error){_status.Text="无法采用笔刷属性："+error.Message;}
        finally{if(!IsDisposed){Busy(false);EndBrushPick();}}
    }
    private void UpdateExecutionAvailability()=>_execute.Enabled=!_busy&&!_preparing&&_pending is { } request &&
        (request.Kind!=EditKind.PreciseUndo||UndoCapacityPolicy.Check(request.Changes.Count+request.Closure.Count,_shown?.VerifiedUndoCapacity,_undoCapacity.Checked).Allowed);
    private void Busy(bool busy)
    {
        _busy=busy;ApplyStyles();UpdateCaptureExclusion();_refresh.Enabled=_tools.Enabled=_delete.Enabled=!busy;_eventPlan.Enabled=_undoPlan.Enabled=_images.Enabled=!busy&&!_preparing;
        _chooseEdit.Enabled=_chooseUndo.Enabled=_captureBrush.Enabled=_cancelBrush.Enabled=!busy;
        // A busy panel is click-through (see CreateParams): Alt+M cancels, a button could not be pressed.
        _cancel.Enabled=_cancel.Visible=false;_close.Enabled=!busy;if(_overlay is not null)_overlay.InteractionEnabled=!busy;
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
            IReadOnlySet<EditableStrokeKey> closure=new HashSet<EditableStrokeKey>();
            if(preciseUndo)
            {
                var undo=session.SelectPreciseUndo(selection);
                if(!undo.IsValid)throw new InvalidOperationException(string.Join("；",undo.Issues.Select(i=>i.Message)));
                var undoRequest=undo.Request(selection);changes=undoRequest.Changes;closure=undoRequest.Closure;
                string name=session.LayerName(undo.CurrentLayer!.Value);
                detail=$"图层「{name}」撤销：基础 {undo.BaseCount} 笔 + 叠放闭包 {undo.ClosureCount} 笔 = {undo.Selected.Count} 笔。";
                _overlay?.ShowPreciseUndo(undo.Selected.Select(s=>s.Key));
            }
            else
            {
                _overlay?.ShowEventSelection();
                if(model.ChosenImage is null||model.SelectedEvents.Count==0)throw new InvalidOperationException("请选择单张脏矩阵，然后在画布上扫选整笔。");
                if(!_delete.Checked&&!_numberEnabled.Checked&&!_changeBrush.Checked&&!_changeColor.Checked)throw new InvalidOperationException("请选择数字参数修改、改颜色或更换子工具。");
                if(!_delete.Checked&&_changeColor.Checked&&_chosenRgb is null)throw new InvalidOperationException("请先点颜色按钮选择要改成的颜色。");
                if(!_delete.Checked&&_changeColor.Checked&&ColorRestorer.MissingPicker(session.Shortcuts) is { } noPicker)throw new InvalidOperationException(noPicker);
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
                    {ReplacementBrushState=_changeBrush.Checked?_liveBrushEvidence:default,Rgb=_changeColor.Checked?_chosenRgb:null};
                // A stroke an earlier edit changed is edited as that new version: the request carries both changes.
                changes=model.SelectedEvents.ToDictionary(k=>k,k=>MemolineEdits.Effective(session.Index.Strokes[k],change));detail=$"修改 {changes.Count} 个完整笔接触："+(_delete.Checked?"删除":"参数 / 颜色 / 子工具覆盖")+"。";
            }
            var request=new EditRequest(preciseUndo?EditKind.PreciseUndo:EditKind.EventEditing,session.CurrentLayer,selection,changes){Closure=closure};
            var plan=await _host.PlanEditAsync(request,CancellationToken.None);if(IsDisposed||version!=_requestVersion)return;
            if(!plan.IsValid)throw new InvalidOperationException(string.Join("；",plan.Issues.Select(i=>i.Message)));
            _pending=request;RenderPlan(plan);_tabs.SelectedTab=_planPage;
            if(preciseUndo)
            {
                var capacity=UndoCapacityPolicy.Check(changes.Count+closure.Count,session.VerifiedUndoCapacity,false);
                _undoCapacity.Text=$"我已确认 CSP 撤销容量至少为 {capacity.Required}（{changes.Count+closure.Count} 笔 + 5）";
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
        await RunExecutionAsync(request,_undoCapacity.Checked);
    }
    private async Task RunExecutionAsync(EditRequest request,bool capacityConfirmed)
    {
        Busy(true);_operation?.Dispose();_operation=new();var bounds=Bounds;var minimum=MinimumSize;
        try
        {
            Owner=null;_overlay?.Hide();_tabs.Visible=_tools.Visible=_summary.Visible=_options.Visible=false;
            _modeBar.Visible=false;_help.Height=42;_help.Text="正在复现所选事件。";
            MinimumSize=new(340,140);Size=new(620,230);var area=Screen.FromPoint(Cursor.Position).WorkingArea;Location=new(area.Left,area.Bottom-Height);Show();BringToFront();
            _status.Text="正在复现。再次按 Alt+M 可取消；此时面板不接收点击，以免挡住 CSP。";
            var result=await _host.ExecuteEditAsync(request,capacityConfirmed,_operation.Token);
            bool done=result.Status is ReconstructionStatus.Executed or ReconstructionStatus.PixelVerified;
            if(done)_progressFrame?.Complete();
            _status.Text=done?"已完成，原图层已存入「回收箱」，正常录制已恢复。下一次编辑请再按 Alt+M。":"复现已停止："+string.Join("；",result.Issues.Select(i=>i.Message));
        }
        catch(OperationCanceledException){_status.Text="已取消复现，正常录制已恢复。";}
        catch(Exception error){_status.Text="编辑失败："+error.Message;}
        finally
        {
            if(_progressFrame is not null){await Task.Delay(250);CloseProgressFrame();}
            CloseOverlay();_shown=null;_interaction=null;InvalidatePlan();
            if(!IsDisposed){MinimumSize=minimum;ConfigureWorkflow();Bounds=bounds;_options.Visible=true;Busy(false);Show();if(!KeepCspForeground)Activate();}
        }
    }
    private void CloseOverlay(){Owner=null;_overlay?.Dispose();_overlay=null;ApplyStyles();}
    /// <summary>Keeps the chosen strokes and plan inputs but gives CSP back to the user (to pick a brush).</summary>
    private void Unfreeze()
    {
        if(_overlay is null||_busy||_preparing||_mode!=EditKind.EventEditing)return;
        CloseOverlay();UpdateCaptureExclusion();
        _status.Text=_interaction?.SelectedEvents.Count>0
            ?$"已解冻 CSP：可在 CSP 中切换笔刷和属性。已选的 {_interaction.SelectedEvents.Count} 笔保留；需要改选请点「重选范围」重新冻结。"
            :"已解冻 CSP。尚未扫选整笔；请点「重选范围」重新冻结后再选。";
    }
    private void RequestAutoUndo()
    {
        if(_mode!=EditKind.PreciseUndo||_busy||_interaction?.RegionSelection is null||_interaction.ScreenPolygon is not { Count: >=3 } polygon)return;
        if(_overlay is not null)_overlay.InteractionEnabled=false; // The box is fixed from here on.
        _progressFrame??=new SelectionProgressFrame(polygon);_progressFrame.Show();_progressFrame.Progress=.03;
        if(_preparing||_shown is null){_autoUndoPending=true;_status.Text="选区已固定，编辑边界就绪后自动精确撤销…（再次按 Alt+M 可取消）";return;}
        BeginInvoke(async()=>await AutoPreciseUndoAsync());
    }
    private async Task AutoPreciseUndoAsync()
    {
        if(_busy||_shown is not { } session||_interaction?.RegionSelection is not { } region){CloseProgressFrame();return;}
        var undo=session.SelectPreciseUndo(region.Clone());
        if(!undo.IsValid)
        {
            CloseProgressFrame();if(_overlay is not null)_overlay.InteractionEnabled=true;BeginRegion();
            _status.Text="无法精确撤销："+string.Join("；",undo.Issues.Select(i=>i.Message))+" 请重新框选。";return;
        }
        // The target is the layer the selection found strokes on: the current one, or the nearest that has some.
        var request=undo.Request(region.Clone());
        if(undo.CurrentLayer!=session.CurrentLayer)_status.Text=$"当前图层在选区内没有笔画，精确撤销最近的图层「{session.LayerName(undo.CurrentLayer!.Value)}」…";
        if(_progressFrame is not null)_progressFrame.Progress=.08;
        // No preview and no undo-capacity confirmation: the user chose to run precise undo directly.
        await RunExecutionAsync(request,capacityConfirmed:true);
    }
    private void ReportProgress(string text)
    {
        if(_progressFrame is not { } frame)return;
        var replay=System.Text.RegularExpressions.Regex.Match(text,@"重放事件 (\d+)/(\d+)");
        if(replay.Success&&int.Parse(replay.Groups[2].Value) is > 0 and var total)frame.Progress=.55+.35*int.Parse(replay.Groups[1].Value)/total;
        else if(text.Contains("粘贴"))frame.Progress=.3;
        else if(text.Contains("保存")||text.Contains("切换图层"))frame.Progress=Math.Max(frame.Progress,.15);
    }
    private void CloseProgressFrame(){_progressFrame?.Close();_progressFrame?.Dispose();_progressFrame=null;}
    private void ShowRecycleOnly()
    {
        _mode=null;CloseOverlay();_tabs.TabPages.Clear();_tabs.TabPages.Add(_recyclePage);
        _tabs.Visible=true;_tools.Visible=_summary.Visible=false;MinimumSize=new(420,560);Size=new(470,760);
        _help.Text="事件编辑和精确撤销替换掉的原图层。\r\n选中一项查看图层属性；复制图像后在 CSP 按 Ctrl+V，会粘贴到原位置。";
        _refresh.Visible=_eventPlan.Visible=_undoPlan.Visible=_execute.Visible=false;RefreshRecycle();Show();
    }
    private void RefreshRecycle()
    {
        _recycleList.Items.Clear();foreach(Image image in _recycleThumbs.Images)image.Dispose();_recycleThumbs.Images.Clear();_recycleProperties.Items.Clear();
        _recycleCopy.Enabled=_recycleOpen.Enabled=false;
        if(_host.RecycleRoot is not { } root)return;
        foreach(var entry in RecycleBin.List(root))
        {
            int index=-1;
            try
            {
                if(File.Exists(entry.ImagePath))
                {
                    using var stream=File.OpenRead(entry.ImagePath);using var full=Image.FromStream(stream,false,false);
                    var thumb=new Bitmap(96,96);using(var g=Graphics.FromImage(thumb))
                    {
                        for(int y=0;y<96;y+=8)for(int x=0;x<96;x+=8){using var b=new SolidBrush(((x+y)/8)%2==0?Color.White:Color.FromArgb(225,225,225));g.FillRectangle(b,x,y,8,8);}
                        float scale=Math.Min(96f/full.Width,96f/full.Height);g.DrawImage(full,(96-full.Width*scale)/2,(96-full.Height*scale)/2,full.Width*scale,full.Height*scale);
                    }
                    _recycleThumbs.Images.Add(thumb);index=_recycleThumbs.Images.Count-1;
                }
            }
            catch(Exception error)when(error is IOException or ArgumentException or OutOfMemoryException){}
            _recycleList.Items.Add(new ListViewItem($"{entry.LayerName}\r\n{entry.Created:MM-dd HH:mm} · {entry.KindDisplay}"){Tag=entry,ImageIndex=index,ToolTipText=entry.OutcomeDisplay});
        }
    }
    private RecycleEntry? SelectedRecycleEntry()=>_recycleList.SelectedItems.Count==1?_recycleList.SelectedItems[0].Tag as RecycleEntry:null;
    private void ShowRecycleEntry()
    {
        _recycleProperties.Items.Clear();var entry=SelectedRecycleEntry();
        _recycleCopy.Enabled=entry is not null&&File.Exists(entry.ImagePath);_recycleOpen.Enabled=entry is not null;
        if(entry is null)return;
        foreach(var (name,value) in RecycleBin.DescribeProperties(entry))_recycleProperties.Items.Add(new ListViewItem([name,value]));
    }
    private void CopyRecycledImage()
    {
        if(SelectedRecycleEntry() is not { } entry)return;
        try
        {
            var image=RecycleBin.LoadImage(entry);
            DirtyMatrixPaster.ClipboardImageCodec.SetImage(image.Width,image.Height,image.Bytes);RecycleBin.MarkCopied(entry);
            _status.Text=$"已复制「{entry.LayerName}」的完整画布图像。在 CSP 中按 Ctrl+V，会作为新图层粘贴在原位置。";
        }
        catch(Exception error){_status.Text="无法复制回收箱图像："+error.Message;}
    }
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
    {if(disposing){_host.SetProgressObserver(null);_operation?.Cancel();_operation?.Dispose();CloseOverlay();_thumbnails.Dispose();ClearThumbnailCache();_brushPreview.Image?.Dispose();}base.Dispose(disposing);}
}
