namespace StrokeReplay;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--parser")) ParserChecks.Run();
        if (args.Length == 0 || args.Contains("--view")) ViewRestoreChecks.Run();
        if (args.Length == 0 || args.Contains("--monitor")) await MonitorChecks.RunAsync();
        if (args.Length == 0 || args.Contains("--pen")) await PenReplayChecks.RunAsync();
        Console.WriteLine("All memoline replay checks passed.");
    }
}
