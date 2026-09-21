using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Document;

public enum LayerBlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    Darken,
    Lighten,
    Difference,
    ColorDodge,
    ColorBurn,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

public static class LayerBlendModeExtensions
{
    public static string Label(this LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.ColorDodge => "Color Dodge",
        LayerBlendMode.ColorBurn => "Color Burn",
        LayerBlendMode.SoftLight => "Soft Light",
        _ => mode.ToString(),
    };

    public static SKBlendMode ToSK(this LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Normal => SKBlendMode.SrcOver,
        LayerBlendMode.Multiply => SKBlendMode.Multiply,
        LayerBlendMode.Screen => SKBlendMode.Screen,
        LayerBlendMode.Overlay => SKBlendMode.Overlay,
        LayerBlendMode.SoftLight => SKBlendMode.SoftLight,
        LayerBlendMode.Darken => SKBlendMode.Darken,
        LayerBlendMode.Lighten => SKBlendMode.Lighten,
        LayerBlendMode.Difference => SKBlendMode.Difference,
        LayerBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        LayerBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        LayerBlendMode.Hue => SKBlendMode.Hue,
        LayerBlendMode.Saturation => SKBlendMode.Saturation,
        LayerBlendMode.Color => SKBlendMode.Color,
        LayerBlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>8-bit coverage over a layer: white reveals, black hides.</summary>
public sealed record LayerMask(ImportedImage Asset)
{
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Where the mask sits on the document once it has been moved apart from its layer; null while
    /// it covers the layer's own pixel grid (and follows every change to it).
    /// </summary>
    public LayerTransform? Placement { get; init; }

    /// <summary>Linked, layer and mask move together; unlinked, each transforms on its own, as in Photoshop.</summary>
    public bool IsLinked { get; init; } = true;

    public SKBitmap? EnabledImage => IsEnabled ? Asset.Image : null;

    /// <summary>The same mask with new pixels (in its own grid), still enabled or not, linked or not, and where it sits.</summary>
    public LayerMask Replacing(ImportedImage asset) => this with { Asset = asset };

    public static bool IsValid(SKBitmap image) => image.ColorType == SKColorType.Gray8;

    /// <summary>A uniform 1×1 mask; avoids allocating full-resolution pixels before painting.</summary>
    public static LayerMask Solid(bool revealing)
    {
        var image = Bitmaps.Create(1, 1, mask: true);
        image.Erase(revealing ? SKColors.White : SKColors.Black);
        return new LayerMask(new ImportedImage(image, image, "Layer Mask"));
    }

    public static ImportedImage AssetFrom(SKBitmap image)
    {
        if (!IsValid(image))
        {
            throw new ArgumentException("A mask is 8-bit gray.", nameof(image));
        }

        return new ImportedImage(image, MaskThumbnail(image), "Layer Mask");
    }

    private static SKBitmap MaskThumbnail(SKBitmap image)
    {
        var factor = Math.Min(1, 96.0 / Math.Max(image.Width, image.Height));
        var w = Math.Max(1, (int)(image.Width * factor));
        var h = Math.Max(1, (int)(image.Height * factor));
        var thumb = Bitmaps.Create(w, h, mask: true);
        using var canvas = new SKCanvas(thumb);
        Bitmaps.Copy(canvas, image, new SKRect(0, 0, w, h));
        return thumb;
    }
}

/// <summary>A selection outline in document pixels, top-left origin.</summary>
public sealed record DocumentSelection(SKPath Path)
{
    public bool Antialiased { get; init; } = true;

    public bool IsEmpty => Path.IsEmpty || Path.Bounds.IsEmpty;

    public Rect Bounds
    {
        get
        {
            var b = Path.Bounds;
            return Rect.FromEdges(b.Left, b.Top, b.Right, b.Bottom);
        }
    }

    /// <summary>A rectangular selection clipped to the document; null when the rectangle has no area.</summary>
    public static DocumentSelection? Rectangle(Rect rect, Rect documentBounds, bool antialiased = false)
    {
        var clipped = rect.Intersection(documentBounds).Integral();
        if (clipped.IsEmpty)
        {
            return null;
        }

        var path = new SKPath();
        path.AddRect(clipped.ToSK());
        return new DocumentSelection(path) { Antialiased = antialiased };
    }

    /// <summary>Grayscale coverage at document resolution (white = selected).</summary>
    public SKBitmap Coverage(int width, int height)
    {
        var bitmap = Bitmaps.Create(width, height, mask: true);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = Antialiased, Style = SKPaintStyle.Fill };
        Path.FillType = SKPathFillType.Winding;
        canvas.DrawPath(Path, paint);
        return bitmap;
    }
}

/// <summary>One row of the layer list: pixels placed by a transform, or a folder of them.</summary>
public sealed record ImageLayer
{
    public ImageLayer(ImportedImage asset, Point origin)
    {
        Id = Guid.NewGuid();
        Asset = asset;
        Transform = new LayerTransform(origin, new Size(asset.Width, asset.Height));
        Name = asset.Name;
    }

    /// <summary>A blank layer: pixels are allocated when painting begins, not when adding it.</summary>
    public ImageLayer(string name, Size blankSize)
    {
        Id = Guid.NewGuid();
        Asset = null;
        Transform = new LayerTransform(Point.Zero, blankSize);
        Name = name;
    }

    public ImageLayer(Guid id, ImportedImage? asset, string name, bool isVisible, LayerTransform transform)
    {
        Id = id;
        Asset = asset;
        Name = name;
        IsVisible = isVisible;
        Transform = transform;
    }

