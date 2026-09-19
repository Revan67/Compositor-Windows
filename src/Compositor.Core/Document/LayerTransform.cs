using Compositor.Core.Geometry;
using SkiaSharp;

namespace Compositor.Core.Document;

public enum LayerSampling
{
    Nearest,
    Smooth,
    High,
}

public static class LayerSamplingExtensions
{
    public static string Label(this LayerSampling sampling) => sampling switch
    {
        LayerSampling.Nearest => "Nearest",
        LayerSampling.Smooth => "Smooth",
        _ => "High quality",
    };

    public static SKSamplingOptions Options(this LayerSampling sampling) => sampling switch
    {
        LayerSampling.Nearest => Raster.Bitmaps.Nearest,
        LayerSampling.Smooth => Raster.Bitmaps.Linear,
        _ => Raster.Bitmaps.High,
    };
}

/// <summary>Unrotated bounds in document pixels; rotation is clockwise around their center.</summary>
public readonly record struct LayerTransform(Point Origin, Size Size, double Rotation = 0, bool FlipX = false, bool FlipY = false, LayerSampling Sampling = LayerSampling.High)
{
    public static readonly IReadOnlyList<Point> Handles =
    [
        new(0, 0), new(0.5, 0), new(1, 0), new(1, 0.5), new(1, 1), new(0.5, 1), new(0, 1), new(0, 0.5),
    ];

    public Point Center => new(Origin.X + (Size.Width / 2), Origin.Y + (Size.Height / 2));

    public double Radians => Rotation % 360 * Math.PI / 180;

    public Rect Bounds => new(Origin, Size);

    public bool IsValid =>
        Origin.IsFinite && Size.IsFinite && double.IsFinite(Rotation)
        && Size.Width is >= 1 and <= 300_000 && Size.Height is >= 1 and <= 300_000
        && Math.Abs(Origin.X) <= 1_000_000 && Math.Abs(Origin.Y) <= 1_000_000;

    /// <summary>Where a unit-square position (0…1 on each axis) lands on the document.</summary>
    public Point PointAt(Point unit)
    {
        var x = (unit.X - 0.5) * Size.Width;
        var y = (unit.Y - 0.5) * Size.Height;
        var (sin, cos) = Math.SinCos(Radians);
        return new Point(Center.X + (x * cos) - (y * sin), Center.Y + (x * sin) + (y * cos));
    }

    public bool Contains(Point point)
    {
        var x = point.X - Center.X;
        var y = point.Y - Center.Y;
        var (sin, cos) = Math.SinCos(Radians);
        return Math.Abs((x * cos) + (y * sin)) <= Size.Width / 2
            && Math.Abs((-x * sin) + (y * cos)) <= Size.Height / 2;
    }

    /// <summary>Width as a percentage of the <paramref name="pixelSize"/> it places (100% draws them 1:1).</summary>
    public double ScalePercent(Size pixelSize) => Size.Width / Math.Max(1, pixelSize.Width) * 100;

    /// <summary>Both sides set to <paramref name="percent"/> of <paramref name="pixelSize"/>, keeping the center (and rotation and flips).</summary>
    public LayerTransform ScaledToPercent(double percent, Size pixelSize)
    {
        var size = new Size(pixelSize.Width * percent / 100, pixelSize.Height * percent / 100);
        return this with { Size = size, Origin = new Point(Center.X - (size.Width / 2), Center.Y - (size.Height / 2)) };
    }

    /// <summary>
    /// Whole pixels and whole degrees: what dragging, scaling and rotating leave behind. Typed
    /// values are used as they are, so a fraction can still be asked for by hand.
    /// </summary>
    public LayerTransform Rounded() => this with
    {
        Origin = Origin.Rounded(),
        Size = new Size(Math.Max(1, Math.Round(Size.Width)), Math.Max(1, Math.Round(Size.Height))),
        Rotation = Math.Round(Rotation),
    };

    /// <summary>Maps a raster's pixel grid (<paramref name="width"/> × <paramref name="height"/>) onto the document.</summary>
    public AffineTransform PixelToDocument(int width, int height) =>
        AffineTransform.Translation(Center.X, Center.Y)
            .Rotated(Radians)
            .Scaled(Size.Width / width * (FlipX ? -1 : 1), Size.Height / height * (FlipY ? -1 : 1))
            .Translated(-width / 2.0, -height / 2.0);

    /// <summary>The unit square (0…1, y down) mapped where this transform places a layer on the document.</summary>
    public AffineTransform UnitToDocument => PixelToDocument(1, 1);

    /// <summary>
    /// A transform placing the unit square as <paramref name="map"/> does — a rotated, maybe flipped
    /// rectangle (shear, which only uneven scaling of something rotated adds, is dropped). Keeps this
    /// transform's sampling.
    /// </summary>
    public LayerTransform Placing(AffineTransform map)
    {
        // Kept horizontal flip and the rotation nearest this one's, so the numbers stay familiar.
        var sign = FlipX ? -1.0 : 1.0;
        var angle = Math.Atan2(map.B * sign, map.A * sign);
        var along = (-map.C * Math.Sin(angle)) + (map.D * Math.Cos(angle));
        var middle = map.Apply(new Point(0.5, 0.5));
        var size = new Size(Math.Sqrt((map.A * map.A) + (map.B * map.B)), Math.Abs(along));
        var degrees = angle * 180 / Math.PI;
        return this with
        {
            Size = size,
            Rotation = degrees + (Math.Round((Rotation - degrees) / 360) * 360),
            FlipY = along < 0,
            Origin = new Point(middle.X - (size.Width / 2), middle.Y - (size.Height / 2)),
        };
    }

    /// <summary>This placement carried along as a layer moves from <paramref name="old"/> to <paramref name="new"/>.</summary>
    public LayerTransform Following(LayerTransform old, LayerTransform @new)
    {
        if (old == @new)
        {
            return this;
        }

        // A plain move carries exactly.
        if (old.Size == @new.Size && old.Rotation == @new.Rotation && old.FlipX == @new.FlipX && old.FlipY == @new.FlipY)
        {
            return this with { Origin = Origin.Offset(@new.Origin.X - old.Origin.X, @new.Origin.Y - old.Origin.Y) };
        }

        return Placing(UnitToDocument.Concatenating(old.UnitToDocument.Inverted()).Concatenating(@new.UnitToDocument));
    }

    /// <summary>The same place on the document, whatever the sampling.</summary>
    public bool SamePlacement(LayerTransform other) => (this with { Sampling = other.Sampling }) == other;
}

