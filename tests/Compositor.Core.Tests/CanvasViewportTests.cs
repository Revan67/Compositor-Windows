using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Rendering;

namespace Compositor.Core.Tests;

/// <summary>The reference <c>CompositorTests</c> viewport cases.</summary>
public sealed class CanvasViewportTests
{
    private static readonly Size Document = new(1920, 1080);

    [Fact]
    public void ValidDimensions()
    {
        Assert.Equal(1920, CanvasDocument.ValidDimension(" 1920 "));
        Assert.Equal(30000, CanvasDocument.ValidDimension("30000"));
        Assert.Null(CanvasDocument.ValidDimension("30001"));
        Assert.Null(CanvasDocument.ValidDimension("0"));
        Assert.Null(CanvasDocument.ValidDimension("12.5"));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ActualPixelsAndRoundTrip(double backing)
    {
        var viewport = new CanvasViewport().Resized(new Size(800, 600), backing, null);
        foreach (var zoom in new[] { 0.25, 1, 3.75 })
        {
            viewport = viewport.Zoomed(zoom, viewport.Center, Document).Translated(new Size(73.5, -44.25));
            var pixel = new Point(183.25, 837.5);
            var result = viewport.DocumentPoint(viewport.ViewPoint(pixel, Document), Document);
            Assert.Equal(pixel.X, result.X, 6);
            Assert.Equal(pixel.Y, result.Y, 6);
            Assert.Equal(Document.Width * zoom, viewport.DocumentRect(Document).Width * backing, 6);
        }
    }

    [Fact]
    public void ZoomKeepsCursorPixelFixed()
    {
        var viewport = new CanvasViewport().Resized(new Size(1000, 700), 2, Document);
        var anchor = new Point(157, 221);
        var before = viewport.DocumentPoint(anchor, Document);
        var after = viewport.Zoomed(4, anchor, Document).DocumentPoint(anchor, Document);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void FitAndResizeModes()
    {
        var viewport = new CanvasViewport().Resized(new Size(800, 600), 2, Document);
        var rect = viewport.DocumentRect(Document);
        Assert.True(rect.Width <= 704.000001);
        Assert.True(rect.Height <= 504.000001);
        Assert.Equal(400, rect.MidX);
        Assert.Equal(300, rect.MidY);

        viewport = viewport.Translated(new Size(60, -35));
        var before = viewport.DocumentPoint(viewport.Center, Document);
        var zoom = viewport.Zoom;
        viewport = viewport.Resized(new Size(1200, 800), 1, Document);
        var after = viewport.DocumentPoint(viewport.Center, Document);
        Assert.Equal(zoom, viewport.Zoom);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        viewport = viewport.Fit(Document);
        Assert.Equal(Size.Zero, viewport.Pan);
        Assert.True(viewport.FollowsFit);
    }

    [Fact]
    public void ZoomIsClampedAndNonFiniteIgnored()
    {
        var viewport = new CanvasViewport().Resized(new Size(800, 600), 1, Document);
        Assert.Equal(CanvasViewport.MaxZoom, viewport.Zoomed(1000, viewport.Center, Document).Zoom);
        Assert.Equal(CanvasViewport.MinZoom, viewport.Zoomed(0, viewport.Center, Document).Zoom);
        Assert.Equal(viewport, viewport.Zoomed(double.NaN, viewport.Center, Document));
    }
}
