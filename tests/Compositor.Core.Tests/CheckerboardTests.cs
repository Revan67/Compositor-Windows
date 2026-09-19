using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class CheckerboardTests
{
    [Fact]
    public void CellsAlternateFromTheOrigin()
    {
        using var bitmap = Checkerboard.Create(8, 8, cellSize: 4);

        Assert.Equal(Checkerboard.Light, bitmap.GetPixel(0, 0));
        Assert.Equal(Checkerboard.Light, bitmap.GetPixel(3, 3));
        Assert.Equal(Checkerboard.Dark, bitmap.GetPixel(4, 0));
        Assert.Equal(Checkerboard.Dark, bitmap.GetPixel(0, 4));
        Assert.Equal(Checkerboard.Light, bitmap.GetPixel(4, 4));
        Assert.Equal(Checkerboard.Light, bitmap.GetPixel(7, 7));
    }

    [Fact]
    public void WorkingBitmapsArePremultipliedBgra()
    {
        using var bitmap = Checkerboard.Create(2, 2);
        Assert.Equal(SKColorType.Bgra8888, bitmap.ColorType);
        Assert.Equal(SKAlphaType.Premul, bitmap.AlphaType);
    }

    [Fact]
    public void RejectsOtherColorTypes()
    {
        using var rgba = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        Assert.Throws<ArgumentException>(() => Checkerboard.Fill(rgba));
    }
}