/// <summary>
/// Several layers transformed together: the upright box around them when the edit began (what
/// the draft edits), and each one's transform then.
/// </summary>
public sealed record TransformGroup(LayerTransform Box, IReadOnlyDictionary<Guid, LayerTransform> Originals);

public abstract record TransformDragMode
{
    public sealed record Move : TransformDragMode;

    public sealed record Resize(int Handle) : TransformDragMode;

    public sealed record Rotate : TransformDragMode;

    public sealed record Distort(int Handle) : TransformDragMode;
}

/// <summary>A drag of a transform handle or body, from <see cref="Start"/>, in document pixels.</summary>
public sealed record TransformDrag(LayerTransform Original, Point Start, TransformDragMode Mode)
{
    /// <summary>The distortion's corners when the drag began; null for an ordinary transform.</summary>
    public IReadOnlyList<Point>? OriginalCorners { get; init; }

    /// <summary>
    /// Corners after dragging to <paramref name="point"/>: a corner handle moves its corner, an edge
    /// handle both of that edge's corners, and the body the whole shape. Null when the drag isn't distorting.
    /// </summary>
    public IReadOnlyList<Point>? Corners(Point point, bool shift = false)
    {
        if (OriginalCorners is null)
        {
            return null;
        }

        var dx = point.X - Start.X;
        var dy = point.Y - Start.Y;
        // Shift keeps what's being dragged on one axis.
        if (shift)
        {
            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                dy = 0;
            }
            else
            {
                dx = 0;
            }
        }

        int[] moved;
        switch (Mode)
        {
            case TransformDragMode.Distort d:
                moved = d.Handle % 2 == 0 ? [d.Handle / 2] : [d.Handle / 2, ((d.Handle / 2) + 1) % 4];
                break;
            case TransformDragMode.Move:
                moved = [0, 1, 2, 3];
                break;
            default:
                return null;
        }

        var result = OriginalCorners.ToArray();
        foreach (var corner in moved)
        {
            result[corner] = result[corner].Offset(dx, dy);
        }

