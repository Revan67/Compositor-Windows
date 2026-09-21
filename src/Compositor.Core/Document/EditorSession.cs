using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;
using System.Diagnostics;

namespace Compositor.Core.Document;

/// <summary>
/// The editable document with its selection and history: every mutation is an undoable edit on an
/// immutable <see cref="CanvasDocument"/>. This is the headless core of the reference session —
/// tool state, busy flags and view state live with the UI, which reports them through
/// <see cref="IsBusy"/> so edits stay refused while a modal operation runs.
/// </summary>
public sealed partial class EditorSession
{
    private readonly HashSet<Guid> _collapsedGroupIds = [];
    private Guid? _opacityEditLayerId;

    public CanvasDocument? Document { get; private set; }

    public Guid? ActiveLayerId { get; private set; }

    /// <summary>Every selected layer; always includes <see cref="ActiveLayerId"/> when set.</summary>
    public IReadOnlySet<Guid> SelectedLayerIds { get; private set; } = new HashSet<Guid>();

    /// <summary>Whether the mask, rather than the pixels, of the active layer is the paint target.</summary>
    public bool IsMaskSelected { get; private set; }

    public DocumentHistory History { get; } = new();

    /// <summary>Set by the UI while an import, dialog, stroke or transform is in progress.</summary>
    public bool IsBusy { get; set; }

    public IReadOnlySet<Guid> CollapsedGroupIds => _collapsedGroupIds;

    public event Action? Changed;

    public bool IsModified => History.IsModified;
    public bool CanUndo => !IsBusy && History.CanUndo;
    public bool CanRedo => !IsBusy && History.CanRedo;
    public bool CanEditLayers => Document is not null && !IsBusy;
    public ImageLayer? ActiveLayer => Document?.Layers.FirstOrDefault(l => l.Id == ActiveLayerId);
    public IReadOnlyList<ImageLayer> Layers => Document?.Layers ?? [];

    /// <summary>Rows for the layer list: top first, collapsed folders closed.</summary>
    public List<LayerHierarchy.Entry> LayerRows => LayerHierarchy.Entries(Layers, topFirst: true, collapsed: _collapsedGroupIds);

    // MARK: History

    public void Undo()
    {
        var name = History.UndoName;
        if (CanUndo && History.Undo() is { } snapshot)
        {
            Restore(snapshot);
            Trace.TraceInformation($"History undo: {name}; undo={History.UndoCount}; redo={History.CanRedo}");
        }
    }

    public void Redo()
    {
        var name = History.RedoName;
        if (CanRedo && History.Redo() is { } snapshot)
        {
            Restore(snapshot);
            Trace.TraceInformation($"History redo: {name}; undo={History.UndoCount}; redo={History.CanRedo}");
        }
    }

    /// <summary>Nestable transaction boundary; a complete gesture groups its edits under one name.</summary>
    public void BeginEdit(string name)
    {
        if (!History.IsEditing)
        {
            Trace.TraceInformation($"Edit begin: {name}; activeLayer={ActiveLayerId}; layers={Layers.Count}");
        }

        History.Begin(name, Document, ActiveLayerId);
    }

    public void EndEdit()
    {
        var wasEditing = History.IsEditing;
        var before = History.UndoCount;
        History.End(Document, ActiveLayerId);
        if (wasEditing && !History.IsEditing)
        {
            Trace.TraceInformation($"Edit end: changed={History.UndoCount > before}; undo={History.UndoCount}; activeLayer={ActiveLayerId}; layers={Layers.Count}");
        }
        Changed?.Invoke();
    }

    private void Restore(DocumentHistory.Snapshot snapshot)
    {
        var keepMaskTarget = IsMaskSelected && ActiveLayerId == snapshot.ActiveLayerId;
        Document = snapshot.Document;
        ActiveLayerId = snapshot.ActiveLayerId;
        SelectedLayerIds = ActiveLayerId is { } id ? new HashSet<Guid> { id } : new HashSet<Guid>();
        IsMaskSelected = keepMaskTarget && ActiveLayer?.Mask is not null;
        Changed?.Invoke();
    }

    private void Replace(CanvasDocument? document)
    {
        Document = document;
        if (ActiveLayerId is { } active && document?.Layers.Any(l => l.Id == active) != true)
        {
            ActiveLayerId = null;
        }

        SelectedLayerIds = new HashSet<Guid>(SelectedLayerIds.Where(id => document?.Layers.Any(l => l.Id == id) == true));
    }

