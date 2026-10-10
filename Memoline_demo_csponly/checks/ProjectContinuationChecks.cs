using System.Text.Json;
using System.Security.Cryptography;
using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

internal static class ProjectContinuationChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int count=0;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);count++;}
        string directory=Path.Combine(root,"continuation");Directory.CreateDirectory(directory);
        string clip=Path.Combine(directory,"art.clip"), project=Path.Combine(directory,"art.memoline");
        async Task<(string Bundle,string Native)> Make(int index)
        {
            var writer=new MemolineWriter(Path.Combine(directory,"native-"+index),new { test="project" });
            writer.AppendState("initializationConfiguration",writer.NowTicks,[],new { clipPath=clip });
            var source=new HardwareDeviceSource("pen","test","fake","fake");
            var begin=writer.AppendHardware("penBegin",new { x=1,y=2 },source);
            writer.AppendHardware("penEnd",new { x=2,y=2 },source,begin.EventId);
            await writer.DisposeAsync();
            var frames=MemolineReader.Read(writer.FilePath).ToArray();var header=frames[0];
            string aggregate=Path.Combine(directory,index+".jsonl");
            File.WriteAllLines(aggregate,[JsonSerializer.Serialize(new { kind="header",sessionId=writer.SessionId,frequency=header.GetProperty("frequency").GetInt64() }),
                JsonSerializer.Serialize(new { kind="footer",sessionId=writer.SessionId })]);
            string bundle=Path.Combine(directory,index+".memoline");BundleArchive.Seal(writer.FilePath,aggregate,bundle);
            return (bundle,writer.FilePath);
        }
        var first=await Make(1);var second=await Make(2);var third=await Make(3);
        BundleArchive.AppendProjectSession(project,first.Bundle,clip);
        Check(BundleArchive.ReadProjectPath(project)==clip,"project identity persisted");
        Check(ProjectRecordingStore.Resolve(directory,clip)==project,"same CLIP reuses existing recording");
        BundleArchive.AppendProjectSession(project,second.Bundle,clip);
        BundleArchive.AppendProjectSession(project,third.Bundle,clip);
        var sessions=BundleArchive.ExtractProjectSessions(project,Path.Combine(directory,"extract"));
        Check(sessions.Count==3,"three sessions remain in the same container");
        int i=0;
        foreach(var original in new[]{first,second,third})
        {
            string native=Path.Combine(directory,"extract-"+i+".native");BundleArchive.ExtractMechanical(sessions[i++],native);
            Check(File.ReadAllBytes(native).SequenceEqual(File.ReadAllBytes(original.Native)),"native clocks, append IDs and bytes survive continuation");
        }
        byte[] before=File.ReadAllBytes(project);
        try { BundleArchive.AppendProjectSession(project,third.Bundle,clip);throw new Exception("duplicate accepted"); }
        catch(InvalidDataException){count++;}
        Check(File.ReadAllBytes(project).SequenceEqual(before),"duplicate append retains existing project");
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();try { BundleArchive.AppendProjectSession(project,first.Bundle,clip,cancel.Token);throw new Exception("cancel ignored"); }
            catch(OperationCanceledException){count++;}
        }
        Check(File.ReadAllBytes(project).SequenceEqual(before),"cancel leaves project bytes intact");
        string otherClip=Path.Combine(directory,"elsewhere","art.clip");
        Check(ProjectRecordingStore.Resolve(directory,otherClip)!=project,"equal filenames from different paths are distinct projects");
        try { BundleArchive.AppendProjectSession(project,first.Bundle,otherClip);throw new Exception("wrong project accepted"); }
        catch(InvalidDataException){count++;}
        string compact=Path.Combine(directory,"compact.memoline");BundleArchive.Compact(project,compact);
        Check(BundleArchive.ExtractProjectSessions(compact,Path.Combine(directory,"compact-extract")).Count==3,"compact preserves earlier sessions");
        return count;
    }
}
