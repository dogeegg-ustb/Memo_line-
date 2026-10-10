using System.Text.Json;
using PacketReplay;

namespace MemolineDemo.Editing;

/// <summary>Property presence remains causal; only explicit checkbox readback proves no reference scene is needed.</summary>
internal static class ReferenceReadEvidence
{
    internal static HashSet<string>? OffKeys(JsonElement evidence,string expectedName)
    {
        if(evidence.ValueKind!=JsonValueKind.Object)return null;
        var state=evidence;
        if(evidence.TryGetProperty("state",out var envelopeState))
        {
            if(!J.Confirmed(evidence)||J.Get(J.Get(evidence,"evidence"),"causalAmbiguous").ValueKind==JsonValueKind.True)return null;
            state=envelopeState;
        }
        else if(J.Text(state,"status") is "unknown" or "ambiguous")return null;
        if(J.Text(state,"name") is not { } name||!BrushSnapshot.SameName(name,expectedName)||J.Get(state,"properties").ValueKind!=JsonValueKind.Array)return null;
        var rows=J.Array(J.Get(state,"properties")).Where(p=>J.Text(p,"key")?.StartsWith("anti_overflow.",StringComparison.Ordinal)==true).ToArray();
        string[] known=["anti_overflow.do_not_cross_lines_of_reference_layer","anti_overflow.color_margin",
            "anti_overflow.area_scaling","anti_overflow.scaling_mode","anti_overflow.fill_up_to_vector_path"];
        var keys=new HashSet<string>(StringComparer.Ordinal);
        if(rows.Length==0)return null;
        foreach(var row in rows)
        {
            string key=J.Text(row,"key")!;var value=J.Get(row,"value");
            if(!known.Contains(key)||!keys.Add(key)||J.Text(row,"status")!="ok"||J.Text(row,"type")!="checkbox"||
                !(value.ValueKind==JsonValueKind.False||value.ValueKind==JsonValueKind.String&&value.GetString()=="unchecked"))return null;
        }
        return keys;
    }
}
