using SkiaSharp;

namespace Compositor.Core.Raster;

/// <summary>
/// The two working pixel formats. Images are premultiplied BGRA8888 (Skia's native order);
/// masks are 8-bit gray coverage without alpha, white reveals.
/// </summary>
public static class Bitmaps
{
    public static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    public static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);
    public static readonly SKSamplingOptions High = new(SKCubicResampler.Mitchell);

    public static SKImageInfo Info(int width, int height, bool mask) => mask
        ? new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque)
        : new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>A zeroed bitmap: transparent for images, black (hidden) for masks.</summary>
    public static SKBitmap Create(int width, int height, bool mask)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var bitmap = new SKBitmap(Info(width, height, mask));
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    public static bool IsMask(this SKBitmap bitmap) => bitmap.ColorType == SKColorType.Gray8;

    public static int BytesPerPixel(bool mask) => mask ? 1 : 4;

    /// <summary>
    /// Copies <paramref name="image"/> pixels 1:1 into <paramref name="rect"/> with no filtering and
    /// no blending — the raster equivalent of a tile replacement.
    /// </summary>
    public static void Copy(SKCanvas canvas, SKBitmap image, SKRect rect) =>
        Copy(canvas, image, new SKRect(0, 0, image.Width, image.Height), rect);

    /// <summary>Copies only <paramref name="source"/> of <paramref name="image"/> into <paramref name="rect"/>.</summary>
    public static void Copy(SKCanvas canvas, SKBitmap image, SKRect source, SKRect rect)
    {
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        using var wrapped = image.AsImage();
        canvas.DrawImage(wrapped, source, rect, Nearest, paint);
    }

    /// <summary>
    /// An <see cref="SKImage"/> over <paramref name="bitmap"/>'s own memory, no copy. Valid only while the
    /// bitmap lives and is not resized; dispose it after drawing.
    /// </summary>
    public static SKImage AsImage(this SKBitmap bitmap) => SKImage.FromPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);

    /// <summary>A view of <paramref name="rect"/> sharing pixels with <paramref name="bitmap"/>, or null if it is out of bounds.</summary>
    public static SKBitmap? Crop(this SKBitmap bitmap, SKRectI rect)
    {
        var bounds = new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        if (!bounds.Contains(rect) || rect.IsEmpty)
        {
            return null;
        }

        var subset = new SKBitmap();
        return bitmap.ExtractSubset(subset, rect) ? subset : null;
    }

    public static bool BytesEqual(SKBitmap a, SKBitmap b)
    {
        if (a.Info != b.Info)
        {
            return false;
        }

        return a.GetPixelSpan().SequenceEqual(b.GetPixelSpan());
    }
}
