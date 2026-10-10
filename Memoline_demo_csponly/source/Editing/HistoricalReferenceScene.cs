namespace MemolineDemo.Editing;

internal static class HistoricalReferenceScene
{
    internal static IReadOnlyDictionary<LayerKey,ImageRgba> BeforeStroke(EditIndex index,EditableStroke stroke)
    {
        var scene=new Dictionary<LayerKey,ImageRgba>();
        foreach(var (key,layer) in index.Layers)
        {
            if(key.SessionId!=stroke.Layer.SessionId||layer.Current is null||!layer.ChainValid)
                throw new InvalidDataException("历史参考图层缺少当前目标路由、完整且可信的捕获链："+key.Uuid);
            var next=layer.Packets.Where(p=>!p.IsRecordingBaseline&&
                (p.SessionOrder,p.TriggerTicks).CompareTo((stroke.SessionOrder,stroke.StartTicks))>=0)
                .OrderBy(p=>p.SessionOrder).ThenBy(p=>p.TriggerTicks).FirstOrDefault();
            scene.Add(key,next is null?layer.Current.Copy():new LayerTimeline(layer.Packets,layer.Current).BeforePacket(next.PacketId));
        }
        return scene;
    }
}
