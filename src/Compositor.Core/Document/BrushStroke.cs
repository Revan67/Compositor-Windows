using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Document;

public enum BrushMode
{
    Paint,
    Erase,
}

/// <summary>A live brush gesture. It owns a mutable copy and produces one immutable layer asset on commit.</summary>
public sealed class BrushStroke : IDisposable
{
    private readonly ImageLayer _layer;
    private readonly DocumentSelection? _selection;
    private readonly SKBitmap _pixels;
    private Point? _last;
    private bool _committed;

    public BrushStroke(ImageLayer layer, DocumentSelection? selection, BrushMode mode, double size, double opacity, SKColor color, double hardness = 1)
    {
        if (layer.IsGroup)
        {
            throw new ArgumentException("A group cannot be painted.", nameof(layer));
        }

        _layer = layer;
        _selection = selection;
        Mode = mode;
        Size = Math.Clamp(size, 1, 2_000);
        Opacity = Math.Clamp(opacity, 0, 1);
        Color = color;
        Hardness = Math.Clamp(hardness, 0, 1);
        var width = layer.Asset?.Width ?? Math.Max(1, (int)Math.Round(layer.Transform.Size.Width));
        var height = layer.Asset?.Height ?? Math.Max(1, (int)Math.Round(layer.Transform.Size.Height));
        _pixels = Bitmaps.Create(width, height, mask: false);
        if (layer.Asset is { } asset)
        {
            using var canvas = new SKCanvas(_pixels);
            Bitmaps.Copy(canvas, asset.Image, new SKRect(0, 0, width, height));
        }
    }

    public BrushMode Mode { get; }
    public double Size { get; }
    public double Opacity { get; }
    public SKColor Color { get; }
    public double Hardness { get; }
    public SKBitmap Preview => _pixels;
    public LayerTransform Transform => _layer.Transform;
    public Guid LayerId => _layer.Id;

    public void Add(Point point)
    {
        using var canvas = new SKCanvas(_pixels);
        var documentToPixel = _layer.Transform.PixelToDocument(_pixels.Width, _pixels.Height).Inverted();
        if (_selection is { IsEmpty: false })
        {
            using var localSelection = new SKPath(_selection.Path);
            localSelection.Transform(documentToPixel.ToSK());
            canvas.ClipPath(localSelection, SKClipOperation.Intersect, _selection.Antialiased);
        }

        var localPoint = documentToPixel.Apply(point);
        var localX = documentToPixel.Apply(point.Offset(Size, 0));
        var localY = documentToPixel.Apply(point.Offset(0, Size));
        var width = (Math.Sqrt(Math.Pow(localX.X - localPoint.X, 2) + Math.Pow(localX.Y - localPoint.Y, 2))
            + Math.Sqrt(Math.Pow(localY.X - localPoint.X, 2) + Math.Pow(localY.Y - localPoint.Y, 2))) / 2;

        var alpha = (byte)Math.Round(Opacity * 255);
        // Keep the visible tip bounded to Size: the solid core grows from half to full diameter as
        // hardness rises, while a three-sigma feather occupies the remaining radius.
        var coreWidth = width * (0.5 + (0.5 * Hardness));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = (float)Math.Max(1, coreWidth),
            Color = Mode == BrushMode.Paint ? Color.WithAlpha(alpha) : SKColors.Black.WithAlpha(alpha),
            BlendMode = Mode == BrushMode.Paint ? SKBlendMode.SrcOver : SKBlendMode.DstOut,
        };
        var sigma = width * (1 - Hardness) / 12;
        if (sigma > 0.05)
        {
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)sigma);
        }
        if (_last is { } previous)
        {
            var localPrevious = documentToPixel.Apply(previous);
            canvas.DrawLine((float)localPrevious.X, (float)localPrevious.Y, (float)localPoint.X, (float)localPoint.Y, paint);
        }
        else
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle((float)localPoint.X, (float)localPoint.Y, (float)Math.Max(0.5, coreWidth / 2), paint);
        }

        _last = point;
    }

    public ImportedImage Commit(string name)
    {
        _committed = true;
        var thumbnail = ImageCodec.Thumbnail(_pixels);
        return new ImportedImage(_pixels, thumbnail, name);
    }

    public void Dispose()
    {
        // A committed ImportedImage owns these pixels for document history. Uncommitted strokes can
        // safely release them; callers signal that by disposing before Commit.
        if (!_committed)
        {
            _pixels.Dispose();
        }
    }
}
