using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Compositor.App.ViewModels;
using Compositor.Core.Document;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;
using CorePoint = Compositor.Core.Geometry.Point;
using CoreRect = Compositor.Core.Geometry.Rect;
using CoreSize = Compositor.Core.Geometry.Size;

namespace Compositor.App.Canvas;

/// <summary>
/// The document view: checkerboard, the composite placed by the viewport, the transform overlay
/// and a pixel grid when zoomed far in. Pans with the middle button, Space-drag, the Hand tool or
/// the wheel; zooms with Ctrl+wheel about the pointer or the Zoom tool. The Move tool drags the
/// active layer (auto-selecting the layer under the pointer), its handles resize and rotate,
/// Alt-drag duplicates, arrows nudge. Draws straight onto Avalonia's Skia canvas through the lease.
/// </summary>
public sealed class EditorCanvas : Control
{
    private static readonly SKBitmap CheckerTile = Checkerboard.Create(32, 32, cellSize: 8);
    private static readonly SKColor Background = new(0x1E, 0x1E, 0x1E);
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(Background.Red, Background.Green, Background.Blue));
    private static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x4C, 0x9E, 0xFF)), 1);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x4C, 0xD6)), 1);
    private static readonly IBrush HandleFill = new SolidColorBrush(Colors.White);
    private static readonly IPen HandleStroke = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xC9)), 1);

    private EditorViewModel? _vm;
    private Point? _panStart;
    private bool _spaceHeld;
    private TransformDrag? _drag;
    private HashSet<Guid> _dragExcludes = [];

    public EditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
    }

    /// <summary>Raised with the messages of files that could not be imported by drop.</summary>
    public event Action<IReadOnlyList<string>>? ImportFailed;

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
        if (e.PropertyName is nameof(EditorViewModel.Composite) or nameof(EditorViewModel.Viewport) or nameof(EditorViewModel.HasDocument)
            or nameof(EditorViewModel.OverlayGeometry) or nameof(EditorViewModel.Tool))
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

    // MARK: Drawing

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);
        if (_vm?.Session.Document is not { } document || _vm.Composite is not { } composite)
        {
            return;
        }

        var rect = _vm.Viewport.DocumentRect(document.Size);
        context.Custom(new DrawOperation(bounds, composite, new SKRect((float)rect.MinX, (float)rect.MinY, (float)rect.MaxX, (float)rect.MaxY), _vm.Viewport.Zoom));
        DrawOverlay(context, document.Size);
    }

    /// <summary>The transform box, its handles, the rotation handle and any snap guides, in view coordinates.</summary>
    private void DrawOverlay(DrawingContext context, CoreSize documentSize)
    {
        if (_vm is null)
        {
            return;
        }

        var guides = _vm.Session.SnapGuides;
        foreach (var x in guides.Xs)
        {
            var vx = _vm.Viewport.ViewPoint(new CorePoint(x, 0), documentSize).X;
            context.DrawLine(GuidePen, new Point(vx, 0), new Point(vx, Bounds.Height));
        }

        foreach (var y in guides.Ys)
        {
            var vy = _vm.Viewport.ViewPoint(new CorePoint(0, y), documentSize).Y;
            context.DrawLine(GuidePen, new Point(0, vy), new Point(Bounds.Width, vy));
        }

        if (_vm.OverlayGeometry is not { } geometry)
        {
            return;
        }

        var corners = geometry.Outline.Select(p => new Point(p.X, p.Y)).ToList();
        for (var i = 0; i < 4; i++)
        {
            context.DrawLine(BoxPen, corners[i], corners[(i + 1) % 4]);
        }

        var top = new Point(geometry.Handles[1].X, geometry.Handles[1].Y);
        var rotation = new Point(geometry.RotationHandle.X, geometry.RotationHandle.Y);
        context.DrawLine(BoxPen, top, rotation);
        context.DrawEllipse(HandleFill, HandleStroke, rotation, 4.5, 4.5);
        foreach (var handle in geometry.Handles)
        {
            context.DrawRectangle(HandleFill, HandleStroke, new Rect(handle.X - 4, handle.Y - 4, 8, 8));
        }
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
            _vm.SetZoom(_vm.Viewport.Zoom * Math.Pow(1.25, e.Delta.Y), e.GetPosition(this).ToCore());
        }
        else
        {
            const double step = 40;
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
        var pansWithLeft = _spaceHeld || _vm?.Tool == EditorTool.Hand;
        if (point.Properties.IsMiddleButtonPressed || (pansWithLeft && point.Properties.IsLeftButtonPressed))
        {
            _panStart = point.Position;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || _vm?.Session.Document is not { } document)
        {
            return;
        }

        var view = point.Position.ToCore();
        if (_vm.Tool == EditorTool.Zoom)
        {
            _vm.SetZoom(_vm.Viewport.Zoom * (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.5 : 2), view);
            e.Handled = true;
            return;
        }

        if (_vm.Tool != EditorTool.Move)
        {
            return;
        }

        var session = _vm.Session;
        var docPoint = _vm.Viewport.DocumentPoint(view, document.Size);
        TransformDragMode? mode = null;
        if (_vm.OverlayGeometry is { } geometry)
        {
            mode = geometry.Hit(view);
            if (mode is null && geometry.Contains(view))
            {
                mode = new TransformDragMode.Move();
            }
        }

        if (mode is null)
        {
            // Auto-select: the topmost layer with pixels under the pointer. Ctrl+click keeps the current selection.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || session.LayerAt(docPoint) is not { } hit)
            {
                return;
            }

            session.SelectLayer(hit.Id);
            mode = new TransformDragMode.Move();
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && mode is TransformDragMode.Move)
        {
            session.BeginDuplicateTransform();
        }
        else
        {
            session.BeginTransform(persistent: false);
        }

        if (session.TransformEdit is not { } edit)
        {
            return;
        }

        _dragExcludes = edit.Group?.Originals.Keys.ToHashSet() ?? [edit.LayerId];
        _drag = new TransformDrag(edit.Draft, docPoint, mode);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is null)
        {
            return;
        }

        if (_panStart is { } start)
        {
            var now = e.GetPosition(this);
            _vm.Viewport = _vm.Viewport.Translated(new CoreSize(now.X - start.X, now.Y - start.Y));
            _panStart = now;
            e.Handled = true;
            return;
        }

        if (_vm.Session.Document is not { } document)
        {
            return;
        }

        var view = e.GetPosition(this).ToCore();
        if (_drag is { } drag)
        {
            var docPoint = _vm.Viewport.DocumentPoint(view, document.Size);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var updated = drag.Updated(docPoint, lockRatio: true, shift: shift, option: alt && drag.Mode is TransformDragMode.Resize);
            var guides = SnapGuides.None;
            if (drag.Mode is TransformDragMode.Move && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var corners = EditorSession.Corners(updated);
                var box = CoreRect.FromEdges(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
                var (xs, ys) = _vm.Session.SnapTargets(_dragExcludes);
                var tolerance = TransformSnap.Distance / _vm.Viewport.PointsPerPixel;
                var (offset, x, y) = TransformSnap.Offset(box, xs, ys, tolerance);
                updated = updated with { Origin = updated.Origin.Offset(offset.Width, offset.Height) };
                guides = new SnapGuides(x is { } gx ? [gx] : [], y is { } gy ? [gy] : []);
            }

            _vm.Session.PreviewTransform(updated.Rounded(), guides);
            e.Handled = true;
            return;
        }

        if (_vm.Tool == EditorTool.Move && _vm.OverlayGeometry is { } geometry)
        {
            Cursor = geometry.Hit(view) switch
            {
                TransformDragMode.Rotate => new Cursor(StandardCursorType.Cross),
                TransformDragMode.Resize r => geometry.ResizeCursorDirection(r.Handle) switch
                {
                    0 => new Cursor(StandardCursorType.SizeWestEast),
                    1 => new Cursor(StandardCursorType.TopLeftCorner),
                    2 => new Cursor(StandardCursorType.SizeNorthSouth),
                    _ => new Cursor(StandardCursorType.TopRightCorner),
                },
                _ => geometry.Contains(view) ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default,
            };
        }
        else if (!_spaceHeld)
        {
            Cursor = _vm.Tool == EditorTool.Hand ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
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
            return;
        }

        if (_drag is not null)
        {
            _drag = null;
            e.Pointer.Capture(null);
            _vm?.Session.CommitTransform();
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
            return;
        }

        if (_vm is null)
        {
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Escape when _drag is not null:
                _drag = null;
                _vm.Session.CancelTransform();
                break;
            case Key.Left when _vm.Tool == EditorTool.Move:
                _vm.Session.NudgeLayer(-step, 0);
                break;
            case Key.Right when _vm.Tool == EditorTool.Move:
                _vm.Session.NudgeLayer(step, 0);
                break;
            case Key.Up when _vm.Tool == EditorTool.Move:
                _vm.Session.NudgeLayer(0, -step);
                break;
            case Key.Down when _vm.Tool == EditorTool.Move:
                _vm.Session.NudgeLayer(0, step);
                break;
            case Key.V when plain:
                _vm.Tool = EditorTool.Move;
                break;
            case Key.H when plain:
                _vm.Tool = EditorTool.Hand;
                break;
            case Key.Z when plain:
                _vm.Tool = EditorTool.Zoom;
                break;
            default:
                return;
        }

        e.Handled = true;
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