    private void ReplaceLayers(IEnumerable<ImageLayer> layers) => Replace(Document! with { Layers = layers.ToList() });

    private void Update(Guid id, Func<ImageLayer, ImageLayer> change) =>
        ReplaceLayers(Layers.Select(l => l.Id == id ? change(l) : l));

    // MARK: Document

    public void CreateDocument(int width, int height, bool emptyLayer = false)
    {
        if (IsBusy || width is < 1 or > CanvasDocument.MaxDimension || height is < 1 or > CanvasDocument.MaxDimension)
        {
            return;
        }

        BeginEdit("New Canvas");
        var layer = emptyLayer ? new ImageLayer("Layer 1", new Size(width, height)) : null;
        Document = new CanvasDocument(width, height, layer is null ? [] : [layer]);
        ActiveLayerId = layer?.Id;
        SelectedLayerIds = layer is null ? new HashSet<Guid>() : new HashSet<Guid> { layer.Id };
        IsMaskSelected = false;
        _collapsedGroupIds.Clear();
        EndEdit();
    }

    /// <summary>Installs a loaded project with clean history, as opening does.</summary>
    public void OpenDocument(CanvasDocument document, Guid? activeLayerId)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        ActiveLayerId = activeLayerId is { } id && document.Layers.Any(l => l.Id == id) ? id : null;
        SelectedLayerIds = ActiveLayerId is { } a ? new HashSet<Guid> { a } : new HashSet<Guid>();
        IsMaskSelected = false;
        _collapsedGroupIds.Clear();
        History.Reset();
        Changed?.Invoke();
    }

    public void MarkSaved() => History.MarkSaved();

    // MARK: Selection

    public void SelectLayer(Guid? id)
    {
        if (IsBusy)
        {
            return;
        }

        ActiveLayerId = id is { } value && Layers.Any(l => l.Id == value) ? value : null;
        SelectedLayerIds = ActiveLayerId is { } a ? new HashSet<Guid> { a } : new HashSet<Guid>();
        if (ActiveLayer?.Mask is null)
        {
            IsMaskSelected = false;
        }

        Changed?.Invoke();
    }

    public void SelectLayers(IEnumerable<Guid> ids, Guid? primary)
    {
        if (IsBusy)
        {
            return;
        }

        var valid = new HashSet<Guid>(ids.Where(id => Layers.Any(l => l.Id == id)));
        ActiveLayerId = primary is { } p && valid.Contains(p) ? p : Layers.FirstOrDefault(l => valid.Contains(l.Id))?.Id;
        SelectedLayerIds = valid;
        if (ActiveLayer?.Mask is null)
        {
            IsMaskSelected = false;
        }

        Changed?.Invoke();
    }

    /// <summary>Chooses the layer's pixels or its mask as the target of painting and edits.</summary>
    public void SelectLayerTarget(Guid id, bool mask)
    {
        SelectLayer(id);
        IsMaskSelected = mask && ActiveLayer?.Mask is not null;
        Changed?.Invoke();
    }

    // MARK: Layers

    public string NextLayerName(string prefix = "Layer")
    {
        var names = Layers.Select(l => l.Name).ToHashSet();
        var number = 1;
        while (names.Contains($"{prefix} {number}"))
        {
            number++;
        }

        return $"{prefix} {number}";
    }

    public void AddBlankLayer()
    {
        if (!CanEditLayers || Document is not { } document)
        {
            return;
        }

        var layer = new ImageLayer(NextLayerName(), document.Size) with { ParentId = ParentForNewLayer() };
        Insert(layer, "New Blank Layer");
    }

    /// <summary>Inserts pixels as a new layer above the active one (inside its folder), as one undo step.</summary>
    public void AddPixelLayer(SKBitmap image, Point origin, string name, string editName = "Add Layer", bool dropsSelection = true)
    {
        if (!CanEditLayers || Document is null)
        {
            return;
        }

        var asset = new ImportedImage(image, Project.ImageCodec.Thumbnail(image), name);
        var layer = new ImageLayer(asset, origin) with { Name = name, ParentId = ParentForNewLayer() };
        FinishOpacityEdit();
        BeginEdit(editName);
        InsertAboveActive(layer);
        if (dropsSelection)
        {
            Document = Document! with { Selection = null };
        }

        EndEdit();
    }

    private Guid? ParentForNewLayer() => ActiveLayer is { IsGroup: true } ? ActiveLayerId : ActiveLayer?.ParentId;

    private void Insert(ImageLayer layer, string editName)
    {
        BeginEdit(editName);
        InsertAboveActive(layer);
        EndEdit();
    }

    /// <summary>Just above the active layer; with a folder selected, at the top of that folder.</summary>
    private void InsertAboveActive(ImageLayer layer)
    {
        var layers = Layers.ToList();
        var insertion = ActiveLayerId is { } active && layers.FindIndex(l => l.Id == active) is var index && index >= 0 ? index + 1 : layers.Count;
        if (ActiveLayer is { IsGroup: true } && ActiveLayerId is { } folder)
        {
            var inside = LayerHierarchy.DescendantIds(layers, folder);
            var topmost = layers.FindLastIndex(l => inside.Contains(l.Id));
            insertion = Math.Max(insertion, topmost + 1);
            _collapsedGroupIds.Remove(folder);
        }

        if (layer.ParentId is { } parent)
        {
            _collapsedGroupIds.Remove(parent);
        }

        layers.Insert(Math.Min(insertion, layers.Count), layer);
        ReplaceLayers(layers);
        ActiveLayerId = layer.Id;
        SelectedLayerIds = new HashSet<Guid> { layer.Id };
    }

    public void DeleteLayer(Guid id)
    {
        if (!CanEditLayers || !Layers.Any(l => l.Id == id))
        {
            return;
        }

        FinishDeleting([id], "Delete Layer");
    }

    public void DeleteActiveLayer()
    {
        if (ActiveLayerId is { } id)
        {
            DeleteLayer(id);
        }
    }

    /// <summary>Deletes every selected layer as one undo step (a selected folder takes its contents).</summary>
    public void DeleteSelectedLayers()
    {
        if (!CanEditLayers)
        {
            return;
        }

        var ids = Layers.Select(l => l.Id).Where(SelectedLayerIds.Contains).ToList();
        if (ids.Count <= 1)
        {
            DeleteActiveLayer();
            return;
        }

        FinishDeleting(ids, "Delete Layers");
    }

    /// <summary>
    /// Removes the layers, their descendants and the clipping links that pointed at them; the
    /// active layer becomes the neighbour that took the first removed layer's place.
    /// </summary>
    private void FinishDeleting(IReadOnlyList<Guid> ids, string editName)
    {
        BeginEdit(editName);
        foreach (var id in ids)
        {
            var layers = Layers.ToList();
            var index = layers.FindIndex(l => l.Id == id);
            if (index < 0)
            {
                continue;
            }

            var removed = LayerHierarchy.DescendantIds(layers, id);
            removed.Add(id);
            var remaining = layers.Where(l => !removed.Contains(l.Id))
                .Select(l => l.MaskSourceId is { } source && removed.Contains(source) ? l with { MaskSourceId = null } : l)
                .ToList();
            ReplaceLayers(remaining);
            if (ActiveLayerId is null || removed.Contains(ActiveLayerId.Value))
            {
                ActiveLayerId = remaining.Count == 0 ? null : remaining[Math.Min(index, remaining.Count - 1)].Id;
                SelectedLayerIds = ActiveLayerId is { } a ? new HashSet<Guid> { a } : new HashSet<Guid>();
            }
        }

        EndEdit();
    }

    public void RenameLayer(Guid id, string name)
    {
        name = name.Trim();
        if (IsBusy || name.Length == 0 || !Layers.Any(l => l.Id == id))
        {
            return;
        }

        BeginEdit("Rename Layer");
        Update(id, l => l with { Name = name });
        EndEdit();
    }

    public void ToggleLayerVisibility(Guid id)
    {
        if (!CanEditLayers || Layers.FirstOrDefault(l => l.Id == id) is not { } layer)
        {
            return;
        }

        BeginEdit(layer.IsVisible ? "Hide Layer" : "Show Layer");
        Update(id, l => l with { IsVisible = !l.IsVisible });
        EndEdit();
    }

    /// <summary>
    /// Photoshop's eye swipe: pressing an eye shows or hides that layer, and dragging over other eyes
    /// gives them the same state, all as one undo step. Returns the state being painted, or null.
    /// </summary>
    public bool? BeginVisibilitySwipe(Guid id)
    {
        if (!CanEditLayers || Layers.FirstOrDefault(l => l.Id == id) is not { } layer)
        {
            return null;
        }

        var visible = !layer.IsVisible;
        BeginEdit(visible ? "Show Layer" : "Hide Layer");
        SetVisibilityInSwipe(id, visible);
        return visible;
    }

    public void SetVisibilityInSwipe(Guid id, bool visible)
    {
        if (Layers.FirstOrDefault(l => l.Id == id) is { } layer && layer.IsVisible != visible)
        {
            Update(id, l => l with { IsVisible = visible });
            Changed?.Invoke();
        }
    }

    public void EndVisibilitySwipe() => EndEdit();

    /// <summary>Moves the rows at <paramref name="offsets"/> (top-first list order) before <paramref name="destination"/>.</summary>
    public void ReorderLayers(IReadOnlyCollection<int> offsets, int destination)
    {
        if (!CanEditLayers)
        {
            return;
        }

        // List order is top-to-bottom; the compositor stores bottom-to-top.
        var list = Layers.Reverse().ToList();
        if (offsets.Count == 0 || offsets.Any(o => o < 0 || o >= list.Count) || destination < 0 || destination > list.Count)
        {
            return;
        }

        var moving = offsets.OrderBy(o => o).Select(o => list[o]).ToList();
        var movingIds = moving.Select(l => l.Id).ToHashSet();
        var before = list.Take(destination).Where(l => !movingIds.Contains(l.Id));
        var after = list.Skip(destination).Where(l => !movingIds.Contains(l.Id));
        var reordered = before.Concat(moving).Concat(after).ToList();
        BeginEdit("Reorder Layers");
        ReplaceLayers(Enumerable.Reverse(reordered));
        EndEdit();
    }

    public bool CanMoveActiveLayer(int offset)
    {
        if (!CanEditLayers || ActiveLayer is not { } active)
        {
            return false;
        }

        var siblings = Layers.Where(l => l.ParentId == active.ParentId).ToList();
        var index = siblings.FindIndex(l => l.Id == active.Id);
        return index >= 0 && index + offset >= 0 && index + offset < siblings.Count;
    }

    /// <summary>Swaps the active layer with the sibling <paramref name="offset"/> places up (+) or down (−).</summary>
    public void MoveActiveLayer(int offset)
    {
        if (!CanMoveActiveLayer(offset) || ActiveLayer is not { } active)
        {
            return;
        }

        var layers = Layers.ToList();
        var siblings = layers.Where(l => l.ParentId == active.ParentId).ToList();
        var index = siblings.FindIndex(l => l.Id == active.Id);
        var a = layers.FindIndex(l => l.Id == active.Id);
        var b = layers.FindIndex(l => l.Id == siblings[index + offset].Id);
        (layers[a], layers[b]) = (layers[b], layers[a]);
        BeginEdit("Reorder Layers");
        ReplaceLayers(layers);
        EndEdit();
    }

    public void DuplicateActiveLayer()
    {
        if (!CanEditLayers || ActiveLayer is not { IsGroup: false } layer)
        {
            return;
        }

        var layers = Layers.ToList();
        var index = layers.FindIndex(l => l.Id == layer.Id);
        var copy = layer with { Id = Guid.NewGuid(), Name = $"{layer.Name} copy" };
        BeginEdit("Duplicate Layer");
        layers.Insert(index + 1, copy);
        ReplaceLayers(layers);
        ActiveLayerId = copy.Id;
        SelectedLayerIds = new HashSet<Guid> { copy.Id };
        EndEdit();
    }

    /// <summary>
    /// A copy of the layer placed where it was dropped (inside <paramref name="parent"/>, above
    /// <paramref name="aboveTarget"/>, or at the very bottom), as one undo step. Folders aren't duplicated this way.
    /// </summary>
    public bool DuplicateLayer(Guid id, Guid? parent, Guid? aboveTarget = null, bool atBottom = false)
    {
        if (!CanEditLayers || Layers.FirstOrDefault(l => l.Id == id) is not { IsGroup: false } || !CanPlaceLayer(id, parent))
        {
            return false;
        }

        BeginEdit("Duplicate Layer");
        try
        {
            SelectLayer(id);
            DuplicateActiveLayer();
            return ActiveLayerId is { } copy && copy != id && PlaceLayer(copy, parent, aboveTarget, atBottom);
        }
        finally
        {
            EndEdit();
        }
    }

    // MARK: Appearance

    public void BeginOpacityEdit()
    {
        if (!CanEditLayers || _opacityEditLayerId is not null || ActiveLayerId is not { } id)
        {
            return;
        }

        BeginEdit("Layer Opacity");
        _opacityEditLayerId = id;
    }

    public void FinishOpacityEdit()
    {
        if (_opacityEditLayerId is null)
        {
            return;
        }

        _opacityEditLayerId = null;
        EndEdit();
    }

    public void SetLayerOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || !CanEditLayers || (_opacityEditLayerId ?? ActiveLayerId) is not { } id || !Layers.Any(l => l.Id == id))
        {
            return;
        }

        var standalone = _opacityEditLayerId is null;
        if (standalone)
        {
            BeginEdit("Layer Opacity");
        }

        Update(id, l => l with { Opacity = Math.Clamp(opacity, 0, 1) });
        if (standalone)
        {
            EndEdit();
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Sets every selected image layer's opacity as one undo step; folders are skipped.</summary>
    public void SetSelectedLayersOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || !CanEditLayers)
        {
            return;
        }

        FinishOpacityEdit();
        BeginEdit("Layer Opacity");
        ReplaceLayers(Layers.Select(l => SelectedLayerIds.Contains(l.Id) && !l.IsGroup ? l with { Opacity = Math.Clamp(opacity, 0, 1) } : l));
        EndEdit();
    }

    public void SetLayerBlendMode(LayerBlendMode mode)
    {
        if (!CanEditLayers || ActiveLayer is not { IsGroup: false } layer)
        {
            return;
        }

        FinishOpacityEdit();
        BeginEdit("Layer Blend Mode");
        Update(layer.Id, l => l with { BlendMode = mode });
        EndEdit();
    }

    // MARK: Folders

    public void AddGroup()
    {
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= Project.ProjectStore.MaxLayers)
        {
            return;
        }

        var group = new ImageLayer(NextLayerName("Folder"), document.Size) with { IsGroup = true, ParentId = ParentForNewLayer() };
        var layers = Layers.ToList();
        var insertion = ActiveLayerId is { } active && layers.FindIndex(l => l.Id == active) is var index && index >= 0 ? index + 1 : layers.Count;
        layers.Insert(insertion, group);
        if (!IsValid(layers))
        {
            return;
        }

        BeginEdit("New Folder");
        ReplaceLayers(layers);
        ActiveLayerId = group.Id;
        SelectedLayerIds = new HashSet<Guid> { group.Id };
        if (group.ParentId is { } parent)
        {
            _collapsedGroupIds.Remove(parent);
        }

        EndEdit();
    }

    /// <summary>Wraps the selected layers in a new folder at the topmost selected branch of their common parent.</summary>
    public void GroupSelectedLayers()
    {
        if (!CanEditLayers || Document is not { } document || document.Layers.Count >= Project.ProjectStore.MaxLayers)
        {
            return;
        }

        var byId = document.Layers.ToDictionary(l => l.Id);
        var selected = SelectedLayerIds.Where(byId.ContainsKey).ToHashSet();
        if (selected.Count == 0)
        {
            return;
        }

        List<Guid?> Ancestors(Guid id)
        {
            var result = new List<Guid?>();
            var parent = byId[id].ParentId;
            while (parent is { } p)
            {
                result.Add(p);
                parent = byId.GetValueOrDefault(p)?.ParentId;
            }

            result.Add(null);
            return result;
        }

        // A selected folder carries its subtree; selected descendants must not be pulled out of it.
        var rootIds = selected.Where(id => !Ancestors(id).Any(a => a is { } p && selected.Contains(p))).ToHashSet();
        var ordered = LayerHierarchy.Entries(document.Layers).Select(e => e.Layer.Id).Where(rootIds.Contains).ToList();
        Guid? parent = null;
        if (ordered.Count > 0)
        {
            parent = Ancestors(ordered[0]).FirstOrDefault(candidate => ordered.All(id => Ancestors(id).Contains(candidate)), null);
        }

        var group = new ImageLayer(NextLayerName("Folder"), document.Size) with { IsGroup = true, ParentId = parent };
        // Put the wrapper at the topmost selected branch in the common parent.
        var branches = ordered.Select(id =>
        {
            var branch = id;
            while (byId[branch].ParentId is { } next && next != parent)
            {
                branch = next;
            }

            return branch;
        }).ToHashSet();
        var highest = document.Layers.ToList().FindLastIndex(l => branches.Contains(l.Id));
        var insertion = highest >= 0 ? document.Layers.Take(highest + 1).Count(l => !rootIds.Contains(l.Id)) : document.Layers.Count;
        var layers = document.Layers.Where(l => !rootIds.Contains(l.Id)).ToList();
        layers.Insert(Math.Min(insertion, layers.Count), group);
        foreach (var id in ordered)
        {
            layers.Add(byId[id] with { ParentId = group.Id });
        }

        if (!IsValid(layers))
        {
            return;
        }

        BeginEdit("Group Layers");
        ReplaceLayers(layers);
        ActiveLayerId = group.Id;
        SelectedLayerIds = new HashSet<Guid> { group.Id };
        if (parent is { } p2)
        {
            _collapsedGroupIds.Remove(p2);
        }

        EndEdit();
    }

    public void ToggleGroupExpansion(Guid id)
    {
        if (IsBusy || Layers.FirstOrDefault(l => l.Id == id) is not { IsGroup: true })
        {
            return;
        }

        if (!_collapsedGroupIds.Remove(id))
        {
            if (ActiveLayerId is { } active && LayerHierarchy.DescendantIds(Layers, id).Contains(active))
            {
                SelectLayer(id);
            }

            _collapsedGroupIds.Add(id);
        }

        Changed?.Invoke();
    }

    public bool CanPlaceLayer(Guid id, Guid? parent)
    {
        if (!CanEditLayers || !Layers.Any(l => l.Id == id))
        {
            return false;
        }

        if (parent is not { } p)
        {
            return true;
        }

        return p != id && !LayerHierarchy.DescendantIds(Layers, id).Contains(p) && Layers.FirstOrDefault(l => l.Id == p) is { IsGroup: true };
    }

    /// <summary>Moves a layer into <paramref name="parent"/>, above <paramref name="aboveTarget"/> (a sibling), at the bottom, or at the top.</summary>
    public bool PlaceLayer(Guid id, Guid? parent, Guid? aboveTarget = null, bool atBottom = false)
    {
        if (!CanPlaceLayer(id, parent) || aboveTarget == id)
        {
            return false;
        }

        var layers = Layers.ToList();
        var index = layers.FindIndex(l => l.Id == id);
        var layer = layers[index] with { ParentId = parent };
        layers.RemoveAt(index);
        var insertion = atBottom ? 0 : layers.Count;
        if (aboveTarget is { } target)
        {
            var targetIndex = layers.FindIndex(l => l.Id == target && l.ParentId == parent);
            if (targetIndex < 0)
            {
                return false;
            }

            insertion = targetIndex + 1;
        }

        layers.Insert(insertion, layer);
        AdoptClipping(id, layers);
        ReleaseDetachedClipping(layers);
        if (!IsValid(layers))
        {
            return false;
        }

        BeginEdit("Move Layer");
        ReplaceLayers(layers);
        ActiveLayerId = id;
        SelectedLayerIds = new HashSet<Guid> { id };
        if (parent is { } p)
        {
            _collapsedGroupIds.Remove(p);
        }

        EndEdit();
        return true;
    }

    public void MoveActiveLayerOutOfGroup()
    {
        if (ActiveLayer is { ParentId: { } parent } layer && Layers.FirstOrDefault(l => l.Id == parent) is { } group)
        {
            PlaceLayer(layer.Id, group.ParentId, aboveTarget: group.Id);
        }
    }

    // MARK: Masks

    public bool CanEditMask => CanEditLayers && ActiveLayer is not null;

    public void AddLayerMask(bool revealing = true)
    {
        if (!CanEditMask || ActiveLayer is not { Mask: null } layer)
        {
            return;
        }

        FinishOpacityEdit();
        BeginEdit(revealing ? "Add Reveal-All Mask" : "Add Hide-All Mask");
        Update(layer.Id, l => l with { Mask = LayerMask.Solid(revealing) });
        IsMaskSelected = true;
        EndEdit();
    }

    public void ToggleLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: { } mask } layer)
        {
            return;
        }

        FinishOpacityEdit();
        BeginEdit(mask.IsEnabled ? "Disable Layer Mask" : "Enable Layer Mask");
        Update(layer.Id, l => l with { Mask = l.Mask! with { IsEnabled = !l.Mask.IsEnabled } });
        EndEdit();
    }

    public void DeleteLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: not null } layer)
        {
            return;
        }

        FinishOpacityEdit();
        BeginEdit("Delete Layer Mask");
        Update(layer.Id, l => l with { Mask = null });
        IsMaskSelected = false;
        EndEdit();
    }

    /// <summary>Replaces the active layer's mask pixels (a paint commit, fill or invert).</summary>
    public void ReplaceLayerMask(Guid id, ImportedImage asset, string editName)
    {
        if (!CanEditLayers || Layers.FirstOrDefault(l => l.Id == id) is not { } layer)
        {
            return;
        }

        BeginEdit(editName);
        Update(id, l => l with { Mask = layer.Mask?.Replacing(asset) ?? new LayerMask(asset) });
        EndEdit();
    }

    public void ToggleMaskLink(Guid id)
    {
        if (!CanEditLayers || Layers.FirstOrDefault(l => l.Id == id) is not { Mask: { } mask })
        {
            return;
        }

        FinishOpacityEdit();
        BeginEdit(mask.IsLinked ? "Unlink Layer Mask" : "Link Layer Mask");
        Update(id, l => l with { Mask = l.Mask! with { IsLinked = !l.Mask.IsLinked } });
        EndEdit();
    }

    /// <summary>Replaces the layer's pixels (a paint commit, filter or adjustment result), keeping everything else.</summary>
    public void ReplaceLayerAsset(Guid id, ImportedImage asset, LayerTransform transform, string editName)
    {
        if (!CanEditLayers || !Layers.Any(l => l.Id == id))
        {
            return;
        }

        BeginEdit(editName);
        Update(id, l => l with { Asset = asset, Transform = transform });
        EndEdit();
    }

    public void SetLayerTransform(Guid id, LayerTransform transform, string editName = "Transform")
    {
        if (!CanEditLayers || !transform.IsValid || Layers.FirstOrDefault(l => l.Id == id) is not { } layer)
        {
            return;
        }

        BeginEdit(editName);
        Update(id, l => l with
        {
            Transform = transform,
            Mask = l.Mask is { } mask ? mask with { Placement = mask.PlacementMovingLayer(layer.Transform, transform) } : null,
        });
        EndEdit();
    }

    public void SetSelection(DocumentSelection? selection, string editName = "Select")
    {
        if (!CanEditLayers)
        {
            return;
        }

        BeginEdit(editName);
        Document = Document! with { Selection = selection };
        EndEdit();
    }

    // MARK: Clipping masks

    public bool CanLinkMask(Guid source, Guid target)
    {
        if (!CanEditLayers || source == target)
        {
            return false;
        }

        if (Layers.FirstOrDefault(l => l.Id == source) is not { IsGroup: false } || Layers.FirstOrDefault(l => l.Id == target) is not { IsGroup: false })
        {
            return false;
        }

        var trial = Layers.Select(l => l.Id == target ? l with { MaskSourceId = source } : l).ToList();
        return IsValid(trial);
    }

    public bool LinkMask(Guid source, Guid target)
    {
        if (!CanLinkMask(source, target))
        {
            return false;
        }

        if (Layers.First(l => l.Id == target).MaskSourceId == source)
        {
            return true;
        }

        BeginEdit("Create Clipping Mask");
        Update(target, l => l with { MaskSourceId = source });
        EndEdit();
        return true;
    }

    /// <summary>
    /// Releases a clipping mask. Releasing a base releases the clipped children above it that share
    /// that base; releasing a child leaves lower siblings untouched.
    /// </summary>
    public void RemoveLiveMask(Guid target)
    {
        if (!CanEditLayers || Layers.FirstOrDefault(l => l.Id == target) is not { MaskSourceId: { } source } targetLayer)
        {
            return;
        }

        var siblings = Layers.Where(l => l.ParentId == targetLayer.ParentId).ToList();
        var targetIndex = siblings.FindIndex(l => l.Id == target);
        var releases = siblings.Skip(targetIndex).TakeWhile(l => l.Id == target || l.MaskSourceId == source).Select(l => l.Id).ToHashSet();
        BeginEdit("Release Clipping Mask");
        ReplaceLayers(Layers.Select(l => releases.Contains(l.Id) ? l with { MaskSourceId = null } : l));
        EndEdit();
    }

    /// <summary>
    /// A layer dropped into the middle of a clipping group joins it, as in Photoshop: dropped between
    /// a base and a layer clipped to it, it is clipped to that base too.
    /// </summary>
    internal static void AdoptClipping(Guid id, List<ImageLayer> layers)
    {
        var layer = layers.FirstOrDefault(l => l.Id == id);
        if (layer is null || layer.IsGroup)
        {
            return;
        }

        var siblings = layers.Where(l => l.ParentId == layer.ParentId).ToList();
        var index = siblings.FindIndex(l => l.Id == id);
        if (index <= 0 || index + 1 >= siblings.Count || siblings[index + 1].MaskSourceId is not { } source || source == id)
        {
            return;
        }

        var below = siblings[index - 1];
        if (below.Id != source && below.MaskSourceId != source)
        {
            return;
        }

        var position = layers.FindIndex(l => l.Id == id);
        layers[position] = layers[position] with { MaskSourceId = source };
    }

    /// <summary>An unclipped layer left in the middle of a clipping group breaks it up: links above it are released.</summary>
    internal static void ReleaseDetachedClipping(List<ImageLayer> layers)
    {
        var release = new HashSet<Guid>();
        foreach (var stack in layers.GroupBy(l => l.ParentId))
        {
            Guid? @base = null;
            foreach (var layer in stack)
            {
                if (layer.MaskSourceId is { } source)
                {
                    if (source != @base)
                    {
                        release.Add(layer.Id);
                        @base = layer.Id;
                    }
                }
                else
                {
                    @base = layer.IsGroup ? null : layer.Id;
                }
            }
        }

        for (var i = 0; i < layers.Count; i++)
        {
            if (release.Contains(layers[i].Id))
            {
                layers[i] = layers[i] with { MaskSourceId = null };
            }
        }
    }

    private static bool IsValid(IReadOnlyList<ImageLayer> layers)
    {
        try
        {
            LayerHierarchy.Validate(layers);
            LayerHierarchy.ValidateMaskLinks(layers);
            return true;
        }
        catch (InvalidProjectException)
        {
            return false;
        }
    }
}

