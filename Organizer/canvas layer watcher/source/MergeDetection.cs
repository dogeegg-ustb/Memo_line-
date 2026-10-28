using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CanvasLayerWatcher;

/// <summary>Layers that vanished between two saves Memoline did not make. A merge down leaves the layer below
/// changed only where the vanished layer had pixels; anything else (and an empty vanished layer) is a deletion.</summary>
internal sealed record LayerVanishing(long Generation, long TriggerTicks, string BeforeSnapshot, string AfterSnapshot,
    IReadOnlyList<LayerReference> Before, IReadOnlyList<LayerReference> After, IReadOnlyList<LayerReference> Vanished);

internal static class MergeDetection
{
    /// <summary>The surviving layers a vanished layer may have been merged into: the nearest one below it in the
    /// saved order (CSP's merge down), then the nearest one above.</summary>
    internal static IEnumerable<LayerReference> Candidates(LayerVanishing vanishing, LayerReference layer)
    {
        var survivors = vanishing.After.Select(l => l.Uuid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        int index = vanishing.Before.ToList().FindIndex(l => string.Equals(l.Uuid, layer.Uuid, StringComparison.OrdinalIgnoreCase));
        if (index < 0) yield break;
        bool Survives(LayerReference other) => other.Uuid is { } uuid && survivors.Contains(uuid);
        if (vanishing.Before.Take(index).LastOrDefault(Survives) is { } below) yield return below;
        if (vanishing.Before.Skip(index + 1).FirstOrDefault(Survives) is { } above) yield return above;
    }

    /// <summary>True when <paramref name="targetAfter"/> equals <paramref name="targetBefore"/> wherever the vanished
    /// layer was transparent and differs somewhere under its pixels (a plain deletion leaves the target unchanged).</summary>
    internal static bool MergedInto(string vanishedBefore, string targetBefore, string targetAfter)
    {
        using var vanished = LayerDiff.Load(vanishedBefore);
        using var before = LayerDiff.Load(targetBefore);
        using var after = LayerDiff.Load(targetAfter);
        if (vanished.Size != before.Size || before.Size != after.Size) return false;
        var rect = new Rectangle(Point.Empty, vanished.Size);
        var a = vanished.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var b = before.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var c = after.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int width = rect.Width;
            var va = new int[width]; var vb = new int[width]; var vc = new int[width];
            bool received = false;
            for (int y = 0; y < rect.Height; y++)
            {
                Marshal.Copy(a.Scan0 + y * a.Stride, va, 0, width);
                Marshal.Copy(b.Scan0 + y * b.Stride, vb, 0, width);
                Marshal.Copy(c.Scan0 + y * c.Stride, vc, 0, width);
                for (int x = 0; x < width; x++)
                {
                    // Fully transparent pixels compare equal whatever their colour bytes hold.
                    bool changed = vb[x] != vc[x] && (((uint)vb[x] >> 24) != 0 || ((uint)vc[x] >> 24) != 0);
                    if (((uint)va[x] >> 24) != 0) received |= changed;
                    else if (changed) return false;
                }
            }
            return received;
        }
        finally { vanished.UnlockBits(a); before.UnlockBits(b); after.UnlockBits(c); }
    }
}
