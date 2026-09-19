using Compositor.Core.Geometry;
using SkiaSharp;

namespace Compositor.Core.Raster;

/// <summary>A complete replacement tile: every pixel inside <see cref="Rect"/>, transparent ones included.</summary>
public sealed record RasterPatch(Rect Rect, SKBitmap Image);

/// <summary>
/// Immutable sparse raster. Paint commits share untouched tiles with their source. A contiguous
/// bitmap is materialized only when a consumer (export or an image-processing operation) actually
/// asks for the bytes, never on mouse-up.
/// </summary>
public sealed class RasterSnapshot
{
    /// <summary>Spatial-index cell size for the patch handoff; also the brush tile size.</summary>
    public const int TileSize = 256;

    private readonly Lock _lock = new();
    private SKBitmap? _materialized;

    public RasterSnapshot(int width, int height, SKBitmap? @base, Rect baseRect, IReadOnlyList<RasterPatch> patches, bool isMask = false, Point? alignment = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Base = @base;
        BaseRect = baseRect;
        Patches = patches;
        IsMask = isMask;
        Alignment = alignment ?? baseRect.Origin;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The pixels underneath the patches, placed at <see cref="BaseRect"/>; null for a raster painted from nothing.</summary>
    public SKBitmap? Base { get; }
    public Rect BaseRect { get; }
    public IReadOnlyList<RasterPatch> Patches { get; }
    public bool IsMask { get; }

    /// <summary>
    /// Where this raster's halving grids start (see the tiled renderer): its base's origin, or for
    /// a raster painted from nothing, the grid of the stroke that made it — carried across commits
    /// so they never shift.
    /// </summary>
    public Point Alignment { get; }

    public bool HasMaterializedPixels
    {
        get
        {
            lock (_lock)
            {
                return _materialized is not null;
            }
        }
    }

    /// <summary>The contiguous pixels, drawn on first access and cached. Read-only by convention.</summary>
    public SKBitmap Pixels
    {
        get
        {
            lock (_lock)
            {
                if (_materialized is null)
                {
                    var bitmap = Bitmaps.Create(Width, Height, IsMask);
                    using var canvas = new SKCanvas(bitmap);
                    Draw(new Rect(0, 0, Width, Height), canvas);
                    _materialized = bitmap;
                }

                return _materialized;
            }
        }
    }

    /// <summary>
    /// A new snapshot with <paramref name="additions"/> laid over <paramref name="source"/>'s raster (or
    /// its contiguous image, for a first commit). New patches are complete replacement tiles, so
    /// older patches are split at their edges to keep the display list disjoint and flat.
    /// </summary>
    /// <param name="source">The asset being painted on, or null for a blank layer.</param>
    /// <param name="sourceRect">Where the source's pixels sit, in the same space as <paramref name="additions"/> and <paramref name="crop"/>.</param>
    /// <param name="additions">The tiles this commit replaces, in stroke space.</param>
    /// <param name="crop">The new raster's extent in stroke space; its size is the new pixel size.</param>
    /// <param name="isMask">Gray coverage rather than colour.</param>
    public static RasterSnapshot Replacing(ImportedImage? source, Rect sourceRect, IReadOnlyList<RasterPatch> additions, Rect crop, bool isMask = false)
    {
        var old = source?.Raster;
        var dx = sourceRect.MinX - crop.MinX;
        var dy = sourceRect.MinY - crop.MinY;
        var patches = (old?.Patches ?? []).Select(p => p with { Rect = p.Rect.Offset(dx, dy) }).ToList();
        var placed = additions.Select(p => p with { Rect = p.Rect.Offset(-crop.MinX, -crop.MinY) }).ToList();

        // Spatial indexing keeps the handoff proportional to touched tiles, rather than comparing
        // every old tile with every new tile on a large document.
        var buckets = new Dictionary<(int X, int Y), List<int>>();
        for (var index = 0; index < placed.Count; index++)
        {
            foreach (var cell in Cells(placed[index].Rect))
            {
                if (!buckets.TryGetValue(cell, out var list))
                {
                    buckets[cell] = list = [];
                }

                list.Add(index);
            }
        }

        var split = new List<RasterPatch>(patches.Count + placed.Count);
        foreach (var patch in patches)
        {
            var candidates = Cells(patch.Rect).SelectMany(cell => buckets.GetValueOrDefault(cell) ?? []).Distinct();
            var pieces = new List<RasterPatch> { patch };
            foreach (var index in candidates)
            {
                var addition = placed[index];
                var next = new List<RasterPatch>();
                foreach (var piece in pieces)
                {
                    var overlap = piece.Rect.Intersection(addition.Rect);
                    if (overlap.IsEmpty)
                    {
                        next.Add(piece);
                        continue;
                    }

                    var r = piece.Rect;
                    Rect[] parts =
                    [
                        new(r.MinX, r.MinY, r.Width, overlap.MinY - r.MinY),
                        new(r.MinX, overlap.MaxY, r.Width, r.MaxY - overlap.MaxY),
                        new(r.MinX, overlap.MinY, overlap.MinX - r.MinX, overlap.Height),
                        new(overlap.MaxX, overlap.MinY, r.MaxX - overlap.MaxX, overlap.Height),
                    ];
                    foreach (var part in parts)
                    {
                        if (part.Width <= 0 || part.Height <= 0)
                        {
                            continue;
                        }

                        var image = piece.Image.Crop(part.Offset(-r.MinX, -r.MinY).ToSKI());
                        if (image is not null)
                        {
                            next.Add(new RasterPatch(part, image));
                        }
                    }
                }

                pieces = next;
            }

            split.AddRange(pieces);
        }

        split.AddRange(placed);

        var bounds = new Rect(0, 0, crop.Width, crop.Height);
        var clipped = new List<RasterPatch>(split.Count);
        foreach (var patch in split)
        {
            var rect = patch.Rect.Intersection(bounds);
            if (rect.IsEmpty)
            {
                continue;
            }

            if (rect == patch.Rect)
            {
                clipped.Add(patch);
                continue;
            }

            var image = patch.Image.Crop(rect.Offset(-patch.Rect.MinX, -patch.Rect.MinY).ToSKI());
            if (image is not null)
            {
                clipped.Add(new RasterPatch(rect, image));
            }
        }

        var @base = old?.Base ?? (old is null ? source?.Image : null);
        var baseRect = old?.BaseRect.Offset(dx, dy) ?? sourceRect.Offset(-crop.MinX, -crop.MinY);
        var alignment = new Point(sourceRect.MinX + (old?.Alignment.X ?? 0) - crop.MinX, sourceRect.MinY + (old?.Alignment.Y ?? 0) - crop.MinY);
        return new RasterSnapshot((int)crop.Width, (int)crop.Height, @base, baseRect, clipped, isMask, alignment);
    }

    /// <summary>
    /// Draws this raster scaled into <paramref name="rect"/> of <paramref name="canvas"/>: pixels are
    /// copied, not blended, and nothing is filtered. Used when allocating a brush tile and when
    /// materializing.
    /// </summary>
    public void Draw(Rect rect, SKCanvas canvas)
    {
        canvas.Save();
        canvas.ClipRect(rect.ToSK());
        if (IsMask)
        {
            // A mask expansion reveals new pixels outside its original extent.
            canvas.Clear(SKColors.White);
        }

        var sx = rect.Width / Width;
        var sy = rect.Height / Height;
        Rect Mapped(Rect r) => new(rect.MinX + (r.MinX * sx), rect.MinY + (r.MinY * sy), r.Width * sx, r.Height * sy);

        var visible = FromSK(canvas.LocalClipBounds);
        if (Base is { } @base)
        {
            var target = Mapped(BaseRect);
            if (target.Intersects(visible))
            {
                Bitmaps.Copy(canvas, @base, target.ToSK());
            }
        }

        foreach (var patch in Patches)
        {
            var target = Mapped(patch.Rect);
            if (target.Intersects(visible))
            {
                Bitmaps.Copy(canvas, patch.Image, target.ToSK());
            }
        }

        canvas.Restore();
    }

    /// <summary>A copy at most 96 pixels on its long side.</summary>
    public SKBitmap Thumbnail()
    {
        var factor = Math.Min(1, 96.0 / Math.Max(Width, Height));
        var w = Math.Max(1, (int)(Width * factor));
        var h = Math.Max(1, (int)(Height * factor));
        var bitmap = Bitmaps.Create(w, h, IsMask);
        using var canvas = new SKCanvas(bitmap);
        Draw(new Rect(0, 0, w, h), canvas);
        return bitmap;
    }

    private static IEnumerable<(int X, int Y)> Cells(Rect rect)
    {
        if (rect.IsEmpty)
        {
            yield break;
        }

        var minY = (int)Math.Floor(rect.MinY / TileSize);
        var maxY = (int)Math.Ceiling(rect.MaxY / TileSize) - 1;
        var minX = (int)Math.Floor(rect.MinX / TileSize);
        var maxX = (int)Math.Ceiling(rect.MaxX / TileSize) - 1;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                yield return (x, y);
            }
        }
    }

    private static Rect FromSK(SKRect r) => Rect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
}
