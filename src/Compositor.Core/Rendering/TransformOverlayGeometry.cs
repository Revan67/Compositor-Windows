using Compositor.Core.Document;
using Compositor.Core.Geometry;

namespace Compositor.Core.Rendering;

/// <summary>The transform handles in view coordinates, and which one a pointer is over.</summary>
public sealed record TransformOverlayGeometry
{
    /// <summary>How far a pointer may be from a handle or edge, in view units, and still grab it.</summary>
    public const double HitDistance = 10;

    /// <summary>The rotation handle sits this far above the top-centre handle, along the layer's up direction.</summary>
    public const double RotationHandleOffset = 28;

    public TransformOverlayGeometry(LayerTransform transform, CanvasViewport viewport, Size documentSize)
    {
        Handles = LayerTransform.Handles.Select(h => viewport.ViewPoint(transform.PointAt(h), documentSize)).ToList();
        var (sin, cos) = Math.SinCos(transform.Radians);
        RotationHandle = new Point(Handles[1].X + (sin * RotationHandleOffset), Handles[1].Y - (cos * RotationHandleOffset));
    }

    /// <summary>Corner, edge-midpoint, corner… clockwise from top-left, matching <see cref="LayerTransform.Handles"/>.</summary>
    public IReadOnlyList<Point> Handles { get; }

    public Point RotationHandle { get; }

    /// <summary>The four corners in order, for drawing the box outline.</summary>
    public IEnumerable<Point> Outline => new[] { Handles[0], Handles[2], Handles[4], Handles[6] };

    public TransformDragMode? Hit(Point point)
    {
        bool Near(Point other) => Math.Sqrt(((point.X - other.X) * (point.X - other.X)) + ((point.Y - other.Y) * (point.Y - other.Y))) <= HitDistance;

        if (Near(RotationHandle))
        {
            return new TransformDragMode.Rotate();
        }

        for (var index = 0; index < Handles.Count; index++)
        {
            if (Near(Handles[index]))
            {
                return new TransformDragMode.Resize(index);
            }
        }

        foreach (var (start, end, handle) in new[] { (0, 2, 1), (2, 4, 3), (4, 6, 5), (6, 0, 7) })
        {
            var a = Handles[start];
            var b = Handles[end];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            if (lengthSquared <= 0)
            {
                continue;
            }

            var t = (((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared;
            var px = point.X - a.X - (t * dx);
            var py = point.Y - a.Y - (t * dy);
            if (t is >= 0 and <= 1 && Math.Sqrt((px * px) + (py * py)) <= HitDistance)
            {
                return new TransformDragMode.Resize(handle);
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="point"/> is inside the box (the body, for moving).</summary>
    public bool Contains(Point point)
    {
        // Winding test against the four corners.
        var corners = Outline.ToList();
        var inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            var a = corners[i];
            var b = corners[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Which of the four diagonal/orthogonal resize cursors suits handle <paramref name="index"/> given the
    /// box's rotation on screen: 0 = horizontal, 1 = diagonal ╲, 2 = vertical, 3 = diagonal ╱.
    /// </summary>
    public int ResizeCursorDirection(int index)
    {
        var angle = Math.Atan2(Handles[2].Y - Handles[0].Y, Handles[2].X - Handles[0].X);
        double[] offsets = [Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, 0, Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, 0];
        return (((int)Math.Round((angle + offsets[index]) / (Math.PI / 4)) % 4) + 4) % 4;
    }
}
