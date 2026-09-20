using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>The reference <c>CanvasSizeTests</c>, <c>ImageSizeTests</c> and <c>CropTests</c>.</summary>
public sealed class ResizeAndCropTests
{
    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bitmap = Bitmaps.Create(w, h, false);
        bitmap.Erase(color);
        return bitmap;
    }

    private static ImportedImage Asset(SKBitmap image, string name = "image") => new(image, ImageCodec.Thumbnail(image), name);

    [Fact]
    public void EveryAnchorPreservesSourceAndTransformForExpansionAndShrink()
    {
        var layer = new ImageLayer(Asset(Solid(64, 32, SKColors.Red)), Point.Zero) with
        {
            Transform = new LayerTransform(new Point(3, 7), new Size(64, 32), Rotation: 37, FlipX: true),
        };
        var source = new CanvasDocument(64, 32, [layer]);
        foreach (var delta in new[] { 5, -5 })
        {
            for (var anchor = 0; anchor <= 8; anchor++)
            {
                var result = CanvasResizer.Resize(source, new CanvasSizeOptions(64 + delta, 32 + delta) { Anchor = anchor });
                var output = result.Layers[0];
                int[] expected = [0, delta == 5 ? 2 : -3, delta];
                Assert.Equal(layer.Transform.Origin.X + expected[anchor % 3], output.Transform.Origin.X);
                Assert.Equal(layer.Transform.Origin.Y + expected[anchor / 3], output.Transform.Origin.Y);
                Assert.Equal(layer.Transform.Size, output.Transform.Size);
                Assert.True(output.Transform.Rotation == 37 && output.Transform.FlipX);
                Assert.Equal(layer.Id, output.Id);
                Assert.Same(layer.Asset, output.Asset);
                Assert.Equal((64 + delta, 32 + delta), (result.Width, result.Height));
            }
        }
    }

    [Fact]
    public void RelativeRatioAndUnitsUseFinalDimensions()
    {
        var draft = new CanvasSizeDraft(1000, 500, 100) { Width = 1000, Height = 500, Relative = true, Locked = true };
        draft = draft.With(200, widthAxis: true);
        Assert.True(draft.Width == 1200 && draft.Height == 600);
        Assert.Equal(100, draft.Displayed(widthAxis: false));
        draft = draft.With(-250, widthAxis: false);
        Assert.True(draft.Width == 500 && draft.Height == 250);
        draft = draft with { Relative = false, Unit = CanvasUnit.Inches };
        draft = draft.With(10, widthAxis: true);
        Assert.True(draft.Width == 1000 && draft.Height == 500);
        draft = draft with { Unit = CanvasUnit.Percent };
        draft = draft.With(50, widthAxis: true);
        Assert.True(draft.Width == 500 && draft.Height == 250);
        draft = draft with { Unit = CanvasUnit.Centimeters };
        Assert.Equal(12.7, draft.Displayed(widthAxis: true), 3);
        draft = (draft with { Unit = CanvasUnit.Pixels }).With(0, widthAxis: true);
        Assert.False(draft.IsValid);
    }

    [Fact]
    public void ColoredExtensionPreservesOldTransparencyAndShrinkDoesNotAddFill()
    {
        var holed = Solid(4, 4, SKColors.Red);
        holed.SetPixel(1, 1, SKColors.Empty);
        var document = new CanvasDocument(4, 4, [new ImageLayer(Asset(holed), Point.Zero)]);
        var grown = CanvasResizer.Resize(document, new CanvasSizeOptions(8, 8) { Anchor = 0, Fill = new CanvasExtensionColor(0, 0, 1) });
        Assert.Equal(2, grown.Layers.Count);
        Assert.Equal("Canvas Extension", grown.Layers[0].Name);
        using var flat = DocumentRenderer.Flatten(grown);
        Assert.Equal(SKColors.Red, flat.GetPixel(0, 0));
        Assert.Equal(SKColors.Empty, flat.GetPixel(1, 1));
        Assert.Equal(SKColors.Blue, flat.GetPixel(6, 6));

        var shrunk = CanvasResizer.Resize(document, new CanvasSizeOptions(2, 2) { Fill = new CanvasExtensionColor(0, 0, 1) });
        Assert.Single(shrunk.Layers);
        Assert.Same(document, CanvasResizer.Resize(document, new CanvasSizeOptions(4, 4)));
    }

    [Fact]
    public void CanvasSizeIsOneUndoStepAndRefitsTheView()
    {
        var session = new EditorSession();
        session.CreateDocument(10, 10);
        session.AddPixelLayer(Solid(4, 4, SKColors.Red), new Point(3, 3), "Red");
        var resized = 0;
        session.DocumentResized += () => resized++;
        var count = session.History.UndoCount;
        session.ApplyCanvasSize(new CanvasSizeOptions(20, 10) { Anchor = 8 });
        Assert.Equal((20, 10), (session.Document!.Width, session.Document.Height));
        Assert.Equal(new Point(13, 3), session.ActiveLayer!.Origin);
        Assert.Equal(count + 1, session.History.UndoCount);
        Assert.Equal(1, resized);
        session.Undo();
        Assert.Equal(10, session.Document!.Width);
    }

    [Fact]
    public void RotatedHiddenLayerScalesInDocumentAxesAndInvalidSizeIsRejected()
    {
        var layer = new ImageLayer(Asset(Solid(64, 32, SKColors.Red)), Point.Zero) with
        {
            IsVisible = false,
            Transform = new LayerTransform(new Point(-16, 4), new Size(64, 32), Rotation: 90),
        };
        var input = new CanvasDocument(64, 32, [layer]);
        var result = ImageResizer.Resize(input, new ImageSizeOptions(128, 96, 72) { Sampling = LayerSampling.Nearest });
        var output = result.Layers[0];
        Assert.False(output.IsVisible);
        Assert.Equal(0, output.Transform.Rotation);
        // A 90-degree 64×32 layer becomes 32×64, then scales 2× horizontally and 3× vertically.
        Assert.True(Math.Abs(output.Transform.Size.Width - 64) <= 1);
        Assert.True(Math.Abs(output.Transform.Size.Height - 192) <= 1);
        Assert.True(output.Transform.Origin.Y < 0);
        Assert.Equal(layer.Id, output.Id);
        Assert.Throws<ProjectException>(() => ImageResizer.Resize(input, new ImageSizeOptions(30_000, 30_000, 72)));
    }

    [Fact]
    public void ImageSizeResamplesPixelsKeepsMasksAndResolutionOnlyKeepsSources()
    {
        var mask = Bitmaps.Create(2, 2, mask: true);
        mask.Erase(SKColors.White);
        mask.SetPixel(1, 1, SKColors.Black);
        var layer = new ImageLayer(Asset(Solid(2, 2, SKColors.Red)), Point.Zero) with { Mask = new LayerMask(LayerMask.AssetFrom(mask)) };
        var document = new CanvasDocument(2, 2, [layer]);

        var doubled = ImageResizer.Resize(document, new ImageSizeOptions(4, 4, 144) { Sampling = LayerSampling.Nearest });
        Assert.Equal(4, doubled.Layers[0].Asset!.Width);
        Assert.Equal(4, doubled.Layers[0].Mask!.Asset.Width);
        Assert.Equal(144, doubled.Resolution);
        using var flat = DocumentRenderer.Flatten(doubled);
        Assert.Equal(SKColors.Red, flat.GetPixel(0, 0));
        Assert.Equal(SKColors.Empty, flat.GetPixel(3, 3));

        var resolutionOnly = ImageResizer.Resize(document, new ImageSizeOptions(2, 2, 300));
        Assert.Same(layer.Asset, resolutionOnly.Layers[0].Asset);
        Assert.Equal(300, resolutionOnly.Resolution);
    }

    [Fact]
    public void DragGeometrySupportsReverseRatioMoveAndEveryHandle()
    {
        var rect = CropGeometry.Create(new Point(100, 100), new Point(20, 60), ratio: 2);
        Assert.Equal(new Rect(20, 60, 80, 40), rect);
        var move = new CropDrag(new Point(40, 70), rect, new CropDragMode.Move());
        Assert.Equal(rect.Offset(-10, -30), move.Updated(new Point(30, 40), ratio: null));
        for (var index = 0; index < 8; index++)
        {
            var unit = LayerTransform.Handles[index];
            var start = new Point(rect.MinX + (unit.X * rect.Width), rect.MinY + (unit.Y * rect.Height));
            var drag = new CropDrag(start, rect, new CropDragMode.Resize(index));
            var next = drag.Updated(new Point(start.X + (((unit.X * 2) - 1) * 20), start.Y + (((unit.Y * 2) - 1) * 10)), ratio: 2);
            Assert.True(CropGeometry.IsValid(next));
            Assert.True(Math.Abs((next.Width / next.Height) - 2) < 0.05);
            Assert.NotEqual(rect, next);
        }
    }

    [Fact]
    public void CropEdgesSnapToNearbyEdges()
    {
        var snap = new CropSnap([0, 200, 50, 150], [0, 100, 20, 80], Tolerance: 6);
        var rect = new Rect(10, 10, 60, 40);
        var move = new CropDrag(new Point(30, 30), rect, new CropDragMode.Move());
        var movedTo = new Point(26, 34);
        Assert.Equal(new Rect(0, 20, 60, 40), snap.Apply(move.Updated(movedTo, null), move, movedTo, null));

        var cornerIndex = LayerTransform.Handles.ToList().FindIndex(h => h.X == 1 && h.Y == 1);
        var resize = new CropDrag(new Point(70, 50), rect, new CropDragMode.Resize(cornerIndex));
        var near = new Point(146, 83);
        Assert.Equal(new Rect(10, 10, 140, 70), snap.Apply(resize.Updated(near, null), resize, near, null));
        var far = new Point(120, 60);
        Assert.Equal(new Rect(10, 10, 110, 50), snap.Apply(resize.Updated(far, null), resize, far, null));

        var ratioRect = new Rect(10, 10, 138, 69);
        Assert.Equal(ratioRect, snap.Apply(ratioRect, resize, new Point(148, 79), ratio: 2));

        var create = new CropDrag(new Point(52, 18), Rect.Zero, new CropDragMode.Create());
        var dragged = new Point(147, 77);
        Assert.Equal(new Rect(52, 18, 98, 62), snap.Apply(create.Updated(dragged, null), create, dragged, null));
    }

    [Fact]
    public void SnapTargetsAreTheCanvasAndLayerBounds()
    {
        var session = new EditorSession();
        session.CreateDocument(400, 300);
        session.AddPixelLayer(Solid(100, 60, SKColors.Red), new Point(150, 120), "Red");
        var (xs, ys) = session.CropSnapTargets();
        Assert.Equal(new HashSet<double> { 0, 400, 150, 250 }, xs.ToHashSet());
        Assert.Equal(new HashSet<double> { 0, 300, 120, 180 }, ys.ToHashSet());
    }

    [Fact]
    public void CropTranslatesWithoutResamplingAndUndoRestoresBounds()
    {
        var session = new EditorSession();
        session.CreateDocument(100, 80);
        session.AddPixelLayer(Solid(10, 10, SKColors.Red), new Point(40, 30), "Red");
        var asset = session.ActiveLayer!.Asset;
        session.SetCropRect(new Rect(20, 10, 50, 40));
        session.CommitCrop();
        Assert.Null(session.CropRect);
        Assert.Equal((50, 40), (session.Document!.Width, session.Document.Height));
        Assert.Equal(new Point(20, 20), session.ActiveLayer!.Origin);
        Assert.Same(asset, session.ActiveLayer.Asset);
        Assert.Equal("Crop", session.History.UndoName);
        session.Undo();
        Assert.Equal((100, 80), (session.Document!.Width, session.Document.Height));
        Assert.Equal(new Point(40, 30), session.ActiveLayer!.Origin);

        session.SetCropRect(new Rect(0, 0, 0, 0));
        Assert.Null(session.CropRect);
        session.SetCropRatioChoice("1:1");
        session.SetCropRect(new Rect(0, 0, 100, 80));
        session.SetCropRatioChoice("16:9");
        Assert.Equal(56, session.CropRect!.Value.Height);
        session.CancelCrop();
        Assert.Null(session.CropRect);
    }
}
