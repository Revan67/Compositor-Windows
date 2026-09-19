using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>The session half of the reference <c>TransformTests</c>, plus group boxes and hit testing.</summary>
public sealed class TransformSessionTests
{
    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bitmap = Bitmaps.Create(w, h, false);
        bitmap.Erase(color);
        return bitmap;
    }

    private static EditorSession WithPaintedLayer(int width = 200, int height = 100)
    {
        var session = new EditorSession();
        session.CreateDocument(width, height);
        session.AddPixelLayer(Solid(width, height, SKColors.Red), Point.Zero, "Painted");
        return session;
    }

    [Fact]
    public void DuplicateTransformPreservesOriginalAndSupportsUndoAndCancel()
    {
        var session = WithPaintedLayer(400, 200);
        var original = session.ActiveLayer!;
        var count = session.History.UndoCount;
        session.BeginDuplicateTransform();
        var moved = original.Transform with { Origin = original.Transform.Origin.Offset(50, 0) };
        session.PreviewTransform(moved);
        session.CommitTransform();
        Assert.Equal(2, session.Layers.Count);
        Assert.Equal(original.Transform, session.Layers[0].Transform);
        Assert.Equal(moved, session.ActiveLayer!.Transform);
        Assert.Equal(count + 1, session.History.UndoCount);
        session.Undo();
        Assert.Single(session.Layers);
        Assert.Equal(original.Id, session.ActiveLayerId);
        session.BeginDuplicateTransform();
        session.PreviewTransform(moved);
        session.CancelTransform();
        Assert.Single(session.Layers);
        Assert.Equal(original.Id, session.ActiveLayerId);
        Assert.Equal(count, session.History.UndoCount);
    }

    [Fact]
    public void PreviewCommitCancelAndUndoPreserveSources()
    {
        var session = WithPaintedLayer();
        var original = session.ActiveLayer!;
        var count = session.History.UndoCount;
        session.BeginTransform();
        var value = original.Transform with { Origin = new Point(-45, 34), Size = new Size(80, 140), Rotation = 23, FlipX = true };
        for (var i = 0; i < 30; i++)
        {
            session.PreviewTransform(value);
        }

        Assert.Equal(original.Transform, session.ActiveLayer!.Transform);
        Assert.Equal(value, session.DisplayedDocument!.Layers[0].Transform);
        Assert.Equal(count, session.History.UndoCount);
        session.CancelTransform();
        Assert.Equal(original, session.ActiveLayer);
        session.BeginTransform();
        session.PreviewTransform(value);
        session.CommitTransform();
        Assert.Equal(count + 1, session.History.UndoCount);
        Assert.Equal(value, session.ActiveLayer!.Transform);
        Assert.Same(original.Asset, session.ActiveLayer.Asset);
        session.Undo();
        Assert.Equal(original, session.ActiveLayer);
        session.Redo();
        Assert.Equal(value, session.ActiveLayer!.Transform);
        Assert.Equal(original.Id, session.ActiveLayerId);
    }

    [Fact]
    public void NoOpAndInvalidValues()
    {
        var session = WithPaintedLayer();
        var count = session.History.UndoCount;
        session.BeginTransform();
        session.CommitTransform();
        Assert.Equal(count, session.History.UndoCount);
        session.BeginTransform();
        var draft = session.TransformEdit!.Draft;
        session.PreviewTransform(draft with { Size = new Size(0, 100) });
        Assert.Equal(200, session.TransformEdit!.Draft.Size.Width);
        session.PreviewTransform(draft with { Size = new Size(double.PositiveInfinity, 100) });
        Assert.Equal(200, session.TransformEdit!.Draft.Size.Width);
        session.PreviewTransform(draft with { Origin = new Point(10, 0) });
        session.CommitTransform();
        Assert.Null(session.TransformEdit);
        Assert.Equal(10, session.ActiveLayer!.Origin.X);
        Assert.Equal(count + 1, session.History.UndoCount);
    }

    [Fact]
    public void NudgeIsOneUndoStepAndCarriesALinkedMask()
    {
        var session = WithPaintedLayer();
        var id = session.ActiveLayerId!.Value;
        var mask = Bitmaps.Create(2, 2, mask: true);
        mask.Erase(SKColors.White);
        session.ReplaceLayerMask(id, LayerMask.AssetFrom(mask), "Mask");
        var count = session.History.UndoCount;
        session.NudgeLayer(1, 0);
        session.NudgeLayer(0, -3);
        Assert.Equal(new Point(1, -3), session.ActiveLayer!.Origin);
        Assert.Equal(count + 2, session.History.UndoCount);
        Assert.Null(session.ActiveLayer.Mask!.Placement);
    }

    [Fact]
    public void GroupTransformMovesEveryMemberAndFolderContents()
    {
        var session = new EditorSession();
        session.CreateDocument(400, 400);
        session.AddGroup();
        var folder = session.ActiveLayerId!.Value;
        session.AddPixelLayer(Solid(50, 50, SKColors.Red), new Point(10, 10), "A");
        var a = session.ActiveLayerId!.Value;
        session.AddPixelLayer(Solid(50, 50, SKColors.Blue), new Point(100, 200), "B");
        var b = session.ActiveLayerId!.Value;
        session.SelectLayer(folder);
        Assert.True(session.TransformsAsGroup);
        Assert.Equal(2, session.GroupTransformMembers.Count);
        Assert.Equal(new LayerTransform(new Point(10, 10), new Size(140, 240)), session.GroupTransformBox);

        session.BeginTransform();
        var box = session.TransformEdit!.Draft;
        session.PreviewTransform(box with { Origin = box.Origin.Offset(20, 30) });
        session.CommitTransform();
        Assert.Equal(new Point(30, 40), session.Layers.First(l => l.Id == a).Origin);
        Assert.Equal(new Point(120, 230), session.Layers.First(l => l.Id == b).Origin);
        Assert.Equal("Transform Layers", session.History.UndoName);

        // Scaling the box scales members about it.
        session.BeginTransform();
        box = session.TransformEdit!.Draft;
        session.PreviewTransform(box with { Size = new Size(280, 480) });
        session.CommitTransform();
        Assert.Equal(new Size(100, 100), session.Layers.First(l => l.Id == a).Size);
    }

    [Fact]
    public void LayerAtPicksTheTopmostOpaquePixel()
    {
        var session = new EditorSession();
        session.CreateDocument(100, 100);
        session.AddPixelLayer(Solid(100, 100, SKColors.Red), Point.Zero, "Back");
        var back = session.ActiveLayerId!.Value;
        var holed = Solid(40, 40, SKColors.Blue);
        holed.SetPixel(5, 5, SKColors.Empty);
        session.AddPixelLayer(holed, new Point(30, 30), "Front");
        var front = session.ActiveLayerId!.Value;
        Assert.Equal(front, session.LayerAt(new Point(50, 50))!.Id);
        Assert.Equal(back, session.LayerAt(new Point(35.5, 35.5))!.Id);
        Assert.Equal(back, session.LayerAt(new Point(5, 5))!.Id);
        Assert.Null(session.LayerAt(new Point(-1, 5)));
        session.ToggleLayerVisibility(front);
        Assert.Equal(back, session.LayerAt(new Point(50, 50))!.Id);
    }

    [Fact]
    public void SnapTargetsIncludeCanvasAndOtherLayers()
    {
        var session = new EditorSession();
        session.CreateDocument(200, 100);
        session.AddPixelLayer(Solid(20, 10, SKColors.Red), new Point(30, 40), "A");
        var a = session.ActiveLayerId!.Value;
        session.AddPixelLayer(Solid(20, 10, SKColors.Red), new Point(90, 20), "B");
        var (xs, ys) = session.SnapTargets(new HashSet<Guid> { a });
        Assert.Equal([0, 100, 200, 90, 100, 110], xs);
        Assert.Equal([0, 50, 100, 20, 25, 30], ys);
    }

    [Fact]
    public void OverlayHitTestingMatchesHandlesEdgesAndRotation()
    {
        var viewport = new CanvasViewport().Resized(new Size(900, 600), 1, new Size(1000, 800)).Zoomed(1, new Point(450, 300), new Size(1000, 800));
        var transform = new LayerTransform(new Point(100, 200), new Size(100, 50), Rotation: 90);
        var geometry = new TransformOverlayGeometry(transform, viewport, new Size(1000, 800));
        var corner = viewport.DocumentPoint(geometry.Handles[4], new Size(1000, 800));
        Assert.Equal(transform.PointAt(new Point(1, 1)).X, corner.X, 6);
        Assert.Equal(transform.PointAt(new Point(1, 1)).Y, corner.Y, 6);
        Assert.IsType<TransformDragMode.Rotate>(geometry.Hit(geometry.RotationHandle));
        for (var index = 0; index < geometry.Handles.Count; index++)
        {
            var hit = Assert.IsType<TransformDragMode.Resize>(geometry.Hit(geometry.Handles[index]));
            Assert.Equal(index, hit.Handle);
        }

        // A point along the top edge (between handles 0 and 2) resizes by the top-centre handle.
        var top = new Point((geometry.Handles[0].X * 0.75) + (geometry.Handles[2].X * 0.25), (geometry.Handles[0].Y * 0.75) + (geometry.Handles[2].Y * 0.25));
        Assert.Equal(1, Assert.IsType<TransformDragMode.Resize>(geometry.Hit(top)).Handle);
        var centre = viewport.ViewPoint(transform.Center, new Size(1000, 800));
        Assert.Null(geometry.Hit(centre));
        Assert.True(geometry.Contains(centre));
        Assert.False(geometry.Contains(new Point(-100, -100)));
    }
}
