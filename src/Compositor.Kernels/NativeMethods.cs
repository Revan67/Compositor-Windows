using System.Runtime.InteropServices;

namespace Compositor.Kernels;

/// <summary>
/// P/Invoke surface of compositor_kernels.dll — the reference C pixel kernels, unchanged.
/// </summary>
/// <remarks>
/// The reference feeds these CG's RGBA; the port's working format is Skia's premultiplied BGRA.
/// Most kernels treat channels 0–2 symmetrically and 3 as alpha, so the order does not matter
/// (verified: brush bounds, levels, wand, content fill, heal, lens, noise, clamp,
/// extract/restore alpha). Two are <b>order-dependent</b> because they weight channel 0 as red
/// for Rec.709 luma: <see cref="adjust_gradient_map"/> (which also reads its table as RGB
/// triplets) and <see cref="adjust_grain"/>. Their Core wrappers must swap R and B around the
/// call, or the luma is computed with red and blue exchanged. Per-channel tables for
/// <see cref="levels_apply"/> are supplied in the buffer's channel order.
/// <para>
/// C <c>long</c> is 32-bit on Windows, so <see cref="wand_mask"/> returns <see cref="int"/> and
/// <see cref="heal_coverage_bounds"/> fills an <see cref="int"/> array; <c>size_t</c> maps to
/// <see cref="nuint"/>.
/// </para>
/// </remarks>
#pragma warning disable IDE1006 // Names match the C symbols on purpose.
public static unsafe partial class NativeMethods
{
    private const string Library = "compositor_kernels";

    // AdjustPixels.h
    [LibraryImport(Library)] public static partial void adjust_gradient_map(byte* rgba, nuint width, nuint height, nuint stride, byte* table);
    [LibraryImport(Library)] public static partial void adjust_grain(byte* rgba, nuint width, nuint height, nuint stride, double amount, double size, double roughness, uint seed, double originX, double originY, double unitsPerPixel);
    [LibraryImport(Library)] public static partial void rgba_clamp_premultiplied(byte* rgba, nuint count);

    // BrushPixels.h
    [LibraryImport(Library)] public static partial void brush_alpha_bounds(byte* bytes, nuint width, nuint height, nuint stride, nuint* bounds);
    [LibraryImport(Library)] public static partial void layer_extract_alpha(byte* rgba, nuint rgbaStride, byte* gray, nuint grayStride, nuint width, nuint height);
    [LibraryImport(Library)] public static partial void layer_unpremultiply_opaque(byte* rgba, nuint stride, nuint width, nuint height);
    [LibraryImport(Library)] public static partial void layer_restore_alpha(byte* rgba, nuint stride, byte* alpha, nuint alphaStride, nuint width, nuint height);

    // ContentFill.h
    [LibraryImport(Library)] public static partial int content_fill(byte* rgba, nuint stride, byte* mask, nuint maskStride, int width, int height);

    // HealPixels.h
    [LibraryImport(Library)] public static partial void heal_coverage_bounds(byte* gray, nuint width, nuint height, nuint stride, int* bounds);
    [LibraryImport(Library)] public static partial int spot_heal(byte* rgba, byte* coverage, nuint width, nuint height, nuint stride, float opacity, int mode, uint seed);

    // LensPixels.h
    [LibraryImport(Library)] public static partial void lens_distort(byte* source, byte* destination, nuint width, nuint height, nuint stride, double k);

    // LevelsPixels.h
    [LibraryImport(Library)] public static partial void levels_apply(byte* pixels, nuint count, float* tables);
    [LibraryImport(Library)] public static partial void levels_histogram(byte* pixels, byte* coverage, nuint count, double* bins);

    // NoisePixels.h
    [LibraryImport(Library)] public static partial void noise_add(byte* rgba, nuint width, nuint height, nuint stride, float amount, int gaussian, int monochromatic, uint seed);

    // WandPixels.h
    [LibraryImport(Library)] public static partial int wand_mask(byte* rgba, nuint width, nuint height, nuint stride, nuint seedX, nuint seedY, nuint radius, int tolerance, int contiguous, byte* mask);
    [LibraryImport(Library)] public static partial int wand_trace(byte* mask, nuint width, nuint height, int** points, nuint* pointCount, int** loops, nuint* loopCount);

    // KernelsExports.c — releases buffers that wand_trace allocated with the DLL's own CRT.
    [LibraryImport(Library)] public static partial void kernels_free(void* pointer);
}
#pragma warning restore IDE1006
