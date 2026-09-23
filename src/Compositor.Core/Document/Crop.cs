using Compositor.Core.Geometry;
using Compositor.Core.Project;

namespace Compositor.Core.Document;

public static class CropGeometry
{
    /// <summary>Whole pixels, at least one each way.</summary>
    public static Rect Snapped(Rect rect)
    {
        var minX = Math.Min(rect.MinX, rect.MaxX);
        var minY = Math.Min(rect.MinY, rect.MaxY);
        var maxX = Math.Max(rect.MinX, rect.MaxX);
        var maxY = Math.Max(rect.MinY, rect.MaxY);
        var x = Math.Round(minX);
        var y = Math.Round(minY);
        return new Rect(x, y, Math.Max(1, Math.Round(maxX) - x), Math.Max(1, Math.Round(maxY) - y));
    }

    public static bool IsValid(Rect rect) =>
        rect.IsFinite && rect.Width is >= 1 and <= CanvasDocument.MaxDimension && rect.Height is >= 1 and <= CanvasDocument.MaxDimension
        && Math.Abs(rect.MinX) <= 1_000_000 && Math.Abs(rect.MinY) <= 1_000_000;

    /// <summary>A frame dragged from <paramref name="start"/> to <paramref name="end"/> — or, symmetric (Alt), grown out from <paramref name="start"/> as its centre.</summary>
    public static Rect Create(Point start, Point end, double? ratio, bool symmetric = false)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (ratio is { } r)
        {
            if (Math.Abs(dx) > Math.Abs(dy) * r)
            {
                dy = (dy < 0 ? -1 : 1) * Math.Abs(dx) / r;
            }
            else
            {
                dx = (dx < 0 ? -1 : 1) * Math.Abs(dy) * r;
            }
        }

        if (symmetric)
        {
            return Snapped(new Rect(start.X - Math.Abs(dx), start.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2));
        }

        return Snapped(new Rect(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Abs(dx), Math.Abs(dy)));
    }
}

public abstract record CropDragMode
{
    public sealed record Create : CropDragMode;

    public sealed record Move : CropDragMode;

    public sealed record Resize(int Handle) : CropDragMode;
}

public sealed record CropDrag(Point Start, Rect Original, CropDragMode Mode)
{
    /// <summary>Symmetric (Alt held) keeps the frame's centre fixed: the opposite edges move with the dragged ones.</summary>
    public Rect Updated(Point point, double? ratio, bool symmetric = false)
    {
        switch (Mode)
        {
            case CropDragMode.Create:
                return CropGeometry.Create(Start, point, ratio, symmetric);
            case CropDragMode.Move:
                return CropGeometry.Snapped(Original.Offset(point.X - Start.X, point.Y - Start.Y));
            case CropDragMode.Resize resize:
            {
                var transform = new LayerTransform(Original.Origin, Original.Size);
                var drag = new TransformDrag(transform, Start, new TransformDragMode.Resize(resize.Handle));
                var next = drag.Updated(point, lockRatio: ratio is not null, shift: false, option: symmetric);
                return CropGeometry.Snapped(new Rect(next.Origin, next.Size));
            }

            default:
                return Original;
        }
    }
}

/// <summary>Crop edges snap to nearby layer and canvas edges while dragging.</summary>
public sealed record CropSnap(IReadOnlyList<double> Xs, IReadOnlyList<double> Ys, double Tolerance)
{
    private double? Nearest(double value, IReadOnlyList<double> targets)
    {
        double? best = null;
        foreach (var target in targets)
        {
            if (Math.Abs(target - value) > Tolerance)
            {
                continue;
            }

            if (best is { } current && Math.Abs(current - value) <= Math.Abs(target - value))
            {
                continue;
            }

            best = target;
        }

        return best;
    }

    /// <summary>
    /// Moving the frame snaps its closest edges and keeps its size; creating or resizing snaps only the
    /// edges on the side being dragged — mirrored about the centre when symmetric. With a fixed ratio
    /// only moves snap, so the ratio stays exact.
    /// </summary>
    public Rect Apply(Rect rect, CropDrag drag, Point point, double? ratio, bool symmetric = false)
    {
        if (Tolerance <= 0)
        {
            return rect;
        }

        bool horizontal;
        bool vertical;
        switch (drag.Mode)
        {
            case CropDragMode.Move:
            {
                double Shift(double[] edges, IReadOnlyList<double> targets) =>
                    edges.Select(edge => Nearest(edge, targets) is { } t ? t - edge : (double?)null).Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(0).MinBy(Math.Abs);
                return rect.Offset(Shift([rect.MinX, rect.MaxX], Xs), Shift([rect.MinY, rect.MaxY], Ys));
            }

            case CropDragMode.Create:
                if (ratio is not null)
                {
                    return rect;
                }

                horizontal = true;
                vertical = true;
                break;
            case CropDragMode.Resize resize:
            {
                if (ratio is not null)
                {
                    return rect;
                }

                var handle = LayerTransform.Handles[resize.Handle];
                horizontal = handle.X != 0.5;
                vertical = handle.Y != 0.5;
                break;
            }

            default:
                return rect;
        }

        var result = rect;
        // The dragged edge is the one on the pointer's side.
        if (horizontal)
        {
            if (Math.Abs(point.X - result.MinX) <= Math.Abs(point.X - result.MaxX))
            {
                if (Nearest(result.MinX, Xs) is { } x && x < result.MaxX)
                {
                    result = Rect.FromEdges(x, result.MinY, result.MaxX, result.MaxY);
                }
            }
            else if (Nearest(result.MaxX, Xs) is { } x && x > result.MinX)
            {
                result = result with { Width = x - result.MinX };
            }
        }

        if (vertical)
        {
            if (Math.Abs(point.Y - result.MinY) <= Math.Abs(point.Y - result.MaxY))
            {
                if (Nearest(result.MinY, Ys) is { } y && y < result.MaxY)
                {
                    result = Rect.FromEdges(result.MinX, y, result.MaxX, result.MaxY);
                }
            }
            else if (Nearest(result.MaxY, Ys) is { } y && y > result.MinY)
            {
                result = result with { Height = y - result.MinY };
            }
        }

        if (symmetric)
        {
            // The snapped (dragged) edge sets the half size; the opposite edge mirrors it about the centre.
            var center = drag.Mode is CropDragMode.Create ? drag.Start : drag.Original.Center;
            if (horizontal)
            {
                var half = point.X >= center.X ? result.MaxX - center.X : center.X - result.MinX;
                if (half >= 0.5)
                {
                    result = result with { X = center.X - half, Width = half * 2 };
                }
            }

            if (vertical)
            {
                var half = point.Y >= center.Y ? result.MaxY - center.Y : center.Y - result.MinY;
                if (half >= 0.5)
                {
                    result = result with { Y = center.Y - half, Height = half * 2 };
                }
            }
        }

        return result;
    }
}

