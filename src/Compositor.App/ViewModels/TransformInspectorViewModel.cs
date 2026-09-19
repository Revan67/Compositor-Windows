using Compositor.Core.Document;
using Compositor.Core.Geometry;

namespace Compositor.App.ViewModels;

/// <summary>
/// Exact values for the active layer's placement. Typing a value applies it as one undo step;
/// the fields show the pending draft while a drag is in progress.
/// </summary>
public sealed class TransformInspectorViewModel(EditorSession session) : ObservableObject
{
    public IReadOnlyList<LayerSampling> Samplings { get; } = Enum.GetValues<LayerSampling>();

    private LayerTransform? Current => session.ActiveLayer is { } layer && session.CanTransform ? session.EditedTransform(layer) : null;

    public bool IsEnabled => Current is not null;

    public double X { get => Current?.Origin.X ?? 0; set => Apply(t => t with { Origin = new Point(value, t.Origin.Y) }); }
    public double Y { get => Current?.Origin.Y ?? 0; set => Apply(t => t with { Origin = new Point(t.Origin.X, value) }); }
    public double Width { get => Current?.Size.Width ?? 0; set => Apply(t => t with { Size = new Size(Math.Max(1, value), t.Size.Height) }); }
    public double Height { get => Current?.Size.Height ?? 0; set => Apply(t => t with { Size = new Size(t.Size.Width, Math.Max(1, value)) }); }
    public double Rotation { get => Current?.Rotation ?? 0; set => Apply(t => t with { Rotation = value }); }
    public bool FlipX { get => Current?.FlipX ?? false; set => Apply(t => t with { FlipX = value }); }
    public bool FlipY { get => Current?.FlipY ?? false; set => Apply(t => t with { FlipY = value }); }

    public LayerSampling Sampling
    {
        get => Current?.Sampling ?? LayerSampling.High;
        set => Apply(t => t with { Sampling = value });
    }

    /// <summary>Width as a percentage of the layer's pixels; setting it scales both sides about the centre.</summary>
    public double ScalePercent
    {
        get => Current is { } t && session.TransformPixelSize is { } pixels ? Math.Round(t.ScalePercent(pixels), 1) : 100;
        set
        {
            if (session.TransformPixelSize is { } pixels && value > 0)
            {
                Apply(t => t.ScaledToPercent(value, pixels));
            }
        }
    }

    public bool CanScale => session.TransformPixelSize is not null;

    private void Apply(Func<LayerTransform, LayerTransform> change)
    {
        if (Current is not { } current)
        {
            return;
        }

        var next = change(current);
        if (next == current || !next.IsValid)
        {
            Refresh();
            return;
        }

        if (session.TransformEdit is not null)
        {
            session.PreviewTransform(next);
            return;
        }

        session.BeginTransform(persistent: false);
        session.PreviewTransform(next);
        session.CommitTransform();
    }

    public void Refresh()
    {
        foreach (var name in new[] { nameof(IsEnabled), nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(Rotation), nameof(FlipX), nameof(FlipY), nameof(Sampling), nameof(ScalePercent), nameof(CanScale) })
        {
            Raise(name);
        }
    }
}
