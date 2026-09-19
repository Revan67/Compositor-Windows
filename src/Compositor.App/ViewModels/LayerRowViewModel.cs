using Avalonia.Media.Imaging;
using Compositor.Core.Document;

namespace Compositor.App.ViewModels;

/// <summary>One row of the layers panel, top first.</summary>
public sealed class LayerRowViewModel(LayerHierarchy.Entry entry, Bitmap? thumbnail, bool isActive, bool isSelected, bool isCollapsed) : ObservableObject
{
    public ImageLayer Layer { get; } = entry.Layer;
    public Guid Id => Layer.Id;
    public string Name => Layer.Name;
    public int Depth { get; } = entry.Depth;

    /// <summary>The layer's own flag; a hidden folder dims its contents through <see cref="EffectivelyVisible"/>.</summary>
    public bool IsVisible => Layer.IsVisible;
    public bool EffectivelyVisible { get; } = entry.Visible;
    public bool IsGroup => Layer.IsGroup;
    public bool HasMask => Layer.Mask is not null;
    public bool IsClipped => Layer.MaskSourceId is not null;
    public bool IsActive { get; } = isActive;
    public bool IsSelected { get; } = isSelected;
    public bool IsCollapsed { get; } = isCollapsed;
    public Bitmap? Thumbnail { get; } = thumbnail;

    public double Indent => (Depth * 16) + (IsClipped ? 16 : 0);
    public string Detail => Layer.IsGroup ? "Folder" : Layer.BlendMode == LayerBlendMode.Normal && Layer.Opacity >= 1 ? string.Empty : $"{Layer.BlendMode.Label()} · {Math.Round(Layer.Opacity * 100)}%";
    public double RowOpacity => EffectivelyVisible ? 1 : 0.45;
}