public sealed partial class EditorSession
{
    /// <summary>The crop frame while the Crop tool is active; null when nothing has been dragged yet (the whole canvas).</summary>
    public Rect? CropRect { get; private set; }

    public string CropRatioChoice { get; private set; } = "Free";

    public static readonly IReadOnlyList<string> CropRatioChoices = ["Free", "Original", "1:1", "4:3", "16:9"];

    public double? CropRatio => CropRatioChoice switch
    {
        "Original" => Document is { } d ? (double)d.Width / d.Height : null,
        "1:1" => 1,
        "4:3" => 4.0 / 3,
        "16:9" => 16.0 / 9,
        _ => null,
    };

    public void SetCropRect(Rect? rect)
    {
        CropRect = rect is { } r && CropGeometry.IsValid(r) ? r : null;
        Changed?.Invoke();
    }

    public void SetCropRatioChoice(string choice)
    {
        CropRatioChoice = CropRatioChoices.Contains(choice) ? choice : "Free";
        if (CropRect is { } rect && CropRatio is { } ratio)
        {
            var height = rect.Width / ratio;
            var next = CropGeometry.Snapped(new Rect(rect.MinX, rect.MidY - (height / 2), rect.Width, height));
            if (CropGeometry.IsValid(next))
            {
                CropRect = next;
            }
        }

        Changed?.Invoke();
    }

    public void CancelCrop()
    {
        CropRect = null;
        Changed?.Invoke();
    }

    /// <summary>What crop edges snap to: the canvas edges and every visible layer's upright bounds, in whole document pixels.</summary>
    public (List<double> Xs, List<double> Ys) CropSnapTargets()
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (Document is not { } document)
        {
            return (xs, ys);
        }

        xs.AddRange([0, document.Width]);
        ys.AddRange([0, document.Height]);
        foreach (var layer in LayerHierarchy.VisibleLayers(document.Layers).Where(l => l.Asset is not null))
        {
            var corners = Corners(layer.Transform);
            xs.AddRange([Math.Round(corners.Min(c => c.X)), Math.Round(corners.Max(c => c.X))]);
            ys.AddRange([Math.Round(corners.Min(c => c.Y)), Math.Round(corners.Max(c => c.Y))]);
        }

        return (xs, ys);
    }

    public void CommitCrop()
    {
        if (!CanEditLayers || Document is not { } document || CropRect is not { } rect || !CropGeometry.IsValid(rect))
        {
            return;
        }

        var resized = CanvasResizer.Resize(document, new CanvasSizeOptions((int)rect.Width, (int)rect.Height) { ContentOffset = new Point(-rect.MinX, -rect.MinY) });
        CropRect = null;
        ApplyDocumentSize(resized, "Crop");
    }

    public void ApplyCanvasSize(CanvasSizeOptions options)
    {
        if (CanEditLayers && Document is { } document)
        {
            ApplyDocumentSize(CanvasResizer.Resize(document, options), "Canvas Size");
        }
    }

    public void ApplyImageSize(ImageSizeOptions options)
    {
        if (CanEditLayers && Document is { } document)
        {
            ApplyDocumentSize(ImageResizer.Resize(document, options), "Image Size");
        }
    }

    /// <summary>Installs a resized document as one undo step; the caller re-fits the view.</summary>
    public void ApplyDocumentSize(CanvasDocument resized, string editName)
    {
        ArgumentNullException.ThrowIfNull(resized);
        if (Document?.Id != resized.Id)
        {
            return;
        }

        CommitTransform();
        BeginEdit(editName);
        Replace(resized);
        EndEdit();
        DocumentResized?.Invoke();
    }

    /// <summary>Raised after Crop, Canvas Size or Image Size, so the view can fit the new canvas.</summary>
    public event Action? DocumentResized;
}
