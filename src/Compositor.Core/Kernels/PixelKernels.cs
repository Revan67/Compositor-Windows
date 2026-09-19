using Compositor.Kernels;
using SkiaSharp;

namespace Compositor.Core.Kernels;

/// <summary>Typed wrappers over the C pixel kernels, taking working-format bitmaps.</summary>
public static unsafe class PixelKernels
{
    /// <summary>Copies the alpha channel of <paramref name="bgra"/> into the gray bitmap <paramref name="gray"/> of the same size.</summary>
    public static void ExtractAlpha(SKBitmap bgra, SKBitmap gray)
    {
        RequireSameSize(bgra, gray);
        NativeMethods.layer_extract_alpha((byte*)bgra.GetPixels(), (nuint)bgra.RowBytes, (byte*)gray.GetPixels(), (nuint)gray.RowBytes, (nuint)bgra.Width, (nuint)bgra.Height);
    }

    /// <summary>Divides colour by alpha and sets alpha to 255, so translucent pixels become their opaque colour.</summary>
    public static void UnpremultiplyOpaque(SKBitmap bgra) =>
        NativeMethods.layer_unpremultiply_opaque((byte*)bgra.GetPixels(), (nuint)bgra.RowBytes, (nuint)bgra.Width, (nuint)bgra.Height);

    /// <summary>Re-premultiplies <paramref name="bgra"/> by the coverage in <paramref name="alpha"/>.</summary>
    public static void RestoreAlpha(SKBitmap bgra, SKBitmap alpha)
    {
        RequireSameSize(bgra, alpha);
        NativeMethods.layer_restore_alpha((byte*)bgra.GetPixels(), (nuint)bgra.RowBytes, (byte*)alpha.GetPixels(), (nuint)alpha.RowBytes, (nuint)bgra.Width, (nuint)bgra.Height);
    }

    /// <summary>The tight rectangle of non-zero alpha as (left, top, right, bottom), exclusive; all zero when empty.</summary>
    public static (int Left, int Top, int Right, int Bottom) AlphaBounds(SKBitmap bgra)
    {
        var bounds = stackalloc nuint[4];
        NativeMethods.brush_alpha_bounds((byte*)bgra.GetPixels(), (nuint)bgra.Width, (nuint)bgra.Height, (nuint)bgra.RowBytes, bounds);
        return ((int)bounds[0], (int)bounds[1], (int)bounds[2], (int)bounds[3]);
    }

    private static void RequireSameSize(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException("Bitmaps must be the same size.");
        }
    }
}
