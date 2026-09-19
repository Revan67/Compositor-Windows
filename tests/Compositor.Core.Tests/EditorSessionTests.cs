using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The reference <c>HistoryTests</c>, <c>LayerTests</c>, <c>GroupTests</c>, <c>LayerMaskTests</c>,
/// <c>LiveMaskTests</c> and <c>LayerAppearanceTests</c> cases that concern the document model and
/// history rather than tools or the canvas.
/// </summary>
public sealed class EditorSessionTests
{
    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bitmap = Bitmaps.Create(w, h, false);
        bitmap.Erase(color);
        return bitmap;
    }

    private static EditorSession WithThreeLayers()
    {
        var session = new EditorSession();
        session.CreateDocument(800, 600);
        for (var i = 0; i < 3; i++)
        {
            session.AddBlankLayer();
        }

        return session;
    }

    private static List<string> Names(EditorSession s) => s.Layers.Select(l => l.Name).ToList();

    // MARK: History

    [Fact]
    public void EveryLayerEditRoundTripsWithSelection()
    {
        var session = new EditorSession();
        var states = new List<(CanvasDocument? Document, Guid? Active)> { (null, null) };
        void Capture() => states.Add((session.Document, session.ActiveLayerId));

        session.CreateDocument(800, 600);
        Capture();
        session.AddBlankLayer();
        Capture();
        session.AddBlankLayer();
        Capture();
        var id = session.ActiveLayerId!.Value;
        session.RenameLayer(id, "Foreground");
        Capture();
        session.ToggleLayerVisibility(id);
        Capture();
        session.ReorderLayers([0], 2);
        Capture();
        session.MoveActiveLayer(1);
        Capture();
        session.DeleteActiveLayer();
        Capture();

        foreach (var expected in states.SkipLast(1).AsEnumerable().Reverse())
        {
            Assert.True(session.CanUndo);
            session.Undo();
            Assert.Equal(expected.Document, session.Document);
            Assert.Equal(expected.Active, session.ActiveLayerId);
        }

        Assert.False(session.CanUndo);
        foreach (var expected in states.Skip(1))
        {
            session.Redo();
            Assert.Equal(expected.Document, session.Document);
            Assert.Equal(expected.Active, session.ActiveLayerId);
        }

        Assert.False(session.CanRedo);
    }

    [Fact]
    public void NoOpsAndSaveRevisionPreserveHistory()
    {
        var session = new EditorSession();
        session.CreateDocument(800, 600);
        session.AddBlankLayer();
        var id = session.ActiveLayerId!.Value;
        session.MarkSaved();
        Assert.False(session.IsModified);
        session.RenameLayer(id, "Changed");
        Assert.True(session.IsModified);
        session.Undo();
        Assert.False(session.IsModified);

        var count = session.History.UndoCount;
        session.RenameLayer(id, "Layer 1");
        session.RenameLayer(id, "   ");
        session.ReorderLayers([0], 1);
        Assert.Equal(count, session.History.UndoCount);
        Assert.True(session.CanRedo);
        session.Redo();
        Assert.True(session.IsModified);
        session.Undo();
        session.AddBlankLayer();
        Assert.False(session.CanRedo);
        Assert.True(session.IsModified);
    }

    [Fact]
    public void ReplacementCanvasAndNestedTransactionsUndoAsOne()
    {
        var session = new EditorSession();
        session.CreateDocument(100, 200);
        session.BeginEdit("Layer Setup");
        session.AddBlankLayer();
        session.AddBlankLayer();
        Assert.False(session.CanUndo);
        session.EndEdit();
        Assert.Equal("Layer Setup", session.History.UndoName);
        session.Undo();
        Assert.Empty(session.Layers);
        session.Redo();
        var previous = session.Document;
        session.CreateDocument(300, 400);
        session.Undo();
        Assert.Equal(previous, session.Document);
        session.Redo();
        Assert.Equal(new Size(300, 400), session.Document!.Size);
    }

    [Fact]
    public void HistoryIsBlockedWhileBusy()
    {
        var session = new EditorSession();
        session.CreateDocument(10, 10);
        session.AddBlankLayer();
        session.IsBusy = true;
        Assert.False(session.CanUndo);
        session.Undo();
        session.AddBlankLayer();
        session.DeleteActiveLayer();
        Assert.Single(session.Layers);
        session.IsBusy = false;
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void HistoryBoundsEntriesAndUniqueRetainedPixels()
    {
        var asset = new ImportedImage(Solid(64, 32, SKColors.Red), Solid(8, 4, SKColors.Red), "png");
        var history = new DocumentHistory(entryLimit: 2, retainedByteLimit: 0);
        var doc = new CanvasDocument(64, 32, [new ImageLayer(asset, Point.Zero)]);
        foreach (var name in new[] { "A", "B", "C" })
        {
            history.Begin("Rename", doc, doc.Layers[0].Id);
            doc = doc with { Layers = [doc.Layers[0] with { Name = name }] };
            history.End(doc, doc.Layers[0].Id);
        }

        Assert.Equal(2, history.UndoCount);
        Assert.Equal(0, history.RetainedBytes(doc));
        history.Begin("Delete", doc, doc.Layers[0].Id);
        doc = doc with { Layers = [] };
        history.End(doc, null);
        // The bitmaps now live only in history, which has a zero byte budget.
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RetainedBytes(doc));
    }

    // MARK: Layers

    [Fact]
    public void BlankLayersAreTransparentAndInsertedAboveSelection()
    {
        var session = WithThreeLayers();
        session.SelectLayer(session.Layers[0].Id);
        session.AddBlankLayer();
        Assert.Equal(["Layer 1", "Layer 4", "Layer 2", "Layer 3"], Names(session));
        Assert.Equal(session.Layers[1].Id, session.ActiveLayerId);
        Assert.Null(session.Layers[1].Asset);
        Assert.Equal(new Size(800, 600), session.Layers[1].Size);
        Assert.Equal(Point.Zero, session.Layers[1].Origin);
    }

    [Fact]
    public void DeletionPreservesCanvasAndChoosesNeighbor()
    {
        var session = WithThreeLayers();
        var layers = session.Layers.ToList();
        session.SelectLayer(layers[1].Id);
        session.DeleteLayer(layers[0].Id);
        Assert.Equal(layers[1].Id, session.ActiveLayerId);
        session.DeleteActiveLayer();
        Assert.Equal(layers[2].Id, session.ActiveLayerId);
        session.DeleteActiveLayer();
        Assert.Null(session.ActiveLayerId);
        Assert.Empty(session.Layers);
        Assert.Equal(new Size(800, 600), session.Document!.Size);
        session.AddBlankLayer();
        Assert.Single(session.Layers);
    }

    [Fact]
    public void RenameAndVisibilityKeepIdentity()
    {
        var session = WithThreeLayers();
        var id = session.ActiveLayerId!.Value;
        session.RenameLayer(id, "  Foreground \n");
        session.RenameLayer(id, " \n ");
        Assert.Equal("Foreground", session.ActiveLayer!.Name);
        session.ToggleLayerVisibility(id);
        Assert.False(session.ActiveLayer!.IsVisible);
        Assert.Equal(id, session.ActiveLayerId);
        session.ToggleLayerVisibility(id);
        Assert.True(session.ActiveLayer!.IsVisible);
    }

    [Fact]
    public void ReorderTranslatesVisibleOrderAndKeepsSelection()
    {
        var session = WithThreeLayers();
        var active = session.ActiveLayerId;
        session.ReorderLayers([0], 3);
        Assert.Equal(["Layer 3", "Layer 1", "Layer 2"], Names(session));
        Assert.Equal(active, session.ActiveLayerId);
        Assert.False(session.CanMoveActiveLayer(-1));
        session.MoveActiveLayer(1);
        Assert.Equal(["Layer 1", "Layer 3", "Layer 2"], Names(session));
        session.ReorderLayers([99], 0);
        Assert.Equal(3, session.Layers.Count);
    }

    [Fact]
    public void UnavailableActionsDoNotChangeDocument()
    {
        var session = new EditorSession();
        session.AddBlankLayer();
        Assert.Null(session.Document);
        session.CreateDocument(30_000, 30_000);
        session.AddBlankLayer();
        Assert.Null(session.ActiveLayer!.Asset);
        session.IsBusy = true;
        session.AddBlankLayer();
        session.DeleteActiveLayer();
        Assert.Single(session.Layers);
        session.CreateDocument(0, 10);
        Assert.Equal(30_000, session.Document!.Width);
    }

    [Fact]
    public void NewCanvasStartsWithOneSelectedEmptyLayer()
    {
        var session = new EditorSession();
        session.CreateDocument(640, 480, emptyLayer: true);
        var layer = Assert.Single(session.Layers);
        Assert.Equal("Layer 1", layer.Name);
        Assert.Null(layer.Asset);
        Assert.Equal(layer.Id, session.ActiveLayerId);
        Assert.Equal(new HashSet<Guid> { layer.Id }, session.SelectedLayerIds);
    }

    [Fact]
    public void DeletingAMultiSelectionRemovesEveryLayerInOneStep()
    {
        var session = WithThreeLayers();
        session.SelectLayers([session.Layers[0].Id, session.Layers[2].Id], session.Layers[2].Id);
        var count = session.History.UndoCount;
        session.DeleteSelectedLayers();
        Assert.Equal(["Layer 2"], Names(session));
        Assert.Equal(count + 1, session.History.UndoCount);
        Assert.Equal(session.Layers[0].Id, session.ActiveLayerId);
        session.Undo();
        Assert.Equal(3, session.Layers.Count);
    }

    [Fact]
    public void DuplicatingPlacesTheCopyAboveAsOneStep()
    {
        var session = new EditorSession();
        session.CreateDocument(4, 4);
        session.AddPixelLayer(Solid(2, 2, SKColors.Red), new Point(1, 1), "Red");
        var original = session.ActiveLayer!;
        session.AddBlankLayer();
        var count = session.History.UndoCount;
        Assert.True(session.DuplicateLayer(original.Id, parent: null, atBottom: true));
        Assert.Equal(count + 1, session.History.UndoCount);
        var copy = session.Layers[0];
        Assert.Equal("Red copy", copy.Name);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Same(original.Asset, copy.Asset);
        Assert.Equal(copy.Id, session.ActiveLayerId);
        session.Undo();
        Assert.Equal(2, session.Layers.Count);
    }

    [Fact]
    public void CompositingHonorsVisibilityOrderAndBlankLayers()
    {
        var session = new EditorSession();
        session.CreateDocument(2, 2);
        session.AddPixelLayer(Solid(2, 2, SKColors.Red), Point.Zero, "Red");
        session.AddBlankLayer();
        session.AddPixelLayer(Solid(2, 2, SKColors.Blue), Point.Zero, "Blue");
        using var top = DocumentRenderer.Flatten(session.Document!);
        Assert.Equal(SKColors.Blue, top.GetPixel(0, 0));
        session.ToggleLayerVisibility(session.ActiveLayerId!.Value);
        using var hidden = DocumentRenderer.Flatten(session.Document!);
        Assert.Equal(SKColors.Red, hidden.GetPixel(0, 0));
        session.ReorderLayers([1], 3);
        Assert.Equal(["Layer 1", "Red", "Blue"], Names(session));
        session.ToggleLayerVisibility(session.Layers[^1].Id);
        using var reordered = DocumentRenderer.Flatten(session.Document!);
        Assert.Equal(SKColors.Blue, reordered.GetPixel(1, 1));
    }

    // MARK: Groups

    [Fact]
    public void NestedGroupsMoveOutCollapseAndDeleteUndo()
    {
        var session = new EditorSession();
        session.CreateDocument(100, 100);
        session.AddGroup();
        var outer = session.ActiveLayerId!.Value;
        session.AddGroup();
        var inner = session.ActiveLayerId!.Value;
        session.AddBlankLayer();
        var child = session.ActiveLayerId!.Value;
        Assert.Equal(inner, session.ActiveLayer!.ParentId);
        Assert.False(session.PlaceLayer(outer, inner));
        Assert.False(session.PlaceLayer(inner, inner));
        session.ToggleGroupExpansion(outer);
        Assert.Equal([outer], session.LayerRows.Select(r => r.Layer.Id));
        Assert.Equal(outer, session.ActiveLayerId);
        session.ToggleGroupExpansion(outer);
        Assert.Equal([0, 1, 2], session.LayerRows.Select(r => r.Depth));
        session.SelectLayer(child);
        session.MoveActiveLayerOutOfGroup();
        Assert.Equal(outer, session.ActiveLayer!.ParentId);
        session.Undo();
        Assert.Equal(inner, session.Layers.First(l => l.Id == child).ParentId);
        session.SelectLayer(outer);
        session.DeleteActiveLayer();
        Assert.Empty(session.Layers);
        session.Undo();
        Assert.Equal(3, session.Layers.Count);
        Assert.Equal(inner, session.Layers.First(l => l.Id == child).ParentId);
    }

    [Fact]
    public void GroupingSelectedLayersWrapsThemAtTheTopmostBranch()
    {
        var session = WithThreeLayers();
        var ids = session.Layers.Select(l => l.Id).ToList();
        session.SelectLayers([ids[0], ids[2]], ids[2]);
        session.GroupSelectedLayers();
        var folder = session.ActiveLayer!;
        Assert.True(folder.IsGroup);
        Assert.Equal("Folder 1", folder.Name);
        Assert.Equal([ids[1], folder.Id, ids[0], ids[2]], session.Layers.Select(l => l.Id));
        Assert.All(session.Layers.Where(l => l.Id != ids[1] && l.Id != folder.Id), l => Assert.Equal(folder.Id, l.ParentId));
        // A new layer with the folder selected goes to the top of the folder.
        session.AddBlankLayer();
        Assert.Equal(folder.Id, session.ActiveLayer!.ParentId);
        Assert.Equal(session.Layers[^1].Id, session.ActiveLayerId);
    }

    [Fact]
    public void HiddenParentOverridesChildrenInRenderOrder()
    {
        var session = new EditorSession();
        session.CreateDocument(2, 2);
        session.AddGroup();
        var folder = session.ActiveLayerId!.Value;
        session.AddPixelLayer(Solid(2, 2, SKColors.Red), Point.Zero, "Red");
        Assert.Equal(folder, session.ActiveLayer!.ParentId);
        session.ToggleLayerVisibility(folder);
        Assert.True(session.ActiveLayer!.IsVisible);
        using var flat = DocumentRenderer.Flatten(session.Document!);
        Assert.Equal(SKColors.Empty, flat.GetPixel(0, 0));
        Assert.Empty(LayerHierarchy.VisibleLayers(session.Layers));
    }

    // MARK: Masks

    [Fact]
    public void AddDisableDeleteUndoAndTargetSelection()
    {
        var session = new EditorSession();
        session.CreateDocument(4, 4);
        session.AddPixelLayer(Solid(4, 4, SKColors.Red), Point.Zero, "Red");
        var original = session.ActiveLayer!.Asset!.Image;
        var count = session.History.UndoCount;
        session.AddLayerMask(revealing: false);
        Assert.True(session.IsMaskSelected && session.ActiveLayer!.Mask is not null);
        Assert.Equal(1, session.ActiveLayer!.Mask!.Asset.Width);
        Assert.Equal(count + 1, session.History.UndoCount);
        session.AddLayerMask();
        Assert.Equal(count + 1, session.History.UndoCount);
        session.ToggleLayerMask();
        Assert.False(session.ActiveLayer!.Mask!.IsEnabled);
        session.DeleteLayerMask();
        Assert.True(session.ActiveLayer!.Mask is null && !session.IsMaskSelected);
        session.Undo();
        Assert.False(session.ActiveLayer!.Mask!.IsEnabled);
        session.Undo();
        Assert.True(session.ActiveLayer!.Mask!.IsEnabled);
        session.Undo();
        Assert.Null(session.ActiveLayer!.Mask);
        session.Redo();
        var id = session.ActiveLayerId!.Value;
        session.SelectLayerTarget(id, mask: true);
        Assert.True(session.IsMaskSelected);
        session.SelectLayerTarget(id, mask: false);
        Assert.True(!session.IsMaskSelected && ReferenceEquals(session.ActiveLayer!.Asset!.Image, original));
        session.AddGroup();
        session.AddLayerMask();
        Assert.True(session.ActiveLayer!.IsGroup && session.ActiveLayer.Mask is not null);
    }

    [Fact]
    public void HideAllMaskHidesAndUnlinkedMaskStaysPutWhenTheLayerMoves()
    {
        var session = new EditorSession();
        session.CreateDocument(4, 4);
        session.AddPixelLayer(Solid(4, 4, SKColors.Red), Point.Zero, "Red");
        var id = session.ActiveLayerId!.Value;
        session.AddLayerMask(revealing: false);
        using var hidden = DocumentRenderer.Flatten(session.Document!);
        Assert.Equal(SKColors.Empty, hidden.GetPixel(2, 2));

        // A 2×2 mask, unlinked: moving the layer leaves the mask where it was on the document.
        var mask = Bitmaps.Create(2, 2, mask: true);
        mask.Erase(SKColors.White);
        session.ReplaceLayerMask(id, LayerMask.AssetFrom(mask), "Paint Mask");
        session.ToggleMaskLink(id);
        Assert.False(session.ActiveLayer!.Mask!.IsLinked);
        var moved = session.ActiveLayer.Transform with { Origin = new Point(2, 2) };
        session.SetLayerTransform(id, moved);
        Assert.Equal(new LayerTransform(Point.Zero, new Size(4, 4)), session.ActiveLayer!.Mask!.Placement);
        session.Undo();
        Assert.Null(session.ActiveLayer!.Mask!.Placement);
    }

    // MARK: Clipping masks

    [Fact]
    public void AltClickCreatesSharedStackAndDragOutReleases()
    {
        var s = new EditorSession();
        s.CreateDocument(2, 2);
        for (var i = 0; i < 3; i++)
        {
            s.AddPixelLayer(Solid(2, 2, SKColors.White), Point.Zero, $"L{i}");
        }

        var ids = s.Layers.Select(l => l.Id).ToList();
        Assert.False(s.CanToggleClippingMask(ids[0]));
        Assert.True(s.CanToggleClippingMask(ids[1]));
        s.ToggleClippingMask(ids[0]);
        Assert.Null(s.Layers[0].MaskSourceId);
        s.ToggleClippingMask(ids[1]);
        s.ToggleClippingMask(ids[2]);
        Assert.Equal(ids[0], s.Layers[1].MaskSourceId);
        Assert.Equal(ids[0], s.Layers[2].MaskSourceId);
        Assert.True(s.CanToggleClippingMask(ids[2]));
        s.ToggleClippingMask(ids[2]);
        Assert.Null(s.Layers[2].MaskSourceId);
        Assert.Equal(ids[0], s.Layers[1].MaskSourceId);
        s.ToggleClippingMask(ids[2]);
        s.ToggleClippingMask(ids[1]);
        Assert.True(s.Layers[1].MaskSourceId is null && s.Layers[2].MaskSourceId is null);
        s.Undo();
        Assert.Equal(ids[0], s.Layers[2].MaskSourceId);
        Assert.True(s.PlaceLayer(ids[2], null, atBottom: true));
        Assert.True(s.Layers[0].Id == ids[2] && s.Layers[0].MaskSourceId is null);
        Assert.Equal(ids[0], s.Layers[^1].MaskSourceId);
        s.Undo();
        Assert.True(s.Layers[^1].Id == ids[2] && s.Layers[^1].MaskSourceId == ids[0]);
    }

    [Fact]
    public void DroppingIntoAStackAdoptsItAndDeletingTheBaseReleasesLinks()
    {
        var s = new EditorSession();
        s.CreateDocument(2, 2);
        for (var i = 0; i < 3; i++)
        {
            s.AddPixelLayer(Solid(2, 2, SKColors.White), Point.Zero, $"L{i}");
        }

        var ids = s.Layers.Select(l => l.Id).ToList();
        Assert.True(s.LinkMask(ids[0], ids[2]));
        Assert.False(s.LinkMask(ids[2], ids[0]));
        // L1 sits between the base and its clipped layer: moving it there adopts the clip.
        Assert.True(s.PlaceLayer(ids[1], null, aboveTarget: ids[0]));
        Assert.Equal(ids[0], s.Layers[1].MaskSourceId);
        Assert.Equal(ids[0], s.Layers[2].MaskSourceId);
        s.DeleteLayer(ids[0]);
        Assert.All(s.Layers, l => Assert.Null(l.MaskSourceId));
        s.Undo();
        Assert.Equal(ids[0], s.Layers[2].MaskSourceId);
    }

    // MARK: Appearance

    [Fact]
    public void OpacityDragIsOneUndoAndBlendModeIsAnother()
    {
        var session = WithThreeLayers();
        var count = session.History.UndoCount;
        session.BeginOpacityEdit();
        session.SetLayerOpacity(0.7);
        session.SetLayerOpacity(0.4);
        session.SetLayerOpacity(0.25);
        Assert.Equal(count, session.History.UndoCount);
        session.FinishOpacityEdit();
        Assert.Equal(count + 1, session.History.UndoCount);
        Assert.Equal(0.25, session.ActiveLayer!.Opacity);
        session.SetLayerOpacity(2);
        Assert.Equal(1, session.ActiveLayer!.Opacity);
        session.SetLayerBlendMode(LayerBlendMode.Screen);
        Assert.Equal(LayerBlendMode.Screen, session.ActiveLayer!.BlendMode);
        Assert.Equal("Layer Blend Mode", session.History.UndoName);
        session.Undo();
        session.Undo();
        Assert.Equal(0.25, session.ActiveLayer!.Opacity);

        session.SelectLayers(session.Layers.Select(l => l.Id), session.Layers[0].Id);
        session.SetSelectedLayersOpacity(0.5);
        Assert.All(session.Layers, l => Assert.Equal(0.5, l.Opacity));
        Assert.Equal("Layer Opacity", session.History.UndoName);
    }
}
