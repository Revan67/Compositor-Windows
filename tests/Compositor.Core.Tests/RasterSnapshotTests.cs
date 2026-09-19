using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Unit tests of the sparse raster itself. The reference suite of the same name drives it through
/// brush strokes; those scenarios (immutability across strokes, display/export agreement, mask
/// mouse-up) are ported alongside the brush in Phase 3.
/// </summary>
public sealed class RasterSnapshotTests
{
    private static SKBitmap Solid(int w, int h, SKColor color, bool mask = false)
    {
        var bitmap = Bitmaps.Create(w, h, mask);
        bitmap.Erase(color);
        return bitmap;
    }

    private static ImportedImage Asset(SKBitmap image) => new(image, Solid(1, 1, SKColors.Transparent), "test");

    private static RasterPatch Patch(double x, double y, int w, int h, SKColor color, bool mask = false) =>
        new(new Rect(x, y, w, h), Solid(w, h, color, mask));

    [Fact]
    public void FirstCommitKeepsTheImageAsBaseAndDoesNotMaterialize()
    {
        var source = Asset(Solid(64, 64, SKColors.Red));
        var raster = RasterSnapshot.Replacing(source, new Rect(0, 0, 64, 64), [Patch(16, 16, 8, 8, SKColors.Blue)], new Rect(0, 0, 64, 64));

        Assert.Same(source.Image, raster.Base);
        Assert.Equal(new Rect(0, 0, 64, 64), raster.BaseRect);
        Assert.Single(raster.Patches);
        Assert.False(raster.HasMaterializedPixels);

        var pixels = raster.Pixels;
        Assert.True(raster.HasMaterializedPixels);
        Assert.Same(pixels, raster.Pixels);
        Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, pixels.GetPixel(15, 16));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(16, 16));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(23, 23));
        Assert.Equal(SKColors.Red, pixels.GetPixel(24, 23));
    }

    [Fact]
    public void SecondCommitSharesTheBaseAndSplitsTheOlderPatchAroundTheNewOne()
    {
        var source = Asset(Solid(64, 64, SKColors.Red));
        var first = RasterSnapshot.Replacing(source, new Rect(0, 0, 64, 64), [Patch(8, 8, 32, 32, SKColors.Blue)], new Rect(0, 0, 64, 64));
        var painted = new ImportedImage(first, first.Thumbnail(), "test");
        var second = RasterSnapshot.Replacing(painted, new Rect(0, 0, 64, 64), [Patch(16, 16, 16, 16, SKColors.Green)], new Rect(0, 0, 64, 64));

        Assert.Same(first.Base, second.Base);
        // The 32² blue patch loses its middle: four pieces around the green square, plus the green square.
        Assert.Equal(5, second.Patches.Count);
        Assert.False(first.HasMaterializedPixels);
        Assert.False(second.HasMaterializedPixels);

        // Pieces never overlap each other.
        for (var i = 0; i < second.Patches.Count; i++)
        {
            for (var j = i + 1; j < second.Patches.Count; j++)
            {
                Assert.False(second.Patches[i].Rect.Intersects(second.Patches[j].Rect), $"{second.Patches[i].Rect} overlaps {second.Patches[j].Rect}");
            }
        }

        var pixels = second.Pixels;
        Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(8, 8));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(15, 31));
        Assert.Equal(SKColors.Green, pixels.GetPixel(16, 16));
        Assert.Equal(SKColors.Green, pixels.GetPixel(31, 31));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(32, 32));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(39, 39));
        Assert.Equal(SKColors.Red, pixels.GetPixel(40, 40));
        // The first snapshot is untouched by the second commit.
        Assert.Equal(SKColors.Blue, first.Pixels.GetPixel(20, 20));
    }

    [Fact]
    public void CropShiftsEverythingAndCarriesAlignment()
    {
        // A blank layer painted from nothing: no base. The stroke's tile at (300,300) crops the raster to it.
        var first = RasterSnapshot.Replacing(null, new Rect(300, 300, 32, 32), [Patch(300, 300, 32, 32, SKColors.Blue)], new Rect(300, 300, 32, 32));
        Assert.Null(first.Base);
        Assert.Equal(new Rect(0, 0, 32, 32), first.Patches[0].Rect);
        Assert.Equal(new Point(0, 0), first.Alignment);

        // The next stroke extends up-left by 100; the old patch and alignment move by +100.
        var painted = new ImportedImage(first, first.Thumbnail(), "test");
        var second = RasterSnapshot.Replacing(painted, new Rect(300, 300, 32, 32), [Patch(200, 200, 16, 16, SKColors.Green)], new Rect(200, 200, 132, 132));
        Assert.Equal(132, second.Width);
        Assert.Contains(second.Patches, p => p.Rect == new Rect(100, 100, 32, 32));
        Assert.Contains(second.Patches, p => p.Rect == new Rect(0, 0, 16, 16));
        Assert.Equal(new Point(100, 100), second.Alignment);
        Assert.Equal(SKColors.Green, second.Pixels.GetPixel(0, 0));
        Assert.Equal(SKColors.Empty, second.Pixels.GetPixel(50, 50));
        Assert.Equal(SKColors.Blue, second.Pixels.GetPixel(100, 100));
    }

    [Fact]
    public void PatchesOutsideTheCropAreClippedOrDropped()
    {
        var raster = RasterSnapshot.Replacing(null, new Rect(0, 0, 32, 32),
            [Patch(-8, -8, 16, 16, SKColors.Blue), Patch(100, 100, 8, 8, SKColors.Green)], new Rect(0, 0, 32, 32));

        var patch = Assert.Single(raster.Patches);
        Assert.Equal(new Rect(0, 0, 8, 8), patch.Rect);
        Assert.Equal(8, patch.Image.Width);
        Assert.Equal(SKColors.Blue, raster.Pixels.GetPixel(7, 7));
        Assert.Equal(SKColors.Empty, raster.Pixels.GetPixel(8, 8));
    }

    [Fact]
    public void MaskExpansionRevealsWhiteOutsideTheOriginalExtent()
    {
        var black = Solid(16, 16, SKColors.Black, mask: true);
        var source = new ImportedImage(black, Solid(1, 1, SKColors.Black, true), "mask");
        // The mask grows from 16² at (8,8) to 32²; painted white in one corner tile.
        var raster = RasterSnapshot.Replacing(source, new Rect(8, 8, 16, 16), [Patch(0, 0, 4, 4, new SKColor(0x80, 0x80, 0x80), mask: true)], new Rect(0, 0, 32, 32), isMask: true);

        Assert.True(raster.IsMask);
        var pixels = raster.Pixels;
        Assert.Equal(SKColorType.Gray8, pixels.ColorType);
        var span = pixels.GetPixelSpan();
        Assert.Equal(0x80, span[0]);                               // painted patch
        Assert.Equal(0xFF, span[(5 * pixels.RowBytes) + 5]);       // expansion: revealed
        Assert.Equal(0x00, span[(8 * pixels.RowBytes) + 8]);       // original black mask
        Assert.Equal(0x00, span[(23 * pixels.RowBytes) + 23]);
        Assert.Equal(0xFF, span[(24 * pixels.RowBytes) + 24]);
    }

    [Fact]
    public void ThumbnailFitsNinetySixPixelsAndKeepsAspect()
    {
        var raster = RasterSnapshot.Replacing(Asset(Solid(400, 200, SKColors.Red)), new Rect(0, 0, 400, 200), [], new Rect(0, 0, 400, 200));
        using var thumb = raster.Thumbnail();
        Assert.Equal(96, thumb.Width);
        Assert.Equal(48, thumb.Height);
        Assert.Equal(SKColors.Red, thumb.GetPixel(50, 20));
        Assert.False(raster.HasMaterializedPixels);
    }

    [Fact]
    public void DrawCopiesRatherThanBlends()
    {
        var raster = RasterSnapshot.Replacing(null, new Rect(0, 0, 8, 8), [Patch(0, 0, 8, 8, new SKColor(0, 0, 255, 0x40))], new Rect(0, 0, 8, 8));
        using var target = Solid(8, 8, SKColors.White);
        using var canvas = new SKCanvas(target);
        raster.Draw(new Rect(0, 0, 8, 8), canvas);

        // Premultiplied (0,0,255,0x40) copied, not composited over white.
        var p = target.GetPixel(3, 3);
        Assert.Equal(0x40, p.Alpha);
        Assert.Equal(0, p.Red);
    }
}
