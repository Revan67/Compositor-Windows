using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Compositor.App;

/// <summary>Conversions between Core's Skia types and Avalonia's, used only for small things like thumbnails.</summary>
public static class Interop
{
    /// <summary>A copy of <paramref name="bitmap"/> as an Avalonia bitmap; masks are shown as gray.</summary>
    public static unsafe Bitmap ToAvalonia(SKBitmap bitmap)
    {
        var writeable = new WriteableBitmap(new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = writeable.Lock();
        var source = (byte*)bitmap.GetPixels();
        var target = (byte*)buffer.Address;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = target + (y * buffer.RowBytes);
            var from = source + (y * bitmap.RowBytes);
            if (bitmap.ColorType == SKColorType.Gray8)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var g = from[x];
                    row[x * 4] = g;
                    row[(x * 4) + 1] = g;
                    row[(x * 4) + 2] = g;
                    row[(x * 4) + 3] = 255;
                }
            }
            else
            {
                Buffer.MemoryCopy(from, row, buffer.RowBytes, bitmap.Width * 4);
            }
        }

        return writeable;
    }

    public static Core.Geometry.Point ToCore(this Point p) => new(p.X, p.Y);

    public static Core.Geometry.Size ToCore(this Size s) => new(s.Width, s.Height);
}
