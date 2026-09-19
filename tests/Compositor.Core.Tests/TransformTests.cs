using Compositor.Core.Document;
using Compositor.Core.Geometry;

namespace Compositor.Core.Tests;

/// <summary>The geometry half of the reference <c>TransformTests</c>; the session-driven half arrives with the Move tool.</summary>
public sealed class TransformTests
{
    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;

    [Fact]
    public void RotatedResizeKeepsOppositeAnchorAtEveryHandle()
    {
        var original = new LayerTransform(new Point(31, -19), new Size(200, 100), Rotation: 37);
        for (var index = 0; index < LayerTransform.Handles.Count; index++)
        {
            var handle = LayerTransform.Handles[index];
            var opposite = new Point(1 - handle.X, 1 - handle.Y);
            var start = original.PointAt(handle);
            var drag = new TransformDrag(original, start, new TransformDragMode.Resize(index));
            foreach (var locked in new[] { true, false })
            {
                var changed = drag.Updated(new Point(start.X + 34, start.Y + 17), lockRatio: locked, shift: false);
                Assert.True(Near(original.PointAt(opposite), changed.PointAt(opposite)), $"handle {index} locked {locked}");
                if (locked)
                {
                    Assert.True(Math.Abs((changed.Size.Width / changed.Size.Height) - 2) < 0.0001);
                }

                Assert.True(changed.IsValid);
            }
        }
    }

    [Fact]
    public void MoveRotateAndShiftConstraints()
    {
        var original = new LayerTransform(Point.Zero, new Size(100, 50));
        var move = new TransformDrag(original, new Point(40, 20), new TransformDragMode.Move());
        var moved = move.Updated(new Point(60, 25), lockRatio: true, shift: true);
        Assert.Equal(new Point(20, 0), moved.Origin);

        var rotate = new TransformDrag(original, new Point(100, 25), new TransformDragMode.Rotate());
        var rotated = rotate.Updated(new Point(50, 75), lockRatio: true, shift: false);
        Assert.True(Math.Abs(rotated.Rotation - 90) < 0.0001);
        Assert.Equal(original.Center, rotated.Center);
        var snapped = rotate.Updated(new Point(99, 45), lockRatio: true, shift: true);
        Assert.Equal(0, snapped.Rotation % 15);

        var resize = new TransformDrag(original, new Point(100, 50), new TransformDragMode.Resize(4));
        var free = resize.Updated(new Point(150, 50), lockRatio: true, shift: true);
        Assert.Equal(new Size(150, 50), free.Size);
    }

    [Fact]
    public void RotatedHitTesting()
    {
        var transform = new LayerTransform(new Point(100, 200), new Size(100, 50), Rotation: 90);
        Assert.True(transform.Contains(transform.Center));
        Assert.True(transform.Contains(new Point(150, 265)));
        Assert.False(transform.Contains(new Point(190, 225)));
    }

    [Fact]
    public void ScalePercentSetsBothSidesAboutTheCenter()
    {
        var pixels = new Size(400, 200);
        var stretched = new LayerTransform(new Point(10, 20), new Size(800, 600), Rotation: 30);
        Assert.Equal(200, stretched.ScalePercent(pixels));
        var scaled = stretched.ScaledToPercent(50, pixels);
        Assert.Equal(new Size(200, 100), scaled.Size);
        Assert.True(Near(scaled.Center, stretched.Center));
        Assert.Equal(30, scaled.Rotation);
        Assert.Equal(50, scaled.ScalePercent(pixels));
    }

    [Fact]
    public void FollowingAPlainMoveCarriesExactlyAndAScaleGoesThroughPlacement()
    {
        var layer = new LayerTransform(new Point(0, 0), new Size(100, 100));
        var mask = new LayerTransform(new Point(25, 25), new Size(50, 50), Rotation: 10);

        var movedLayer = layer with { Origin = new Point(40, -10) };
        Assert.Equal(mask with { Origin = new Point(65, 15) }, mask.Following(layer, movedLayer));

        var doubled = layer with { Size = new Size(200, 200) };
        var followed = mask.Following(layer, doubled);
        Assert.True(Near(new Point(100, 100), followed.Center));
        Assert.True(Math.Abs(followed.Size.Width - 100) < 0.001 && Math.Abs(followed.Size.Height - 100) < 0.001);
        Assert.True(Math.Abs(followed.Rotation - 10) < 0.001);
        Assert.Equal(mask, mask.Following(layer, layer));
    }

    [Fact]
    public void PlacingRecoversAFlippedRotatedTransform()
    {
        var t = new LayerTransform(new Point(12, -7), new Size(80, 30), Rotation: 200, FlipX: true, FlipY: true, Sampling: LayerSampling.Nearest);
        var back = new LayerTransform(Point.Zero, new Size(1, 1), Rotation: 190, FlipX: true, Sampling: LayerSampling.Nearest).Placing(t.UnitToDocument);
        Assert.True(Near(t.Center, back.Center));
        Assert.True(Math.Abs(t.Size.Width - back.Size.Width) < 1e-9 && Math.Abs(t.Size.Height - back.Size.Height) < 1e-9);
        Assert.True(Math.Abs(t.Rotation - back.Rotation) < 1e-9);
        back = back.Rounded();
        Assert.True(back.FlipX && back.FlipY);
        Assert.Equal(LayerSampling.Nearest, back.Sampling);
        Assert.True(back.SamePlacement(t with { Sampling = LayerSampling.High }));
        Assert.False(back.SamePlacement(t with { Origin = new Point(0, 0) }));
    }

    [Fact]
    public void RoundedKeepsAtLeastOnePixel()
    {
        var t = new LayerTransform(new Point(1.6, -2.5), new Size(0.2, 9.5), Rotation: 44.6);
        Assert.Equal(new LayerTransform(new Point(2, -2), new Size(1, 10), Rotation: 45), t.Rounded());
        Assert.False((t with { Size = new Size(0.5, 10) }).IsValid);
        Assert.False((t with { Origin = new Point(2_000_000, 0) }).IsValid);
        Assert.False((t with { Rotation = double.NaN }).IsValid);
    }

    [Fact]
    public void SnapPicksTheNearestGuideWithinToleranceOnEachAxis()
    {
        var box = new Rect(100, 100, 50, 20);
        var (offset, x, y) = TransformSnap.Offset(box, xs: [153, 300], ys: [95, 400], tolerance: 5);
        Assert.Equal(new Size(3, -5), offset);
        Assert.Equal(153, x);
        Assert.Equal(95, y);

        var (none, nx, ny) = TransformSnap.Offset(box, xs: [200], ys: [200], tolerance: 5);
        Assert.Equal(Size.Zero, none);
        Assert.Null(nx);
        Assert.Null(ny);
    }
}
