using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReplay;

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
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ReplacementBrushState { get; init; }
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

    /// <summary>Sets pixels <paramref name="x0"/>..<paramref name="x1"/> (inclusive) of row <paramref name="y"/>, a word at a time.</summary>
    public void SetRun(int x0, int x1, int y)
    {
        if (x0 > x1) return;
        if (!TryIndex(x0, y, out long bit) || !TryIndex(x1, y, out long end)) throw new ArgumentOutOfRangeException(nameof(x0));
        while (bit <= end)
        {
            int word = (int)(bit >> 6), shift = (int)(bit & 63), n = (int)Math.Min(64 - shift, end - bit + 1);
            ulong run = (n == 64 ? ulong.MaxValue : (1UL << n) - 1) << shift;
            Count += System.Numerics.BitOperations.PopCount(run & ~_words[word]);
            _words[word] |= run; bit += n;
        }
    }

    public IEnumerable<(int X, int Y)> Pixels()
    {
        // Same row-major order as a per-pixel scan, but empty 64-pixel words are skipped:
        // canvas-sized masks are mostly empty.
        if (Count == 0) yield break;
        for (int w = 0; w < _words.Length; w++)
        {
            ulong word = _words[w];
            while (word != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                long index = ((long)w << 6) + bit;
                yield return (Left + (int)(index % Width), Top + (int)(index / Width));
            }
        }
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

    public bool Intersects(PixelMask other)
    {
        if (IsEmpty || other.IsEmpty) return false;
        if (Left == other.Left && Top == other.Top && Width == other.Width && Height == other.Height)
        {
            for (int i = 0; i < _words.Length; i++)
                if ((_words[i] & other._words[i]) != 0) return true;
            return false;
        }
        int left = Math.Max(Left, other.Left), top = Math.Max(Top, other.Top);
        long right = Math.Min((long)Left + Width, (long)other.Left + other.Width);
        long bottom = Math.Min((long)Top + Height, (long)other.Top + other.Height);
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (Contains(x, y) && other.Contains(x, y)) return true;
        return false;
    }

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

    /// <summary>Every pixel of the bounds set, without visiting them one by one (a canvas has 35 million).</summary>
    public static PixelMask Filled(int left, int top, int width, int height)
    {
        var result = new PixelMask(left, top, width, height);
        long bits = (long)width * height;
        if (bits == 0) return result;
        result._words.AsSpan().Fill(ulong.MaxValue);
        if ((bits & 63) != 0) result._words[^1] = (1UL << (int)(bits & 63)) - 1;
        result.Count = bits; return result;
    }

    /// <summary>ORs a mask lying inside these bounds into this one, up to 64 pixels of a row at a time.</summary>
    public void UnionWithin(PixelMask other)
    {
        if (other.IsEmpty) return;
        if (other.Left < Left || other.Top < Top || (long)other.Left + other.Width > (long)Left + Width ||
            (long)other.Top + other.Height > (long)Top + Height)
            throw new ArgumentException("合并的像素位集超出目标外框。", nameof(other));
        if (other.Left == Left && other.Top == Top && other.Width == Width && other.Height == Height) { UnionInPlace(other); return; }
        for (int row = 0; row < other.Height; row++)
        {
            long source = (long)row * other.Width, target = (long)(other.Top - Top + row) * Width + (other.Left - Left);
            for (int remaining = other.Width; remaining > 0;)
            {
                int n = Math.Min(64, remaining);
                ulong bits = ReadBits(other._words, source, n);
                if (bits != 0) OrBits(_words, target, bits, n);
                source += n; target += n; remaining -= n;
            }
        }
        Recount();
    }
    private static ulong ReadBits(ulong[] words, long bit, int n)
    {
        int word = (int)(bit >> 6), shift = (int)(bit & 63);
        ulong value = words[word] >> shift;
        if (shift != 0 && shift + n > 64) value |= words[word + 1] << (64 - shift);
        return n == 64 ? value : value & ((1UL << n) - 1);
    }
    private static void OrBits(ulong[] words, long bit, ulong value, int n)
    {
        int word = (int)(bit >> 6), shift = (int)(bit & 63);
        words[word] |= value << shift;
        if (shift != 0 && shift + n > 64) words[word + 1] |= value >> (64 - shift);
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
    // Endpoint pixels do not prove the read or write footprint of missing operations.
    public bool UnknownHistoryInterval { get; init; }
    public PixelMask W { get; init; } = new PixelMask(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
    public PixelMask Probe { get; init; } = new PixelMask(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
    public PixelMask Impact { get; init; } = new PixelMask(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
}

public sealed record CoverageSegment(double StartX, double StartY, double EndX, double EndY);
public sealed record LabelEvidence(string Id, ulong? OperationId, string Source, long FromTicks, long ToTicks,
    double Radius, IReadOnlyList<CoverageSegment> Segments, IReadOnlyList<string> ImageIds)
{
    public DirtyMatrix.Core.CoverageSettings? CoverageSettings { get; init; }
}

public sealed record LayerPacket(string PacketId, LayerKey Layer, long TriggerTicks,
    ImageRgba? Before, ImageRgba? Current, IReadOnlyList<DirtyImage> Images,
    bool IsBoundary = false, bool IsRecordingBaseline = false,
    string? AfterSnapshotId = null, string? NowSnapshotId = null,
    string? BaselineSource = null, int CanvasWidth = 0, int CanvasHeight = 0,
    bool FullResolution = true)
{
    public int SessionOrder { get; init; }
    public LayerKey? SourceLayer { get; init; }
    public IReadOnlyList<LabelEvidence> Labels { get; init; } = [];
    public JsonElement AfterDescriptor { get; init; }
    public JsonElement NowDescriptor { get; init; }
}

internal sealed record EditableStroke(EditableStrokeKey Key, ulong OperationId, long StartTicks,
    long EndTicks, IReadOnlyList<StrokeReplay.MemolineReplaySample> Samples,
    string? BrushName, LayerKey Layer, bool IsCausal, bool Eligible = true,
    string? IneligibleReason = null, IReadOnlyDictionary<string, double>? BrushNumbers = null)
{
    internal int SessionOrder { get; init; }
    internal LayerKey? SourceLayer { get; init; }
    internal bool IsUndone { get; init; }
    public ulong BeginEventId { get; init; }
    public JsonElement BrushState { get; init; }
    public BrushClassification? Classification { get; init; }
    public IReadOnlyList<DirtyImageKey> Images { get; init; } = [];
    public IReadOnlyDictionary<string, double> Numbers { get; } = BrushNumbers ?? ImmutableDictionary<string, double>.Empty;
    public PixelMask WriteMask { get; init; } = new(0, 0, 0, 0);
    public PixelMask ProbeMask { get; init; } = new(0, 0, 0, 0);
    public PixelMask ImpactMask { get; init; } = new(0, 0, 0, 0);
    public IReadOnlyList<LabelEvidence> CoverageLabels { get; init; } = [];
    // A size override requires the recipe recorded by the watcher, not guessed defaults.
    public DirtyMatrix.Core.CoverageSettings? FrozenCoverageSettings { get; init; }
}

internal sealed record UndoRedoEvidence(ulong InputAppendId,string Command,bool Ambiguous)
{
    internal int SessionOrder { get; init; }
}
internal sealed record UndoCheckpoint(string PacketId, ImageRgba Seed, ImageRgba Expected,
    IReadOnlyList<EditableStrokeKey> Events);
internal sealed record LayerIndex(LayerKey Layer, IReadOnlyList<LayerPacket> Packets,
    IReadOnlyList<EditableStroke> Strokes, bool ChainValid, IReadOnlyList<PlanIssue> Issues,
    ImageRgba? Baseline = null, ImageRgba? Current = null)
{
    internal IReadOnlyList<UndoRedoEvidence> UndoRedo { get; init; } = [];
    internal IReadOnlyList<UndoCheckpoint> UndoCheckpoints { get; init; } = [];
}

internal sealed record EditIndex(IReadOnlyDictionary<LayerKey, LayerIndex> Layers,
    IReadOnlyDictionary<EditableStrokeKey, EditableStroke> Strokes,
    IReadOnlyDictionary<DirtyImageKey, DirtyImage> Images,
    IReadOnlyList<PlanIssue> Issues)
{
    internal NativeUndoTimelineResult? UndoTimeline { get; init; }
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

internal sealed record EditSession(SessionSnapshot LiveView, EditIndex Index, LayerKey? CurrentLayer)
{
    internal IReadOnlyDictionary<string, SessionSnapshot> Sources { get; init; } =
        ImmutableDictionary<string, SessionSnapshot>.Empty;
    internal SessionSnapshot Source(string sessionId) => sessionId == LiveView.SessionId ? LiveView :
        Sources.TryGetValue(sessionId, out var source) ? source :
        throw new InvalidDataException("历史笔画缺少对应的原生会话：" + sessionId);
    internal JsonElement CanvasViewEvidence { get; init; }
    internal IReadOnlyList<string> BrushChoices { get; init; } = [];
    internal int? VerifiedUndoCapacity { get; init; }
    internal JsonElement LiveBrushEvidence { get; init; }
    internal ShortcutConfiguration? Shortcuts { get; init; }
}