        return result;
    }

    public LayerTransform Updated(Point point, bool lockRatio, bool shift, bool option = false)
    {
        var result = Original;
        switch (Mode)
        {
            case TransformDragMode.Distort:
                break;
            case TransformDragMode.Move:
            {
                var dx = point.X - Start.X;
                var dy = point.Y - Start.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy))
                    {
                        dy = 0;
                    }
                    else
                    {
                        dx = 0;
                    }
                }

                result = result with { Origin = result.Origin.Offset(dx, dy) };
                break;
            }

            case TransformDragMode.Rotate:
            {
                var center = Original.Center;
                var delta = Math.Atan2(point.Y - center.Y, point.X - center.X) - Math.Atan2(Start.Y - center.Y, Start.X - center.X);
                var rotation = result.Rotation + (delta * 180 / Math.PI);
                if (shift)
                {
                    rotation = Math.Round(rotation / 15) * 15;
                }

                result = result with { Rotation = rotation };
                break;
            }

            case TransformDragMode.Resize resize:
            {
                var handle = LayerTransform.Handles[resize.Handle];
                var anchorUnit = option ? new Point(0.5, 0.5) : new Point(1 - handle.X, 1 - handle.Y);
                var anchor = Original.PointAt(anchorUnit);
                // Use the initial handle plus pointer delta to avoid a jump on grab.
                var initialHandle = Original.PointAt(handle);
                var dx = initialHandle.X + point.X - Start.X - anchor.X;
                var dy = initialHandle.Y + point.Y - Start.Y - anchor.Y;
                // Center-to-handle distances cover half the size on each axis.
                var span = option ? 2.0 : 1.0;
                var (sin, cos) = Math.SinCos(Original.Radians);
                var localX = ((dx * cos) + (dy * sin)) * span;
                var localY = ((-dx * sin) + (dy * cos)) * span;
                var sx = (handle.X * 2) - 1;
                var sy = (handle.Y * 2) - 1;
                var width = sx == 0 ? Original.Size.Width : Math.Max(1, localX * sx);
                var height = sy == 0 ? Original.Size.Height : Math.Max(1, localY * sy);
                if (lockRatio != shift)
                {
                    double factor;
                    if (sx == 0)
                    {
                        factor = height / Original.Size.Height;
                    }
                    else if (sy == 0)
                    {
                        factor = width / Original.Size.Width;
                    }
                    else
                    {
                        // Project onto the original diagonal for proportional scaling.
                        var w = Original.Size.Width;
                        var h = Original.Size.Height;
                        factor = Math.Max(1 / Math.Min(w, h), ((localX * sx * w) + (localY * sy * h)) / ((w * w) + (h * h)));
                    }

                    width = Original.Size.Width * factor;
                    height = Original.Size.Height * factor;
                }

                var offsetX = (0.5 - anchorUnit.X) * width;
                var offsetY = (0.5 - anchorUnit.Y) * height;
                var center = new Point(anchor.X + (offsetX * cos) - (offsetY * sin), anchor.Y + (offsetX * sin) + (offsetY * cos));
                result = result with
                {
                    Size = new Size(width, height),
                    Origin = new Point(center.X - (width / 2), center.Y - (height / 2)),
                };
                break;
            }
        }

        return result.IsValid ? result : Original;
    }
}

/// <summary>
/// Moving a layer snaps its edges and center to the canvas and to the other layers. The pull is a
/// fixed distance on screen, so it feels the same at any zoom, and small enough to slide past
/// without a fight.
/// </summary>
public static class TransformSnap
{
    /// <summary>How close, in screen points, a guide comes before it snaps.</summary>
    public const double Distance = 10;

    /// <summary>
    /// <paramref name="box"/> moved so that whichever of its left, center or right lands nearest an
    /// <paramref name="xs"/> target does, and the same vertically — each axis on its own, and only
    /// within <paramref name="tolerance"/> document pixels. The targets it landed on come back too,
    /// to draw a line along.
    /// </summary>
    public static (Size Offset, double? X, double? Y) Offset(Rect box, IReadOnlyList<double> xs, IReadOnlyList<double> ys, double tolerance)
    {
        var horizontal = Shift([box.MinX, box.MidX, box.MaxX], xs, tolerance);
        var vertical = Shift([box.MinY, box.MidY, box.MaxY], ys, tolerance);
        return (new Size(horizontal.Move, vertical.Move), horizontal.Target, vertical.Target);
    }

    /// <summary>The smallest move that puts one of <paramref name="guides"/> on one of <paramref name="targets"/>, and the target it met.</summary>
    private static (double Move, double? Target) Shift(double[] guides, IReadOnlyList<double> targets, double tolerance)
    {
        (double Move, double Target)? best = null;
        foreach (var guide in guides)
        {
            foreach (var target in targets)
            {
                var move = target - guide;
                if (Math.Abs(move) > tolerance)
                {
                    continue;
                }

                if (best is { } current && Math.Abs(current.Move) <= Math.Abs(move))
                {
                    continue;
                }

                best = (move, target);
            }
        }

        return (best?.Move ?? 0, best?.Target);
    }
}
