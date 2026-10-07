using System.Collections.Immutable;
using System.Text.Json;

namespace MemolineDemo.Editing;

public enum EditKind { EventEditing, PreciseUndo }

public enum PlanIssueCode
{
    ChainBroken, ChainLossy, NoBaseline, UnreplayableEventInMatrix,
    UndoRedoInRange, RuntimeClosureViolation, UnsupportedOverride,
    ColorUnrestorable, ViewUnrestorable, BrushUnrestorable,
    LayerIdentityUnconfirmed, PhysicalInputDuringTransaction,
    PasteNotExact, CaptureMismatch, ShortcutsMissing, NoSelection
}

public sealed record PlanIssue(PlanIssueCode Code, string Message,
    DirtyImageKey? Image = null, EditableStrokeKey? Stroke = null);

public readonly record struct LayerKey(string SessionId, long Generation, string Uuid)
{
    public override string ToString() => $"{SessionId}/{Generation}/{Uuid}";
}

public readonly record struct EditableStrokeKey(string SessionId, ulong BeginAppendId);

public readonly record struct DirtyImageKey(LayerKey Layer, string PacketId, string ImageId);

public sealed record EventChange(bool Delete = false, string? BrushName = null,
    IReadOnlyDictionary<string, double>? Numbers = null)
{
    public IReadOnlyDictionary<string, double> NumberValues { get; } =
        Numbers ?? ImmutableDictionary<string, double>.Empty;
}

/// <summary>Canvas pixel bitset with an explicit canvas-space origin and extent.</summary>
public sealed class PixelMask : IEquatable<PixelMask>
{
    private readonly ulong[] _words;
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public long Count { get; private set; }
    public bool IsEmpty => Count == 0;

    public PixelMask(int left, int top, int width, int height)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
        Left = left; Top = top; Width = width; Height = height;
        _words = new ulong[checked((width * (long)height + 63) / 64)];
    }

    public PixelMask(int width, int height) : this(0, 0, width, height) { }

    public bool Contains(int x, int y)
    {
        if (!TryIndex(x, y, out long index)) return false;
        return (_words[index >> 6] & (1UL << (int)(index & 63))) != 0;
    }

    public bool Set(int x, int y, bool value = true)
    {
        if (!TryIndex(x, y, out long index)) throw new ArgumentOutOfRangeException();
        ref ulong word = ref _words[index >> 6];
        ulong bit = 1UL << (int)(index & 63);
        bool old = (word & bit) != 0;
        if (old == value) return false;
        if (value) { word |= bit; Count++; } else { word &= ~bit; Count--; }
        return true;
    }

    public IEnumerable<(int X, int Y)> Pixels()
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Contains(Left + x, Top + y)) yield return (Left + x, Top + y);
    }

    public PixelMask Clone()
    {
        var copy = new PixelMask(Left, Top, Width, Height);
        Array.Copy(_words, copy._words, _words.Length); copy.Count = Count; return copy;
    }

    public PixelMask Union(PixelMask other) => Combine(other, static (a, b) => a | b);
    public PixelMask Intersect(PixelMask other) => Combine(other, static (a, b) => a & b);
    public PixelMask Except(PixelMask other) => Combine(other, static (a, b) => a & ~b);
    public PixelMask Or(PixelMask other) => Union(other);
    public PixelMask And(PixelMask other) => Intersect(other);

    public PixelMask Add(PixelMask other) => UnionInPlace(other);
    public PixelMask Subtract(PixelMask other) => ExceptInPlace(other);

    public PixelMask UnionInPlace(PixelMask other)
    {
        EnsureSameBounds(other);
        for (int i = 0; i < _words.Length; i++) _words[i] |= other._words[i];
        Recount(); return this;
    }

    public PixelMask ExceptInPlace(PixelMask other)
    {
        EnsureSameBounds(other);
        for (int i = 0; i < _words.Length; i++) _words[i] &= ~other._words[i];
        Recount(); return this;
    }

    public static PixelMask FromPredicate(int left, int top, int width, int height, Func<int, int, bool> predicate)
    {
        var result = new PixelMask(left, top, width, height);
        for (int y = top; y < top + height; y++) for (int x = left; x < left + width; x++)
            if (predicate(x, y)) result.Set(x, y);
        return result;
    }

    private PixelMask Combine(PixelMask other, Func<ulong, ulong, ulong> operation)
    {
        EnsureSameBounds(other); var result = new PixelMask(Left, Top, Width, Height);
        for (int i = 0; i < _words.Length; i++) result._words[i] = operation(_words[i], other._words[i]);
        result.Recount(); return result;
    }
    private void EnsureSameBounds(PixelMask other)
    {
        if (Left != other.Left || Top != other.Top || Width != other.Width || Height != other.Height)
            throw new ArgumentException("像素位集的画布外框不一致。", nameof(other));
    }
    private bool TryIndex(int x, int y, out long index)
    {
        if (x < Left || y < Top || x >= Left + Width || y >= Top + Height) { index = -1; return false; }
        index = (long)(y - Top) * Width + (x - Left); return true;
    }
    private void Recount()
    {
        Count = 0; foreach (ulong word in _words) Count += System.Numerics.BitOperations.PopCount(word);
    }
    public bool Equals(PixelMask? other)
        => other is not null && Left == other.Left && Top == other.Top && Width == other.Width &&
           Height == other.Height && _words.AsSpan().SequenceEqual(other._words);
    public override bool Equals(object? obj) => obj is PixelMask other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Left, Top, Width, Height, Count);
}

