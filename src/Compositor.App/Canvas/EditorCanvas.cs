using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Compositor.App.ViewModels;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;
using CoreSize = Compositor.Core.Geometry.Size;

namespace Compositor.App.Canvas;

/// <summary>
/// The document view: checkerboard, the composite placed by the viewport, a pixel grid when zoomed
/// far in. Pans with the middle button, Space-drag or the wheel; zooms with Ctrl+wheel about the
/// pointer. Draws straight onto Avalonia's Skia canvas through the render lease.
/// </summary>
public sealed class EditorCanvas : Control
{
    private static readonly SKBitmap CheckerTile = Checkerboard.Create(32, 32, cellSize: 8);
    private static readonly SKColor Background = new(0x1E, 0x1E, 0x1E);

    private EditorViewModel? _vm;
    private Point? _panStart;
    private bool _spaceHeld;

    public EditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as EditorViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            SyncViewport();
        }

        InvalidateVisual();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.Composite) or nameof(EditorViewModel.Viewport) or nameof(EditorViewModel.HasDocument))
        {
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SyncViewport();
    }

    private double RenderScaling => (VisualRoot as TopLevel)?.RenderScaling ?? 1;

    private void SyncViewport()
    {
        if (_vm is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        _vm.Viewport = _vm.Viewport.Resized(Bounds.Size.ToCore(), RenderScaling, _vm.Session.Document?.Size);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(Background.Red, Background.Green, Background.Blue)), bounds);
        if (_vm?.Session.Document is not { } document || _vm.Composite is not { } composite)
        {
            return;
        }

        var rect = _vm.Viewport.DocumentRect(document.Size);
        context.Custom(new DrawOperation(bounds, composite, new SKRect((float)rect.MinX, (float)rect.MinY, (float)rect.MaxX, (float)rect.MaxY), _vm.Viewport.Zoom));
    }

    // MARK: Input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm?.Session.Document is null)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var factor = Math.Pow(1.25, e.Delta.Y);
            _vm.SetZoom(_vm.Viewport.Zoom * factor, e.GetPosition(this).ToCore());
        }
        else
        {
            var step = 40.0;
            var dx = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : e.Delta.X;
            var dy = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0 : e.Delta.Y;
            _vm.Viewport = _vm.Viewport.Translated(new CoreSize(dx * step, dy * step));
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed || (_spaceHeld && point.Properties.IsLeftButtonPressed))
        {
            _panStart = point.Position;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panStart is { } start && _vm is not null)
        {
            var now = e.GetPosition(this);
            _vm.Viewport = _vm.Viewport.Translated(new CoreSize(now.X - start.X, now.Y - start.Y));
            _panStart = now;
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panStart is not null)
        {
            _panStart = null;
            e.Pointer.Capture(null);
            Cursor = _spaceHeld ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space && !_spaceHeld)
        {
            _spaceHeld = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
            if (_panStart is null)
            {
                Cursor = Cursor.Default;
            }

            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm?.Session.Document is not { } document)
        {
            return;
        }

        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        if (files is not { Count: > 0 })
        {
            return;
        }

        var at = _vm.Viewport.DocumentPoint(e.GetPosition(this).ToCore(), document.Size);
        var failures = _vm.Import(files, at);
        if (failures.Count > 0)
        {
            ImportFailed?.Invoke(failures);
        }

        e.Handled = true;
    }

    /// <summary>Raised with the messages of files that could not be imported by drop.</summary>
    public event Action<IReadOnlyList<string>>? ImportFailed;

    private sealed class DrawOperation(Rect bounds, SKBitmap composite, SKRect target, double zoom) : ICustomDrawOperation
    {
        private readonly Rect _bounds = bounds;
        private readonly SKBitmap _composite = composite;
        private readonly SKRect _target = target;
        private readonly double _zoom = zoom;

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) =>
            other is DrawOperation op && ReferenceEquals(op._composite, _composite) && op._target == _target && op._bounds == _bounds && op._zoom == _zoom;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            if (lease is null)
            {
                return;
            }

            using (lease)
            {
                var canvas = lease.SkCanvas;
                canvas.Save();
                canvas.ClipRect(_target);

                // Checkerboard anchored to the view so it does not swim under the document while panning.
                using (var tile = CheckerTile.AsImage())
                using (var shader = tile.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat))
                using (var paint = new SKPaint { Shader = shader })
                {
                    canvas.DrawRect(_target, paint);
                }

                var enlarging = _target.Width >= _composite.Width;
                using (var image = _composite.AsImage())
                using (var paint = new SKPaint { IsAntialias = false })
                {
                    canvas.DrawImage(image, _target, enlarging ? Bitmaps.Nearest : LayerRenderer.Downsampling, paint);
                }

                // A pixel grid once each document pixel is 8 or more device pixels wide.
                if (_zoom >= 8)
                {
                    var step = _target.Width / _composite.Width;
                    using var grid = new SKPaint { Color = new SKColor(0, 0, 0, 0x50), StrokeWidth = 1, IsAntialias = false };
                    var clip = canvas.LocalClipBounds;
                    for (var x = _target.Left + step; x < _target.Right; x += step)
                    {
                        if (x >= clip.Left && x <= clip.Right)
                        {
                            canvas.DrawLine(x, _target.Top, x, _target.Bottom, grid);
                        }
                    }

                    for (var y = _target.Top + step; y < _target.Bottom; y += step)
                    {
                        if (y >= clip.Top && y <= clip.Bottom)
                        {
                            canvas.DrawLine(_target.Left, y, _target.Right, y, grid);
                        }
                    }
                }

                canvas.Restore();
            }
        }

        public void Dispose()
        {
        }
    }
}
