using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Compositor.App.Canvas;

/// <summary>
/// Draws an <see cref="SKBitmap"/> straight onto Avalonia's Skia canvas, centred and 1:1. This is
/// the seed of the editor canvas: no intermediate <c>Bitmap</c>, no per-frame copy.
/// </summary>
public sealed class SkiaBitmapView : Control
{
    public static readonly StyledProperty<SKBitmap?> BitmapProperty =
        AvaloniaProperty.Register<SkiaBitmapView, SKBitmap?>(nameof(Bitmap));

    static SkiaBitmapView()
    {
        AffectsRender<SkiaBitmapView>(BitmapProperty);
    }

    public SKBitmap? Bitmap
    {
        get => GetValue(BitmapProperty);
        set => SetValue(BitmapProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Bitmap is { } bitmap)
        {
            context.Custom(new DrawOperation(new Rect(Bounds.Size), bitmap));
        }
    }

    private sealed class DrawOperation(Rect bounds, SKBitmap bitmap) : ICustomDrawOperation
    {
        private readonly Rect bounds = bounds;
        private readonly SKBitmap bitmap = bitmap;

        public Rect Bounds => bounds;

        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) =>
            other is DrawOperation op && ReferenceEquals(op.bitmap, bitmap) && op.bounds == bounds;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            if (lease is null)
            {
                // Not the Skia backend; nothing to draw with. Phase 0 requires Skia, so make it visible.
                throw new InvalidOperationException("Avalonia is not rendering with Skia.");
            }

            using (lease)
            {
                var canvas = lease.SkCanvas;
                var x = (float)Math.Round((bounds.Width - bitmap.Width) / 2);
                var y = (float)Math.Round((bounds.Height - bitmap.Height) / 2);
                canvas.DrawBitmap(bitmap, x, y);
            }
        }

        public void Dispose()
        {
        }
    }
}
