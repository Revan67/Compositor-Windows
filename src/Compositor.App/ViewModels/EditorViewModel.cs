using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.App.ViewModels;

/// <summary>
/// The window's state: one <see cref="EditorSession"/>, its viewport, the cached composite the canvas
/// draws, and the rows the layers panel shows. Everything the menus can do is a command here.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private readonly Dictionary<SKBitmap, Bitmap> _thumbnails = new(ReferenceEqualityComparer.Instance);
    private CanvasViewport _viewport = new();
    private SKBitmap? _composite;
    private SKBitmap? _retired;
    private CanvasDocument? _compositeOf;
    private string? _path;
    private LayerRowViewModel? _selectedRow;
    private bool _syncingSelection;
    private EditorTool _tool = EditorTool.Move;

    public EditorViewModel()
    {
        Session.Changed += OnSessionChanged;
        Session.DocumentResized += Fit;
        CropCommit = new RelayCommand(CommitCrop, () => Tool == EditorTool.Crop);
        CropCancel = new RelayCommand(CancelCrop, () => Tool == EditorTool.Crop);
        Undo = new RelayCommand(Session.Undo, () => Session.CanUndo);
        Redo = new RelayCommand(Session.Redo, () => Session.CanRedo);
        AddLayer = new RelayCommand(Session.AddBlankLayer, () => Session.CanEditLayers);
        AddFolder = new RelayCommand(Session.AddGroup, () => Session.CanEditLayers);
        DuplicateLayer = new RelayCommand(Session.DuplicateActiveLayer, () => Session.ActiveLayer is { IsGroup: false });
        DeleteLayer = new RelayCommand(Session.DeleteSelectedLayers, () => Session.ActiveLayer is not null);
        MoveLayerUp = new RelayCommand(() => Session.MoveActiveLayer(1), () => Session.CanMoveActiveLayer(1));
        MoveLayerDown = new RelayCommand(() => Session.MoveActiveLayer(-1), () => Session.CanMoveActiveLayer(-1));
        GroupLayers = new RelayCommand(Session.GroupSelectedLayers, () => Session.SelectedLayerIds.Count > 0);
        AddMask = new RelayCommand(() => Session.AddLayerMask(), () => Session.ActiveLayer is { Mask: null });
        ToggleMask = new RelayCommand(Session.ToggleLayerMask, () => Session.ActiveLayer?.Mask is not null);
        DeleteMask = new RelayCommand(Session.DeleteLayerMask, () => Session.ActiveLayer?.Mask is not null);
        ToggleClipping = new RelayCommand(() => Session.ToggleClippingMask(Session.ActiveLayerId!.Value), () => Session.ActiveLayerId is { } id && Session.CanToggleClippingMask(id));
        ZoomIn = new RelayCommand(() => ZoomBy(2), () => Session.Document is not null);
        ZoomOut = new RelayCommand(() => ZoomBy(0.5), () => Session.Document is not null);
        ActualSize = new RelayCommand(() => SetZoom(1), () => Session.Document is not null);
        FitToWindow = new RelayCommand(Fit, () => Session.Document is not null);
    }

    public EditorSession Session { get; } = new();

    public TransformInspectorViewModel Inspector => _inspector ??= new TransformInspectorViewModel(Session);

    private TransformInspectorViewModel? _inspector;

    public ObservableCollection<LayerRowViewModel> Rows { get; } = [];

    public IReadOnlyList<LayerBlendMode> BlendModes { get; } = Enum.GetValues<LayerBlendMode>();

    public RelayCommand Undo { get; }
    public RelayCommand Redo { get; }
    public RelayCommand AddLayer { get; }
    public RelayCommand AddFolder { get; }
    public RelayCommand DuplicateLayer { get; }
    public RelayCommand DeleteLayer { get; }
    public RelayCommand MoveLayerUp { get; }
    public RelayCommand MoveLayerDown { get; }
    public RelayCommand GroupLayers { get; }
    public RelayCommand AddMask { get; }
    public RelayCommand ToggleMask { get; }
    public RelayCommand DeleteMask { get; }
    public RelayCommand ToggleClipping { get; }
    public RelayCommand ZoomIn { get; }
    public RelayCommand ZoomOut { get; }
    public RelayCommand ActualSize { get; }
    public RelayCommand FitToWindow { get; }
    public RelayCommand CropCommit { get; }
    public RelayCommand CropCancel { get; }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool != value)
            {
                Session.CommitTransform();
                if (_tool == EditorTool.Crop)
                {
                    Session.CancelCrop();
                }

                Set(ref _tool, value);
                Raise(nameof(ShowsTransformControls));
                Raise(nameof(ShowsCropControls));
                Raise(nameof(OverlayGeometry));
            }
        }
    }

    /// <summary>The transform box and handles for the active layer (or the selection's box), in view coordinates.</summary>
    public TransformOverlayGeometry? OverlayGeometry
    {
        get
        {
            if (Tool != EditorTool.Move || Session.Document is not { } document || !Session.CanTransform || Session.ActiveLayer is not { } layer)
            {
                return null;
            }

            return new TransformOverlayGeometry(Session.EditedTransform(layer), Viewport, document.Size);
        }
    }

    public bool ShowsTransformControls => Tool == EditorTool.Move && Session.CanTransform;

    public bool ShowsCropControls => Tool == EditorTool.Crop && HasDocument;

    public IReadOnlyList<string> CropRatioChoices => EditorSession.CropRatioChoices;

    public string CropRatioChoice
    {
        get => Session.CropRatioChoice;
        set => Session.SetCropRatioChoice(value);
    }

    /// <summary>The crop frame while the Crop tool is active: what was dragged, else the whole canvas.</summary>
    public Compositor.Core.Geometry.Rect? CropFrame => Tool == EditorTool.Crop && Session.Document is { } d ? Session.CropRect ?? d.Bounds : null;

    public void CommitCrop()
    {
        Session.CommitCrop();
        Tool = EditorTool.Move;
    }

    public void CancelCrop() => Tool = EditorTool.Move;

    public CanvasViewport Viewport
    {
        get => _viewport;
        set
        {
            if (Set(ref _viewport, value))
            {
                Raise(nameof(ZoomPercent));
                Raise(nameof(OverlayGeometry));
            }
        }
    }

    public string ZoomPercent => Session.Document is null ? string.Empty : $"{Math.Round(_viewport.Zoom * 100, _viewport.Zoom < 0.1 ? 1 : 0)}%";

    /// <summary>The path this document was opened from or saved to; null for a new canvas.</summary>
    public string? Path
    {
        get => _path;
        set
        {
            if (Set(ref _path, value))
            {
                Raise(nameof(Title));
            }
        }
    }

    public string Title => (Session.Document is null ? "Compositor" : $"{(Path is null ? "Untitled" : System.IO.Path.GetFileNameWithoutExtension(Path))}{(Session.IsModified ? " •" : string.Empty)} — Compositor");

    public bool HasDocument => Session.Document is not null;

    public string UndoLabel => Session.History.CanUndo ? $"Undo {Session.History.UndoName}" : "Undo";
    public string RedoLabel => Session.History.CanRedo ? $"Redo {Session.History.RedoName}" : "Redo";

    public LayerRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value) || _syncingSelection || value is null)
            {
                return;
            }

            Session.SelectLayer(value.Id);
        }
    }

    public ImageLayer? ActiveLayer => Session.ActiveLayer;

    public double ActiveOpacityPercent
    {
        get => (Session.ActiveLayer?.Opacity ?? 1) * 100;
        set => Session.SetLayerOpacity(value / 100);
    }

    public LayerBlendMode ActiveBlendMode
    {
        get => Session.ActiveLayer?.BlendMode ?? LayerBlendMode.Normal;
        set
        {
            if (Session.ActiveLayer is { } layer && layer.BlendMode != value)
            {
                Session.SetLayerBlendMode(value);
            }
        }
    }

    public bool CanEditAppearance => Session.ActiveLayer is { IsGroup: false };

    /// <summary>The document flattened at 1:1, rebuilt only when the document changes.</summary>
    public SKBitmap? Composite
    {
        get
        {
            if (Session.DisplayedDocument is not { } document)
            {
                return null;
            }

            if (!ReferenceEquals(_compositeOf, document))
            {
                var next = DocumentRenderer.Flatten(document);
                // A render pass already queued may still reference the previous composite, so it outlives one swap.
                _retired?.Dispose();
                _retired = _composite;
                _composite = next;
                _compositeOf = document;
            }

            return _composite;
        }
    }

    // MARK: Documents

    public void NewCanvas(int width, int height)
    {
        Session.CreateDocument(width, height, emptyLayer: true);
        Path = null;
        Fit();
    }

    public void Open(string path)
    {
        var snapshot = ProjectStore.Load(path);
        Session.OpenDocument(snapshot.ToDocument(), snapshot.Manifest.ActiveLayerId);
        Path = path;
        Fit();
    }

    public void Save(string path)
    {
        ProjectStore.Save(ProjectSnapshot.From(Session.Document!, Session.ActiveLayerId), path);
        Session.MarkSaved();
        Path = path;
        OnSessionChanged();
    }

    /// <summary>Imports each file as a layer above the active one, centred (or at <paramref name="at"/>), as one undo step.</summary>
    public List<string> Import(IEnumerable<string> paths, Point? at = null)
    {
        var failures = new List<string>();
        if (Session.Document is not { } document)
        {
            return failures;
        }

        var budget = ImageCodec.PixelBudget - document.Layers.Sum(l => (long)(l.Asset?.Width ?? 0) * (l.Asset?.Height ?? 0));
        Session.BeginEdit("Import Images");
        foreach (var path in paths)
        {
            try
            {
                var asset = ImageCodec.Decode(path, budget);
                budget -= (long)asset.Width * asset.Height;
                var centre = at ?? new Point(document.Width / 2.0, document.Height / 2.0);
                var origin = new Point(Math.Round(centre.X - (asset.Width / 2.0)), Math.Round(centre.Y - (asset.Height / 2.0)));
                Session.AddPixelLayer(asset.Image, origin, asset.Name, "Import Images");
            }
            catch (ImageImportException e)
            {
                failures.Add($"{System.IO.Path.GetFileName(path)}: {e.Message}");
            }
        }

        Session.EndEdit();
        return failures;
    }

    public byte[] ExportPng() => ImageCodec.EncodePng(Composite!, Session.Document!.Resolution);

    public byte[] ExportJpeg(JpegOptions options) => ImageCodec.EncodeJpeg(Composite!, options, Session.Document!.Resolution);

    // MARK: Viewport

    public void Fit()
    {
        if (Session.Document is { } document)
        {
            Viewport = Viewport.Fit(document.Size);
        }
    }

    public void ZoomBy(double factor) => SetZoom(Viewport.Zoom * factor);

    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (Session.Document is { } document)
        {
            Viewport = Viewport.Zoomed(zoom, anchor ?? Viewport.Center, document.Size);
        }
    }

    // MARK: Rows

    private void OnSessionChanged()
    {
        RebuildRows();
        foreach (var command in new[] { Undo, Redo, AddLayer, AddFolder, DuplicateLayer, DeleteLayer, MoveLayerUp, MoveLayerDown, GroupLayers, AddMask, ToggleMask, DeleteMask, ToggleClipping, ZoomIn, ZoomOut, ActualSize, FitToWindow, CropCommit, CropCancel })
        {
            command.Refresh();
        }

        Raise(nameof(Composite));
        Raise(nameof(Title));
        Raise(nameof(HasDocument));
        Raise(nameof(UndoLabel));
        Raise(nameof(RedoLabel));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveOpacityPercent));
        Raise(nameof(ActiveBlendMode));
        Raise(nameof(CanEditAppearance));
        Raise(nameof(ZoomPercent));
        Raise(nameof(OverlayGeometry));
        Raise(nameof(ShowsTransformControls));
        Raise(nameof(ShowsCropControls));
        Raise(nameof(CropRatioChoice));
        Raise(nameof(CropFrame));
        Inspector.Refresh();
    }

    private void RebuildRows()
    {
        var live = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        var rows = Session.LayerRows.Select(entry =>
        {
            var source = entry.Layer.Asset?.Thumbnail ?? entry.Layer.Mask?.Asset.Thumbnail;
            Bitmap? thumbnail = null;
            if (source is not null)
            {
                live.Add(source);
                if (!_thumbnails.TryGetValue(source, out thumbnail))
                {
                    _thumbnails[source] = thumbnail = Interop.ToAvalonia(source);
                }
            }

            return new LayerRowViewModel(entry, thumbnail, entry.Layer.Id == Session.ActiveLayerId, Session.SelectedLayerIds.Contains(entry.Layer.Id), Session.CollapsedGroupIds.Contains(entry.Layer.Id));
        }).ToList();

        foreach (var stale in _thumbnails.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _thumbnails[stale].Dispose();
            _thumbnails.Remove(stale);
        }

        _syncingSelection = true;
        try
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            SelectedRow = rows.FirstOrDefault(r => r.IsActive);
        }
        finally
        {
            _syncingSelection = false;
        }
    }
}
