using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>A gray mask and where it sits on the document, as a folder mask or a layer's own.</summary>
public readonly record struct MaskPlacement(SKBitmap Mask, LayerTransform Transform);

/// <summary>
/// Draws layers into a y-down document coordinate system, shared by the canvas and export.
/// </summary>
/// <remarks>
/// Where the reference intersected the clip with a gray image (<c>clip(to:mask:)</c>), Skia has no
/// coverage clip, so a layer is drawn into a saved layer and each mask multiplied in with
/// <see cref="SKBlendMode.DstIn"/>. Opacity and blend mode go on that saved layer's paint, so they
/// apply exactly once however many pieces the content is drawn in, and the layer still blends with
/// whatever is beneath it on the canvas — folder masks never isolate their contents.
/// </remarks>
public static class LayerRenderer
{
    public static readonly SKSamplingOptions Downsampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Alpha := red (the gray level of a gray source); colour left black.</summary>
    private static readonly SKColorFilter LuminanceToAlpha = SKColorFilter.CreateColorMatrix(
    [
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        1, 0, 0, 0, 0,
    ]);

    /// <summary>
    /// Draws <paramref name="image"/> placed by <paramref name="transform"/> about <paramref name="center"/>,
    /// at <paramref name="scale"/> canvas units per document pixel, through its own <paramref name="mask"/>
    /// (stretched over the layer) and any <paramref name="folderMasks"/> (each at its own placement).
    /// </summary>
    public static void Draw(SKCanvas canvas, SKBitmap image, LayerTransform transform, Point center, double scale = 1, double opacity = 1,
        LayerBlendMode blendMode = LayerBlendMode.Normal, SKBitmap? mask = null, IReadOnlyList<MaskPlacement>? folderMasks = null)
    {
        var bounds = LocalBounds(transform, scale);
        var sampling = Sampling(transform.Sampling, finalFactor: bounds.Width * DeviceScale(canvas) / Math.Max(1, image.Width));
        var placement = Placement(transform, center);
        var isolate = mask is not null || opacity < 1 || blendMode != LayerBlendMode.Normal || folderMasks is { Count: > 0 };

        if (isolate)
        {
            using var layerPaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)),
                BlendMode = blendMode.ToSK(),
            };
            canvas.SaveLayer(placement.MapRect(bounds), layerPaint);
        }

        canvas.Save();
        canvas.Concat(placement);
        using (var paint = new SKPaint { IsAntialias = transform.Sampling != LayerSampling.Nearest })
        using (var wrapped = image.AsImage())
        {
            canvas.DrawImage(wrapped, bounds, sampling, paint);
        }

        if (mask is not null)
        {
            MultiplyByMask(canvas, mask, bounds, transform.Sampling);
        }

        canvas.Restore();

        if (folderMasks is not null)
        {
            foreach (var folder in folderMasks)
            {
                canvas.Save();
                canvas.Concat(Placement(folder.Transform, new Point(folder.Transform.Center.X * scale, folder.Transform.Center.Y * scale)));
                MultiplyByMask(canvas, folder.Mask, LocalBounds(folder.Transform, scale), folder.Transform.Sampling);
                canvas.Restore();
            }
        }

        if (isolate)
        {
            canvas.Restore();
        }
    }

    /// <summary>Multiplies what is already drawn by a gray mask stretched over <paramref name="bounds"/> in the current coordinates.</summary>
    public static void MultiplyByMask(SKCanvas canvas, SKBitmap mask, SKRect bounds, LayerSampling sampling)
    {
        using var wrapped = mask.AsImage();
        using var shader = wrapped.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling.Options(),
            SKMatrix.CreateScaleTranslation(bounds.Width / mask.Width, bounds.Height / mask.Height, bounds.Left, bounds.Top));
        using var paint = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            IsAntialias = sampling != LayerSampling.Nearest,
            Shader = shader,
            ColorFilter = LuminanceToAlpha,
        };
        canvas.DrawRect(bounds, paint);
    }

    /// <summary>Draws a gray mask onto a gray surface as coverage, placed by <paramref name="transform"/>.</summary>
    public static void DrawCoverage(SKCanvas canvas, SKBitmap mask, LayerTransform transform)
    {
        canvas.Save();
        canvas.Concat(Placement(transform, transform.Center));
        using var paint = new SKPaint { IsAntialias = transform.Sampling != LayerSampling.Nearest };
        using var wrapped = mask.AsImage();
        canvas.DrawImage(wrapped, LocalBounds(transform, 1), transform.Sampling.Options(), paint);
        canvas.Restore();
    }

    /// <summary>
    /// The filter for the last resample, <paramref name="finalFactor"/> device pixels per image pixel.
    /// Shrinking uses mipmapped linear sampling: sharp halvings that come out the same for a piece
    /// of an image as for the whole (painted layers draw in pieces). Enlarging keeps the layer's own setting.
    /// </summary>
    public static SKSamplingOptions Sampling(LayerSampling sampling, double finalFactor) => sampling switch
    {
        LayerSampling.Nearest => Bitmaps.Nearest,
        _ when finalFactor <= 1 => Downsampling,
        _ => sampling.Options(),
    };

    /// <summary>Device pixels per unit along the canvas's x axis.</summary>
    public static double DeviceScale(SKCanvas canvas)
    {
        var m = canvas.TotalMatrix;
        return Math.Sqrt((m.ScaleX * m.ScaleX) + (m.SkewY * m.SkewY));
    }

    /// <summary>The layer's rectangle centred on the origin, before rotation and flips.</summary>
    public static SKRect LocalBounds(LayerTransform transform, double scale)
    {
        var w = (float)(transform.Size.Width * scale);
        var h = (float)(transform.Size.Height * scale);
        return new SKRect(-w / 2, -h / 2, w / 2, h / 2);
    }

    /// <summary>Centre, rotation and flips: maps <see cref="LocalBounds"/> onto the canvas.</summary>
    public static SKMatrix Placement(LayerTransform transform, Point center) =>
        SKMatrix.CreateTranslation((float)center.X, (float)center.Y)
            .PreConcat(SKMatrix.CreateRotation((float)transform.Radians))
            .PreConcat(SKMatrix.CreateScale(transform.FlipX ? -1 : 1, transform.FlipY ? -1 : 1));
}
