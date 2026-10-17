using System.Runtime;

namespace MemolineDemo.Editing;

/// <summary>
/// Keeps verified pixels that no running operation reads in compressed form. Project history holds a start and
/// an end canvas per layer and session (139 MB each at 4961×7016) for the whole recording; compressed they are
/// about 1 MB, and any later read restores them transparently.
/// </summary>
internal static class EditingMemory
{
    internal static void Park(SessionSnapshot session)
    {
        foreach (var packet in session.Packets) Park(packet);
    }

    internal static void Park(EditIndex index)
    {
        foreach (var layer in index.Layers.Values)
        {
            layer.Baseline?.Park(); layer.Current?.Park();
            foreach (var packet in layer.Packets) Park(packet);
            foreach (var checkpoint in layer.UndoCheckpoints) { checkpoint.Seed.Park(); checkpoint.Expected.Park(); }
        }
        foreach (var image in index.Images.Values) { image.After.Park(); image.Now.Park(); }
    }

    private static void Park(LayerPacket packet)
    {
        packet.Before?.Park(); packet.Current?.Park();
        foreach (var image in packet.Images) { image.After.Park(); image.Now.Park(); }
    }

    /// <summary>
    /// Frees replaced full canvases at once. Each one is 139 MB, and the GC would otherwise let several
    /// accumulate before a full collection; a replay flush takes seconds, so the pause does not show.
    /// </summary>
    internal static void Collect() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

    /// <summary>Returns freed full-canvas arrays to Windows; the GC otherwise keeps the large-object heap committed.</summary>
    internal static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
