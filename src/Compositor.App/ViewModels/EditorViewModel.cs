using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Compositor.App.Diagnostics;
using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Raster;
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
    private Compositor.Core.Geometry.Rect? _marqueeDraft;
    private Point? _marqueeStart;
    private SelectionCombineMode _marqueeMode;
    private Point? _selectionMoveStart;
    private DocumentSelection? _selectionMoveOriginal;
    private DocumentSelection? _selectionPreview;
    private BrushStroke? _brushStroke;
    private CanvasDocument? _brushPreviewDocument;
    private double _brushSize = 24;
    private double _brushOpacityPercent = 100;
    private double _brushHardnessPercent = 100;
    private SKColor _brushColor = SKColors.Black;
    private DateTimeOffset _brushStarted;
    private int _brushPointCount;
    private Compositor.Core.Geometry.Rect _brushBounds;
    private Point _brushEndPoint;
    private Point? _lastBrushPoint;
    private Guid? _lastBrushLayerId;

    public EditorViewModel()
    {
        Session.Changed += OnSessionChanged;
        Session.DocumentResized += Fit;
        CropCommit = new RelayCommand(CommitCrop, () => Tool == EditorTool.Crop, "Crop.Commit");
        CropCancel = new RelayCommand(CancelCrop, () => Tool == EditorTool.Crop, "Crop.Cancel");
        SelectAll = new RelayCommand(SelectAllPixels, () => HasDocument, name: "Select.All");
        Deselect = new RelayCommand(() => Session.SetSelection(null, "Deselect"), () => Session.Document?.Selection is not null, name: "Select.Deselect");
        Undo = new RelayCommand(Session.Undo, () => Session.CanUndo, "Edit.Undo");
        Redo = new RelayCommand(Session.Redo, () => Session.CanRedo, "Edit.Redo");
        AddLayer = new RelayCommand(Session.AddBlankLayer, () => Session.CanEditLayers, "Layer.Add");
        AddFolder = new RelayCommand(Session.AddGroup, () => Session.CanEditLayers, "Layer.AddFolder");
        DuplicateLayer = new RelayCommand(Session.DuplicateActiveLayer, () => Session.ActiveLayer is { IsGroup: false }, "Layer.Duplicate");
        DeleteLayer = new RelayCommand(Session.DeleteSelectedLayers, () => Session.ActiveLayer is not null, "Layer.Delete");
        MoveLayerUp = new RelayCommand(() => Session.MoveActiveLayer(1), () => Session.CanMoveActiveLayer(1), "Layer.MoveUp");
        MoveLayerDown = new RelayCommand(() => Session.MoveActiveLayer(-1), () => Session.CanMoveActiveLayer(-1), "Layer.MoveDown");
        GroupLayers = new RelayCommand(Session.GroupSelectedLayers, () => Session.SelectedLayerIds.Count > 0, "Layer.Group");
        AddMask = new RelayCommand(() => Session.AddLayerMask(), () => Session.ActiveLayer is { Mask: null }, "Layer.AddMask");
        ToggleMask = new RelayCommand(Session.ToggleLayerMask, () => Session.ActiveLayer?.Mask is not null, "Layer.ToggleMask");
        DeleteMask = new RelayCommand(Session.DeleteLayerMask, () => Session.ActiveLayer?.Mask is not null, "Layer.DeleteMask");
        ToggleClipping = new RelayCommand(() => Session.ToggleClippingMask(Session.ActiveLayerId!.Value), () => Session.ActiveLayerId is { } id && Session.CanToggleClippingMask(id), "Layer.ToggleClipping");
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
    public RelayCommand SelectAll { get; }
    public RelayCommand Deselect { get; }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool != value)
            {
                AppLog.Info("Tool", $"Changed {_tool} -> {value}");
                Session.CommitTransform();
                if (_tool == EditorTool.Crop)
                {
                    Session.CancelCrop();
                }

                if (_tool == EditorTool.Marquee)
                {
                    CancelMarquee();
                }

                if (_tool is EditorTool.Brush or EditorTool.Eraser)
                {
                    CancelBrushStroke();
                }

                Set(ref _tool, value);
                Raise(nameof(ShowsTransformControls));
                Raise(nameof(ShowsCropControls));
                Raise(nameof(ShowsSelectionControls));
                Raise(nameof(ShowsBrushControls));
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

    public bool ShowsSelectionControls => Tool == EditorTool.Marquee && HasDocument;

    public bool ShowsBrushControls => Tool is EditorTool.Brush or EditorTool.Eraser && HasDocument;

    public double BrushSize
    {
        get => _brushSize;
        set => Set(ref _brushSize, Math.Clamp(value, 1, 2_000));
    }

    public double BrushOpacityPercent
    {
        get => _brushOpacityPercent;
        set => Set(ref _brushOpacityPercent, Math.Clamp(value, 1, 100));
    }

    public double BrushHardnessPercent
    {
        get => _brushHardnessPercent;
        set => Set(ref _brushHardnessPercent, Math.Clamp(value, 0, 100));
    }

    public string BrushColorHex
    {
        get => $"#{_brushColor.Red:X2}{_brushColor.Green:X2}{_brushColor.Blue:X2}";
        set
        {
            if (SKColor.TryParse(value, out var color) && color != _brushColor)
            {
                _brushColor = color.WithAlpha(255);
                Raise();
            }
        }
    }

    public bool BeginBrushStroke(Point point, bool straightLine = false)
    {
        if (Session.Document is not { } document || Session.ActiveLayer is not { IsGroup: false } layer || Session.IsMaskSelected)
        {
            return false;
        }

        _brushStroke = new BrushStroke(layer, document.Selection, Tool == EditorTool.Eraser ? BrushMode.Erase : BrushMode.Paint, BrushSize, BrushOpacityPercent / 100, _brushColor, BrushHardnessPercent / 100);
        _brushStarted = DateTimeOffset.UtcNow;
        var lineStart = straightLine && _lastBrushLayerId == layer.Id ? _lastBrushPoint : null;
        _brushPointCount = lineStart is null ? 1 : 2;
        _brushBounds = lineStart is { } start
            ? Compositor.Core.Geometry.Rect.FromEdges(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y), Math.Max(start.X, point.X), Math.Max(start.Y, point.Y))
            : new Compositor.Core.Geometry.Rect(point.X, point.Y, 0, 0);
        _brushEndPoint = point;
        Session.IsBusy = true;
        AppLog.Info("Paint", $"Stroke begin: mode={_brushStroke.Mode}; layer={layer.Id}; size={BrushSize:0.##}; hardness={BrushHardnessPercent:0.##}; opacity={BrushOpacityPercent:0.##}; color={BrushColorHex}; straight={lineStart is not null}; selection={document.Selection is not null}; x={point.X:0.##}; y={point.Y:0.##}");
        if (lineStart is { } previous)
        {
            _brushStroke.Add(previous);
        }
        _brushStroke.Add(point);
        RefreshBrushPreview(document, layer);
        return true;
    }

    public void ContinueBrushStroke(Point point)
    {
        if (_brushStroke is not { } stroke || Session.Document is not { } document || Session.ActiveLayer is not { } layer)
        {
            return;
        }

        stroke.Add(point);
        _brushPointCount++;
        _brushEndPoint = point;
        _brushBounds = Compositor.Core.Geometry.Rect.FromEdges(Math.Min(_brushBounds.MinX, point.X), Math.Min(_brushBounds.MinY, point.Y), Math.Max(_brushBounds.MaxX, point.X), Math.Max(_brushBounds.MaxY, point.Y));
        RefreshBrushPreview(document, layer);
    }

    public void CommitBrushStroke()
    {
        if (_brushStroke is not { } stroke)
        {
            return;
        }

        var mode = stroke.Mode;
        var asset = stroke.Commit(Session.ActiveLayer?.Name ?? "Layer");
        Session.IsBusy = false;
        _brushStroke = null;
        _brushPreviewDocument = null;
        _lastBrushPoint = _brushEndPoint;
        _lastBrushLayerId = stroke.LayerId;
        Session.ReplaceLayerAsset(stroke.LayerId, asset, stroke.Transform, mode == BrushMode.Erase ? "Erase Stroke" : "Brush Stroke");
        AppLog.Info("Paint", $"Stroke commit: mode={mode}; layer={stroke.LayerId}; points={_brushPointCount}; bounds={_brushBounds.X:0.##},{_brushBounds.Y:0.##},{_brushBounds.Width:0.##},{_brushBounds.Height:0.##}; elapsedMs={(DateTimeOffset.UtcNow - _brushStarted).TotalMilliseconds:0.##}");
    }

    public void CancelBrushStroke()
    {
        if (_brushStroke is null)
        {
            return;
        }

        _brushStroke.Dispose();
        AppLog.Info("Paint", $"Stroke cancelled: points={_brushPointCount}; elapsedMs={(DateTimeOffset.UtcNow - _brushStarted).TotalMilliseconds:0.##}");
        _brushStroke = null;
        _brushPreviewDocument = null;
        Session.IsBusy = false;
        _compositeOf = null;
        Raise(nameof(Composite));
    }

    private void RefreshBrushPreview(CanvasDocument document, ImageLayer layer)
    {
        var previewAsset = new ImportedImage(_brushStroke!.Preview, _brushStroke.Preview, layer.Name);
        _brushPreviewDocument = document with { Layers = document.Layers.Select(item => item.Id == layer.Id ? item with { Asset = previewAsset } : item).ToList() };
        _compositeOf = null;
        Raise(nameof(Composite));
    }

    public Compositor.Core.Geometry.Rect? SelectionFrame => MarqueeDraft ?? Session.Document?.Selection?.Bounds;

    public DocumentSelection? DisplayedSelection => _selectionPreview ?? Session.Document?.Selection;

    public bool IsSelectionGestureActive => MarqueeDraft is not null || _selectionMoveStart is not null;

    public Compositor.Core.Geometry.Rect? MarqueeDraft
    {
        get => _marqueeDraft;
        private set
        {
            if (Set(ref _marqueeDraft, value))
            {
                Raise(nameof(SelectionFrame));
            }
        }
    }

    public void BeginMarquee(Point point, SelectionCombineMode mode = SelectionCombineMode.Replace)
    {
        if (Session.Document is null)
        {
            return;
        }

        if (mode == SelectionCombineMode.Replace && Session.Document.Selection is { } selection && selection.Path.Contains((float)point.X, (float)point.Y))
        {
            _selectionMoveStart = point;
            _selectionMoveOriginal = selection;
            _selectionPreview = selection;
            Raise(nameof(DisplayedSelection));
            Raise(nameof(IsSelectionGestureActive));
            AppLog.Info("Selection", $"Move begin: x={point.X:0.##}; y={point.Y:0.##}");
            return;
        }

        _marqueeStart = point;
        _marqueeMode = mode;
        MarqueeDraft = new Compositor.Core.Geometry.Rect(point.X, point.Y, 0, 0);
        Raise(nameof(IsSelectionGestureActive));
        AppLog.Info("Selection", $"Marquee begin: x={point.X:0.##}; y={point.Y:0.##}");
    }

    public void UpdateMarquee(Point point, bool square)
    {
        if (_selectionMoveStart is { } moveStart && _selectionMoveOriginal is { } original && Session.Document is { } document)
        {
            _selectionPreview = original.Translated(point.X - moveStart.X, point.Y - moveStart.Y, document.Bounds);
            Raise(nameof(DisplayedSelection));
            return;
        }

        if (_marqueeStart is not { } start)
        {
            return;
        }

        var dx = point.X - start.X;
        var dy = point.Y - start.Y;
        if (square)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.CopySign(side, dx == 0 ? 1 : dx);
            dy = Math.CopySign(side, dy == 0 ? 1 : dy);
        }

        MarqueeDraft = Compositor.Core.Geometry.Rect.FromEdges(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Max(start.X, start.X + dx), Math.Max(start.Y, start.Y + dy));
    }

    public void CommitMarquee()
    {
        if (_selectionMoveStart is not null)
        {
            Session.SetSelection(_selectionPreview, "Move Selection");
            AppLog.Info("Selection", $"Move commit: bounds={_selectionPreview?.Bounds}");
        }
        else if (Session.Document is { } document && MarqueeDraft is { } draft)
        {
            var shape = DocumentSelection.Rectangle(draft, document.Bounds);
            Session.SetSelection(DocumentSelection.Combine(document.Selection, shape, _marqueeMode), $"Marquee {_marqueeMode}");
            AppLog.Info("Selection", $"Marquee commit: mode={_marqueeMode}; x={draft.X:0.##}; y={draft.Y:0.##}; width={draft.Width:0.##}; height={draft.Height:0.##}");
        }

        _marqueeStart = null;
        MarqueeDraft = null;
        ClearSelectionMove();
    }

    public void CancelMarquee()
    {
        if (IsSelectionGestureActive)
        {
            AppLog.Info("Selection", "Marquee cancelled");
        }

        _marqueeStart = null;
        MarqueeDraft = null;
        ClearSelectionMove();
    }

    public void NudgeSelection(double dx, double dy)
    {
        if (Session.Document is { Selection: { } selection } document)
        {
            Session.SetSelection(selection.Translated(dx, dy, document.Bounds), "Move Selection");
        }
    }

    private void ClearSelectionMove()
    {
        _selectionMoveStart = null;
        _selectionMoveOriginal = null;
        _selectionPreview = null;
        Raise(nameof(DisplayedSelection));
        Raise(nameof(IsSelectionGestureActive));
    }

    private void SelectAllPixels()
    {
        if (Session.Document is { } document)
        {
            Session.SetSelection(DocumentSelection.Rectangle(document.Bounds, document.Bounds), "Select All");
        }
    }

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
            if ((_brushPreviewDocument ?? Session.DisplayedDocument) is not { } document)
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

    public void Recover(ProjectSnapshot snapshot)
    {
        Session.OpenDocument(snapshot.ToDocument(), snapshot.Manifest.ActiveLayerId, recovered: true);
        Path = null;
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
        foreach (var command in new[] { Undo, Redo, AddLayer, AddFolder, DuplicateLayer, DeleteLayer, MoveLayerUp, MoveLayerDown, GroupLayers, AddMask, ToggleMask, DeleteMask, ToggleClipping, ZoomIn, ZoomOut, ActualSize, FitToWindow, CropCommit, CropCancel, SelectAll, Deselect })
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
        Raise(nameof(ShowsSelectionControls));
        Raise(nameof(ShowsBrushControls));
        Raise(nameof(SelectionFrame));
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