    public Guid Id { get; init; }
    public ImportedImage? Asset { get; init; }
    public LayerTransform Transform { get; init; }
    public string Name { get; init; }
    public bool IsVisible { get; init; } = true;
    public Guid? ParentId { get; init; }
    public bool IsGroup { get; init; }
    public double Opacity { get; init; } = 1;
    public LayerBlendMode BlendMode { get; init; } = LayerBlendMode.Normal;

    /// <summary>A clipping mask: the layer whose live alpha multiplies this one's.</summary>
    public Guid? MaskSourceId { get; init; }
    public LayerMask? Mask { get; init; }

    public Point Origin => Transform.Origin;
    public Size Size => Transform.Size;
}

/// <summary>The document: canvas size, resolution and the layer stack, bottom to top.</summary>
public sealed record CanvasDocument
{
    public const int MaxDimension = 30_000;

    public CanvasDocument(int width, int height, IReadOnlyList<ImageLayer>? layers = null, double resolution = 72, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Width = width;
        Height = height;
        Layers = layers ?? [];
        Resolution = resolution;
    }

    public Guid Id { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Resolution { get; init; }

    /// <summary>Bottom to top.</summary>
    public IReadOnlyList<ImageLayer> Layers { get; init; }

    /// <summary>Part of the document so undo/redo covers selection changes. Not saved to disk.</summary>
    public DocumentSelection? Selection { get; init; }

    public Size Size => new(Width, Height);
    public Rect Bounds => new(0, 0, Width, Height);

    /// <summary>Structural: two documents are equal when every layer is (layers compare their assets by reference).</summary>
    public bool Equals(CanvasDocument? other) =>
        other is not null && Id == other.Id && Width == other.Width && Height == other.Height && Resolution == other.Resolution
        && Layers.SequenceEqual(other.Layers) && Equals(Selection, other.Selection);

    public override int GetHashCode() => HashCode.Combine(Id, Width, Height, Resolution, Layers.Count);

    /// <summary>A typed canvas dimension, or null when it is not a whole number of pixels within the limit.</summary>
    public static int? ValidDimension(string value) =>
        int.TryParse(value.Trim(), out var n) && n is >= 1 and <= MaxDimension ? n : null;
}

public static class LayerMaskPlacement
{
    /// <summary>
    /// Where the mask sits once its layer moves from <paramref name="old"/> to <paramref name="new"/>:
    /// carried along when linked (still covering the layer, or its own placement moved the same way);
    /// left where it was on the document when unlinked.
    /// </summary>
    public static LayerTransform? PlacementMovingLayer(this LayerMask mask, LayerTransform old, LayerTransform @new)
    {
        // A uniform mask looks the same wherever it sits.
        if (mask.Asset.Width <= 1 && mask.Asset.Height <= 1)
        {
            return null;
        }

        var moved = mask.IsLinked ? mask.Placement?.Following(old, @new) : mask.Placement ?? old;
        return moved is { } m && !m.SamePlacement(@new) ? m : null;
    }

    /// <summary>
    /// What a mask shows beyond its pixels once placed apart from its layer: white or black,
    /// whichever most of its edge is (read from the small thumbnail) — so a reveal-all mask keeps
    /// revealing and a hide-all mask hiding.
    /// </summary>
    public static byte Background(SKBitmap thumbnail)
    {
        var width = thumbnail.Width;
        var height = thumbnail.Height;
        if (width <= 0 || height <= 0 || thumbnail.ColorType != SKColorType.Gray8)
        {
            return 255;
        }

        var span = thumbnail.GetPixelSpan();
        var total = 0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (y == 0 || y == height - 1 || x == 0 || x == width - 1)
                {
                    total += span[(y * thumbnail.RowBytes) + x];
                    count++;
                }
            }
        }

        return total * 2 >= count * 255 ? (byte)255 : (byte)0;
    }

    /// <summary>
    /// The mask as a layer's renderers take it: gray stretched over the layer's <paramref name="width"/> ×
    /// <paramref name="height"/> pixel grid. Covering the layer (<see cref="LayerMask.Placement"/> null)
    /// that's the mask itself; placed apart, it is resampled into that grid, at most <paramref name="limit"/>
    /// pixels across. Null while disabled.
    /// </summary>
    public static SKBitmap? ClipImage(this LayerMask mask, LayerTransform layer, int width, int height, double? limit = null)
    {
        var image = mask.EnabledImage;
        if (image is null)
        {
            return null;
        }

        if (mask.Placement is not { } placement || placement.SamePlacement(layer) || width <= 0 || height <= 0)
        {
            return image;
        }

        var factor = limit is { } l ? Math.Min(1, Math.Max(1, l) / Math.Max(width, height)) : 1;
        var w = Math.Max(1, (int)Math.Ceiling(width * factor));
        var h = Math.Max(1, (int)Math.Ceiling(height * factor));
        return Placed(w, h, layer, placement, image, Background(mask.Asset.Thumbnail));
    }

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> gray grid stretched over a layer at
    /// <paramref name="layer"/>, holding <paramref name="mask"/> placed on the document by
    /// <paramref name="placement"/>; <paramref name="background"/> elsewhere.
    /// </summary>
    public static SKBitmap Placed(int width, int height, LayerTransform layer, LayerTransform placement, SKBitmap mask, byte background)
    {
        var bitmap = Bitmaps.Create(width, height, mask: true);
        bitmap.Erase(new SKColor(background, background, background));
        using var canvas = new SKCanvas(bitmap);
        canvas.Concat(placement.PixelToDocument(mask.Width, mask.Height).Concatenating(layer.PixelToDocument(width, height).Inverted()).ToSK());
        using var paint = new SKPaint { IsAntialias = true };
        using var wrapped = mask.AsImage();
        canvas.DrawImage(wrapped, new SKRect(0, 0, mask.Width, mask.Height), Bitmaps.High, paint);
        return bitmap;
    }
}
