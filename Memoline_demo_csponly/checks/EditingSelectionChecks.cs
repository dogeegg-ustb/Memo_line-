using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using MemolineDemo.Editing;
using StrokeReplay;

namespace MemolineDemo;

internal static class EditingSelectionChecks
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static int Run()
    {
        int checks=0;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);checks++;}
        var size=new Size(10,10);
        var forward=CanvasSelectionGeometry.Polygon(size,CanvasSelectionGeometry.Rectangle(new(2,3),new(8,9)));
        var reverse=CanvasSelectionGeometry.Polygon(size,CanvasSelectionGeometry.Rectangle(new(8,9),new(2,3)));
        Check(forward.Equals(reverse)&&forward.Count==36&&forward.Contains(2,3)&&!forward.Contains(8,9),"Reverse drags retain the same half-open pixels.");
        var triangle=CanvasSelectionGeometry.Polygon(size,[new(0,0),new(10,0),new(0,10)]);
        Check(triangle.Count==45&&triangle.Contains(7,1)&&!triangle.Contains(8,8),"Lassos retain their actual polygon.");
        var concave=CanvasSelectionGeometry.Polygon(size,[new(0,0),new(10,0),new(10,3),new(3,3),new(3,10),new(0,10)]);
        Check(concave.Count==51&&!concave.Contains(5,5)&&concave.Contains(1,8),"Concave lasso cutouts remain unselected.");
        Check(CanvasSelectionGeometry.Polygon(size,CanvasSelectionGeometry.Rectangle(new(-5,-5),new(20,20))).Count==100,"Masks remain inside canvas bounds.");
        Check(CanvasSelectionGeometry.Polygon(size,[new(1,1),new(2,2)]).IsEmpty,"Unfinished lassos cannot be committed.");
        var sparse=new PixelMask(10,10);sparse.Set(8,8);
        Check(!triangle.Intersects(sparse),"Actual mask pixels determine intersection.");
        var offset=new PixelMask(7,1,2,2);offset.Set(7,1);
        Check(triangle.Intersects(offset)&&offset.Intersects(triangle),"Cropped mask origins remain in canvas coordinates.");
        Exception? error=null;
        var thread=new Thread(()=>
        {
            try
            {
                var host=new FakeHost();var session=host.CurrentSession!;
                using var panel=new EditingPanel(host,model=>new EventSelectionOverlay(model,new FrozenCspDesktop(new Bitmap(1000,1000),new(0,0,1000,1000),new(0,0,1000,1000),0)));
                Invoke(panel,"OpenAsync");
                Check(host.Begins==0&&Get<EventSelectionOverlay?>(panel,"_overlay") is null,
                    "Alt+M opens the two-function panel before capturing or freezing any canvas.");
                var createParams=(CreateParams)typeof(EditingPanel).GetProperty("CreateParams",Private)!.GetValue(panel)!;
                Check(!panel.ShowInTaskbar&&(createParams.ExStyle&0x08000000)!=0&&
                    (bool)typeof(EditingPanel).GetProperty("ShowWithoutActivation",Private)!.GetValue(panel)!,
                    "Opening the Alt+M floating entry must preserve CSP foreground focus.");
                object[] mouseActivate=[Message.Create(panel.Handle,0x0021,0,new nint(0x02010001))];
                typeof(EditingPanel).GetMethod("WndProc",Private)!.Invoke(panel,mouseActivate);
                Check(((Message)mouseActivate[0]).Result==new nint(3),
                    "Clicking a floating entry button must keep CSP active without discarding the click.");
                bool Passive()=>(((CreateParams)typeof(EditingPanel).GetProperty("CreateParams",Private)!.GetValue(panel)!).ExStyle&0x08000000)!=0&&
                    (bool)typeof(EditingPanel).GetProperty("ShowWithoutActivation",Private)!.GetValue(panel)!;
                // The canvas is frozen before the boundary; the boundary saves through CSP's own shortcut, so neither the
                // panel nor the overlay may take the foreground while it is built.
                host.BeforeBegin=()=>
                {
                    Check(Passive()&&Get<EventSelectionOverlay?>(panel,"_overlay") is { KeepTargetForeground: true },
                        "The canvas is frozen and selectable before the boundary, which is built without activating Memoline.");
                    Check(Affinity(panel)==0x11&&Affinity(Get<EventSelectionOverlay>(panel,"_overlay"))==0x11,
                        "While the Recognizer reads CSP from the screen, the panel and frozen overlay are excluded from capture.");
                };
                Invoke(panel,"SelectModeAsync",EditKind.EventEditing);
                var model=Get<CanvasInteraction>(panel,"_interaction");
                Check(host.Begins==1&&Get<TabControl>(panel,"_tabs").TabPages.Count==4,
                    "Choosing event editing establishes its boundary and opens the image-first workflow.");
                Check(Affinity(panel)==0&&Affinity(Get<EventSelectionOverlay>(panel,"_overlay"))==0,
                    "Once the boundary is ready the windows are capturable again, so the user's screenshots show them.");
                createParams=(CreateParams)typeof(EditingPanel).GetProperty("CreateParams",Private)!.GetValue(panel)!;
                Check((createParams.ExStyle&0x08000000)==0,
                    "The frozen editor must allow keyboard input after the passive entry has finished capturing.");
                panel.Size=panel.MinimumSize;panel.PerformLayout();
                var tabBounds=Get<TabControl>(panel,"_tabs").Bounds;
                var status=Get<TextBox>(panel,"_status");
                Check(tabBounds.Top>=Get<Label>(panel,"_summary").Bottom&&tabBounds.Bottom<=status.Top&&tabBounds.Height>=170,
                    "At minimum size the image area cannot cover selection controls or the current-selection summary.");
                int shortStatusHeight=status.Height;
                string longStatus="方案不可执行：\r\n"+string.Join("\r\n",Enumerable.Range(1,24).Select(i=>
                    $"原因 {i}：图层 4cd6b862-a094-49be-aac1-55f15c3b308d 的历史撤销命令缺少可验证的作用笔画，无法建立精确撤销路径。"))+"\r\n最终原因：该笔的历史归属尚未确认。";
                status.Text=longStatus;
                Check(status.Height>shortStatusHeight&&status.Height<=180&&Get<TabControl>(panel,"_tabs").Height>=170,
                    "Text changes must grow long diagnostics immediately without consuming the minimum plan area.");
                status.SelectAll();
                Check(status.ReadOnly&&status.Multiline&&status.ScrollBars==ScrollBars.Vertical&&status.SelectedText==longStatus,
                    "Every UUID and issue must remain selectable for copying in the read-only status field.");
                status.Select(status.TextLength,0);status.ScrollToCaret();
                int finalLineY=status.GetPositionFromCharIndex(status.TextLength-1).Y;
                Check(status.GetCharIndexFromPosition(new Point(3,3))>0&&finalLineY>=0&&finalLineY<status.ClientSize.Height,
                    "The final error line must be reachable by scrolling at the minimum editor size.");
                status.Text="已恢复正常录制。";
                Check(status.Height==shortStatusHeight,
                    "A short status must release the space used by a previous long error without a manual layout call.");
                var images=Get<ListView>(panel,"_images");var events=Get<ListView>(panel,"_events");var execute=Get<Button>(panel,"_execute");
                Check(!events.CheckBoxes&&images.Items.Count==0&&events.Items.Count==0,"Opening selection must expose no direct event-checkbox shortcut.");
                var region=CanvasSelectionGeometry.Rectangle(new(0,0),new(10,10)).Select(model.Transform.CanvasToScreen).ToArray();
                model.SetRegion(region);
                Check(images.Items.Count==2&&events.Items.Count==0&&model.SelectedEvents.Count==0,"A region lists cross-layer dirty images without selecting strokes.");
                Check(images.View==View.LargeIcon&&images.LargeImageList!.ImageSize==new Size(128,128),
                    "Dirty matrices must be displayed as image cards, not a stroke checkbox list.");
                Invoke(panel,"PlanAsync",false);
                Check(host.Plans==0&&!execute.Enabled,"A region alone cannot skip image choice and sweep selection.");
                var chosen=model.Candidates.Single(c=>c.Image.Key.Layer.Uuid=="other");
                model.SelectImage(chosen.Image.Key);
                Check(events.Items.Count==1&&model.SelectedEvents.Count==0,"Choosing one image exposes only its complete contacts.");
                Invoke(panel,"PlanAsync",false);
                Check(host.Plans==0,"Image choice alone cannot silently select its events.");
                model.BeginSweep(model.Transform.CanvasToScreen(new(4,4)));model.EndSweep();
                using(var picker=new EventSelectionOverlay(model,new FrozenCspDesktop(new Bitmap(2000,1000),new(0,0,2000,1000),new(0,0,2000,1000),0)))
                {
                    Rectangle picked=default;picker.Mode=EventSelectionMode.BrushPanelRectangle;picker.BrushPanelSelected+=rectangle=>picked=rectangle;
                    picker.GetType().GetMethod("BeginPointer",Private)!.Invoke(picker,[new PointF(1200,100)]);
                    picker.GetType().GetMethod("EndPointer",Private)!.Invoke(picker,[new PointF(1450,450)]);
                    using var crop=picker.CropScreenRegion(picked);
                    Check(picked==new Rectangle(1200,100,250,350)&&crop.Size==picked.Size&&model.SelectedEvents.SetEquals(chosen.Image.Events),
                        "A brush-panel screenshot rectangle outside the canvas retains the previously swept complete contacts.");
                }
                Invoke(panel,"PlanAsync",false);
                Check(execute.Enabled&&host.Request!.Changes.Keys.SequenceEqual(chosen.Image.Events),"Canvas sweep keys must reach the real planner and executable request.");
                Check(host.Request!.CurrentLayer?.Uuid=="ink"&&host.Request.Changes.Keys.All(k=>session.Index.Strokes[k].Layer.Uuid=="other"),"Cross-layer event edits keep the independently verified actual current layer.");
                Check(Get<TreeView>(panel,"_details").Nodes.Count>0,"A valid plan must expose actual reconstruction segments.");
                Get<CheckBox>(panel,"_delete").Checked=false;Get<CheckBox>(panel,"_changeBrush").Checked=true;
                Get<ComboBox>(panel,"_brush").SelectedItem="Replacement";
                using(var screenshot=new Bitmap(240,360))Invoke(panel,"AcceptBrushPanelAsync",new Rectangle(10,20,240,360),screenshot);
                Check(host.SelectedPanel==new Rectangle(10,20,240,360)&&Get<PictureBox>(panel,"_brushPreview").Image is not null,
                    "The replacement brush must come from the user's screenshot rectangle and retain its visible crop.");
                Invoke(panel,"PlanAsync",false);
                Check(execute.Enabled&&host.Request!.Changes.Values.Single().BrushName=="Replacement","Subtool choice must become a planner override.");
                Check(host.Request!.Changes.Values.Single().NumberValues["brush_size"]==2,
                    "Choosing a brush screenshot applies its captured numeric values even without a separate numeric checkbox.");
                Get<CheckBox>(panel,"_numberEnabled").Checked=true;Get<NumericUpDown>(panel,"_value").Value=12;
                Invoke(panel,"PlanAsync",false);
                Check(host.Request!.Changes.Values.Single().NumberValues["brush_size"]==12,"Numeric and subtool changes can be combined in one event override.");
                Get<CheckBox>(panel,"_delete").Checked=true;
                Invoke(panel,"SelectModeAsync",EditKind.PreciseUndo);model=Get<CanvasInteraction>(panel,"_interaction");model.SetRegion(region);
                Invoke(panel,"PlanAsync",true);
                var capacity=Get<CheckBox>(panel,"_undoCapacity");
                Check(host.Request!.Kind==EditKind.PreciseUndo&&host.Request.Changes.Keys.All(k=>session.Index.Strokes[k].Layer.Uuid=="ink"),"Precise undo uses only the actual current layer, without image selection or sweeping.");
                Check(Get<TabControl>(panel,"_tabs").TabPages.Count==1&&images.Items.Count==0&&model.ChosenImage is null,
                    "Precise undo does not expose the event-editing image and sweep stages.");
                Check(Get<Label>(panel,"_summary").Text.Contains("基础 1 笔")&&Get<Label>(panel,"_summary").Text.Contains("共 1 笔"),
                    "Selecting a precise-undo region immediately exposes the actual current-layer contact and closure counts.");
                Check(!execute.Enabled&&capacity.Enabled&&capacity.Text.Contains("至少为 6"),"Even one undo stroke requires a visible S+5 capacity confirmation.");
                Check(Get<TreeView>(panel,"_details").Bottom<=capacity.Top&&capacity.Bottom<=capacity.Parent!.ClientSize.Height,
                    "The undo capacity checkbox must remain visible below the plan, never hidden beneath its tree.");
                capacity.Checked=true;Check(execute.Enabled,"Explicit capacity confirmation enables the reviewed undo plan.");
                Invoke(panel,"BeginRegion");
                Check(!execute.Enabled&&model.RegionSelection is null&&model.SelectedEvents.Count==0&&!capacity.Checked,"Reselecting resets all stale plan and capacity state.");
                Invoke(panel,"SelectModeAsync",EditKind.EventEditing);model=Get<CanvasInteraction>(panel,"_interaction");
                model.SetRegion(region);model.SelectImage(chosen.Image.Key);model.SetSelectedEvents(chosen.Image.Events);
                host.DelayedPlan=new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending=(Task)panel.GetType().GetMethod("PlanAsync",Private)!.Invoke(panel,[false])!;
                model.ClearSelection();host.DelayedPlan.SetResult(ReconstructionPlanner.Build(session.Index,host.Request!));
                while(!pending.IsCompleted){Application.DoEvents();Thread.Yield();}
                pending.GetAwaiter().GetResult();Check(!execute.Enabled,"A delayed preview cannot revive a superseded canvas selection.");
                int beginsBefore=host.Begins;
                Invoke(panel,"SelectModeAsync",EditKind.PreciseUndo);
                Check(host.Begins==beginsBefore&&Get<CanvasInteraction>(panel,"_interaction")==model&&model.RegionSelection is null,
                    "Switching function on a frozen canvas keeps its boundary and only resets the region.");
                host.DelayedBegin=new(TaskCreationOptions.RunContinuationsAsynchronously);
                var capture=(Task)panel.GetType().GetMethod("BeginAsync",Private)!.Invoke(panel,[])!;
                Check(Passive()&&!capture.IsCompleted&&Get<CanvasInteraction?>(panel,"_interaction") is { IsReady: false },
                    "While the boundary is still being built, the frozen selection is open and CSP keeps the foreground.");
                panel.OpenAsync().GetAwaiter().GetResult();
                while(!capture.IsCompleted){Application.DoEvents();Thread.Yield();}
                capture.GetAwaiter().GetResult();
                Check(status.Text.Contains("已取消")&&host.DelayedBegin.Task.IsCompleted==false,
                    "Alt+M during capture cancels the pending operation instead of reactivating a panel over CSP.");
                createParams=(CreateParams)typeof(EditingPanel).GetProperty("CreateParams",Private)!.GetValue(panel)!;
                Check((createParams.ExStyle&0x08000000)!=0&&
                    (bool)typeof(EditingPanel).GetProperty("ShowWithoutActivation",Private)!.GetValue(panel)!,
                    "Cancelled capture must leave a passive floating panel so CSP recognition can continue.");
                host.DelayedBegin=null;host.BeginError=new ArgumentException("Parameter is not valid.");
                Invoke(panel,"SelectModeAsync",EditKind.EventEditing);
                createParams=(CreateParams)typeof(EditingPanel).GetProperty("CreateParams",Private)!.GetValue(panel)!;
                Check(status.Text.Contains("Parameter is not valid")&&Get<EventSelectionOverlay?>(panel,"_overlay") is null&&
                    (createParams.ExStyle&0x08000000)!=0,
                    "Failed selection must preserve its error and CSP foreground rather than activating an unfrozen editor.");
                panel.MinimumSize=new(340,140);panel.Size=new(620,230);
                foreach(string field in new[]{"_tabs","_tools","_summary","_modeBar"})Get<Control>(panel,field).Visible=false;
                Get<Label>(panel,"_help").Height=42;Invoke(panel,"Busy",true);status.Text=longStatus;
                Check(status.Top>=Get<Label>(panel,"_help").Bottom&&status.Bottom<=Get<FlowLayoutPanel>(panel,"_actions").Top,
                    "Long progress errors must fit between the help and cancellation controls in the compact busy window.");
                status.Select(status.TextLength,0);status.ScrollToCaret();finalLineY=status.GetPositionFromCharIndex(status.TextLength-1).Y;
                createParams=(CreateParams)typeof(EditingPanel).GetProperty("CreateParams",Private)!.GetValue(panel)!;
                Check(finalLineY>=0&&finalLineY<status.ClientSize.Height&&(createParams.ExStyle&0x08000000)!=0,
                    "Compact progress diagnostics remain scrollable while the busy panel preserves CSP focus.");
            }
            catch(Exception caught){error=caught;}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(30)))throw new TimeoutException("Canvas panel checks did not finish.");
        if(error is not null)throw new InvalidOperationException("Canvas panel checks failed.",error);
        return checks;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
    private static uint Affinity(Control window)=>GetWindowDisplayAffinity(window.Handle,out uint affinity)?affinity:uint.MaxValue;
    private static T Get<T>(object target,string field)=>(T)target.GetType().GetField(field,Private)!.GetValue(target)!;
    private static void Invoke(object target,string method,params object[] args)=>((Task?)target.GetType().GetMethod(method,Private)!.Invoke(target,args))?.GetAwaiter().GetResult();
    private sealed class FakeHost : IDemoEditingHost
    {
        public bool IsRecording=>true;
        public EditSession? CurrentSession{get;}
        internal EditRequest? Request{get;private set;}
        internal int Plans{get;private set;}
        internal int Begins{get;private set;}
        internal Action? BeforeBegin{get;set;}
        internal Rectangle SelectedPanel{get;private set;}
        internal TaskCompletionSource<ReconstructionPlan>? DelayedPlan{get;set;}
        internal TaskCompletionSource<EditSession>? DelayedBegin{get;set;}
        internal Exception? BeginError{get;set;}
        internal FakeHost()
        {
            var ink=new LayerKey("selection",1,"ink");var other=new LayerKey("selection",1,"other");
            var full=new ImageRgba(10,10,new byte[400]);
            EditableStroke Stroke(ulong id,LayerKey layer,int x,int y)
            {
                var mask=new PixelMask(10,10);mask.Set(x,y);
                return new(new("selection",id),id,(long)id,(long)id+1,[],"Pencil",layer,false,BrushNumbers:new Dictionary<string,double>{{"brush_size",2}})
                {BeginEventId=id,WriteMask=mask,BrushState=BrushEvidence("Pencil").GetProperty("state"),
                    CoverageLabels=[new("label",id,"pen",1,2,1,[new(x,y,x+1,y)],["image"])]};
            }
            var strokes=new[]{Stroke(1,ink,1,1),Stroke(2,other,4,4)};
            var images=strokes.Select(s=>new DirtyImage(new(s.Layer,"packet","image"),s.WriteMask,full,full,[s.Key])).ToArray();
            strokes=strokes.Select(s=>s with{Images=[images.Single(i=>i.Key.Layer==s.Layer).Key]}).ToArray();
            var packets=images.Select(i=>new LayerPacket("packet",i.Key.Layer,10,full,full,[i],IsBoundary:true,CanvasWidth:10,CanvasHeight:10)).ToArray();
            var layers=packets.ToDictionary(p=>p.Layer,p=>new LayerIndex(p.Layer,[p],strokes.Where(s=>s.Layer==p.Layer).ToArray(),true,[],Current:full));
            var index=new EditIndex(layers,strokes.ToDictionary(s=>s.Key),images.ToDictionary(i=>i.Key),[]);
            CurrentSession=new(new("fake","selection",1000,[],new MemolineReplayDocument("fake","selection",1000,[]),packets,3,10),index,ink)
            {BrushChoices=["Pencil","Replacement"],LiveBrushEvidence=BrushEvidence("Pencil"),Shortcuts=new PacketReplay.ShortcutConfiguration([
                new("tool_pencil","Pencil",null,"P"),new("tool_replacement","Replacement",null,"R")]),CanvasViewEvidence=JsonSerializer.SerializeToElement(new{sessionId="selection",status="changed",state=new{transform=new
                {canvasPixelWidth=10,canvasPixelHeight=10,matrices=new{convention="sct-affine-row-major/v1",canvasSpace="normalized",screenToCanvas=new double[]{.001,0,0,0,.001,0},canvasToScreen=new double[]{1000,0,0,0,1000,0}}}}})};
        }
        public Task<EditSession> BeginEditSessionAsync(CancellationToken token)
        {BeforeBegin?.Invoke();Begins++;return BeginError is { } error?Task.FromException<EditSession>(error):DelayedBegin?.Task.WaitAsync(token)??Task.FromResult(CurrentSession!);}
        public Task<ReconstructionPlan> PlanEditAsync(EditRequest request,CancellationToken token)
        {Request=request;Plans++;return DelayedPlan?.Task??Task.FromResult(ReconstructionPlanner.Build(CurrentSession!.Index,request));}
        public Task<ReconstructionResult> ExecuteEditAsync(EditRequest request,bool undoLimitConfirmed,CancellationToken token)=>throw new InvalidOperationException("No CSP input in selection checks.");
        public Task<JsonElement> ReadCurrentCanvasViewAsync(CancellationToken token)=>Task.FromResult(CurrentSession!.CanvasViewEvidence);
        public Task<JsonElement> ConfirmBrushForEditingAsync(string name,CancellationToken token)=>Task.FromResult(BrushEvidence(name));
        public Task<JsonElement> CaptureBrushPanelAsync(Rectangle region,CancellationToken token){SelectedPanel=region;return Task.FromResult(BrushEvidence("Replacement"));}
        private static JsonElement BrushEvidence(string name)=>JsonSerializer.SerializeToElement(new{status="changed",state=new{name,
            properties=new[]{new{key="brush_size",type="number",status="ok",value=2}}},
            valueRegions=new[]{new{propertyKey="brush_size",category="number",status="ok",value=2,screenBbox=new[]{10,10,40,25}}}});
    }
}
