using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class BrushStrokeTests
{
    [Fact]
    public void PaintAndEraseChangeLayerPixels()
    {
        var layer = new ImageLayer("Layer", new Size(32, 32));
        using var paint = new BrushStroke(layer, null, BrushMode.Paint, 8, 1, SKColors.Red);
        paint.Add(new Point(16, 16));
        var painted = paint.Commit("Layer");
        Assert.True(painted.Image.GetPixel(16, 16).Alpha > 0);

        using var erase = new BrushStroke(layer with { Asset = painted }, null, BrushMode.Erase, 8, 1, SKColors.Black);
        erase.Add(new Point(16, 16));
        var erased = erase.Commit("Layer");
        Assert.Equal(0, erased.Image.GetPixel(16, 16).Alpha);
    }

    [Fact]
    public void SelectionClipsAStroke()
    {
        var layer = new ImageLayer("Layer", new Size(40, 20));
        var selection = DocumentSelection.Rectangle(new Rect(0, 0, 20, 20), new Rect(0, 0, 40, 20));
        using var stroke = new BrushStroke(layer, selection, BrushMode.Paint, 8, 1, SKColors.Black);
        stroke.Add(new Point(10, 10));
        stroke.Add(new Point(30, 10));
        var result = stroke.Commit("Layer").Image;

        Assert.True(result.GetPixel(10, 10).Alpha > 0);
        Assert.Equal(0, result.GetPixel(30, 10).Alpha);
    }

    [Fact]
    public void TransformedLayerMapsDocumentPointsIntoItsPixels()
    {
        var layer = new ImageLayer("Layer", new Size(20, 20))
        {
            Transform = new LayerTransform(new Point(50, 25), new Size(40, 40), 90),
        };
        using var stroke = new BrushStroke(layer, null, BrushMode.Paint, 6, 1, SKColors.Blue);
        stroke.Add(layer.Transform.Center);
        var result = stroke.Commit("Layer").Image;

        Assert.True(result.GetPixel(20, 20).Alpha > 0);
    }

    [Fact]
    public void CommittedStrokeIsOneUndoableSessionEdit()
    {
        var session = new EditorSession();
        session.CreateDocument(64, 64, emptyLayer: true);
        var layer = session.ActiveLayer!;
        var before = session.History.UndoCount;
        using var stroke = new BrushStroke(layer, null, BrushMode.Paint, 10, 1, SKColors.Black);
        stroke.Add(new Point(10, 10));
        stroke.Add(new Point(30, 10));

        session.ReplaceLayerAsset(layer.Id, stroke.Commit(layer.Name), layer.Transform, "Brush Stroke");

        Assert.Equal(before + 1, session.History.UndoCount);
        Assert.Equal("Brush Stroke", session.History.UndoName);
        Assert.True(session.ActiveLayer!.Asset!.Image.GetPixel(20, 10).Alpha > 0);
        session.Undo();
        Assert.Null(session.ActiveLayer!.Asset);
    }
}
