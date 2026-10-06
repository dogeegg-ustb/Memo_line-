using System.Text.Json;
using BehaviorRecognizer.Realtime;
using BehaviorRecognizer.Storage.Memoline;
using CanvasLayerWatcher;

namespace MemolineDemo;

internal static class LayerAndRegionChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException(description); count++; }
        var regions = JsonSerializer.SerializeToElement(new Dictionary<string, int[]>
        {
            ["笔刷属性"] = [65,700,345,478], ["工具组"] = [65,102,345,598],
            ["工具栏"] = [0,72,65,1455], ["导航器"] = [2278,102,282,646],
            ["图层"] = [2278,819,282,708], ["画布视口"] = [410,72,1847,1432],
            ["色彩"] = [65,1178,345,349]
        });
        var map = PanelRegionMap.FromRegions(regions, 5, 100);
        foreach (var (x,y,expected) in new (int,int,string)[]
        { (100,800,"brushProperties"),(100,300,"brushSelection"),(20,500,"toolbar"),
          (2300,400,"navigator"),(2300,1000,"layers"),(1200,800,"canvasViewport"),(100,1300,"other") })
            Check(map.Classify(x,y).Region == expected, "Historical screen panel classification: " + expected);
        Check(map.Classify(410,700).Region == "canvasViewport", "Shared right edge is excluded from brush panels");
        Check(map.Classify(2560,1000).Region == "other", "Right edge of layer panel is excluded");
        Check(map.Classify(100,800) is { LayoutAppendId:5, LayoutTicks:100, CoordinateSource:"windowsCursor" }, "Location preserves workspace provenance");
        var unavailable = PanelRegionMap.FromWorkspaceStatus(JsonSerializer.SerializeToElement(new { status = "suspended", layout = new { regions } }));
        Check(unavailable.Classify(1200,800).Status == "layoutUnavailable", "Suspended layout is not reused");
        var negative = PanelRegionMap.FromRegions(JsonSerializer.SerializeToElement(new Dictionary<string,int[]> { ["画布视口"]=[-2000,-100,1000,800] }));
        Check(negative.Classify(-1500,0).Region == "canvasViewport", "Negative screen coordinates are retained");

        var fresh = JsonSerializer.SerializeToElement(new { layers = new[] { new { id=19, name="图层 1", uuid="new-layer" } } });
        var mapped = LayerMapping.FromFile(fresh,"图层1",new(3,"图层 1","old-layer"));
        Check(mapped is { Id:19, Uuid:"new-layer" }, "Saved layer name binds to new ID despite stale initialization identity");
        Check(LayerMapping.FromFile(fresh,"图层1",null).Id == 19, "Name maps without an initial core ID");
        foreach (var bad in new[] { JsonSerializer.SerializeToElement(new { layers=Array.Empty<object>() }),
            JsonSerializer.SerializeToElement(new { layers=new[] { new { id=19,name="图层 1" },new { id=20,name="图层1" } } }) })
        {
            try { LayerMapping.FromFile(bad,"图层1",new(3,"图层 1","old-layer")); }
            catch (InvalidDataException) { count++; continue; }
            throw new InvalidOperationException("Missing/ambiguous saved layer accepted");
        }

        RecorderRealtimeEvent Event(string channel,string kind,long ticks,object data,ulong? operation=null)
            => new(1,"test",ticks,channel,kind,false,0,(ulong)ticks,ticks,ticks,ticks,operation,null,null,JsonSerializer.SerializeToElement(data));
        RecorderRealtimeEvent Layer(long ticks) => Event("core.currentLayerState","stateUpdated",ticks,new { status="changed",state="Ink" });
        RecorderRealtimeEvent View(long ticks,int x) => Event("core.canvasViewState","stateUpdated",ticks,new
        { status="changed",state=new { canvasOriginScreenPx=new { x,y=0 },ocrScalePercent=100,ocrRotationDegrees=0 } });
        var small = PanelRegionMap.FromRegions(JsonSerializer.SerializeToElement(new Dictionary<string,int[]>
        { ["笔刷属性"]=[0,0,50,100],["画布视口"]=[50,0,100,100] }));
        var start = small.Classify(25,20);
        RecorderRealtimeEvent Pen(long ticks,string kind,int x,PenDownLocation location,ulong op=1)
            => Event("tablet",kind,ticks,new { x,y=20,heldKeys=Array.Empty<int>(),penDownLocation=location },op);
        var watcher = new WatchState(); watcher.Reset(); watcher.Accept(Layer(1)); watcher.Accept(View(2,50));
        watcher.SetCanvasArea(new(50,0,100,100));
        watcher.Accept(Pen(3,"penBegin",25,start)); watcher.Accept(Pen(4,"penSample",75,start)); watcher.Accept(Pen(5,"penEnd",75,start));
        Check(!watcher.Dirty && watcher.Accept(View(6,60)) is null, "A brush-panel contact crossing onto canvas is not drawing");
        var evidence = new RecognitionEvidence(); evidence.Reset("test",1000,0);
        evidence.Accept(Layer(1)); evidence.Accept(View(2,50));
        evidence.Accept(Pen(3,"penBegin",25,start)); evidence.Accept(Pen(4,"penSample",75,start)); evidence.Accept(Pen(5,"penEnd",75,start));
        var request = new CaptureRequest(watcher.Generation,10,"Ink",new(50,0,100,0),[1,2]);
        Check(RecognitionEvidence.Build(evidence.Snapshot(0,10),request,new(100,100)).Labels.Length==0,"Panel contacts stay out of dirty search coverage");
        var canvas = small.Classify(75,20);
        watcher.Accept(Pen(7,"penBegin",75,canvas,2)); watcher.Accept(Pen(8,"penEnd",80,canvas,2));
        Check(watcher.Dirty && watcher.Accept(View(9,70)) is not null,"Canvas contacts still trigger capture");
        evidence.Accept(Pen(7,"penBegin",75,canvas,2)); evidence.Accept(Pen(8,"penEnd",80,canvas,2));
        var dirty = RecognitionEvidence.Build(evidence.Snapshot(0,10),request,new(100,100));
        Check(dirty.Labels is [{ OperationId:2, PenDownLocation.Region:"canvasViewport" }],"Dirty labels retain the original pen starting region");
        Check(evidence.Snapshot(0,10).Inputs.Length==5,"Both panel and canvas mechanical input observations are retained");
        return count;
    }
}
