namespace Compositor.Core.Document;

public sealed class InvalidProjectException(string message) : Exception(message);

/// <summary>The layer tree implied by <see cref="ImageLayer.ParentId"/>, walked bottom-to-top like the array.</summary>
public static class LayerHierarchy
{
    public const int MaxDepth = 64;

    /// <summary>The longest chain of clipping-mask links.</summary>
    public const int MaxMaskChain = 256;

    public sealed record Entry(ImageLayer Layer, int Depth, bool Visible);

    /// <summary>
    /// Every layer in tree order with its nesting depth and effective visibility (a hidden folder
    /// hides its contents without changing their flags). <paramref name="topFirst"/> gives list
    /// order; <paramref name="collapsed"/> folders keep their contents out of the result.
    /// </summary>
    public static List<Entry> Entries(IReadOnlyList<ImageLayer> layers, bool topFirst = false, IReadOnlySet<Guid>? collapsed = null)
    {
        var children = layers.GroupBy(l => l.ParentId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var result = new List<Entry>(layers.Count);
        Visit(Guid.Empty, 0, true);
        return result;

        void Visit(Guid parent, int depth, bool visible)
        {
            if (depth > MaxDepth || !children.TryGetValue(parent, out var siblings))
            {
                return;
            }

            foreach (var layer in topFirst ? Enumerable.Reverse(siblings) : siblings)
            {
                var effective = visible && layer.IsVisible;
                result.Add(new Entry(layer, depth, effective));
                if (layer.IsGroup && collapsed?.Contains(layer.Id) != true)
                {
                    Visit(layer.Id, depth + 1, effective);
                }
            }
        }
    }

    /// <summary>The non-folder layers that draw, bottom to top.</summary>
    public static List<ImageLayer> VisibleLayers(IReadOnlyList<ImageLayer> layers) =>
        Entries(layers).Where(e => e.Visible && !e.Layer.IsGroup).Select(e => e.Layer).ToList();

    /// <summary>
    /// Rejects duplicate ids, folders with pixels, parents that are missing or not folders, cycles,
    /// and nesting deeper than <see cref="MaxDepth"/>.
    /// </summary>
    public static void Validate(IReadOnlyList<ImageLayer> layers)
    {
        var byId = new Dictionary<Guid, ImageLayer>();
        foreach (var layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer) || (layer.IsGroup && layer.Asset is not null))
            {
                throw new InvalidProjectException("Duplicate layer id or a folder with pixels.");
            }
        }

        foreach (var layer in layers)
        {
            var seen = new HashSet<Guid> { layer.Id };
            var parent = layer.ParentId;
            while (parent is { } id)
            {
                if (seen.Count > MaxDepth || !seen.Add(id) || !byId.TryGetValue(id, out var node) || !node.IsGroup)
                {
                    throw new InvalidProjectException("Layer parent chain is missing, cyclic, not a folder, or too deep.");
                }

                parent = node.ParentId;
            }

            if (layer.IsGroup && seen.Count > MaxDepth)
            {
                throw new InvalidProjectException("Folders nest too deeply.");
            }
        }
    }

    /// <summary>
    /// Rejects clipping-mask links that are missing, self-referential, cyclic, on or to a folder, or
    /// chained past <see cref="MaxMaskChain"/> nodes.
    /// </summary>
    public static void ValidateMaskLinks(IReadOnlyList<ImageLayer> layers)
    {
        var byId = new Dictionary<Guid, ImageLayer>();
        foreach (var layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer))
            {
                throw new InvalidProjectException("Duplicate layer id.");
            }
        }

        foreach (var layer in layers)
        {
            var path = new HashSet<Guid>();
            Guid? current = layer.Id;
            while (current is { } id)
            {
                if (path.Count >= MaxMaskChain || !path.Add(id) || !byId.TryGetValue(id, out var record))
                {
                    throw new InvalidProjectException("Clipping-mask chain is missing, cyclic or too long.");
                }

                if (record.MaskSourceId is { } source)
                {
                    if (record.IsGroup || !byId.TryGetValue(source, out var target) || target.IsGroup)
                    {
                        throw new InvalidProjectException("Clipping masks link non-folder layers only.");
                    }
                }

                current = record.MaskSourceId;
            }
        }
    }

    public static HashSet<Guid> DescendantIds(IReadOnlyList<ImageLayer> layers, Guid id)
    {
        var children = layers.Where(l => l.ParentId is not null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>([id]);
        while (pending.TryPop(out var parent))
        {
            if (!children.TryGetValue(parent, out var kids))
            {
                continue;
            }

            foreach (var child in kids)
            {
                if (result.Add(child.Id))
                {
                    pending.Push(child.Id);
                }
            }
        }

        return result;
    }
}
