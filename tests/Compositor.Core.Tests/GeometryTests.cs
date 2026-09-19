using Compositor.Core.Geometry;

namespace Compositor.Core.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void DisjointIntersectionIsNullAndNullNeverIntersects()
    {
        var a = new Rect(0, 0, 10, 10);
        var b = new Rect(20, 20, 5, 5);
        var n = a.Intersection(b);

        Assert.True(n.IsNull);
        Assert.True(n.IsEmpty);
        Assert.False(n.Intersects(a));
        Assert.False(a.Intersects(n));
        Assert.True(n.Intersection(a).IsNull);
        Assert.Equal(a, n.Union(a));
    }

    [Fact]
    public void EdgeTouchingRectanglesIntersectToAnEmptyButNotNullRect()
    {
        // CG: touching edges give a zero-width rect, which is empty but not null.
        var i = new Rect(0, 0, 10, 10).Intersection(new Rect(10, 0, 10, 10));
        Assert.False(i.IsNull);
        Assert.True(i.IsEmpty);
        Assert.Equal(0, i.Width);
    }

    [Fact]
    public void IntegralExpandsOutward()
    {
        Assert.Equal(new Rect(1, -3, 4, 5), new Rect(1.2, -2.1, 3.1, 3.5).Integral());
        Assert.Equal(new Rect(2, 2, 3, 3), new Rect(2, 2, 3, 3).Integral());
    }

    [Fact]
    public void AffineChainingMatchesCoreGraphicsOrder()
    {
        // CG: t.rotated(by:).scaledBy(...) applies the scale first to points, then rotation, then t.
        var t = AffineTransform.Translation(100, 0).Rotated(Math.PI / 2).Scaled(2, 2);
        var p = t.Apply(new Point(1, 0));

        // (1,0) → scale → (2,0) → rotate 90° → (0,2) → translate → (100,2)
        Assert.Equal(100, p.X, 9);
        Assert.Equal(2, p.Y, 9);
    }

    [Fact]
    public void ConcatenatingAppliesLeftThenRight()
    {
        var t = AffineTransform.Scale(2, 2).Concatenating(AffineTransform.Translation(5, 5));
        Assert.Equal(new Point(7, 7), t.Apply(new Point(1, 1)));
    }

    [Fact]
    public void InverseUndoesTheTransform()
    {
        var t = AffineTransform.Translation(3, -4).Rotated(0.7).Scaled(2, 0.5);
        var p = new Point(12.5, -3.25);
        var back = t.Inverted().Apply(t.Apply(p));
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
        Assert.Equal(AffineTransform.Identity, AffineTransform.Scale(0, 1).Inverted() with { A = 1 });
    }

    [Fact]
    public void RectTransformIsTheBoundingBoxOfTheCorners()
    {
        var r = AffineTransform.Rotation(Math.PI / 2).Apply(new Rect(0, 0, 4, 2));
        Assert.Equal(-2, r.MinX, 9);
        Assert.Equal(0, r.MinY, 9);
        Assert.Equal(2, r.Width, 9);
        Assert.Equal(4, r.Height, 9);
    }

    [Fact]
    public void SkMatrixHasTheSameEffect()
    {
        var t = AffineTransform.Translation(3, -4).Rotated(0.7).Scaled(2, 0.5);
        var m = t.ToSK();
        var p = m.MapPoint(new SkiaSharp.SKPoint(5, 6));
        var q = t.Apply(new Point(5, 6));
        Assert.Equal(q.X, p.X, 4);
        Assert.Equal(q.Y, p.Y, 4);
    }
}
