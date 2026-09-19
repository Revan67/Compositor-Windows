using System.Buffers.Binary;
using System.IO.Hashing;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Project;

public enum ImageImportError
{
    Unreadable,
    Unsupported,
    TooLarge,
}

public sealed class ImageImportException(ImageImportError error) : Exception(Describe(error))
{
    public ImageImportError Error { get; } = error;

    private static string Describe(ImageImportError error) => error switch
    {
        ImageImportError.Unreadable => "The image could not be read. It may be damaged or unavailable.",
        ImageImportError.Unsupported => "Choose a JPEG, PNG or WebP image.",
        _ => "This import exceeds the current 100-megapixel document budget or 30,000-pixel side limit.",
    };
}

/// <summary>JPEG export settings: quality and the matte that replaces transparency.</summary>
public readonly record struct JpegOptions(double Quality = 0.85, double Red = 1, double Green = 1, double Blue = 1);

/// <summary>Image files in and out: decoding to the working format, encoding with resolution metadata.</summary>
public static class ImageCodec
{
    public const int MaxSide = 30_000;
    public const long PixelBudget = 100_000_000;
    public const int ThumbnailSide = 96;

    /// <summary>
    /// Decodes a PNG, JPEG or WebP file into premultiplied BGRA, applying its EXIF orientation.
    /// HEIC and TIFF are not decoded by Skia on Windows; they are a later WIC path.
    /// </summary>
    public static ImportedImage Decode(string path, long remainingPixels = PixelBudget)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ImageImportException(ImageImportError.Unreadable);
        }

        return Decode(bytes, Path.GetFileNameWithoutExtension(path), remainingPixels);
    }

    public static ImportedImage Decode(byte[] bytes, string name, long remainingPixels = PixelBudget)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes)) ?? throw new ImageImportException(ImageImportError.Unreadable);
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
        {
            throw new ImageImportException(ImageImportError.Unsupported);
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new ImageImportException(ImageImportError.Unreadable);
        }

        if (info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > remainingPixels)
        {
            throw new ImageImportException(ImageImportError.TooLarge);
        }

        var decoded = new SKBitmap(Bitmaps.Info(info.Width, info.Height, mask: false));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            decoded.Dispose();
            throw new ImageImportException(ImageImportError.Unreadable);
        }

        var image = Oriented(decoded, codec.EncodedOrigin);
        return new ImportedImage(image, Thumbnail(image), name);
    }

    /// <summary>A copy at most <see cref="ThumbnailSide"/> pixels on its long side.</summary>
    public static SKBitmap Thumbnail(SKBitmap image)
    {
        var factor = Math.Min(1, (double)ThumbnailSide / Math.Max(image.Width, image.Height));
        var w = Math.Max(1, (int)Math.Round(image.Width * factor));
        var h = Math.Max(1, (int)Math.Round(image.Height * factor));
        var thumb = Bitmaps.Create(w, h, image.IsMask());
        using var canvas = new SKCanvas(thumb);
        using var wrapped = image.AsImage();
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(wrapped, new SKRect(0, 0, w, h), factor < 1 ? Rendering.LayerRenderer.Downsampling : Bitmaps.High, paint);
        return thumb;
    }

    /// <summary>PNG with alpha and a <c>pHYs</c> chunk carrying <paramref name="dpi"/>.</summary>
    public static byte[] EncodePng(SKBitmap image, double dpi = 72)
    {
        using var wrapped = image.AsImage();
        using var data = wrapped.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("The image could not be encoded.");
        return WithPngResolution(data.ToArray(), dpi);
    }

    /// <summary>Opaque JPEG: transparency composited over the matte, then encoded at the chosen quality with JFIF density set to <paramref name="dpi"/>.</summary>
    public static byte[] EncodeJpeg(SKBitmap image, JpegOptions options, double dpi = 72)
    {
        using var flattened = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(new SKColor(Channel(options.Red), Channel(options.Green), Channel(options.Blue)));
            using var wrapped = image.AsImage();
            canvas.DrawImage(wrapped, 0, 0);
        }

        var quality = (int)Math.Round(Math.Clamp(options.Quality, 0, 1) * 100);
        using var wrappedFlat = flattened.AsImage();
        using var data = wrappedFlat.Encode(SKEncodedImageFormat.Jpeg, quality) ?? throw new InvalidOperationException("The image could not be encoded.");
        return WithJpegResolution(data.ToArray(), dpi);
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static SKBitmap Oriented(SKBitmap decoded, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return decoded;
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? decoded.Height : decoded.Width;
        var h = swap ? decoded.Width : decoded.Height;
        var upright = Bitmaps.Create(w, h, mask: false);
        using var canvas = new SKCanvas(upright);
        canvas.SetMatrix(OrientationMatrix(origin, w, h));
        using var wrapped = decoded.AsImage();
        canvas.DrawImage(wrapped, 0, 0);
        decoded.Dispose();
        return upright;
    }

    /// <summary>Maps decoded (stored) pixels to their upright position, for an upright canvas of <paramref name="w"/> × <paramref name="h"/>.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    // MARK: Resolution metadata. Skia's encoders write none, so the chunks are patched in by hand.

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Inserts (or replaces) a <c>pHYs</c> chunk right after <c>IHDR</c>.</summary>
    internal static byte[] WithPngResolution(byte[] png, double dpi)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            return png;
        }

        var perMeter = (uint)Math.Round(dpi / 0.0254);
        var chunk = new byte[4 + 4 + 9 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0), 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), perMeter);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), perMeter);
        chunk[16] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), Crc32.HashToUInt32(chunk.AsSpan(4, 13)));

        // Walk chunks: copy everything except an existing pHYs, inserting ours after IHDR.
        var output = new MemoryStream(png.Length + chunk.Length);
        output.Write(png, 0, 8);
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var total = 12 + length;
            if (offset + total > png.Length)
            {
                break;
            }

            var type = png.AsSpan(offset + 4, 4);
            if (!type.SequenceEqual("pHYs"u8))
            {
                output.Write(png, offset, total);
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                output.Write(chunk);
            }

            offset += total;
        }

        return output.ToArray();
    }

    /// <summary>Sets the JFIF APP0 density to dots per inch, adding the segment if the encoder left it out.</summary>
    internal static byte[] WithJpegResolution(byte[] jpeg, double dpi)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return jpeg;
        }

        var density = (ushort)Math.Clamp(Math.Round(dpi), 1, ushort.MaxValue);
        // An existing JFIF segment directly after SOI: patch in place.
        if (jpeg.Length >= 20 && jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8))
        {
            var patched = (byte[])jpeg.Clone();
            patched[13] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(patched.AsSpan(14), density);
            BinaryPrimitives.WriteUInt16BigEndian(patched.AsSpan(16), density);
            return patched;
        }

        var app0 = new byte[18];
        app0[0] = 0xFF;
        app0[1] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(2), 16);
        "JFIF\0"u8.CopyTo(app0.AsSpan(4));
        app0[9] = 1;
        app0[10] = 1;
        app0[11] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(12), density);
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(14), density);
        var result = new byte[jpeg.Length + app0.Length];
        result[0] = 0xFF;
        result[1] = 0xD8;
        app0.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + app0.Length));
        return result;
    }

    /// <summary>The resolution a PNG's <c>pHYs</c> or a JPEG's JFIF segment declares, in dots per inch, or null.</summary>
    public static double? ReadDpi(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            var offset = 8;
            while (offset + 12 <= bytes.Length)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
                if (bytes.AsSpan(offset + 4, 4).SequenceEqual("pHYs"u8) && length == 9 && bytes[offset + 16] == 1)
                {
                    return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8)) * 0.0254;
                }

                offset += 12 + length;
            }

            return null;
        }

        if (bytes.Length >= 20 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[3] == 0xE0 && bytes.AsSpan(6, 5).SequenceEqual("JFIF\0"u8))
        {
            var x = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(14));
            return bytes[13] switch
            {
                1 => x,
                2 => x * 2.54,
                _ => null,
            };
        }

        return null;
    }
}
