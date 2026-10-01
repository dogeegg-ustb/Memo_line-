using System.Diagnostics;
using BehaviorRecognizer.Capture;

if (args.Contains("--child"))
{
    Thread.Sleep(30000);
    return;
}
using var job=new ProcessLifetimeJob();
var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
start.ArgumentList.Add("--child");
using var child=Process.Start(start)!;
try
{
    job.Add(child);
    job.Dispose();
    if (!child.WaitForExit(5000)) throw new Exception("Helper survived lifetime job closure.");
    Console.WriteLine("Passed: closing the recorder's lifetime job terminates its background helper.");
}
finally { if (!child.HasExited) child.Kill(entireProcessTree:true); }
