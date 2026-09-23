using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Project;

public enum CanvasUnit
{
    Pixels,
    Percent,
    Inches,
    Centimeters,
}

/// <summary>The Canvas Size sheet's numbers: typed in any unit, absolute or relative, optionally ratio-locked.</summary>
public sealed record CanvasSizeDraft(int OriginalWidth, int OriginalHeight, double Resolution)
{
    public double Width { get; init; }
    public double Height { get; init; }
    public bool Relative { get; init; }
    public bool Locked { get; init; }
    public CanvasUnit Unit { get; init; } = CanvasUnit.Pixels;

    public static CanvasSizeDraft For(CanvasDocument document) => new(document.Width, document.Height, document.Resolution) { Width = document.Width, Height = document.Height };

    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height)
        && Math.Round(Width) is >= 1 and <= CanvasDocument.MaxDimension && Math.Round(Height) is >= 1 and <= CanvasDocument.MaxDimension;

    public int PixelWidth => (int)Math.Round(Width);
    public int PixelHeight => (int)Math.Round(Height);

    public double Displayed(bool widthAxis)
    {
        var original = (double)(widthAxis ? OriginalWidth : OriginalHeight);
        var pixels = (widthAxis ? Width : Height) - (Relative ? original : 0);
        return Unit switch
        {
            CanvasUnit.Pixels => pixels,
            CanvasUnit.Percent => pixels / original * 100,
            CanvasUnit.Inches => pixels / Resolution,
            _ => pixels / Resolution * 2.54,
        };
    }

    public CanvasSizeDraft With(double value, bool widthAxis)
    {
        var original = (double)(widthAxis ? OriginalWidth : OriginalHeight);
        var pixels = Unit switch
        {
            CanvasUnit.Pixels => value,
            CanvasUnit.Percent => value / 100 * original,
            CanvasUnit.Inches => value * Resolution,
            _ => value / 2.54 * Resolution,
        };
        var final = pixels + (Relative ? original : 0);
        if (widthAxis)
        {
            return this with { Width = final, Height = Locked ? final * OriginalHeight / OriginalWidth : Height };
        }

        return this with { Height = final, Width = Locked ? final * OriginalWidth / OriginalHeight : Width };
    }
}

public readonly record struct CanvasExtensionColor(double Red, double Green, double Blue);

/// <summary>A new canvas size and where the old content sits in it.</summary>
public sealed record CanvasSizeOptions(int Width, int Height)
{
    /// <summary>Row-major, top-left (0) through bottom-right (8); centre is 4.</summary>
    public int Anchor { get; init; } = 4;

    /// <summary>Fills the newly exposed area, as a new bottom layer, when growing.</summary>
    public CanvasExtensionColor? Fill { get; init; }

    /// <summary>Crop supplies an explicit document-space translation instead of an anchor.</summary>
    public Point? ContentOffset { get; init; }

    public Point Offset(int fromWidth, int fromHeight)
    {
        if (ContentOffset is { } explicitOffset)
        {
            return explicitOffset;
        }

        // Floor puts the extra pixel on the right/bottom when expanding, and removes it from the left/top when shrinking around the centre.
        return new Point(Math.Floor((Width - fromWidth) * (Anchor % 3) / 2.0), Math.Floor((Height - fromHeight) * (Anchor / 3) / 2.0));
    }
}

public static class CanvasResizer
{
    /// <summary>The document on a canvas of the new size: every layer and placed mask shifted by the offset, plus an optional fill layer.</summary>
    public static CanvasDocument Resize(CanvasDocument document, CanvasSizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (options.Width is < 1 or > CanvasDocument.MaxDimension || options.Height is < 1 or > CanvasDocument.MaxDimension || options.Anchor is < 0 or > 8)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        var offset = options.Offset(document.Width, document.Height);
        if (!offset.IsFinite || Math.Abs(offset.X) > 1_000_000 || Math.Abs(offset.Y) > 1_000_000)
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        if (options.Width == document.Width && options.Height == document.Height && offset == Point.Zero)
        {
            return document;
        }

        var layers = document.Layers.Select(layer =>
        {
            var transform = layer.Transform with { Origin = layer.Transform.Origin.Offset(offset.X, offset.Y) };
            if (!transform.IsValid)
            {
                throw new ProjectException(ProjectError.TooLarge);
            }

            return layer with
            {
                Transform = transform,
                Mask = layer.Mask is { Placement: { } placement } mask ? mask with { Placement = placement with { Origin = placement.Origin.Offset(offset.X, offset.Y) } } : layer.Mask,
            };
        }).ToList();

        // A coloured extension is separate bottom-layer content. The old canvas intersection remains
        // transparent, including holes in the existing artwork.
        if (options.Fill is { } color && (options.Width > document.Width || options.Height > document.Height))
        {
            var used = document.Layers.Sum(l => (long)(l.Asset?.Width ?? 0) * (l.Asset?.Height ?? 0));
            if ((long)options.Width * options.Height > ImageCodec.PixelBudget - used || layers.Count >= ProjectStore.MaxLayers)
            {
                throw new ProjectException(ProjectError.TooLarge);
            }

            foreach (var channel in new[] { color.Red, color.Green, color.Blue })
            {
                if (!double.IsFinite(channel) || channel is < 0 or > 1)
                {
                    throw new ProjectException(ProjectError.Invalid);
                }
            }

            var bitmap = Bitmaps.Create(options.Width, options.Height, mask: false);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255)));
                using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
                canvas.DrawRect(new SKRect((float)offset.X, (float)offset.Y, (float)(offset.X + document.Width), (float)(offset.Y + document.Height)), clear);
            }

            var extension = new ImageLayer(new ImportedImage(bitmap, ImageCodec.Thumbnail(bitmap), "Canvas Extension"), Point.Zero) with { Name = "Canvas Extension" };
            layers.Insert(0, extension);
        }

        return document with { Width = options.Width, Height = options.Height, Layers = layers };
    }
}

