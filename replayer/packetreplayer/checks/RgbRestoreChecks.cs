using System.Drawing;
using System.Text.Json;
using PacketReplay;

internal static partial class PacketChecks
{
    private sealed class RgbFeed : IPacketStateFeed
    {
        internal int[] Values=[0,0,0];internal int Capture;internal bool Freeze,Hidden,WrongValue;internal string Slot="main";
        internal readonly List<string[]> Requests=[];
        public BrushSnapshot? Brush=>null; public string? Layer=>null;public JsonElement Layers=>default;public JsonElement LayersEvidence=>default;
        public SubtoolSnapshot? Subtools=>null;public ShortcutConfiguration? Shortcuts=>null;public long Revision=>Capture;
        public JsonElement Color{get;private set;}=Json(new { kind="rgb",rgb=new[]{0,0,0} });
        public JsonElement ColorInput{get;private set;}
        public void EnsureConnected(){}
        public Task RequestAsync(string[] modules,CancellationToken token,string? saveId=null)
        {
            token.ThrowIfCancellationRequested();Requests.Add(modules);
            if(modules.Contains("colorInputState"))
            {
                if(!Freeze || Capture==0)Capture++;
                string id="frame-"+Capture;
                ColorInput=Json(new { status=Hidden?"unknown":"changed",state=new { status="ok",activeTab="rgb",activeColorSlot=Slot,
                    captureFrameId=id,panelRoi=new[]{-100,50,400,240},
                    valueRegions=Values.Select((v,i)=>new { channel="rgb"[i].ToString(),value=v,status="ok",screenBbox=new[]{200,90+i*40,24,18} }).ToArray() },
                    evidence=new { captureId=id,panelRoi=new[]{-100,50,400,240},capturedTicks=Capture*100,captureEndTicks=Capture*100+1 } });
            }
            if(modules.Contains("colorState"))Color=Json(new {kind="rgb",rgb=Values});
            return Task.CompletedTask;
        }
    }
    private sealed class RgbWriter(RgbFeed feed):IPacketInput
    {
        internal readonly List<(Rectangle Area,int Value)> Writes=[];
        public void EnsureTarget(){}
        public Task NumberAsync(Rectangle area,double value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Writes.Add((area,(int)value));
            int i=(area.Top-90)/40;feed.Values[i]=feed.WrongValue?0:(int)value;return Task.CompletedTask;
        }
        public Task ShortcutAsync(ShortcutBinding b,CancellationToken t)=>throw new Exception("unexpected shortcut");
        public Task ClickAsync(Rectangle r,CancellationToken t)=>throw new Exception("unexpected click");
        public Task ScrollAsync(Rectangle r,int d,CancellationToken t)=>throw new Exception("unexpected scroll");
    }
    internal static async Task RgbRestoreAsync()
    {
        var target=Json(new {kind="rgb",rgb=new[]{12,34,56}});var feed=new RgbFeed();var input=new RgbWriter(feed);
        await new ColorRestorer(feed,input).RestoreAsync(target,default);
        Check(input.Writes.Select(w=>w.Value).SequenceEqual(new[]{12,34,56}),"RGB values are committed in channel order");
        Check(input.Writes.Select(w=>w.Area.Top).SequenceEqual(new[]{90,130,170}),"RGB writes use current OCR rectangles with negative-screen ROI");
        Check(feed.Requests.Count(r=>r.SequenceEqual(new[]{"colorInputState"}))==4,"each write gets its own fresh OCR readback");
        Check(PacketReplayRunner.ColorMatches(feed.Color,target),"colorState is verified after slider writes");
        input.Writes.Clear();feed.Requests.Clear();await new ColorRestorer(feed,input).RestoreAsync(target,default);
        Check(input.Writes.Count==0&&feed.Requests.Count==0,"unchanged confirmed colors require no writes or requests");
        foreach(string fault in new[]{"hidden","sub","old-frame","wrong-value"})
        {
            feed=new RgbFeed { Hidden=fault=="hidden",Slot=fault=="sub"?"sub":"main",Freeze=fault=="old-frame",WrongValue=fault=="wrong-value" };input=new(feed);
            await RejectAsync<ReplayStateException>(()=>new ColorRestorer(feed,input).RestoreAsync(target,default));
            Check(input.Writes.Count<2,"invalid panel, active slot or readback stops before the next channel");
        }
        using var cancel=new CancellationTokenSource();cancel.Cancel();feed=new();input=new(feed);
        await RejectAsync<OperationCanceledException>(()=>new ColorRestorer(feed,input).RestoreAsync(target,cancel.Token));
        Check(input.Writes.Count==0,"cancelled color restore never injects input");
    }
}
