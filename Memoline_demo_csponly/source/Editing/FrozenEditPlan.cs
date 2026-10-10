using System.Security.Cryptography;
using System.Text.Json;

namespace MemolineDemo.Editing;

internal static class FrozenEditPlan
{
    internal static JsonElement Create(EditSession session,ReconstructionPlan plan)
    {
        var selection=plan.Request.Selection;
        var bits=new byte[checked((selection.Width*(long)selection.Height+7)/8)];
        foreach(var (x,y) in selection.Pixels())
        {
            long i=(y-selection.Top)*(long)selection.Width+x-selection.Left;
            bits[i>>3]|=(byte)(1<<(int)(i&7));
        }
        return JsonSerializer.SerializeToElement(new
        {
            schema="memoline-frozen-edit-plan/v1",sessionId=session.LiveView.SessionId,
            boundaryAppendId=session.LiveView.BoundaryAppendId,boundaryTicks=session.LiveView.BoundaryTicks,
            sources=session.Sources.Values.Select(s=>new{s.SessionId,s.Frequency,s.BoundaryAppendId,s.BoundaryTicks}).ToArray(),
            kind=plan.Request.Kind.ToString(),currentLayer=plan.Request.CurrentLayer,
            selection=new {selection.Left,selection.Top,selection.Width,selection.Height,bitOrder="row-major-lsb/v1",bits=Convert.ToBase64String(bits)},
            changes=plan.Request.Changes.OrderBy(c=>session.Index.Strokes.GetValueOrDefault(c.Key)?.SessionOrder??0).ThenBy(c=>c.Key.BeginAppendId).Select(c=>new
                {key=c.Key,delete=c.Value.Delete,numbers=c.Value.NumberValues,brushName=c.Value.BrushName,
                    replacementBrushState=c.Value.ReplacementBrushState.ValueKind==JsonValueKind.Undefined?(JsonElement?)null:c.Value.ReplacementBrushState}).ToArray(),
            layers=plan.Layers.Select(l=>new
            {
                layer=l.Layer,l.SeedPacketId,
                groups=l.Groups.Select(g=>new {g.PacketId,g.SessionOrder,g.SourceLayer,g.TriggerTicks,images=g.Images.Select(i=>i.Key).ToArray(),
                    unconfirmedHistoryIntervals=g.Images.Where(i=>i.UnknownHistoryInterval).Select(i=>i.Key).ToArray()}).ToArray(),
                untrusted=l.Untrusted.OrderBy(k=>k.PacketId).ThenBy(k=>k.ImageId).ToArray(),
                undoHistoryCheckpoints=session.Index.Layers[l.Layer].UndoCheckpoints
                    .Where(p=>l.Groups.Any(g=>g.PacketId==p.PacketId)).Select(p=>new
                    { p.PacketId, events=p.Events, requiresOriginalReplay=true,
                        seedSha256=Convert.ToHexString(SHA256.HashData(p.Seed.Bytes)),
                        expectedSha256=Convert.ToHexString(SHA256.HashData(p.Expected.Bytes)) }).ToArray(),
                segments=l.Segments.Select(s=>new {kind=s.Kind.ToString(),images=s.Images,events=s.Events,
                    reasons=s.Reasons.Select(r=>new {r.Rule,pixels=r.Pixels.Select(p=>new {p.X,p.Y}).ToArray()}).ToArray()}).ToArray(),
                currentSha256=session.Index.Layers[l.Layer].Current is { } current
                    ?Convert.ToHexString(SHA256.HashData(current.Bytes)):null
            }).ToArray()
        });
    }
}
