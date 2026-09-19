using SkiaSharp;

namespace Compositor.Core.Geometry;

/// <summary>
/// A 2-D affine transform with <c>CGAffineTransform</c>'s layout and composition order:
/// <c>x' = a·x + c·y + tx</c>, <c>y' = b·x + d·y + ty</c>. The chaining methods
/// (<see cref="Rotated"/>, <see cref="Scaled"/>, <see cref="Translated"/>) prepend like CG's
/// <c>rotated(by:)</c> family, so the last one applied acts first on points.
/// </summary>
public readonly record struct AffineTransform(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly AffineTransform Identity = new(1, 0, 0, 1, 0, 0);

    public static AffineTransform Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    public static AffineTransform Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static AffineTransform Rotation(double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new AffineTransform(cos, sin, -sin, cos, 0, 0);
    }

    public bool IsIdentity => this == Identity;

    /// <summary><c>this</c> followed by <paramref name="other"/> (CG's <c>concatenating</c>).</summary>
    public AffineTransform Concatenating(AffineTransform other) => new(
        (A * other.A) + (B * other.C),
        (A * other.B) + (B * other.D),
        (C * other.A) + (D * other.C),
        (C * other.B) + (D * other.D),
        (Tx * other.A) + (Ty * other.C) + other.Tx,
        (Tx * other.B) + (Ty * other.D) + other.Ty);

    public AffineTransform Translated(double tx, double ty) => Translation(tx, ty).Concatenating(this);

    public AffineTransform Scaled(double sx, double sy) => Scale(sx, sy).Concatenating(this);

    public AffineTransform Rotated(double radians) => Rotation(radians).Concatenating(this);

    public double Determinant => (A * D) - (B * C);

    /// <summary>The inverse, or <c>this</c> when singular (as CG returns the input unchanged).</summary>
    public AffineTransform Inverted()
    {
        var det = Determinant;
        if (det == 0 || !double.IsFinite(det))
        {
            return this;
        }

        var a = D / det;
        var b = -B / det;
        var c = -C / det;
        var d = A / det;
        return new AffineTransform(a, b, c, d, -((Tx * a) + (Ty * c)), -((Tx * b) + (Ty * d)));
    }

    public Point Apply(Point p) => new((A * p.X) + (C * p.Y) + Tx, (B * p.X) + (D * p.Y) + Ty);

    /// <summary>The axis-aligned box around the transformed corners (CG's <c>applying</c> on a rect).</summary>
    public Rect Apply(Rect r)
    {
        if (r.IsNull)
        {
            return r;
        }

        var p0 = Apply(new Point(r.MinX, r.MinY));
        var p1 = Apply(new Point(r.MaxX, r.MinY));
        var p2 = Apply(new Point(r.MinX, r.MaxY));
        var p3 = Apply(new Point(r.MaxX, r.MaxY));
        return Rect.FromEdges(
            Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
            Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
            Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
            Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
    }

    public SKMatrix ToSK() => new((float)A, (float)C, (float)Tx, (float)B, (float)D, (float)Ty, 0, 0, 1);
}