public static class EditorSessionClipping
{
    public static bool CanToggleClippingMask(this EditorSession session, Guid id)
    {
        if (!session.CanEditLayers || session.Layers.FirstOrDefault(l => l.Id == id) is not { IsGroup: false } layer)
        {
            return false;
        }

        if (layer.MaskSourceId is not null)
        {
            return true;
        }

        var siblings = session.Layers.Where(l => l.ParentId == layer.ParentId).ToList();
        var index = siblings.FindIndex(l => l.Id == id);
        if (index <= 0 || siblings[index - 1].IsGroup)
        {
            return false;
        }

        return session.CanLinkMask(siblings[index - 1].MaskSourceId ?? siblings[index - 1].Id, id);
    }

    /// <summary>Alt-click: clips to the next lower sibling, sharing its base when it is already clipped; or releases.</summary>
    public static void ToggleClippingMask(this EditorSession session, Guid id)
    {
        if (!session.CanEditLayers || session.Layers.FirstOrDefault(l => l.Id == id) is not { IsGroup: false } layer)
        {
            return;
        }

        if (layer.MaskSourceId is not null)
        {
            session.RemoveLiveMask(id);
            return;
        }

        var siblings = session.Layers.Where(l => l.ParentId == layer.ParentId).ToList();
        var index = siblings.FindIndex(l => l.Id == id);
        if (index <= 0 || siblings[index - 1] is { IsGroup: true })
        {
            return;
        }

        var below = siblings[index - 1];
        session.LinkMask(below.MaskSourceId ?? below.Id, id);
    }
}
