using BehaviorRecognizer.Storage.Memoline;

namespace MemolineDemo;

internal static class ReplayPersistenceChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int n=0;void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);n++;}
        var writer=new MemolineWriter(Path.Combine(root,"replay-persistence"),new { test="editing" });
        var live=new List<MemolineEvent>();writer.DiagnosticRecorded+=live.Add;
        var source=new HardwareDeviceSource("keyboard","fake","fake","fake");
        writer.AppendHardware("keyInput",new { key="B" },source);
        writer.AppendImmediateState("editTransactionBegan",new { transactionId="t" });
        writer.BeginEditingSuppression();long captured=writer.NowTicks;
        writer.AppendState("coreStateUpdated",captured,[],new { module="colorState",status="changed",state=new { kind="rgb",rgb=new[]{1,2,3} } });
        string package=writer.ReserveStatePackage(0,writer.NowTicks,"replay");
        writer.AppendPackageResult(package,new { status="changed",updates=new[]{new { module="brushState",status="changed",state="replay" }} });
        writer.EndEditingSuppression();writer.AppendImmediateState("editTransactionEnded",new { transactionId="t",outcome="executed" });
        writer.AppendImmediateState("editingActionCompleted",new { transactionId="t",editKind="eventEditing",changes=new[]{new { delete=true }} });
        writer.AppendState("stateResult",captured,[],new { from="late-replay-analysis" });
        writer.AppendHardware("keyInput",new { key="Z",modifiers=new[]{"Ctrl"} },source);
        writer.AppendState("shortcutResolved",writer.NowTicks,[],new { command="undo" });
        await writer.DisposeAsync();var frames=MemolineReader.Read(writer.FilePath).ToArray();
        Check(!frames.Any(f=>f.GetProperty("kind").GetString() is "coreStateUpdated" or "statePackageReserved" or "statePackageResult" or "stateResult"),"replay OCR and delayed replay results do not enter mechanical history");
        Check(live.Any(f=>f.Kind=="coreStateUpdated" && f.AppendId==0),"live OCR readback remains available without persistent append IDs");
        Check(frames.Count(f=>f.GetProperty("kind").GetString()=="editingActionCompleted")==1,"one semantic edit action is recorded");
        Check(frames.Any(f=>f.GetProperty("kind").GetString()=="shortcutResolved" && f.GetProperty("data").GetProperty("command").GetString()=="undo"),"user undo after replay records normally");
        var appends=frames.Where(f=>f.TryGetProperty("appendId",out _)).Select(f=>f.GetProperty("appendId").GetUInt64()).ToArray();
        Check(appends.SequenceEqual(Enumerable.Range(1,appends.Length).Select(i=>(ulong)i)),"suppression introduces no holes in persisted append IDs");
        return n;
    }
}