public sealed record ImageRgba(int Width, int Height, byte[] Bytes)
{
    public ImageRgba(int width, int height, ReadOnlySpan<byte> bytes) : this(width, height, bytes.ToArray()) { }
    public void Validate() =>
        _ = Width > 0 && Height > 0 && Bytes.Length == checked(Width * Height * 4)
            ? true : throw new InvalidDataException("RGBA 图像尺寸或字节数无效。");
    public ImageRgba Copy() => new(Width, Height, Bytes.ToArray());
}

public sealed record DirtyImage(
    DirtyImageKey Key,
    PixelMask Mask,
    ImageRgba After,
    ImageRgba Now,
    IReadOnlyList<EditableStrokeKey> Events,
    bool IsCausal = false,
    bool HasUnreplayableEvents = false,
    bool IsBaseline = false,
    bool FullResolution = true,
    string? LabelId = null,
    string? OperationId = null)
{
    public IReadOnlyList<string> LabelIds { get; init; } = [];
    public PixelMask W { get; init; } = new PixelMask(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
    public PixelMask Probe { get; init; } = new PixelMask(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
    public PixelMask Impact { get; init; } = new PixelMask(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
}

public sealed record CoverageSegment(double StartX, double StartY, double EndX, double EndY);
public sealed record LabelEvidence(string Id, ulong? OperationId, string Source, long FromTicks, long ToTicks,
    double Radius, IReadOnlyList<CoverageSegment> Segments, IReadOnlyList<string> ImageIds);

public sealed record LayerPacket(string PacketId, LayerKey Layer, long TriggerTicks,
    ImageRgba? Before, ImageRgba? Current, IReadOnlyList<DirtyImage> Images,
    bool IsBoundary = false, bool IsRecordingBaseline = false,
    string? AfterSnapshotId = null, string? NowSnapshotId = null,
    string? BaselineSource = null, int CanvasWidth = 0, int CanvasHeight = 0,
    bool FullResolution = true)
{
    public IReadOnlyList<LabelEvidence> Labels { get; init; } = [];
    public JsonElement AfterDescriptor { get; init; }
    public JsonElement NowDescriptor { get; init; }
}

internal sealed record EditableStroke(EditableStrokeKey Key, ulong OperationId, long StartTicks,
    long EndTicks, IReadOnlyList<StrokeReplay.MemolineReplaySample> Samples,
    string? BrushName, LayerKey Layer, bool IsCausal, bool Eligible = true,
    string? IneligibleReason = null, IReadOnlyDictionary<string, double>? BrushNumbers = null)
{
    public ulong BeginEventId { get; init; }
    public JsonElement BrushState { get; init; }
    public BrushClassification? Classification { get; init; }
    public IReadOnlyList<DirtyImageKey> Images { get; init; } = [];
    public IReadOnlyDictionary<string, double> Numbers { get; } = BrushNumbers ?? ImmutableDictionary<string, double>.Empty;
    public PixelMask WriteMask { get; init; } = new(0, 0, 0, 0);
    public PixelMask ProbeMask { get; init; } = new(0, 0, 0, 0);
    public PixelMask ImpactMask { get; init; } = new(0, 0, 0, 0);
}

internal sealed record LayerIndex(LayerKey Layer, IReadOnlyList<LayerPacket> Packets,
    IReadOnlyList<EditableStroke> Strokes, bool ChainValid, IReadOnlyList<PlanIssue> Issues,
    ImageRgba? Baseline = null, ImageRgba? Current = null);

internal sealed record EditIndex(IReadOnlyDictionary<LayerKey, LayerIndex> Layers,
    IReadOnlyDictionary<EditableStrokeKey, EditableStroke> Strokes,
    IReadOnlyDictionary<DirtyImageKey, DirtyImage> Images,
    IReadOnlyList<PlanIssue> Issues)
{
    public bool IsValid => Issues.Count == 0 && Layers.Values.All(l => l.ChainValid);
    public LayerIndex ForLayer(LayerKey key) => Layers.TryGetValue(key, out var layer)
        ? layer : throw new KeyNotFoundException("找不到编辑图层 " + key);
}

public sealed record EditRequest(EditKind Kind, LayerKey? CurrentLayer, PixelMask Selection,
    IReadOnlyDictionary<EditableStrokeKey, EventChange> Changes)
{
    public static EditRequest Event(LayerKey layer, PixelMask selection,
        IReadOnlyDictionary<EditableStrokeKey, EventChange> changes)
        => new(EditKind.EventEditing, layer, selection, changes);
}

internal sealed record SessionSnapshot(string MechanicalPath, string SessionId, long Frequency,
    IReadOnlyList<JsonElement> Frames, StrokeReplay.MemolineReplayDocument Replay,
    IReadOnlyList<LayerPacket> Packets, ulong BoundaryAppendId, long BoundaryTicks)
{
    public IReadOnlyList<PacketReplay.HistorySlot> History { get; init; } = [];
}

internal sealed record EditSession(SessionSnapshot LiveView, EditIndex Index, LayerKey? CurrentLayer);