public sealed record ImageSizeOptions(int Width, int Height, double Resolution)
{
    public LayerSampling Sampling { get; init; } = LayerSampling.High;
}

public static class ImageResizer
{
    /// <summary>
    /// The document resampled to a new pixel size. Each transformed layer is rasterized on its own:
    /// nonuniform scaling of a rotated rectangle can introduce shear, which width/height/angle
    /// cannot represent, so the result is an upright layer holding the resampled pixels.
    /// </summary>
    public static CanvasDocument Resize(CanvasDocument document, ImageSizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (options.Width is < 1 or > CanvasDocument.MaxDimension || options.Height is < 1 or > CanvasDocument.MaxDimension
            || !double.IsFinite(options.Resolution) || options.Resolution is < 1 or > 9600)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        if (document.Width == options.Width && document.Height == options.Height)
        {
            return document with { Resolution = options.Resolution };
        }

        if ((long)options.Width * options.Height > ImageCodec.PixelBudget)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        var sx = (double)options.Width / document.Width;
        var sy = (double)options.Height / document.Height;
        long usedPixels = 0;
        long usedMaskPixels = 0;
        var layers = new List<ImageLayer>(document.Layers.Count);
        foreach (var layer in document.Layers)
        {
            var corners = EditorSession.Corners(layer.Transform).Select(c => new Point(c.X * sx, c.Y * sy)).ToList();
            var left = Math.Floor(corners.Min(c => c.X));
            var top = Math.Floor(corners.Min(c => c.Y));
            var width = (int)(Math.Ceiling(corners.Max(c => c.X)) - left);
            var height = (int)(Math.Ceiling(corners.Max(c => c.Y)) - top);
            var transform = new LayerTransform(new Point(left, top), new Size(width, height), Sampling: options.Sampling);
            if (!transform.IsValid)
            {
                throw new ProjectException(ProjectError.TooLarge);
            }

            var sourceTransform = layer.Transform with { Sampling = options.Sampling };
            ImportedImage? asset = null;
            if (layer.Asset is { } source)
            {
                CheckBudget(width, height, ref usedPixels);
                var bitmap = Bitmaps.Create(width, height, mask: false);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Translate((float)-left, (float)-top);
                    canvas.Scale((float)sx, (float)sy);
                    LayerRenderer.Draw(canvas, source.Image, sourceTransform, sourceTransform.Center);
                }

                asset = new ImportedImage(bitmap, ImageCodec.Thumbnail(bitmap), source.Name);
            }

            LayerMask? mask = layer.Mask;
            if (layer.Mask is { } existing)
            {
                var image = existing.Asset.Image;
                if (image.Width == 1 && image.Height == 1)
                {
                    // Uniform masks are resolution independent; avoid allocating a full canvas for reveal/hide-all.
                    mask = existing;
                }
                else if (existing.Placement is { } placement)
                {
                    // A mask on its own placement keeps its pixels; the placement scales with the canvas.
                    mask = existing with { Placement = placement.Placing(placement.UnitToDocument.Concatenating(AffineTransform.Scale(sx, sy))) };
                }
                else
                {
                    CheckBudget(width, height, ref usedMaskPixels);
                    var gray = Bitmaps.Create(width, height, mask: true);
                    using (var canvas = new SKCanvas(gray))
                    {
                        canvas.Translate((float)-left, (float)-top);
                        canvas.Scale((float)sx, (float)sy);
                        LayerRenderer.DrawCoverage(canvas, image, sourceTransform);
                    }

                    mask = existing.Replacing(LayerMask.AssetFrom(gray));
                }
            }

            layers.Add(layer with { Transform = transform, Asset = asset, Mask = mask });
        }

        return document with { Width = options.Width, Height = options.Height, Resolution = options.Resolution, Layers = layers };
    }

    private static void CheckBudget(int width, int height, ref long used)
    {
        if (width is < 1 or > CanvasDocument.MaxDimension || height is < 1 or > CanvasDocument.MaxDimension || (long)width * height > ImageCodec.PixelBudget - used)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        used += (long)width * height;
    }
}
