using SkiaSharp;

namespace Compositor.Core.Geometry;

/// <summary>
/// An axis-aligned rectangle in document pixels, stored as origin and size like <c>CGRect</c> so
/// ported math reads the same. <see cref="Null"/> plays the part of <c>CGRect.null</c>: what an
/// intersection of disjoint rectangles returns.
/// </summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public static readonly Rect Zero = new(0, 0, 0, 0);

    /// <summary>The empty result of a failed intersection; never contains or intersects anything.</summary>
    public static readonly Rect Null = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);

    public Rect(Point origin, Size size) : this(origin.X, origin.Y, size.Width, size.Height)
    {
    }

    public static Rect FromEdges(double minX, double minY, double maxX, double maxY) =>
        new(minX, minY, maxX - minX, maxY - minY);

    public Point Origin => new(X, Y);
    public Size Size => new(Width, Height);
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + (Width / 2);
    public double MidY => Y + (Height / 2);
    public Point Center => new(MidX, MidY);

    public bool IsNull => double.IsPositiveInfinity(X);

    /// <summary>True for <see cref="Null"/> and for any rectangle without area.</summary>
    public bool IsEmpty => IsNull || Width <= 0 || Height <= 0;

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);

    public Rect Offset(double dx, double dy) => IsNull ? this : new Rect(X + dx, Y + dy, Width, Height);

    public Rect Inset(double dx, double dy) => IsNull ? this : new Rect(X + dx, Y + dy, Width - (2 * dx), Height - (2 * dy));

    public bool Contains(Point p) => !IsNull && p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;

    public bool Intersects(Rect other) =>
        !IsEmpty && !other.IsEmpty && MinX < other.MaxX && other.MinX < MaxX && MinY < other.MaxY && other.MinY < MaxY;

    /// <summary>The overlap, or <see cref="Null"/> when there is none (CG semantics: edge-touching is null).</summary>
    public Rect Intersection(Rect other)
    {
        if (IsNull || other.IsNull)
        {
            return Null;
        }

        var minX = Math.Max(MinX, other.MinX);
        var minY = Math.Max(MinY, other.MinY);
        var maxX = Math.Min(MaxX, other.MaxX);
        var maxY = Math.Min(MaxY, other.MaxY);
        return maxX < minX || maxY < minY ? Null : FromEdges(minX, minY, maxX, maxY);
    }

    public Rect Union(Rect other)
    {
        if (IsNull)
        {
            return other;
        }

        if (other.IsNull)
        {
            return this;
        }

        return FromEdges(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
    }

    /// <summary>The smallest rectangle with integer edges containing this one (<c>CGRect.integral</c>).</summary>
    public Rect Integral()
    {
        if (IsNull)
        {
            return this;
        }

        var minX = Math.Floor(MinX);
        var minY = Math.Floor(MinY);
        return FromEdges(minX, minY, Math.Ceiling(MaxX), Math.Ceiling(MaxY));
    }

    public Rect Rounded() => IsNull ? this : new Rect(Math.Round(X), Math.Round(Y), Math.Round(Width), Math.Round(Height));

    public SKRect ToSK() => new((float)MinX, (float)MinY, (float)MaxX, (float)MaxY);

    public SKRectI ToSKI() => new((int)Math.Round(MinX), (int)Math.Round(MinY), (int)Math.Round(MaxX), (int)Math.Round(MaxY));

    public override string ToString() => IsNull ? "null" : $"({X}, {Y}, {Width}×{Height})";
}
