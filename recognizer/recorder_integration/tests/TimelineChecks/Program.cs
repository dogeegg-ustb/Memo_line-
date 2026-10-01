using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;

string output=Path.Combine(Path.GetTempPath(),"memoline-timeline-check-"+Guid.NewGuid().ToString("N"));
string path;
using var jsonBytes=new MemoryStream();
await using (var writer=new MemolineWriter(output,new { purpose="timelineCheck" }))
{
    path=writer.FilePath;
    string initial=writer.ReserveStatePackage(0,0,"initial");
    var source=new HardwareDeviceSource("keyboard","test",null,"test");
    var a=writer.AppendHardware("keyInput",new { vk=65 },source);
    string pa=writer.ReserveStatePackage(a.EventId,a.Ticks,"A");
    var b=writer.AppendHardware("keyInput",new { vk=66 },source);
    string pb=writer.ReserveStatePackage(b.EventId,b.Ticks,"B");
    writer.AppendPackageResult(pb,new { status="changed",updates=new[] { new { state="B" } } });
    writer.AppendPackageResult(initial,new { status="changed",updates=new[] { new { state="initial" } } });
    writer.AppendPackageResult(pa,new { status="changed",updates=new[] { new { state="A" } } });
    writer.ReserveStatePackage(b.EventId,b.Ticks,"unfinished");
}
var frames=MemolineReader.Read(path).ToArray();
var timeline=new MemolineTimeline();
foreach (var frame in frames) timeline.Add(frame);
using (var json=new Utf8JsonWriter(jsonBytes)) { json.WriteStartObject(); timeline.Write(json); json.WriteEndObject(); }
using var doc=JsonDocument.Parse(jsonBytes.ToArray());
var logical=doc.RootElement.GetProperty("timeline").EnumerateArray().ToArray();
if (logical.Length!=6 || logical[0].GetProperty("kind").GetString()!="initialState" ||
    logical[1].GetProperty("eventId").GetUInt64()!=1 || logical[2].GetProperty("afterEventId").GetUInt64()!=1 ||
    logical[3].GetProperty("eventId").GetUInt64()!=2 || logical[4].GetProperty("afterEventId").GetUInt64()!=2 ||
    logical[5].GetProperty("status").GetString()!="error") throw new Exception("Logical order or unresolved-slot finalization failed.");
Console.WriteLine("Passed: out-of-order results resolve between their causal inputs; incomplete state remains an explicit error.");
Console.WriteLine(path);
