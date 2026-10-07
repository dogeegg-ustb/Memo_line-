using MemolineDemo;

string root = Path.Combine(Path.GetTempPath(), "memoline-demo-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args is ["--editing-only"])
    {
        EditingChecks.Run(root);
        return;
    }
    if (args is ["--compositor-parity-only"])
    {
        Console.WriteLine($"Compositor parity checks: {await CompositorParityChecks.RunAsync(root)}.");
        return;
    }
    if (args is ["--index-hardening-only"])
    {
        Console.WriteLine($"Edit index hardening checks: {EditIndexHardeningChecks.Run()}.");
        Console.WriteLine($"Footprint contract checks: {FootprintContractChecks.Run()}.");
        return;
    }
    int bundleChecks = BundleChecks.Run(root);
    int aggregateChecks = AggregateChecks.Run(root);
    Console.WriteLine($"End packet checks: {EndPacketChecks.Run(root)}.");
    int lifecycleChecks = LifecycleChecks.Run(root);
    Console.WriteLine($"Input selection checks: {InputSelectionChecks.Run()}.");
    Console.WriteLine($"Layer and pen region checks: {LayerAndRegionChecks.Run()}.");
    Console.WriteLine($"Public interface checks: {await InterfaceChecks.RunAsync(root)}.");
    Console.WriteLine($"Editing core checks: {EditingChecks.Run(root)}.");
    Console.WriteLine($"Compositor parity checks: {await CompositorParityChecks.RunAsync(root)}.");
    Console.WriteLine($"Edit index hardening checks: {EditIndexHardeningChecks.Run()}.");
    Console.WriteLine($"Footprint contract checks: {FootprintContractChecks.Run()}.");
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
