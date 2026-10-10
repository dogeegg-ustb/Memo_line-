using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorRecognizer.Storage.Memoline;
using DirtyMatrixPaster;
using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class CrossSessionJournalChecks
{
    internal static int Run(string checkRoot)
    {
        string root=Path.Combine(checkRoot,"cross-session-journal"); Directory.CreateDirectory(root);
        int count=0;
        void Check(bool value,string reason) { if(!value)throw new InvalidOperationException(reason); count++; }
        void Reject(Action action,string reason)
        {
            try { action(); }
            catch(Exception error) when(error is InvalidDataException or IOException or ArgumentException)
            { count++; return; }
            throw new InvalidOperationException(reason);
        }
        (string Native,string Aggregate,string Session,ulong Begin,ulong End) Make(string name)
        {
            var writer=new MemolineWriter(Path.Combine(root,"native-"+name),new { test="cross-session-journal" });
            var source=new HardwareDeviceSource("pen","test","fixture","fixture");
            var begin=writer.AppendHardwareRecord("penBegin",new {x=2,y=3},source);
            var end=writer.AppendHardwareRecord("penEnd",new {x=3,y=3},source,begin.EventId);
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var frames=MemolineReader.Read(writer.FilePath).ToArray();
            string session=frames[0].GetProperty("sessionId").GetString()!;
            long frequency=frames[0].GetProperty("frequency").GetInt64();
            string aggregate=Path.Combine(root,name+".jsonl");
            File.WriteAllLines(aggregate,[JsonSerializer.Serialize(new {kind="header",sessionId=session,frequency}),
                JsonSerializer.Serialize(new {kind="footer",sessionId=session})],new UTF8Encoding(false));
            return (writer.FilePath,aggregate,session,begin.AppendId,end.AppendId);
        }
        var old=Make("old"); var current=Make("current"); var unrelated=Make("unrelated");
        var layer=new LayerKey(current.Session,1,"current-ink");
        var oldKey=new EditableStrokeKey(old.Session,old.Begin);
        var currentKey=new EditableStrokeKey(current.Session,current.Begin);
        byte[] png=ClipboardImageCodec.EncodePng(1,1,[10,20,30,255]);
        string Journal(string name,LayerKey target,params EditableStrokeKey[] keys)
        {
            string work=Path.Combine(root,name);
            using var journal=EditJournal.Open(work);
            journal.Start(name,target);
            journal.FreezePlan(JsonSerializer.SerializeToElement(new {kind="eventEditing",selected=keys}));
            journal.StartSegment("replay",EditJournalOperation.Replay); journal.Replay("replay",keys);
            journal.StartSegment("capture",EditJournalOperation.Capture);
            var asset=journal.Capture("capture","working-layer",png,1,1);
            journal.WorkLayerDeleted(journal.CheckpointFlushed("working-layer",asset,keys));
            journal.ReproductionInstalled("reproduction",asset); journal.Finish(EditJournalOutcome.Executed);
            return work;
        }
        string work=Journal("edits",layer,oldKey,currentKey);
        string output=Path.Combine(root,"edited.memoline");
        byte[] oldBytes=File.ReadAllBytes(old.Native), currentBytes=File.ReadAllBytes(current.Native);
        BundleArchive.SealWithEdits(current.Native,current.Aggregate,work,output,replaySourceMechanicalPaths:[old.Native]);
        var verified=BundleArchive.Verify(output);
        var journal=BundleArchive.ReadEditJournal(output)!;
        var index=Index(output);
        string sourceEntry="edits/sources/"+old.Session+".memoline";
        Check(verified.SessionId==current.Session && verified.NativeSessionCount==1,
            "Replay source proof must preserve the current session identity without inventing another project session.");
        Check(index["editSources"]!.AsArray().Count==1 && index["editSources"]![0]!["entry"]!.GetValue<string>()==sourceEntry,
            "The archive must identify the exact native source recording for historical event keys.");
        Check(index["editSources"]![0]!["sha256"]!.GetValue<string>()==Hash(oldBytes) &&
            index["editSources"]![0]!["size"]!.GetValue<long>()==oldBytes.LongLength,
            "Historical source metadata must hash decoded original native bytes.");
        Check(Bytes(output,sourceEntry).SequenceEqual(oldBytes) && Bytes(output,BundleArchive.MechanicalEntry).SequenceEqual(currentBytes),
            "Sealing must preserve original historical and current streams byte for byte.");
        Check(journal.Records.Single(r=>r.Kind=="replayCompleted").Data.GetProperty("eventKeys").EnumerateArray()
            .Select(k=>k.GetProperty("sessionId").GetString()).SequenceEqual([old.Session,current.Session]),
            "Journal reading must retain the original historical event identities.");
        Check(File.ReadAllBytes(old.Native).SequenceEqual(oldBytes)&&File.ReadAllBytes(current.Native).SequenceEqual(currentBytes),
            "Source proof creation must leave all native source files unchanged.");
        string compact=Path.Combine(root,"compact.memoline"); BundleArchive.Compact(output,compact);
        Check(Bytes(compact,sourceEntry).SequenceEqual(oldBytes) && BundleArchive.ReadEditJournal(compact)!.HasReplayed,
            "Compaction must retain independently verified historical source bytes and cross-session edits.");
        string project=Path.Combine(root,"project.memoline"), clip=Path.Combine(root,"drawing.clip");
        string oldBundle=Path.Combine(root,"old.memoline"); BundleArchive.Seal(old.Native,old.Aggregate,oldBundle);
        BundleArchive.AppendProjectSession(project,oldBundle,clip); BundleArchive.AppendProjectSession(project,output,clip);
        Check(BundleArchive.Verify(project).NativeSessionCount==2 && BundleArchive.ReadEditJournal(project)!.HasReplayed,
            "Project append must preserve cross-session proof without changing its public contract.");
        var extracted=BundleArchive.ExtractProjectSessions(project,Path.Combine(root,"extracted"));
        Check(extracted.Count==2 && Bytes(extracted[1],sourceEntry).SequenceEqual(oldBytes),
            "Extracting the latest immutable session must retain its historical edit proof.");
        string nextBundle=Path.Combine(root,"next.memoline"); BundleArchive.Seal(unrelated.Native,unrelated.Aggregate,nextBundle);
        BundleArchive.AppendProjectSession(project,nextBundle,clip);
        extracted=BundleArchive.ExtractProjectSessions(project,Path.Combine(root,"extracted-again"));
        Check(extracted.Count==3 && BundleArchive.ReadEditJournal(extracted[1])!.HasReplayed,
            "A prior edited session must retain proof after it becomes project history.");
        string missing=Path.Combine(root,"missing-proof.memoline");
        Reject(()=>BundleArchive.SealWithEdits(current.Native,current.Aggregate,work,missing),
            "Historical journal keys without independently supplied native proof must be rejected.");
        Check(!File.Exists(missing),"A rejected cross-session seal must not publish an incomplete archive.");
        Reject(()=>BundleArchive.SealWithEdits(current.Native,current.Aggregate,work,Path.Combine(root,"wrong-proof.memoline"),
            replaySourceMechanicalPaths:[unrelated.Native]),"An unrelated source session must not authorize an old event key.");
        Reject(()=>BundleArchive.SealWithEdits(current.Native,current.Aggregate,work,Path.Combine(root,"duplicate-proof.memoline"),
            replaySourceMechanicalPaths:[old.Native,old.Native]),"Duplicate native proof sessions must be rejected.");
        Reject(()=>BundleArchive.SealWithEdits(current.Native,current.Aggregate,work,Path.Combine(root,"current-proof.memoline"),
            replaySourceMechanicalPaths:[current.Native]),"The active native recording must not be duplicated as historical proof.");
        string invalidKeyWork=Journal("pen-end-edits",layer,new EditableStrokeKey(old.Session,old.End));
        Reject(()=>BundleArchive.SealWithEdits(current.Native,current.Aggregate,invalidKeyWork,Path.Combine(root,"pen-end-proof.memoline"),
            replaySourceMechanicalPaths:[old.Native]),"Historical keys must resolve to hardware penBegin rather than an arbitrary append ID.");
        string foreignTargetWork=Journal("foreign-target",new(old.Session,1,"old-ink"),oldKey);
        Reject(()=>BundleArchive.SealWithEdits(current.Native,current.Aggregate,foreignTargetWork,Path.Combine(root,"foreign-target.memoline"),
            replaySourceMechanicalPaths:[old.Native]),"A verified historical key must not authorize a foreign transaction target layer.");
        string Mutate(string name,Action<ZipArchive> mutate)
        {
            string path=Path.Combine(root,name+".memoline"); File.Copy(output,path);
            using(var archive=ZipFile.Open(path,ZipArchiveMode.Update))mutate(archive);
            return path;
        }
        void PatchIndex(ZipArchive archive,Action<JsonNode> mutate)
        {
            JsonNode json;
            using(var input=archive.GetEntry("index.json")!.Open())json=JsonNode.Parse(input)!;
            mutate(json); archive.GetEntry("index.json")!.Delete();
            using var target=archive.CreateEntry("index.json").Open(); JsonSerializer.Serialize(target,json);
        }
        Reject(()=>BundleArchive.Verify(Mutate("removed-proof",archive=>archive.GetEntry(sourceEntry+".br")!.Delete())),
            "A journal source entry that disappears from the sealed archive must be rejected.");
        Reject(()=>BundleArchive.Verify(Mutate("relabel-proof",archive=>
        {
            string renamed="edits/sources/forged-session.memoline";
            archive.GetEntry(sourceEntry+".br")!.Delete();
            using(var target=archive.CreateEntry(renamed).Open())target.Write(oldBytes);
            PatchIndex(archive,json=>{json["editSources"]![0]!["entry"]=renamed;json["editSources"]![0]!["codec"]=null;});
        })),"An entry name must match its native header session rather than trusting index claims.");
        Reject(()=>BundleArchive.Verify(Mutate("forged-outer-hash",archive=>
        {
            byte[] corrupt=[..oldBytes]; corrupt[corrupt.Length/2]^=0x40;
            archive.GetEntry(sourceEntry+".br")!.Delete();
            using(var target=archive.CreateEntry(sourceEntry).Open())target.Write(corrupt);
            PatchIndex(archive,json=>{json["editSources"]![0]!["codec"]=null;json["editSources"]![0]!["sha256"]=Hash(corrupt);});
        })),"Native CRC validation must reject tampered source frames even when outer SHA metadata is recomputed.");
        Check(File.ReadAllBytes(old.Native).SequenceEqual(oldBytes)&&File.ReadAllBytes(current.Native).SequenceEqual(currentBytes),
            "All failed proof checks must preserve original source recordings.");
        return count;
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonNode Index(string path)=>JsonNode.Parse(Bytes(path,"index.json"))!;
    private static byte[] Bytes(string path,string name)
    {
        using var archive=ZipFile.OpenRead(path);
        var entry=archive.GetEntry(name)??archive.GetEntry(name+".br")!;
        using var raw=entry.Open();
        using Stream source=entry.FullName.EndsWith(".br",StringComparison.Ordinal)?new BrotliStream(raw,CompressionMode.Decompress):raw;
        using var output=new MemoryStream(); source.CopyTo(output); return output.ToArray();
    }
}
