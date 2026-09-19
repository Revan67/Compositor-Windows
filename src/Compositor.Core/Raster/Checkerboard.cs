using SkiaSharp;

namespace Compositor.Core.Raster;

/// <summary>
/// The transparency checkerboard drawn beneath a document. Cells are anchored to the bitmap
/// origin so the pattern does not swim when the view scrolls.
/// </summary>
public static class Checkerboard
{
    public static readonly SKColor Light = new(0xFF, 0xFF, 0xFF);
    public static readonly SKColor Dark = new(0xCC, 0xCC, 0xCC);

    /// <summary>
    /// Fills <paramref name="bitmap"/> with alternating <paramref name="cellSize"/>-pixel squares.
    /// </summary>
    public static unsafe void Fill(SKBitmap bitmap, int cellSize = 8)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1);
        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            throw new ArgumentException("Working bitmaps are BGRA8888.", nameof(bitmap));
        }

        var light = (uint)Light;
        var dark = (uint)Dark;
        var pixels = (byte*)bitmap.GetPixels();
        var stride = bitmap.RowBytes;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = (uint*)(pixels + (y * stride));
            var rowParity = y / cellSize & 1;
            for (var x = 0; x < bitmap.Width; x++)
            {
                row[x] = ((x / cellSize & 1) ^ rowParity) == 0 ? light : dark;
            }
        }
    }

    /// <summary>Allocates a BGRA8888 premultiplied bitmap and fills it.</summary>
    public static SKBitmap Create(int width, int height, int cellSize = 8)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Fill(bitmap, cellSize);
        return bitmap;
    }
}
