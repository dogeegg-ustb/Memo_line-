namespace WorkspaceBorderDetect.Models;

/// <summary>
/// Shared geometry model (mirrored under app/Models for the WPF host).
/// Keep in sync with app/Models/IntRect.cs until a dedicated class library is linked.
/// </summary>
public readonly struct IntRect : IEquatable<IntRect>
{
    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public IntRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public static IntRect FromXYWH(int x, int y, int width, int height)
        => new(x, y, x + width, y + height);

    public IntRect ClampTo(IntRect bounds)
    {
        int l = Math.Max(Left, bounds.Left);
        int t = Math.Max(Top, bounds.Top);
        int r = Math.Min(Right, bounds.Right);
        int b = Math.Min(Bottom, bounds.Bottom);
        if (r < l) r = l;
        if (b < t) b = t;
        return new IntRect(l, t, r, b);
    }

    public bool Equals(IntRect other)
        => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

    public override bool Equals(object? obj) => obj is IntRect other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    public override string ToString() => $"[{Left},{Top},{Right},{Bottom})";
}
