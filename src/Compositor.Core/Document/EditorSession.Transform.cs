using Compositor.Core.Geometry;

namespace Compositor.Core.Document;

/// <summary>A transform in progress: the draft the handles edit, and what it moves.</summary>
public sealed record TransformEdit(Guid LayerId, LayerTransform Draft, bool Persistent)
{
    /// <summary>Set when several layers are selected: the draft is the box around them all, and each follows it.</summary>
    public TransformGroup? Group { get; init; }
}

/// <summary>Where a moving box snapped, to draw a guide along.</summary>
public readonly record struct SnapGuides(IReadOnlyList<double> Xs, IReadOnlyList<double> Ys)
{
    public static readonly SnapGuides None = new([], []);
}

public sealed partial class EditorSession
{
    private (Guid Copy, Guid Source)? _transformDuplicate;

    /// <summary>The pending transform, or null. The canvas draws the document with this applied.</summary>
    public TransformEdit? TransformEdit { get; private set; }

    public SnapGuides SnapGuides { get; private set; } = SnapGuides.None;

    public bool CanTransform
    {
        get
        {
            if (!CanEditLayers)
            {
                return false;
            }

            // Several selected layers, or a folder's contents, transform together.
            if (TransformsAsGroup)
            {
                return GroupTransformMembers.Count > 0;
            }

            return ActiveLayer is { Asset: not null, IsGroup: false } layer && VisibleIds.Contains(layer.Id);
        }
    }

    /// <summary>Several layers selected, or a folder: the transform moves them (a folder, everything in it) together in one box.</summary>
    public bool TransformsAsGroup => SelectedLayerIds.Count > 1 || (SelectedLayerIds.Count == 1 && ActiveLayer is { IsGroup: true });

    /// <summary>What a group transform moves: the visible pixel layers selected and inside selected folders.</summary>
    public List<ImageLayer> GroupTransformMembers
    {
        get
        {
            if (!TransformsAsGroup || Document is null)
            {
                return [];
            }

            var parents = Layers.ToDictionary(l => l.Id, l => l.ParentId);
            var visible = VisibleIds;
            return Layers.Where(layer =>
            {
                if (layer.Asset is null || layer.IsGroup || !visible.Contains(layer.Id))
                {
                    return false;
                }

                Guid? current = layer.Id;
                for (var i = 0; i < LayerHierarchy.MaxDepth && current is { } id; i++)
                {
                    if (SelectedLayerIds.Contains(id))
                    {
                        return true;
                    }

                    current = parents.GetValueOrDefault(id);
                }

                return false;
            }).ToList();
        }
    }

