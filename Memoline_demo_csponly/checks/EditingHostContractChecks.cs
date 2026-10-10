using System.Text.Json;
using MemolineDemo.Editing;
using PacketReplay;
using StrokeReplay;

namespace MemolineDemo;

internal static class EditingHostContractChecks
{
    internal static int Run()
    {
        int count=0;
        void Check(bool result,string message){if(!result)throw new InvalidOperationException(message);count++;}
        using var raw=JsonDocument.Parse("""{"status":"changed","state":{"transform":{"matrices":{"convention":"sct-affine-row-major/v1","screenToCanvas":[1,0,0,0,1,0]}}}}""");
        var bound=LivePacketFeed.BindCanvasViewToSession(raw.RootElement,"s");
        Check(bound.GetProperty("sessionId").GetString()=="s" && bound.GetProperty("state").GetProperty("transform").GetProperty("matrices").GetProperty("screenToCanvas").GetArrayLength()==6,
            "The selection host must preserve native matrices and bind the requested session.");
        bool rejected=false;
        try{LivePacketFeed.BindCanvasViewToSession(bound,"different");}catch(IOException){rejected=true;}
        Check(rejected,"A foreign-session matrix must not be relabelled as current.");
        var layer=new LayerKey("s",1,"layer");
        var pixels=new ImageRgba(2,1,new byte[8]);
        var index=new EditIndex(new Dictionary<LayerKey,LayerIndex>{[layer]=new(layer,[],[],true,[],Current:pixels)},
            new Dictionary<EditableStrokeKey,EditableStroke>(),new Dictionary<DirtyImageKey,DirtyImage>(),[]);
        var session=new EditSession(new("fake","s",1000,[],new MemolineReplayDocument("fake","s",1000,[]),[],1,1),index,layer);
        Check(DemoEditingHost.EditingBoundaryMatches(session,session),"An unchanged snapshot must retain its reviewed boundary.");
        var altered=pixels.Copy();altered.Bytes[0]=1;
        var after=session with {Index=index with {Layers=new Dictionary<LayerKey,LayerIndex>{[layer]=index.Layers[layer] with {Current=altered}}}};
        Check(!DemoEditingHost.EditingBoundaryMatches(session,after),"Pixel changes during selection must require another review.");
        var selection=new PixelMask(2,1);selection.Set(1,0);
        var key=new EditableStrokeKey("s",7);var imageKey=new DirtyImageKey(layer,"p","i");
        var request=EditRequest.Event(layer,selection,new Dictionary<EditableStrokeKey,EventChange>{[key]=new(Delete:true)});
        var segment=new ReconstructionSegment(ReconstructionSegmentKind.Replay,[imageKey],[key],[new("modifiedEvent",[(1,0)])]);
        var plan=new ReconstructionPlan(index,request,[new(layer,[],"p",new HashSet<DirtyImageKey>{imageKey},
            new Dictionary<DirtyImageKey,TrustReason>{[imageKey]=segment.Reasons[0]},[segment])],[]);
        var frozen=FrozenEditPlan.Create(session,plan);
        Check(frozen.GetProperty("selection").GetProperty("bits").GetString()=="Ag==","The journal must freeze the exact selected pixels.");
        Check(frozen.GetProperty("layers")[0].GetProperty("segments")[0].GetProperty("reasons")[0].GetProperty("Rule").GetString()=="modifiedEvent",
            "The complete reviewed compose/replay segments and reasons must be frozen.");
        return count;
    }
}
