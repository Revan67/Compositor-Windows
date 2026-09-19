using System.Buffers.Binary;
using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>The reference <c>ImageImportTests</c>, <c>ExportTests</c> and <c>JPEGExportTests</c> that concern the codec itself.</summary>
public sealed class ImageCodecTests
{
    /// <summary>64×32: left column opaque red, right column transparent, the rest a gradient.</summary>
    private static SKBitmap Fixture()
    {
        var bitmap = Bitmaps.Create(64, 32, mask: false);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                bitmap.SetPixel(x, y, x == 0 ? SKColors.Red : x == 63 ? SKColors.Empty : new SKColor((byte)(x * 4), (byte)(y * 8), 128));
            }
        }

        return bitmap;
    }

    private static string Temp(string extension) => Path.Combine(Path.GetTempPath(), $"compositor-test-{Guid.NewGuid():N}{extension}");

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void SupportedFormatsDecodeToWorkingFormatWithThumbnail(SKEncodedImageFormat format)
    {
        using var fixture = Fixture();
        using var wrapped = fixture.AsImage();
        using var data = wrapped.Encode(format, 90);
        var path = Temp("." + format.ToString().ToLowerInvariant());
        File.WriteAllBytes(path, data.ToArray());
        try
        {
            var result = ImageCodec.Decode(path);
            Assert.Equal(64, result.Width);
            Assert.Equal(32, result.Height);
            Assert.Equal(SKColorType.Bgra8888, result.Image.ColorType);
            Assert.Equal(SKAlphaType.Premul, result.Image.AlphaType);
            Assert.True(result.Thumbnail.Width <= 96 && result.Thumbnail.Height <= 96);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), result.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PngPreservesTransparency()
    {
        using var fixture = Fixture();
        var result = ImageCodec.Decode(ImageCodec.EncodePng(fixture), "png");
        Assert.Equal(SKColors.Red, result.Image.GetPixel(0, 0));
        Assert.Equal(0, result.Image.GetPixel(63, 0).Alpha);
    }

    [Fact]
    public void LimitsAndInvalidFiles()
    {
        using var fixture = Fixture();
        var png = ImageCodec.EncodePng(fixture);
        Assert.Equal(ImageImportError.TooLarge, Assert.Throws<ImageImportException>(() => ImageCodec.Decode(png, "x", remainingPixels: 10)).Error);
        Assert.Equal(ImageImportError.Unreadable, Assert.Throws<ImageImportException>(() => ImageCodec.Decode("not an image"u8.ToArray(), "x")).Error);
        Assert.Equal(ImageImportError.Unreadable, Assert.Throws<ImageImportException>(() => ImageCodec.Decode(Temp(".png"))).Error);

        using var gifImage = fixture.AsImage();
        using var gif = gifImage.Encode(SKEncodedImageFormat.Gif, 100);
        if (gif is not null)
        {
            Assert.Equal(ImageImportError.Unsupported, Assert.Throws<ImageImportException>(() => ImageCodec.Decode(gif.ToArray(), "x")).Error);
        }

        using var bmpImage = fixture.AsImage();
        using var bmp = bmpImage.Encode(SKEncodedImageFormat.Bmp, 100);
        if (bmp is not null)
        {
            Assert.Equal(ImageImportError.Unsupported, Assert.Throws<ImageImportException>(() => ImageCodec.Decode(bmp.ToArray(), "x")).Error);
        }
    }

    [Fact]
    public void ExifOrientationIsAppliedOnDecode()
    {
        // Orientation 6: stored pixels must be rotated 90° clockwise to display. The stored image is 64×32
        // with red at stored (0,0); upright it is 32×64 with red at the top-right.
        // Solid halves, so JPEG chroma subsampling cannot blur the check: stored left half red, right half blue.
        using var halves = Bitmaps.Create(64, 32, mask: false);
        using (var canvas = new SKCanvas(halves))
        {
            canvas.Clear(SKColors.Blue);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(0, 0, 32, 32), paint);
        }

        using var wrapped = halves.AsImage();
        using var data = wrapped.Encode(SKEncodedImageFormat.Jpeg, 100);
        var oriented = WithExifOrientation(data.ToArray(), 6);

        var result = ImageCodec.Decode(oriented, "rotated");
        Assert.Equal(32, result.Width);
        Assert.Equal(64, result.Height);
        var top = result.Image.GetPixel(16, 8);
        Assert.True(top.Red > 200 && top.Blue < 60, $"top: {top}");
        var bottom = result.Image.GetPixel(16, 56);
        Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"bottom: {bottom}");
    }

    [Fact]
    public void PngExportPreservesDimensionsAlphaOrientationAndTransforms()
    {
        // The reference snapshot: a 6×6 canvas with a 4×4 red layer at (1,1); (4,1) is transparent unless flipped.
        var red = Bitmaps.Create(4, 4, mask: false);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                red.SetPixel(x, y, x < 2 ? SKColors.Red : SKColors.Empty);
            }
        }

        foreach (var (rotation, flip, redX, redY, clearX, clearY) in new[] { (0.0, false, 1, 1, 4, 1), (0.0, true, 4, 1, 1, 1), (90.0, false, 1, 1, 1, 4) })
        {
            var layer = new ImageLayer(new ImportedImage(red, red, "red"), new Point(1, 1)) with
            {
                Transform = new LayerTransform(new Point(1, 1), new Size(4, 4), Rotation: rotation, FlipX: flip, Sampling: LayerSampling.Nearest),
            };
            using var flat = DocumentRenderer.Flatten(new CanvasDocument(6, 6, [layer]));
            var png = ImageCodec.EncodePng(flat, dpi: 300);
            var decoded = ImageCodec.Decode(png, "export");
            Assert.Equal(6, decoded.Width);
            Assert.Equal(6, decoded.Height);
            var hit = decoded.Image.GetPixel(redX, redY);
            Assert.True(hit.Red > 250 && hit.Alpha == 255, $"rotation {rotation} flip {flip}: {hit}");
            Assert.Equal(0, decoded.Image.GetPixel(clearX, clearY).Alpha);
            Assert.Equal(0, decoded.Image.GetPixel(0, 0).Alpha);
            Assert.Equal(300, ImageCodec.ReadDpi(png)!.Value, 0);
        }
    }

    [Fact]
    public void BlankAndOversizedCanvases()
    {
        using var blank = DocumentRenderer.Flatten(new CanvasDocument(2, 2));
        Assert.Equal(0, ImageCodec.Decode(ImageCodec.EncodePng(blank), "blank").Image.GetPixel(1, 1).Alpha);
        Assert.Throws<InvalidOperationException>(() => DocumentRenderer.Flatten(new CanvasDocument(30_000, 30_000)));
    }

    [Fact]
    public void JpegUsesTheChosenMatteAndIsOpaque()
    {
        using var transparent = DocumentRenderer.Flatten(new CanvasDocument(20, 12));
        foreach (var options in new[] { new JpegOptions(), new JpegOptions(Quality: 1, Red: 0, Green: 0, Blue: 1) })
        {
            var jpeg = ImageCodec.EncodeJpeg(transparent, options, dpi: 144);
            Assert.Equal(0xFF, jpeg[0]);
            Assert.Equal(0xD8, jpeg[1]);
            var decoded = ImageCodec.Decode(jpeg, "jpeg");
            Assert.Equal(20, decoded.Width);
            Assert.Equal(12, decoded.Height);
            var pixel = decoded.Image.GetPixel(0, 0);
            Assert.Equal(255, pixel.Alpha);
            Assert.True(Math.Abs((pixel.Red / 255.0) - options.Red) < 0.03);
            Assert.True(Math.Abs((pixel.Green / 255.0) - options.Green) < 0.03);
            Assert.True(Math.Abs((pixel.Blue / 255.0) - options.Blue) < 0.03);
            Assert.Equal(144, ImageCodec.ReadDpi(jpeg));
        }
    }

    [Fact]
    public void JpegQualityChangesBytesAndDecodedPixels()
    {
        using var noisy = Bitmaps.Create(128, 128, mask: false);
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                noisy.SetPixel(x, y, new SKColor((byte)(((x * 37) + (y * 17)) % 256), (byte)(((x * 11) + (y * 53)) % 256), (byte)(((x * 79) + (y * 7)) % 256)));
            }
        }

        var low = ImageCodec.EncodeJpeg(noisy, new JpegOptions(Quality: 0.1));
        var high = ImageCodec.EncodeJpeg(noisy, new JpegOptions(Quality: 1));
        Assert.True(low.Length < high.Length);
        var lowImage = ImageCodec.Decode(low, "low").Image;
        var highImage = ImageCodec.Decode(high, "high").Image;
        var difference = 0.0;
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                difference += Math.Abs(lowImage.GetPixel(x, y).Red - highImage.GetPixel(x, y).Red) / 255.0;
            }
        }

        Assert.True(difference > 1);
    }

    /// <summary>Adds an APP1 Exif segment with just the orientation tag, right after SOI.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        // TIFF header (big-endian) + IFD0 with one entry + next-IFD pointer.
        var tiff = new byte[8 + 2 + 12 + 4];
        tiff[0] = (byte)'M';
        tiff[1] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }
}
