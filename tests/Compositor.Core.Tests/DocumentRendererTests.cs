using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class DocumentRendererTests
{
    private static SKBitmap Solid(int w, int h, SKColor color, bool mask = false)
    {
        var bitmap = Bitmaps.Create(w, h, mask);
        bitmap.Erase(color);
        return bitmap;
    }

    private static ImportedImage Asset(SKBitmap image, string name = "layer") => new(image, Solid(1, 1, SKColors.Empty, image.IsMask()), name);

    private static ImageLayer Layer(SKBitmap image, double x = 0, double y = 0, string name = "layer") => new(Asset(image, name), new Point(x, y));

    /// <summary>Left half red, right half blue; top row has a green pixel at (0,0) so both axes are asymmetric.</summary>
    private static SKBitmap Asymmetric()
    {
        var bitmap = Bitmaps.Create(4, 4, false);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                bitmap.SetPixel(x, y, x < 2 ? SKColors.Red : SKColors.Blue);
            }
        }

        bitmap.SetPixel(0, 0, SKColors.Lime);
        return bitmap;
    }

    private static void AssertColor(SKColor expected, SKColor actual, int tolerance = 1)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance && Math.Abs(expected.Alpha - actual.Alpha) <= tolerance,
            $"expected {expected} got {actual}");
    }

    [Fact]
    public void LayersDrawBottomToTopAtTheirOrigins()
    {
        var doc = new CanvasDocument(8, 8, [Layer(Solid(8, 8, SKColors.Red)), Layer(Solid(2, 2, SKColors.Blue), 3, 3)]);
        using var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(SKColors.Red, flat.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, flat.GetPixel(2, 3));
        Assert.Equal(SKColors.Blue, flat.GetPixel(3, 3));
        Assert.Equal(SKColors.Blue, flat.GetPixel(4, 4));
        Assert.Equal(SKColors.Red, flat.GetPixel(5, 5));
    }

    [Fact]
    public void FlipsMirrorTheLayerAboutItsCentre()
    {
        var image = Asymmetric();
        var plain = new CanvasDocument(4, 4, [Layer(image)]);
        using var upright = DocumentRenderer.Flatten(plain);
        Assert.Equal(SKColors.Lime, upright.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, upright.GetPixel(1, 3));
        Assert.Equal(SKColors.Blue, upright.GetPixel(3, 0));

        var flippedX = Layer(image) with { Transform = new LayerTransform(Point.Zero, new Size(4, 4), FlipX: true) };
        using var fx = DocumentRenderer.Flatten(plain with { Layers = [flippedX] });
        Assert.Equal(SKColors.Lime, fx.GetPixel(3, 0));
        Assert.Equal(SKColors.Blue, fx.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, fx.GetPixel(3, 3));

        var flippedY = Layer(image) with { Transform = new LayerTransform(Point.Zero, new Size(4, 4), FlipY: true) };
        using var fy = DocumentRenderer.Flatten(plain with { Layers = [flippedY] });
        Assert.Equal(SKColors.Lime, fy.GetPixel(0, 3));
        Assert.Equal(SKColors.Red, fy.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, fy.GetPixel(3, 3));
    }

    [Fact]
    public void RotationIsClockwiseAboutTheCentre()
    {
        // A 4×2 red bar centred in a 6×6 canvas, rotated 90° clockwise, becomes a 2×4 bar.
        var layer = Layer(Solid(4, 2, SKColors.Red)) with { Transform = new LayerTransform(new Point(1, 2), new Size(4, 2), Rotation: 90, Sampling: LayerSampling.Nearest) };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(6, 6, [layer]));
        Assert.Equal(SKColors.Red, flat.GetPixel(2, 1));
        Assert.Equal(SKColors.Red, flat.GetPixel(3, 4));
        Assert.Equal(SKColors.Empty, flat.GetPixel(1, 3));
        Assert.Equal(SKColors.Empty, flat.GetPixel(4, 3));
        Assert.Equal(SKColors.Empty, flat.GetPixel(2, 0));
    }

    [Fact]
    public void ScalingPlacesPixelsBySize()
    {
        var layer = Layer(Asymmetric()) with { Transform = new LayerTransform(Point.Zero, new Size(8, 8), Sampling: LayerSampling.Nearest) };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(8, 8, [layer]));
        Assert.Equal(SKColors.Lime, flat.GetPixel(1, 1));
        Assert.Equal(SKColors.Red, flat.GetPixel(3, 7));
        Assert.Equal(SKColors.Blue, flat.GetPixel(4, 0));
    }

    [Fact]
    public void OpacityAppliesOnce()
    {
        var doc = new CanvasDocument(2, 2, [Layer(Solid(2, 2, SKColors.White)), Layer(Solid(2, 2, SKColors.Black)) with { Opacity = 0.25 }]);
        using var flat = DocumentRenderer.Flatten(doc);
        AssertColor(new SKColor(191, 191, 191), flat.GetPixel(0, 0));
    }

    public static TheoryData<LayerBlendMode, byte, byte, double> BlendCases => new()
    {
        // mode, backdrop, source, alpha; expected from the W3C separable formulas below.
        { LayerBlendMode.Multiply, 200, 100, 1 },
        { LayerBlendMode.Screen, 200, 100, 1 },
        { LayerBlendMode.Darken, 200, 100, 0.5 },
        { LayerBlendMode.Lighten, 100, 200, 0.5 },
        { LayerBlendMode.Difference, 200, 50, 1 },
        { LayerBlendMode.Overlay, 200, 100, 1 },
        { LayerBlendMode.Overlay, 60, 100, 1 },
        { LayerBlendMode.SoftLight, 60, 200, 0.75 },
        { LayerBlendMode.ColorDodge, 100, 100, 0.5 },
        { LayerBlendMode.ColorBurn, 150, 100, 0.5 },
    };

    [Theory]
    [MemberData(nameof(BlendCases))]
    public void SeparableBlendModesMatchTheSpecIncludingTranslucentSources(LayerBlendMode mode, byte backdrop, byte source, double alpha)
    {
        static double Blend(LayerBlendMode m, double cb, double cs) => m switch
        {
            LayerBlendMode.Multiply => cb * cs,
            LayerBlendMode.Screen => cb + cs - (cb * cs),
            LayerBlendMode.Darken => Math.Min(cb, cs),
            LayerBlendMode.Lighten => Math.Max(cb, cs),
            LayerBlendMode.Difference => Math.Abs(cb - cs),
            LayerBlendMode.Overlay => cb <= 0.5 ? cs * 2 * cb : cs + ((2 * cb) - 1) - (cs * ((2 * cb) - 1)),
            LayerBlendMode.SoftLight => cs <= 0.5
                ? cb - ((1 - (2 * cs)) * cb * (1 - cb))
                : cb + ((2 * cs - 1) * ((cb <= 0.25 ? (((16 * cb) - 12) * cb + 4) * cb : Math.Sqrt(cb)) - cb)),
            LayerBlendMode.ColorDodge => cb == 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
            LayerBlendMode.ColorBurn => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
            _ => throw new ArgumentOutOfRangeException(nameof(m)),
        };

        // Opaque backdrop: Co = (1 - αs)·Cb + αs·B(Cb, Cs).
        var cb = backdrop / 255.0;
        var cs = source / 255.0;
        var expected = (byte)Math.Round((((1 - alpha) * cb) + (alpha * Blend(mode, cb, cs))) * 255);

        var gray = new SKColor(backdrop, backdrop, backdrop);
        var top = Layer(Solid(2, 2, new SKColor(source, source, source))) with { BlendMode = mode, Opacity = alpha };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(2, 2, [Layer(Solid(2, 2, gray)), top]));
        AssertColor(new SKColor(expected, expected, expected), flat.GetPixel(1, 1), tolerance: 2);
    }

    [Fact]
    public void LayerMaskHidesWhereBlack()
    {
        var mask = Bitmaps.Create(2, 1, mask: true);
        mask.SetPixel(0, 0, SKColors.White);
        mask.SetPixel(1, 0, SKColors.Black);
        var layer = Layer(Solid(4, 4, SKColors.Red)) with { Mask = LayerMask.AssetFrom(mask) is { } a ? new LayerMask(a) : null, Transform = new LayerTransform(Point.Zero, new Size(4, 4), Sampling: LayerSampling.Nearest) };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(4, 4, [layer]));
        Assert.Equal(SKColors.Red, flat.GetPixel(0, 2));
        Assert.Equal(SKColors.Red, flat.GetPixel(1, 2));
        Assert.Equal(SKColors.Empty, flat.GetPixel(2, 2));
        Assert.Equal(SKColors.Empty, flat.GetPixel(3, 2));

        var disabled = layer with { Mask = layer.Mask! with { IsEnabled = false } };
        using var unmasked = DocumentRenderer.Flatten(new CanvasDocument(4, 4, [disabled]));
        Assert.Equal(SKColors.Red, unmasked.GetPixel(3, 2));
    }

    [Fact]
    public void FolderMaskMultipliesDescendantsWithoutIsolatingTheirBlend()
    {
        var folderMask = Bitmaps.Create(2, 1, mask: true);
        folderMask.SetPixel(0, 0, SKColors.White);
        folderMask.SetPixel(1, 0, SKColors.Black);
        var folder = new ImageLayer("Folder", new Size(4, 4)) with { IsGroup = true, Mask = new LayerMask(LayerMask.AssetFrom(folderMask)), Transform = new LayerTransform(Point.Zero, new Size(4, 4), Sampling: LayerSampling.Nearest) };
        var below = Layer(Solid(4, 4, new SKColor(200, 200, 200)));
        var child = Layer(Solid(4, 4, new SKColor(100, 100, 100))) with { ParentId = folder.Id, BlendMode = LayerBlendMode.Multiply };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(4, 4, [below, folder, child]));

        // Left half: 200/255 × 100/255 → 78. Right half: masked out, backdrop shows.
        AssertColor(new SKColor(78, 78, 78), flat.GetPixel(0, 1));
        AssertColor(new SKColor(200, 200, 200), flat.GetPixel(3, 1));
    }

    [Fact]
    public void HiddenLayersAndHiddenFoldersDoNotDraw()
    {
        var folder = new ImageLayer("Folder", new Size(2, 2)) with { IsGroup = true, IsVisible = false };
        var inFolder = Layer(Solid(2, 2, SKColors.Red)) with { ParentId = folder.Id };
        var hidden = Layer(Solid(2, 2, SKColors.Blue)) with { IsVisible = false };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(2, 2, [folder, inFolder, hidden]));
        Assert.Equal(SKColors.Empty, flat.GetPixel(0, 0));
    }

    [Fact]
    public void ClippingStackSharesTheBaseAlphaWithoutThickeningSoftEdges()
    {
        // Base: 2×2 red at (1,1) with alpha 0.5. Clipped child: full-canvas opaque green.
        var soft = Solid(2, 2, new SKColor(255, 0, 0, 128));
        var @base = Layer(soft, 1, 1);
        var clipped = Layer(Solid(4, 4, SKColors.Lime)) with { MaskSourceId = @base.Id };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(4, 4, [@base, clipped]));

        // Outside the base: nothing. Inside: the child's colour at the base's alpha, premultiplied.
        Assert.Equal(SKColors.Empty, flat.GetPixel(0, 0));
        Assert.Equal(SKColors.Empty, flat.GetPixel(3, 3));
        var inside = flat.GetPixel(1, 1);
        Assert.Equal(128, inside.Alpha);
        Assert.Equal(0, inside.Red);
        Assert.Equal(255, inside.Green);
    }

    [Fact]
    public void ClippingStackCompositesWithTheBaseBlendMode()
    {
        var backdrop = Layer(Solid(2, 2, new SKColor(200, 200, 200)));
        var @base = Layer(Solid(2, 2, SKColors.White)) with { BlendMode = LayerBlendMode.Multiply };
        var clipped = Layer(Solid(2, 2, new SKColor(100, 100, 100))) with { MaskSourceId = @base.Id };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(2, 2, [backdrop, @base, clipped]));
        // Group is the clipped grey (drawn over an opaque white base), then multiplied with the backdrop.
        AssertColor(new SKColor(78, 78, 78), flat.GetPixel(0, 0));
    }

    [Fact]
    public void NonAdjacentClippingLinkUsesTheSourceCoverage()
    {
        var source = Layer(Solid(2, 2, SKColors.Red), 1, 1);
        var between = Layer(Solid(4, 4, SKColors.Blue)) with { Opacity = 0.5 };
        var clipped = Layer(Solid(4, 4, SKColors.Lime)) with { MaskSourceId = source.Id };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(4, 4, [source, between, clipped]));
        Assert.Equal(SKColors.Lime, flat.GetPixel(1, 1));
        Assert.Equal(SKColors.Lime, flat.GetPixel(2, 2));
        AssertColor(new SKColor(0, 0, 255, 128), flat.GetPixel(0, 0));
        AssertColor(new SKColor(0, 0, 255, 128), flat.GetPixel(3, 3));
    }

    [Fact]
    public void NonAdjacentClippedLayerStillBlendsWithTheCanvasBeneath()
    {
        // Source at the bottom, an unrelated layer between, then a Multiply layer clipped to the source: inside the
        // source it must multiply with the grey it sits on, not with an empty isolation layer.
        var source = Layer(Solid(2, 2, SKColors.White), 1, 1);
        var grey = Layer(Solid(4, 4, new SKColor(200, 200, 200)));
        var clipped = Layer(Solid(4, 4, new SKColor(100, 100, 100))) with { MaskSourceId = source.Id, BlendMode = LayerBlendMode.Multiply };
        using var flat = DocumentRenderer.Flatten(new CanvasDocument(4, 4, [source, grey, clipped]));
        AssertColor(new SKColor(78, 78, 78), flat.GetPixel(1, 1));
        AssertColor(new SKColor(200, 200, 200), flat.GetPixel(0, 0));
    }

    [Fact]
    public void MaskPlacedApartFromItsLayerIsResampledIntoTheLayerGrid()
    {
        var mask = Bitmaps.Create(1, 1, mask: true);
        mask.Erase(SKColors.White);
        // A white 1×1 mask placed over the right half only; the thumbnail edge is white so the rest reveals… but
        // Background() reads the mask's own thumbnail: white → outside the placement stays revealed.
        var placed = new LayerMask(LayerMask.AssetFrom(mask)) { Placement = new LayerTransform(new Point(2, 0), new Size(2, 4)) };
        var clip = placed.ClipImage(new LayerTransform(Point.Zero, new Size(4, 4)), 4, 4)!;
        Assert.Equal(4, clip.Width);
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, clip.GetPixelSpan()[i]));

        var black = Bitmaps.Create(1, 1, mask: true);
        var hiding = new LayerMask(LayerMask.AssetFrom(black)) { Placement = new LayerTransform(new Point(2, 0), new Size(2, 4)) };
        var hidingClip = hiding.ClipImage(new LayerTransform(Point.Zero, new Size(4, 4)), 4, 4)!;
        Assert.Equal(0, hidingClip.GetPixelSpan()[0]);
        Assert.Equal(0, hidingClip.GetPixelSpan()[3]);
        Assert.Null(hiding.PlacementMovingLayer(new LayerTransform(Point.Zero, new Size(4, 4)), new LayerTransform(new Point(5, 5), new Size(4, 4))));
    }

    [Fact]
    public void InvalidHierarchiesAndLinksAreRejected()
    {
        var a = new ImageLayer("A", new Size(2, 2)) with { IsGroup = true };
        var b = new ImageLayer("B", new Size(2, 2)) with { IsGroup = true, ParentId = a.Id };
        var cyclic = a with { ParentId = b.Id };
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.Validate([cyclic, b]));

        var pixels = Layer(Solid(1, 1, SKColors.Red));
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.Validate([pixels with { IsGroup = true }]));
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.Validate([pixels with { ParentId = Guid.NewGuid() }]));
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.Validate([pixels with { ParentId = pixels.Id }]));

        var other = Layer(Solid(1, 1, SKColors.Blue));
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.ValidateMaskLinks([pixels with { MaskSourceId = a.Id }, a]));
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.ValidateMaskLinks([pixels with { MaskSourceId = other.Id }, other with { MaskSourceId = pixels.Id }]));
        Assert.Throws<InvalidProjectException>(() => LayerHierarchy.ValidateMaskLinks([pixels with { MaskSourceId = Guid.NewGuid() }]));
        LayerHierarchy.ValidateMaskLinks([pixels with { MaskSourceId = other.Id }, other]);
    }

    [Fact]
    public void EntriesFollowTreeOrderAndInheritVisibility()
    {
        var folder = new ImageLayer("F", new Size(1, 1)) with { IsGroup = true, IsVisible = false };
        var child = Layer(Solid(1, 1, SKColors.Red)) with { ParentId = folder.Id };
        var top = Layer(Solid(1, 1, SKColors.Blue));
        var entries = LayerHierarchy.Entries([folder, child, top]);
        Assert.Equal([folder.Id, child.Id, top.Id], entries.Select(e => e.Layer.Id));
        Assert.Equal([0, 1, 0], entries.Select(e => e.Depth));
        Assert.Equal([false, false, true], entries.Select(e => e.Visible));
        Assert.Equal([top.Id, folder.Id, child.Id], LayerHierarchy.Entries([folder, child, top], topFirst: true).Select(e => e.Layer.Id));
        Assert.Equal([top.Id, folder.Id], LayerHierarchy.Entries([folder, child, top], topFirst: true, collapsed: new HashSet<Guid> { folder.Id }).Select(e => e.Layer.Id));
        Assert.Equal([child.Id], LayerHierarchy.DescendantIds([folder, child, top], folder.Id));
    }
}
