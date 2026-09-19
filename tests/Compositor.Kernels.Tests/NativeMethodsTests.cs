namespace Compositor.Kernels.Tests;

/// <summary>
/// Each test asserts a value the C computes, not merely that the call returned, so a missing
/// or mismatched DLL fails loudly here rather than inside a tool later.
/// </summary>
public sealed unsafe class NativeMethodsTests
{
    [Fact]
    public void LevelsApplyMapsThroughTables()
    {
        // Identity table for channel 0, inverted for channel 1, constant 0.5 for channel 2.
        var tables = new float[3 * 256];
        for (var i = 0; i < 256; i++)
        {
            tables[i] = i / 255f;
            tables[256 + i] = 1 - i / 255f;
            tables[512 + i] = 0.5f;
        }

        // Two opaque pixels and one fully transparent, which the kernel must leave alone.
        byte[] pixels = [10, 200, 30, 255, 0, 0, 0, 255, 7, 7, 7, 0];
        fixed (byte* p = pixels)
        fixed (float* t = tables)
        {
            NativeMethods.levels_apply(p, 3, t);
        }

        Assert.Equal([10, 55, 128, 255, 0, 255, 128, 255, 7, 7, 7, 0], pixels);
    }

    [Fact]
    public void BrushAlphaBoundsFindsOpaqueRectangle()
    {
        const int width = 8, height = 6, stride = width * 4;
        var pixels = new byte[stride * height];
        // Alpha at (2,1), (5,1) and (3,4) → bounds x 2..6, y 1..5 (exclusive right/bottom).
        pixels[(1 * stride) + (2 * 4) + 3] = 255;
        pixels[(1 * stride) + (5 * 4) + 3] = 1;
        pixels[(4 * stride) + (3 * 4) + 3] = 9;

        var bounds = new nuint[4];
        fixed (byte* p = pixels)
        fixed (nuint* b = bounds)
        {
            NativeMethods.brush_alpha_bounds(p, width, height, stride, b);
        }

        Assert.Equal([2u, 1u, 6u, 5u], bounds.Select(v => (uint)v));
    }

    [Fact]
    public void BrushAlphaBoundsOfEmptyBufferIsZero()
    {
        var pixels = new byte[4 * 4 * 4];
        var bounds = new nuint[] { 9, 9, 9, 9 };
        fixed (byte* p = pixels)
        fixed (nuint* b = bounds)
        {
            NativeMethods.brush_alpha_bounds(p, 4, 4, 16, b);
        }

        Assert.All(bounds, v => Assert.Equal(0u, (uint)v));
    }

    [Fact]
    public void WandMaskFloodsContiguousColourAndTraceBuffersAreFreed()
    {
        // 4×4: left half one colour, right half another. Seed top-left with zero tolerance.
        const int width = 4, height = 4, stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                pixels[i + (x < 2 ? 0 : 2)] = 255;
                pixels[i + 3] = 255;
            }
        }

        var mask = new byte[width * height];
        int selected;
        fixed (byte* p = pixels)
        fixed (byte* m = mask)
        {
            selected = NativeMethods.wand_mask(p, width, height, stride, 0, 0, 0, 0, 1, m);
        }

        Assert.Equal(8, selected);
        Assert.Equal(8, mask.Count(v => v != 0));
        for (var y = 0; y < height; y++)
        {
            Assert.NotEqual(0, mask[y * width]);
            Assert.NotEqual(0, mask[(y * width) + 1]);
            Assert.Equal(0, mask[(y * width) + 2]);
            Assert.Equal(0, mask[(y * width) + 3]);
        }

        int* points = null;
        int* loops = null;
        nuint pointCount = 0, loopCount = 0;
        int status;
        fixed (byte* m = mask)
        {
            status = NativeMethods.wand_trace(m, width, height, &points, &pointCount, &loops, &loopCount);
        }

        Assert.Equal(0, status);
        Assert.True(pointCount >= 4, $"expected at least the 4 corners of a rectangle, got {pointCount}");
        Assert.Equal(1u, (uint)loopCount);
        NativeMethods.kernels_free(points);
        NativeMethods.kernels_free(loops);
    }
}
