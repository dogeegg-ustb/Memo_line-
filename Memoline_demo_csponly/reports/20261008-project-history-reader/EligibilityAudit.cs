using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public static class EligibilityAudit
{
    static object P(object value, string key) => value?.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);
    static JsonElement G(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    static string T(JsonElement value, string key) => G(value, key).ValueKind == JsonValueKind.String ? G(value, key).GetString() : null;
    static ulong U(JsonElement value, string key) => G(value, key).ValueKind == JsonValueKind.Number && G(value, key).TryGetUInt64(out var found) ? found : 0;
    static IEnumerable<object> E(object value) => value is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
    static object Mask(object value) => new { left=P(value,"Left"), top=P(value,"Top"), width=P(value,"Width"), height=P(value,"Height"), count=P(value,"Count") };
    public static string Run(object session, object index)
    {
        var assembly = session.GetType().Assembly;
        var frames = E(P(session,"Frames")).Cast<JsonElement>().ToArray();
        var builder = assembly.GetType("MemolineDemo.Editing.EditIndexBuilder");
        var usable = builder.GetMethod("Usable",BindingFlags.Static|BindingFlags.NonPublic);
        var latest = builder.GetMethod("Latest",BindingFlags.Static|BindingFlags.NonPublic);
        var resolve = builder.GetMethod("ResolveLayer",BindingFlags.Static|BindingFlags.NonPublic);
        var historyObjects = E(P(session,"History")).OrderBy(h=>Convert.ToUInt64(P(h,"Anchor"))).ThenBy(h=>Convert.ToUInt64(P(h,"Order"))).ToArray();
        var history = System.Array.CreateInstance(usable.GetParameters()[0].ParameterType.GetGenericArguments()[0], historyObjects.Length);
        for(int i=0;i<historyObjects.Length;i++) history.SetValue(historyObjects[i],i);
        var at = assembly.GetType("PacketReplay.StateHistory").GetMethod("At",BindingFlags.Static|BindingFlags.NonPublic);
        var classifierType = assembly.GetType("MemolineDemo.Editing.BrushClassifier");
        var classifier = Activator.CreateInstance(classifierType);
        var classify = classifierType.GetMethod("Classify");
        var catalogFromJson = assembly.GetType("MemolineDemo.Editing.ToolCatalog").GetMethod("FromJson",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
        var catalogs = frames.Where(f=>T(f,"kind")=="shortcutConfiguration").ToArray();
        var replayOperations = E(P(P(session,"Replay"),"Strokes")).Select(s=>Convert.ToUInt64(P(s,"OperationId"))).ToHashSet();
        var ends = frames.Where(f=>T(f,"kind")=="penEnd").Select(f=>U(f,"operationId")).ToHashSet();
        var counts = new Dictionary<string,int>();
        var details = new List<object>();
        foreach(var begin in frames.Where(f=>T(f,"kind")=="penBegin"))
        {
            ulong append=U(begin,"appendId"), ev=U(begin,"eventId"), operation=U(begin,"operationId");
            if(operation==0)operation=ev;
            bool Usable(string module)=>(bool)usable.Invoke(null,new object[]{history,frames,module,ev,append});
            JsonElement Latest(string module)=>(JsonElement)latest.Invoke(null,new object[]{history,module,ev});
            var reasons = new List<string>();
            var state = at.Invoke(null,new object[]{history,ev});
            bool layerUsable=Usable("currentLayerState"),clipUsable=Usable("clipState"),brushUsable=Usable("brushState"),viewUsable=Usable("canvasViewState");
            if(!layerUsable)reasons.Add("currentLayerStateUnusable"); if(!clipUsable)reasons.Add("clipStateUnusable");
            var brush=Latest("brushState"); var clip=Latest("clipState"); var view=Latest("canvasViewState");
            var catalogFrame=catalogs.LastOrDefault(f=>U(f,"appendId")<append);
            var catalog=catalogFromJson.Invoke(null,new object[]{G(catalogFrame,"data")});
            var classification=classify.Invoke(classifier,new object[]{brush,catalog});
            if(!(bool)P(classification,"Eligible"))reasons.Add("brushToolIneligible");
            if(!brushUsable || P(state,"Brush")==null)reasons.Add("brushStateUnusable");
            if(!viewUsable || P(state,"View")==null)reasons.Add("canvasViewStateUnusable");
            var layer=resolve.Invoke(null,new object[]{P(session,"Packets"),P(state,"Layer"),clip,P(session,"SessionId")});
            if(layer==null)reasons.Add("layerIdentityUnresolved");
            var location=G(G(begin,"data"),"penDownLocation");
            if(T(location,"region")!="canvasViewport" || T(location,"status")!="matched")reasons.Add("penLocationUnconfirmed");
            if(T(G(begin,"deviceSource"),"deviceType")!="pen")reasons.Add("nonPenSource");
            if(G(G(begin,"data"),"injected").ValueKind==JsonValueKind.True)reasons.Add("injectedInput");
            if(!ends.Contains(operation))reasons.Add("missingPenEnd");
            if(!replayOperations.Contains(operation))reasons.Add("missingNativeReplay");
            foreach(var reason in reasons){counts.TryGetValue(reason,out int n);counts[reason]=n+1;}
            details.Add(new{appendId=append,eventId=ev,operationId=operation,reasons,
                brush=T(G(brush,"state"),"name"),brushStatus=T(brush,"status"),brushCausalAmbiguous=G(G(brush,"evidence"),"causalAmbiguous").ValueKind==JsonValueKind.True,
                viewStatus=T(view,"status"),viewCausalAmbiguous=G(G(view,"evidence"),"causalAmbiguous").ValueKind==JsonValueKind.True,
                classificationReasons=P(classification,"Reasons")});
        }
        var strokes=E(P(P(index,"Strokes"),"Values")).Select(s=>new{
            key=P(s,"Key").ToString(),beginAppendId=P(P(s,"Key"),"BeginAppendId"),beginEventId=P(s,"BeginEventId"),operationId=P(s,"OperationId"),
            brush=P(s,"BrushName"),eligible=P(s,"Eligible"),isCausal=P(s,"IsCausal"),images=E(P(s,"Images")).Select(i=>i.ToString()).ToArray(),
            write=Mask(P(s,"WriteMask")),probe=Mask(P(s,"ProbeMask"))}).ToArray();
        var images=E(P(P(index,"Images"),"Values")).Select(i=>new{key=P(i,"Key").ToString(),isCausal=P(i,"IsCausal"),
            unreplayable=P(i,"HasUnreplayableEvents"),events=E(P(i,"Events")).Select(e=>e.ToString()).ToArray(),mask=Mask(P(i,"Mask")),write=Mask(P(i,"W"))}).ToArray();
        return JsonSerializer.Serialize(new{session=P(session,"SessionId"),rawPenBegin=details.Count,counts,
            frameCounts=frames.GroupBy(f=>T(f,"kind")).ToDictionary(g=>g.Key,g=>g.Count()),
            strokes,images,contacts=details,
            invalidations=frames.Where(f=>T(f,"kind")=="panelUpdateRequested"&&T(G(f,"data"),"phase")=="invalidate").Select(f=>new{
                append=U(f,"appendId"),data=G(f,"data"),relatedEventIds=G(f,"relatedEventIds")}).ToArray()}, new JsonSerializerOptions{WriteIndented=true});
    }
}
