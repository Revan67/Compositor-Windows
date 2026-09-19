using SkiaSharp;

namespace Compositor.Core.Raster;

/// <summary>
/// A layer's (or mask's) pixels. Immutable once created, so history entries and the renderer can
/// share it. Backed either by contiguous pixels (an import, a filter result) or by a
/// <see cref="RasterSnapshot"/> (a paint commit), whose contiguous form is only produced when
/// something actually needs the bytes.
/// </summary>
public sealed class ImportedImage
{
    private readonly SKBitmap? _contiguous;

    public ImportedImage(SKBitmap image, SKBitmap thumbnail, string name)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(thumbnail);
        _contiguous = image;
        Thumbnail = thumbnail;
        Name = name;
        Width = image.Width;
        Height = image.Height;
        IsMask = image.IsMask();
    }

    public ImportedImage(RasterSnapshot raster, SKBitmap thumbnail, string name)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(thumbnail);
        Raster = raster;
        Thumbnail = thumbnail;
        Name = name;
        Width = raster.Width;
        Height = raster.Height;
        IsMask = raster.IsMask;
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsMask { get; }
    public string Name { get; }
    public SKBitmap Thumbnail { get; }

    /// <summary>The sparse form, when this asset came from a paint commit.</summary>
    public RasterSnapshot? Raster { get; }

    /// <summary>
    /// The contiguous pixels. For a snapshot-backed asset this materializes them on first use;
    /// treat the result as read-only.
    /// </summary>
    public SKBitmap Image => _contiguous ?? Raster!.Pixels;

    /// <summary>True when <see cref="Image"/> can be read without allocating a full bitmap.</summary>
    public bool HasContiguousPixels => _contiguous is not null || Raster!.HasMaterializedPixels;
}
