using SkiaSharp;

namespace Compositor.Core.Geometry;

/// <summary>A position in document pixels, y down.</summary>
public readonly record struct Point(double X, double Y)
{
    public static readonly Point Zero = new(0, 0);

    public static Point operator +(Point a, Size b) => new(a.X + b.Width, a.Y + b.Height);
    public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    public Point Offset(double dx, double dy) => new(X + dx, Y + dy);

    public Point Rounded() => new(Math.Round(X), Math.Round(Y));

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public SKPoint ToSK() => new((float)X, (float)Y);

    public override string ToString() => $"({X}, {Y})";
}
