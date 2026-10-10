using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemolineDemo.Editing;
using PacketReplay;

namespace MemolineDemo;

internal static class ActualProjectEditingChecks
{
    internal static async Task RunAsync(string path, string reportRoot)
    {
        Directory.CreateDirectory(reportRoot);
        string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Console.WriteLine("Reading the supplied sealed project and its original image assets...");
        var sources = await ProjectSessionReader.ReadProjectAsync(path,
            Path.Combine(reportRoot,"cache-"+Guid.NewGuid().ToString("N")));
        var current = sources[^1];
        Console.WriteLine("Building independently clocked historical indexes...");
        var indexes = sources.ToDictionary(s=>s.SessionId,s=>EditIndexBuilder.Build(s));
        var index = ProjectEditIndex.Build(sources.Take(sources.Count-1).ToArray(),current,indexes);
        Console.WriteLine($"Recovered {index.Strokes.Count} eligible contacts in {index.Images.Count} images.");
        var layer = index.Layers.Keys.Single(k=>k.SessionId==current.SessionId);
        var viewData = current.Frames.Where(f=>J.Text(f,"kind")=="coreStateUpdated")
            .Select(f=>J.Get(f,"data")).Last(d=>J.Text(d,"module")=="canvasViewState"&&J.Confirmed(d));
        var bound=JsonNode.Parse(viewData.GetRawText())!.AsObject(); bound["sessionId"]=current.SessionId;
        var session=new EditSession(current,index,layer)
        { Sources=sources.ToDictionary(s=>s.SessionId), CanvasViewEvidence=JsonSerializer.SerializeToElement(bound) };
        var interaction=new CanvasInteraction(session);
        var attempts=new List<object>();
        object? undoResult=null,eventResult=null;
        var groups=index.Layers[layer].Packets.Where(p=>!p.IsRecordingBaseline)
            .OrderBy(p=>p.SessionOrder).ThenBy(p=>p.TriggerTicks).ToArray();
        foreach(var sourceImage in groups.Reverse().SelectMany(p=>p.Images))
        {
            var strokes=sourceImage.Events.Select(k=>index.Strokes.GetValueOrDefault(k)).Where(s=>s is not null).Select(s=>s!).ToArray();
            if(strokes.Length==0)continue;
            (int X,int Y)? point=null;
            int g=Array.FindIndex(groups,p=>p.Images.Any(i=>i.Key==sourceImage.Key));
            var later=groups.Skip(g+1).SelectMany(p=>p.Images).ToArray();
            foreach(var pixel in sourceImage.Mask.Pixels())
                if(strokes.Any(s=>s.WriteMask.Contains(pixel.X,pixel.Y))&&!later.Any(i=>i.Mask.Contains(pixel.X,pixel.Y)))
                {point=pixel;break;}
            if(point is null)continue;
            var (x,y)=point.Value;
            foreach(bool completeImage in new[]{false,true})
            {
            var a=new PointF(Math.Max(0,x-20),Math.Max(0,y-20));
            var b=new PointF(Math.Min(interaction.Transform.CanvasSize.Width,x+21),Math.Min(interaction.Transform.CanvasSize.Height,y+21));
            if(completeImage)
            {
                a=new(sourceImage.Mask.Left,sourceImage.Mask.Top);
                b=new(sourceImage.Mask.Left+sourceImage.Mask.Width,sourceImage.Mask.Top+sourceImage.Mask.Height);
            }
            interaction.SetRegion(CanvasSelectionGeometry.Rectangle(interaction.Transform.CanvasToScreen(a),interaction.Transform.CanvasToScreen(b)));
            var selection=interaction.RegionSelection!;
            var undo=PreciseUndoSelector.Select(index,layer,selection);
            var undoRequest=new EditRequest(EditKind.PreciseUndo,layer,selection,
                undo.Selected.ToDictionary(s=>s.Key,_=>new EventChange(Delete:true)));
            var undoPlan=ReconstructionPlanner.Build(index,undoRequest);
            var candidate=interaction.Candidates.FirstOrDefault(c=>c.Image.Key==sourceImage.Key&&c.CanSelect);
            ReconstructionPlan? eventPlan=null;
            EditRequest? eventRequest=null;
            if(candidate is not null)
            {
                interaction.SelectImage(candidate.Image.Key);
                interaction.SetSelectedEvents(interaction.EligibleStrokes.Select(s=>s.Key));
                eventRequest=new(EditKind.EventEditing,layer,selection,
                    interaction.SelectedEvents.ToDictionary(k=>k,_=>new EventChange(Delete:true)));
                eventPlan=ReconstructionPlanner.Build(index,eventRequest);
            }
            attempts.Add(new {image=sourceImage.Key,completeImage,canvasRectangle=new {a.X,a.Y,right=b.X,bottom=b.Y},
                undoCandidates=undo.CandidateCount,undoSelected=undo.Selected.Count,undoValid=undo.IsValid,
                undoIssues=undo.Issues.Select(i=>i.Message).ToArray(),undoPlanValid=undoPlan.IsValid,
                undoPlanIssues=undoPlan.Issues.Select(i=>i.Message).ToArray(),
                images=interaction.Candidates.Count,selectableImages=interaction.Candidates.Count(c=>c.CanSelect),
                eventPlanValid=eventPlan?.IsValid,eventPlanIssues=eventPlan?.Issues.Select(i=>i.Message).ToArray()});
            async Task<object?> TryExecute(ReconstructionPlan plan,int count)
            {
                if(plan.Layers.SelectMany(l=>l.Segments).Where(s=>s.Kind==ReconstructionSegmentKind.Replay)
                    .SelectMany(s=>s.Events).Any(k=>!plan.Request.Changes.TryGetValue(k,out var c)||!c.Delete))
                {
                    attempts.Add(new {mode=plan.Request.Kind.ToString(),
                        offlineLimitation="This selection needs a fresh CSP brush capture; its plan is valid but recorded PNGs cannot represent a changed seed."});
                    return null;
                }
                try { return await Execute(plan,session,a,b,count); }
                catch(InvalidOperationException error)
                {
                    attempts.Add(new { mode=plan.Request.Kind.ToString(), executionBlocker=error.Message,
                        selectedKeys=plan.Request.Changes.Keys.ToArray() });
                    return null;
                }
            }
            if(undoResult is null&&undo.IsValid&&undoPlan.IsValid)
                undoResult=await TryExecute(undoPlan,undo.Selected.Count);
            if(eventResult is null&&eventPlan?.IsValid==true)
                eventResult=await TryExecute(eventPlan,eventRequest!.Changes.Count);
            Console.WriteLine($"Image {sourceImage.Key.ImageId}, full range {completeImage}: undo plan {undoPlan.IsValid}, event plan {eventPlan?.IsValid}, completed undo {undoResult is not null}, events {eventResult is not null}.");
            if(undoResult is not null&&eventResult is not null)break;
            }
            if(undoResult is not null&&eventResult is not null)break;
        }
        var result=new {source=path,sourceHash,
            sourceUnchanged=sourceHash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            sessionCount=sources.Count,rawPenBegin=sources.Sum(s=>s.Frames.Count(f=>J.Text(f,"kind")=="penBegin")),
            indexedStrokes=index.Strokes.Count,images=index.Images.Count,
            layers=index.Layers.Values.Select(l=>new{l.Layer,l.ChainValid,issues=l.Issues.Select(i=>new{code=i.Code.ToString(),i.Message}).ToArray()}).ToArray(),
            attempts,preciseUndo=undoResult,eventEditing=eventResult,
            scope="Original file, native states, selection, planner and pixel reconstruction. No real CSP input or undo stack."};
        File.WriteAllText(Path.Combine(reportRoot,"actual-selection-results.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Original sessions: {sources.Count}; recorded contacts: {result.rawPenBegin}; indexed strokes: {index.Strokes.Count}; images: {index.Images.Count}.");
        Console.WriteLine($"Precise undo execution: {undoResult is not null}; event editing execution: {eventResult is not null}; input unchanged: {result.sourceUnchanged}.");
        if(undoResult is null||eventResult is null)
            throw new InvalidOperationException("The supplied recording did not provide an executable selection for both modes. See actual-selection-results.json for the exact blockers.");
    }

    private static async Task<object> Execute(ReconstructionPlan plan,EditSession session,PointF a,PointF b,int count)
    {
        var executor=new RecordedPixelExecutor();
        var result=await ReconstructionEngine.RunAsync(plan,executor,CancellationToken.None);
        if(result.Status is ReconstructionStatus.Failed or ReconstructionStatus.Cancelled)
            throw new InvalidOperationException(string.Join("; ",result.Issues.Select(i=>i.Message)));
        if(executor.RecordedReplayCalls!=0||executor.InstallCalls!=1||result.Layers.Count!=1||
            result.Layers.All(l=>ImageCompositor.Difference(l.Value,plan.Index.Layers[l.Key].Current!).IsEmpty))
            throw new InvalidOperationException("The actual-file deletion must change pixels using recorded patches alone, with one offline installation and no synthesized brush replay.");
        // Separately validate the actual native contacts to be restored into the discrete undo stack.
        int schedules=0;
        if(plan.Request.Kind==EditKind.PreciseUndo)
        {
            var events=plan.Request.Changes.Keys.Select(k=>new ReplayEvent(plan.Index.Strokes[k],null)).ToArray();
            schedules=WindowsCspEditingActions.BuildSchedules(session,plan.Request,events,
                StrokeReplay.ReplayMode.Discrete,"offline-work-layer","offline-work-uuid").Count;
        }
        return new {selection=new {a.X,a.Y,right=b.X,bottom=b.Y},selected=count,
            selectedKeys=plan.Request.Changes.Keys.ToArray(),
            status=result.Status.ToString(),executor.RecordedReplayCalls,executor.InstallCalls,nativeReplayBatches=schedules,
            pixelChanges=result.Layers.Select(l=>new{layer=l.Key,
                changedPixels=ImageCompositor.Difference(l.Value,plan.Index.Layers[l.Key].Current!).Count,
                rgbaSha256=Convert.ToHexString(SHA256.HashData(l.Value.Bytes))}).ToArray(),
            replayCapture="Pure deletion seed reconstructed from original patches. Offline installation returns that seed; no CSP paste, final discrete replay, brush rendering or undo stack is claimed."};
    }

    private sealed class RecordedPixelExecutor:ICspReconstructionExecutor
    {
        internal int RecordedReplayCalls,InstallCalls;
        public Task<ImageRgba> FlushAsync(LayerKey layer,ImageRgba seed,IReadOnlyList<ReplayEvent> events,
            IReadOnlyDictionary<LayerKey,ImageRgba> references,CancellationToken token)
        {
            RecordedReplayCalls++;
            throw new InvalidOperationException("A changed seed needs a fresh CSP brush capture; recorded PNGs cannot supply it.");
        }
        public Task<ImageRgba> InstallReproductionLayerAsync(LayerKey layer,ImageRgba expected,CancellationToken token)
        { InstallCalls++;return Task.FromResult(expected.Copy()); }
    }
}
