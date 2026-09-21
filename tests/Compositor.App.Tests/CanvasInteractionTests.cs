using Compositor.App.Canvas;
using Compositor.App.ViewModels;
using Compositor.Core.Document;
using Compositor.Core.Geometry;

namespace Compositor.App.Tests;

public sealed class CanvasInteractionTests
{
    [Fact]
    public void BeginningAnotherEditClearsThePreviousInteraction()
    {
        var state = new CanvasEditInteraction();
        var transform = new TransformDrag(new LayerTransform(Point.Zero, new Size(10, 10)), Point.Zero, new TransformDragMode.Move());
        state.BeginTransform(transform, [Guid.NewGuid()]);
        Assert.Equal(CanvasEditKind.Transform, state.Kind);

        state.BeginCrop(new CropDrag(Point.Zero, new Rect(0, 0, 10, 10), new CropDragMode.Move()));

        Assert.Equal(CanvasEditKind.Crop, state.Kind);
        Assert.Null(state.Transform);
        Assert.Empty(state.TransformExcludes);
    }

    [Fact]
    public void CompleteAndCancelAlwaysReturnToIdle()
    {
        var state = new CanvasEditInteraction();
        state.BeginCrop(new CropDrag(Point.Zero, new Rect(0, 0, 10, 10), new CropDragMode.Move()));
        Assert.Equal(CanvasEditKind.Crop, state.Complete());
        Assert.Equal(CanvasEditKind.None, state.Kind);

        state.BeginTransform(new TransformDrag(new LayerTransform(Point.Zero, new Size(10, 10)), Point.Zero, new TransformDragMode.Move()), []);
        Assert.Equal(CanvasEditKind.Transform, state.Cancel());
        Assert.Equal(CanvasEditKind.None, state.Kind);
    }

    [Fact]
    public void CropCommandsFollowToolLifecycle()
    {
        var viewModel = new EditorViewModel();
        viewModel.NewCanvas(100, 80);
        Assert.False(viewModel.CropCommit.CanExecute(null));

        viewModel.Tool = EditorTool.Crop;
        viewModel.Session.SetCropRect(new Rect(10, 10, 50, 40));
        Assert.True(viewModel.CropCommit.CanExecute(null));

        viewModel.CropCommit.Execute(null);
        Assert.Equal(EditorTool.Move, viewModel.Tool);
        Assert.Equal((50, 40), (viewModel.Session.Document!.Width, viewModel.Session.Document.Height));
        Assert.Null(viewModel.Session.CropRect);
        Assert.False(viewModel.CropCommit.CanExecute(null));
    }

    [Fact]
    public void SwitchingAwayFromCropCancelsItsDraft()
    {
        var viewModel = new EditorViewModel();
        viewModel.NewCanvas(100, 80);
        viewModel.Tool = EditorTool.Crop;
        viewModel.Session.SetCropRect(new Rect(10, 10, 50, 40));

        viewModel.Tool = EditorTool.Hand;

        Assert.Null(viewModel.Session.CropRect);
        Assert.Equal((100, 80), (viewModel.Session.Document!.Width, viewModel.Session.Document.Height));
    }

    [Fact]
    public void MarqueeCommitsOnceAndIsUndoable()
    {
        var viewModel = new EditorViewModel();
        viewModel.NewCanvas(100, 80);
        var before = viewModel.Session.History.UndoCount;
        viewModel.Tool = EditorTool.Marquee;
        viewModel.BeginMarquee(new Point(10, 15));
        viewModel.UpdateMarquee(new Point(60, 45), square: false);

        Assert.Equal(new Rect(10, 15, 50, 30), viewModel.MarqueeDraft);
        Assert.Equal(before, viewModel.Session.History.UndoCount);
        viewModel.CommitMarquee();

        Assert.Equal(new Rect(10, 15, 50, 30), viewModel.Session.Document!.Selection!.Bounds);
        Assert.Equal(before + 1, viewModel.Session.History.UndoCount);
        viewModel.Session.Undo();
        Assert.Null(viewModel.Session.Document!.Selection);
    }

}
