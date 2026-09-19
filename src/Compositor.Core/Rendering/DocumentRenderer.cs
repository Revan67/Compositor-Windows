using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Kernels;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>
/// Flattens a document: visible non-folder layers bottom to top, each through its own mask, every
/// enclosing folder's mask, and its clipping mask if it has one. One instance serves one render;
/// it caches the coverage of clipping-mask sources for that render only.
/// </summary>
/// <remarks>
/// Clipping stacks — a base followed by the siblings clipped to it — share the base's alpha instead
/// of painting that alpha over itself: the base is drawn into a group, the group made opaque, the
/// clipped layers drawn on top, and the base's alpha restored before the group is composited with
/// the base's blend mode. Other clipping links (non-contiguous, or across folders) clip the layer
/// by the source's rendered coverage instead. Adjustment layers plug in here once ported.
/// </remarks>
public sealed class DocumentRenderer
{
    public const long MaxPixels = 100_000_000;

    private readonly Rect _bounds;
    private readonly IReadOnlyDictionary<Guid, ImageLayer> _byId;
    private readonly List<ImageLayer> _visible;
    private readonly Dictionary<Guid, SKBitmap> _coverage = [];
    private readonly HashSet<Guid> _visiting = [];
    private readonly Dictionary<Guid, List<ImageLayer>> _stacks = [];
    private readonly HashSet<Guid> _stacked = [];
    private readonly Dictionary<Guid, IReadOnlyList<MaskPlacement>> _folderMasks = [];

    /// <param name="document">What to draw.</param>
    /// <param name="bounds">The document region being rendered, in document pixels; the whole canvas by default.</param>
    public DocumentRenderer(CanvasDocument document, Rect? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        LayerHierarchy.Validate(document.Layers);
        LayerHierarchy.ValidateMaskLinks(document.Layers);
        _bounds = (bounds ?? document.Bounds).Integral();
        _byId = document.Layers.ToDictionary(l => l.Id);
        _visible = LayerHierarchy.VisibleLayers(document.Layers);
        PrepareStacks();
    }

    /// <summary>The document flattened to premultiplied BGRA at 1:1.</summary>
    public static SKBitmap Flatten(CanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Width is < 1 or > CanvasDocument.MaxDimension || document.Height is < 1 or > CanvasDocument.MaxDimension
            || (long)document.Width * document.Height > MaxPixels)
        {
            throw new InvalidOperationException("Rendering supports canvases up to 100 megapixels and 30,000 pixels per side.");
        }

