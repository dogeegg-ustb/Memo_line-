using MemolineDemo;

string root = Path.Combine(Path.GetTempPath(), "memoline-demo-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    int bundleChecks = BundleChecks.Run(root);
    int aggregateChecks = AggregateChecks.Run(root);
    int lifecycleChecks = LifecycleChecks.Run(root);
    Console.WriteLine($"Bundle checks: {bundleChecks}; aggregate checks: {aggregateChecks}; lifecycle checks: {lifecycleChecks}.");
    Console.WriteLine("Memoline integration checks passed.");
}
finally
{
    string resolved = Path.GetFullPath(root);
    string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolved).StartsWith("memoline-demo-checks-", StringComparison.Ordinal))
        Directory.Delete(resolved, recursive: true);
}
