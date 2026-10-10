using MemolineDemo;

string root = Path.Combine(Path.GetTempPath(), "memoline-demo-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args is ["--capture-csp-desktop"])
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var desktop = FrozenCspDesktop.Capture();
        Console.WriteLine($"Frozen CSP desktop: bounds={desktop.Bounds}, client={desktop.ClientBounds}, windows={desktop.CspWindowBounds.Count}.");
        return;
    }
    if (args is ["--live-reader-only"])
    {
        LiveReaderChecks.Run(root);
        return;
    }
    if (args is ["--read-edit-sources", var mechanical, var diffDirectory, var project])
    {
        var snapshot = MemolineDemo.Editing.LiveSessionReader.Read(mechanical, diffDirectory, 0);
        Console.WriteLine($"Current session read: {snapshot.SessionId}, packets={snapshot.Packets.Count}.");
        var sources = await MemolineDemo.Editing.ProjectSessionReader.ReadProjectAsync(project,
            Path.Combine(root, "project-history", new string('a', 96)));
        Console.WriteLine($"History read: sessions={sources.Count}.");
        var index = MemolineDemo.Editing.ProjectEditIndex.Build(sources, snapshot);
        Console.WriteLine($"Combined index: strokes={index.Strokes.Count}, images={index.Images.Count}.");
        return;
    }
    if (args is ["--actual-undo-history", var undoProject, var undoReport])
    {
        await ActualUndoHistoryChecks.RunAsync(undoProject, undoReport);
        return;
    }
    if (args is ["--undo-history-only"])
    {
        Console.WriteLine($"Native undo timeline checks: {NativeUndoTimelineChecks.Run()}.");
        Console.WriteLine($"Effective undo history checks: {await EffectiveUndoHistoryChecks.RunAsync()}.");
        return;
    }
    if (args is ["--sealing-tail-only"])
    {
        Console.WriteLine($"Sealing tail checks: {await SealingTailChecks.RunAsync(root)}.");
        Console.WriteLine($"End packet checks: {EndPacketChecks.Run(root)}.");
        Console.WriteLine($"Aggregate checks: {AggregateChecks.Run(root)}.");
        return;
    }
    if (args is ["--actual-recording-tail", var sessionCache, var tailReport])
    {
        ActualRecordedTailChecks.Run(sessionCache, tailReport);
        return;
    }
    if (args is ["--actual-project-probe", var probeProject, var probeDirectory])
    {
        await ActualProjectProbeChecks.RunAsync(probeProject, probeDirectory);
        return;
    }
    if (args is ["--actual-project-file", var projectFile, var reportDirectory])
    {
        await ActualProjectEditingChecks.RunAsync(projectFile, reportDirectory);
        return;
    }
    if (args is ["--project-history-only"])
    {
        Console.WriteLine($"Recorded brush group checks: {RecordedBrushGroupChecks.Run()}.");
        Console.WriteLine($"State invalidation recovery checks: {StateInvalidationRecoveryChecks.Run()}.");
        Console.WriteLine($"Project session reader checks: {await ProjectSessionReaderChecks.RunAsync(root)}.");
        Console.WriteLine($"Project edit index checks: {ProjectEditIndexChecks.Run()}.");
        Console.WriteLine($"Cross session replay checks: {CrossSessionReplayChecks.Run()}.");
        Console.WriteLine($"Cross session journal checks: {CrossSessionJournalChecks.Run(root)}.");
        return;
    }
    if (args is ["--recorded-canvas-only"])
    {
        Console.WriteLine($"Recorded canvas view checks: {await RecordedCanvasViewChecks.RunAsync(root)}.");
        return;
    }
    if (args is ["--capacity-only"])
    {
        Console.WriteLine($"Undo capacity checks: {UndoCapacityChecks.Run()}.");
        return;
    }
    if (args is ["--csp-transaction-only"])
    {
        Console.WriteLine($"CSP transaction checks: {await CspEditTransactionChecks.RunAsync(root)}.");
        return;
    }
    if (args is ["--selection-only"])
    {
        Console.WriteLine($"Recorded canvas view checks: {await RecordedCanvasViewChecks.RunAsync(root)}.");
        Console.WriteLine($"Undo capacity checks: {UndoCapacityChecks.Run()}.");
        Console.WriteLine($"Editing host contract checks: {EditingHostContractChecks.Run()}.");
        Console.WriteLine($"Canvas interaction checks: {CanvasInteractionChecks.Run()}.");
        Console.WriteLine($"Brush parameter contract checks: {BrushParameterContractChecks.Run()}.");
        Console.WriteLine($"Editing selection checks: {EditingSelectionChecks.Run()}.");
        return;
    }
    if (args is ["--continuation-only"])
    {
        Console.WriteLine($"Project continuation checks: {await ProjectContinuationChecks.RunAsync(root)}.");
        Console.WriteLine($"Replay persistence checks: {await ReplayPersistenceChecks.RunAsync(root)}.");
        return;
    }
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
    if (args is ["--reconstruction-only"])
    {
        Console.WriteLine($"Reconstruction checks: {await ReconstructionChecks.RunAsync()}.");
        Console.WriteLine($"Precise undo checks: {PreciseUndoChecks.Run()}.");
        Console.WriteLine($"Cross-layer reconstruction checks: {await ReconstructionCrossLayerChecks.RunAsync()}.");
        return;
    }
    if (args is ["--index-hardening-only"])
    {
        Console.WriteLine($"Edit index hardening checks: {EditIndexHardeningChecks.Run()}.");
        Console.WriteLine($"Footprint contract checks: {FootprintContractChecks.Run()}.");
        return;
    }
    if (args is ["--edit-journal-only"])
    {
        Console.WriteLine($"Edit journal checks: {EditJournalChecks.Run(root)}.");
        Console.WriteLine($"Bundle edit checks: {BundleEditChecks.Run(root)}.");
        return;
    }
    int bundleChecks = BundleChecks.Run(root);
    Console.WriteLine($"Native undo timeline checks: {NativeUndoTimelineChecks.Run()}.");
    Console.WriteLine($"Effective undo history checks: {await EffectiveUndoHistoryChecks.RunAsync()}.");
    Console.WriteLine($"Sealing tail checks: {await SealingTailChecks.RunAsync(root)}.");
    Console.WriteLine($"Recorded brush group checks: {RecordedBrushGroupChecks.Run()}.");
    Console.WriteLine($"State invalidation recovery checks: {StateInvalidationRecoveryChecks.Run()}.");
    Console.WriteLine($"Project session reader checks: {await ProjectSessionReaderChecks.RunAsync(root)}.");
    Console.WriteLine($"Project edit index checks: {ProjectEditIndexChecks.Run()}.");
    Console.WriteLine($"Cross session replay checks: {CrossSessionReplayChecks.Run()}.");
    Console.WriteLine($"Cross session journal checks: {CrossSessionJournalChecks.Run(root)}.");
    Console.WriteLine($"Project continuation checks: {await ProjectContinuationChecks.RunAsync(root)}.");
    Console.WriteLine($"Replay persistence checks: {await ReplayPersistenceChecks.RunAsync(root)}.");
    Console.WriteLine($"Editing coordinator checks: {await EditingCoordinatorChecks.RunAsync()}.");
    Console.WriteLine($"Editing boundary checks: {await EditingBoundaryChecks.RunAsync()}.");
    Console.WriteLine($"CSP transaction checks: {await CspEditTransactionChecks.RunAsync(root)}.");
    int aggregateChecks = AggregateChecks.Run(root);
    Console.WriteLine($"End packet checks: {EndPacketChecks.Run(root)}.");
    int lifecycleChecks = LifecycleChecks.Run(root);
    Console.WriteLine($"Input selection checks: {InputSelectionChecks.Run()}.");
    Console.WriteLine($"Layer and pen region checks: {LayerAndRegionChecks.Run()}.");
    Console.WriteLine($"Public interface checks: {await InterfaceChecks.RunAsync(root)}.");
    Console.WriteLine($"Editing core checks: {EditingChecks.Run(root)}.");
    Console.WriteLine($"Editing selection checks: {EditingSelectionChecks.Run()}.");
    Console.WriteLine($"Undo capacity checks: {UndoCapacityChecks.Run()}.");
    Console.WriteLine($"Editing host contract checks: {EditingHostContractChecks.Run()}.");
    Console.WriteLine($"Canvas interaction checks: {CanvasInteractionChecks.Run()}.");
    Console.WriteLine($"Recorded canvas view checks: {await RecordedCanvasViewChecks.RunAsync(root)}.");
    Console.WriteLine($"Brush parameter contract checks: {BrushParameterContractChecks.Run()}.");
    Console.WriteLine($"Compositor parity checks: {await CompositorParityChecks.RunAsync(root)}.");
    Console.WriteLine($"Reconstruction checks: {await ReconstructionChecks.RunAsync()}.");
    Console.WriteLine($"Precise undo checks: {PreciseUndoChecks.Run()}.");
    Console.WriteLine($"Cross-layer reconstruction checks: {await ReconstructionCrossLayerChecks.RunAsync()}.");
    Console.WriteLine($"Edit index hardening checks: {EditIndexHardeningChecks.Run()}.");
    Console.WriteLine($"Footprint contract checks: {FootprintContractChecks.Run()}.");
    Console.WriteLine($"Edit journal checks: {EditJournalChecks.Run(root)}.");
    Console.WriteLine($"Bundle edit checks: {BundleEditChecks.Run(root)}.");
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