        var bitmap = Bitmaps.Create(document.Width, document.Height, mask: false);
        using var canvas = new SKCanvas(bitmap);
        new DocumentRenderer(document).Draw(canvas);
        return bitmap;
    }

    /// <summary>Draws every visible layer onto <paramref name="canvas"/>, whose coordinates are document pixels.</summary>
    public void Draw(SKCanvas canvas)
    {
        foreach (var layer in _visible)
        {
            DrawComposite(layer, canvas);
        }
    }

    private void PrepareStacks()
    {
        for (var index = 0; index < _visible.Count; index++)
        {
            var @base = _visible[index];
            if (@base.MaskSourceId is not null)
            {
                continue;
            }

            var children = new List<ImageLayer>();
            for (var next = index + 1; next < _visible.Count; next++)
            {
                var child = _visible[next];
                if (child.MaskSourceId != @base.Id || child.ParentId != @base.ParentId)
                {
                    break;
                }

                children.Add(child);
            }

            if (children.Count == 0)
            {
                continue;
            }

            _stacks[@base.Id] = children;
            foreach (var child in children)
            {
                _stacked.Add(child.Id);
            }
        }
    }

    private void DrawComposite(ImageLayer layer, SKCanvas canvas)
    {
        if (_stacked.Contains(layer.Id))
        {
            return;
        }

        if (!_stacks.TryGetValue(layer.Id, out var children) || _bounds.IsEmpty || _bounds.Width * _bounds.Height > MaxPixels)
        {
            DrawClipped(layer, canvas);
            return;
        }

        var w = (int)_bounds.Width;
        var h = (int)_bounds.Height;
        using var group = Bitmaps.Create(w, h, mask: false);
        using var alpha = Bitmaps.Create(w, h, mask: true);
        using (var groupCanvas = new SKCanvas(group))
        {
            groupCanvas.Translate((float)-_bounds.MinX, (float)-_bounds.MinY);
            DrawOwn(layer, groupCanvas);
            PixelKernels.ExtractAlpha(group, alpha);
            PixelKernels.UnpremultiplyOpaque(group);
            foreach (var child in children)
            {
                DrawOwn(child, groupCanvas);
            }

            PixelKernels.RestoreAlpha(group, alpha);
        }

        using var paint = new SKPaint { BlendMode = layer.BlendMode.ToSK() };
        using var wrapped = group.AsImage();
        canvas.DrawImage(wrapped, (float)_bounds.MinX, (float)_bounds.MinY, Bitmaps.Nearest, paint);
    }

    /// <summary>
    /// The layer through its clipping-mask source's coverage, if it has one that is not a stack. The
    /// coverage is one more mask multiplied inside the layer's own saved layer, so its opacity and
    /// blend mode still composite against the canvas beneath.
    /// </summary>
    private void DrawClipped(ImageLayer layer, SKCanvas canvas)
    {
        if (layer.MaskSourceId is { } sourceId)
        {
            var coverage = Coverage(sourceId);
            if (coverage is null)
            {
                return;
            }

            DrawOwn(layer, canvas, new MaskPlacement(coverage, new LayerTransform(_bounds.Origin, _bounds.Size, Sampling: LayerSampling.Nearest)));
            return;
        }

        DrawOwn(layer, canvas);
    }

    /// <summary>The layer's own pixels through its mask, its folders' masks and <paramref name="extra"/>, with its opacity and blend mode.</summary>
    private void DrawOwn(ImageLayer layer, SKCanvas canvas, MaskPlacement? extra = null)
    {
        if (layer.Asset is not { } asset)
        {
            return;
        }

        var image = asset.Image;
        var mask = layer.Mask?.ClipImage(layer.Transform, image.Width, image.Height);
        var masks = FolderMasks(layer);
        if (extra is { } coverage)
        {
            masks = [.. masks, coverage];
        }

        LayerRenderer.Draw(canvas, image, layer.Transform, layer.Transform.Center, 1, layer.Opacity, layer.BlendMode, mask, masks);
    }

    /// <summary>Every enabled mask on the folders containing <paramref name="layer"/>, innermost first.</summary>
    private IReadOnlyList<MaskPlacement> FolderMasks(ImageLayer layer)
    {
        var key = layer.ParentId ?? Guid.Empty;
        if (_folderMasks.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = new List<MaskPlacement>();
        var folder = layer.ParentId;
        var depth = 0;
        while (folder is { } id && depth < LayerHierarchy.MaxDepth && _byId.TryGetValue(id, out var current))
        {
            if (current.Mask?.EnabledImage is { } image)
            {
                result.Add(new MaskPlacement(image, current.Transform));
            }

            folder = current.ParentId;
            depth++;
        }

        _folderMasks[key] = result;
        return result;
    }

    /// <summary>
    /// The alpha of <paramref name="id"/> drawn on its own (through its masks, and its own clipping
    /// source), as gray over <see cref="_bounds"/>. Null on a cycle, a chain past the limit, or an
    /// oversized region.
    /// </summary>
    private SKBitmap? Coverage(Guid id)
    {
        if (_coverage.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (_visiting.Contains(id) || _visiting.Count >= LayerHierarchy.MaxMaskChain || _bounds.IsEmpty
            || _bounds.Width * _bounds.Height > MaxPixels || !_byId.TryGetValue(id, out var source))
        {
            return null;
        }

        _visiting.Add(id);
        try
        {
            var w = (int)_bounds.Width;
            var h = (int)_bounds.Height;
            using var pixels = Bitmaps.Create(w, h, mask: false);
            var gray = Bitmaps.Create(w, h, mask: true);
            using (var canvas = new SKCanvas(pixels))
            {
                canvas.Translate((float)-_bounds.MinX, (float)-_bounds.MinY);
                DrawClipped(source, canvas);
            }

            PixelKernels.ExtractAlpha(pixels, gray);
            _coverage[id] = gray;
            return gray;
        }
        finally
        {
            _visiting.Remove(id);
        }
    }
}
