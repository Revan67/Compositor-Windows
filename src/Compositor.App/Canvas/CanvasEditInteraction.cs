using Compositor.Core.Document;

namespace Compositor.App.Canvas;

internal enum CanvasEditKind
{
    None,
    Transform,
    Crop,
}

/// <summary>
/// Owns the transient pointer-edit state shared by canvas tools. Keeping this lifecycle separate
/// prevents a lost capture, tool switch or Escape key from leaving an edit half-active.
/// </summary>
internal sealed class CanvasEditInteraction
{
    public TransformDrag? Transform { get; private set; }

    public CropDrag? Crop { get; private set; }

    public IReadOnlySet<Guid> TransformExcludes { get; private set; } = new HashSet<Guid>();

    public CanvasEditKind Kind => Transform is not null ? CanvasEditKind.Transform : Crop is not null ? CanvasEditKind.Crop : CanvasEditKind.None;

    public void BeginTransform(TransformDrag drag, IEnumerable<Guid> excludes)
    {
        Clear();
        Transform = drag;
        TransformExcludes = excludes.ToHashSet();
    }

    public void BeginCrop(CropDrag drag)
    {
        Clear();
        Crop = drag;
    }

    public CanvasEditKind Complete() => TakeKind();

    public CanvasEditKind Cancel() => TakeKind();

    private CanvasEditKind TakeKind()
    {
        var kind = Kind;
        Clear();
        return kind;
    }

    private void Clear()
    {
        Transform = null;
        Crop = null;
        TransformExcludes = new HashSet<Guid>();
    }
}