    /// <summary>The upright box around <see cref="GroupTransformMembers"/>.</summary>
    public LayerTransform? GroupTransformBox
    {
        get
        {
            var points = GroupTransformMembers.SelectMany(m => Corners(m.Transform)).ToList();
            if (points.Count == 0)
            {
                return null;
            }

            var minX = points.Min(p => p.X);
            var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y);
            var maxY = points.Max(p => p.Y);
            return new LayerTransform(new Point(minX, minY), new Size(Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)));
        }
    }

    public static IReadOnlyList<Point> Corners(LayerTransform transform) =>
        [transform.PointAt(new Point(0, 0)), transform.PointAt(new Point(1, 0)), transform.PointAt(new Point(1, 1)), transform.PointAt(new Point(0, 1))];

    private HashSet<Guid> VisibleIds => LayerHierarchy.Entries(Layers).Where(e => e.Visible).Select(e => e.Layer.Id).ToHashSet();

    public void BeginTransform(bool persistent = true)
    {
        if (TransformEdit is not null || !CanTransform || ActiveLayer is not { } layer)
        {
            return;
        }

        if (TransformsAsGroup)
        {
            if (GroupTransformBox is not { } box)
            {
                return;
            }

            TransformEdit = new TransformEdit(layer.Id, box, persistent)
            {
                Group = new TransformGroup(box, GroupTransformMembers.ToDictionary(m => m.Id, m => m.Transform)),
            };
            Changed?.Invoke();
            return;
        }

        TransformEdit = new TransformEdit(layer.Id, layer.Transform, persistent);
        Changed?.Invoke();
    }

    public void PreviewTransform(LayerTransform value, SnapGuides? guides = null)
    {
        if (!value.IsValid || TransformEdit is null)
        {
            return;
        }

        TransformEdit = TransformEdit with { Draft = value };
        SnapGuides = guides ?? SnapGuides.None;
        Changed?.Invoke();
    }

    /// <summary>Alt-drag: a copy of a single layer is made and the drag moves the copy, all as one undo step.</summary>
    public void BeginDuplicateTransform()
    {
        if (_transformDuplicate is not null || TransformsAsGroup || ActiveLayerId is not { } source)
        {
            return;
        }

        CommitTransform();
        if (!CanTransform)
        {
            return;
        }

        BeginEdit("Duplicate Layer");
        DuplicateActiveLayer();
        if (ActiveLayerId is not { } copy || copy == source)
        {
            EndEdit();
            return;
        }

        _transformDuplicate = (copy, source);
        BeginTransform(persistent: false);
    }

    public void CommitTransform()
    {
        SnapGuides = SnapGuides.None;
        FinishOpacityEdit();
        if (TransformEdit is not { } edit)
        {
            return;
        }

        TransformEdit = null;
        try
        {
            if (edit.Group is { } group)
            {
                if (!edit.Draft.IsValid)
                {
                    return;
                }

                BeginEdit("Transform Layers");
                ReplaceLayers(Layers.Select(l =>
                {
                    if (!group.Originals.TryGetValue(l.Id, out var original))
                    {
                        return l;
                    }

                    var moved = original.Following(group.Box, edit.Draft);
                    if (!moved.IsValid)
                    {
                        return l;
                    }

                    return l with
                    {
                        Transform = moved,
                        Mask = l.Mask is { } mask ? mask with { Placement = mask.PlacementMovingLayer(original, moved) } : null,
                    };
                }));
                EndEdit();
                return;
            }

            if (!edit.Draft.IsValid || Layers.FirstOrDefault(l => l.Id == edit.LayerId) is not { } layer)
            {
                return;
            }

            BeginEdit("Transform Layer");
            Update(layer.Id, l => l with
            {
                Transform = edit.Draft,
                Mask = l.Mask is { } mask ? mask with { Placement = mask.PlacementMovingLayer(layer.Transform, edit.Draft) } : null,
            });
            EndEdit();
        }
        finally
        {
            if (_transformDuplicate is not null)
            {
                _transformDuplicate = null;
                EndEdit();
            }

            Changed?.Invoke();
        }
    }

    public void CancelTransform()
    {
        SnapGuides = SnapGuides.None;
        if (TransformEdit is null)
        {
            return;
        }

        TransformEdit = null;
        if (_transformDuplicate is { } duplicate)
        {
            ReplaceLayers(Layers.Where(l => l.Id != duplicate.Copy));
            ActiveLayerId = duplicate.Source;
            SelectedLayerIds = new HashSet<Guid> { duplicate.Source };
            _transformDuplicate = null;
            EndEdit();
        }

        Changed?.Invoke();
    }

    /// <summary>Pixels the transform places — what 100% scale draws 1:1. Null for a layer without pixels.</summary>
    public Size? TransformPixelSize
    {
        get
        {
            if (TransformEdit?.Group is { } group)
            {
                return group.Box.Size;
            }

            if (TransformEdit is null && TransformsAsGroup)
            {
                return GroupTransformBox?.Size;
            }

            return ActiveLayer?.Asset is { } asset ? new Size(asset.Width, asset.Height) : null;
        }
    }

    /// <summary>Where <paramref name="layer"/>'s transform handles sit: the pending draft, the group box, or the layer itself.</summary>
    public LayerTransform EditedTransform(ImageLayer layer)
    {
        if (TransformEdit is { } edit && edit.LayerId == layer.Id)
        {
            return edit.Draft;
        }

        if (TransformEdit is null && layer.Id == ActiveLayerId && TransformsAsGroup && GroupTransformBox is { } box)
        {
            return box;
        }

        return layer.Transform;
    }

    /// <summary>
    /// A layer's transform under the pending edit: the draft for the edited layer, carried along with
    /// the box for each layer of a group; null when the edit doesn't move it.
    /// </summary>
    public LayerTransform? PendingTransform(ImageLayer layer)
    {
        if (TransformEdit is not { } edit)
        {
            return null;
        }

        if (edit.Group is { } group)
        {
            return group.Originals.TryGetValue(layer.Id, out var original) ? original.Following(group.Box, edit.Draft) : null;
        }

        return edit.LayerId == layer.Id ? edit.Draft : null;
    }

    private (CanvasDocument Document, TransformEdit Edit, CanvasDocument Displayed)? _displayed;

    /// <summary>The document as the canvas should draw it: the pending transform applied to the layers it moves. Memoized per (document, edit).</summary>
    public CanvasDocument? DisplayedDocument
    {
        get
        {
            if (Document is not { } document || TransformEdit is not { } edit)
            {
                return Document;
            }

            if (_displayed is { } cached && ReferenceEquals(cached.Document, document) && ReferenceEquals(cached.Edit, edit))
            {
                return cached.Displayed;
            }

            var displayed = document with
            {
                Layers = document.Layers.Select(l => PendingTransform(l) is { } pending && pending != l.Transform ? l with { Transform = pending } : l).ToList(),
            };
            _displayed = (document, edit, displayed);
            return displayed;
        }
    }

    public void NudgeLayer(double dx, double dy)
    {
        var alreadyEditing = TransformEdit is not null;
        if (!alreadyEditing)
        {
            BeginTransform(persistent: false);
        }

        if (TransformEdit is not { } edit)
        {
            return;
        }

        PreviewTransform(edit.Draft with { Origin = edit.Draft.Origin.Offset(dx, dy) });
        if (!alreadyEditing)
        {
            CommitTransform();
        }
    }

    /// <summary>
    /// The topmost visible pixel layer with an opaque pixel under <paramref name="point"/>, for
    /// auto-select on click. Folders are never picked; layers inside them are.
    /// </summary>
    public ImageLayer? LayerAt(Point point)
    {
        foreach (var layer in Enumerable.Reverse(LayerHierarchy.VisibleLayers(Layers)))
        {
            if (layer.Asset is not { } asset || !layer.Transform.Contains(point))
            {
                continue;
            }

            var pixel = layer.Transform.PixelToDocument(asset.Width, asset.Height).Inverted().Apply(point);
            var x = (int)Math.Floor(pixel.X);
            var y = (int)Math.Floor(pixel.Y);
            if (x < 0 || y < 0 || x >= asset.Width || y >= asset.Height)
            {
                continue;
            }

            if (asset.Image.GetPixel(x, y).Alpha > 0)
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>Edges and centres a moving layer snaps to: the canvas and every other visible layer's upright box.</summary>
    public (List<double> Xs, List<double> Ys) SnapTargets(IReadOnlySet<Guid> excluding)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (Document is not { } document)
        {
            return (xs, ys);
        }

        xs.AddRange([0, document.Width / 2.0, document.Width]);
        ys.AddRange([0, document.Height / 2.0, document.Height]);
        foreach (var layer in LayerHierarchy.VisibleLayers(document.Layers))
        {
            if (excluding.Contains(layer.Id) || layer.Asset is null)
            {
                continue;
            }

            var corners = Corners(layer.Transform);
            var minX = corners.Min(c => c.X);
            var maxX = corners.Max(c => c.X);
            var minY = corners.Min(c => c.Y);
            var maxY = corners.Max(c => c.Y);
            xs.AddRange([minX, (minX + maxX) / 2, maxX]);
            ys.AddRange([minY, (minY + maxY) / 2, maxY]);
        }

        return (xs, ys);
    }
}
