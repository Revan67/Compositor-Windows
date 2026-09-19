using Compositor.Core.Geometry;

namespace Compositor.Core.Rendering;

/// <summary>
/// Maps the document (pixels, top-left origin) onto the view (logical units). Zoom 1 means one
/// document pixel per physical display pixel, whatever the display scaling.
/// </summary>
public sealed record CanvasViewport
{
    public const double MinZoom = 0.001;
    public const double MaxZoom = 32;

    /// <summary>Margin kept around a fitted document, in view units.</summary>
    public const double FitMargin = 96;

    public Size ViewSize { get; init; } = Size.Zero;
    public double BackingScale { get; init; } = 1;
    public double Zoom { get; init; } = 1;
    public Size Pan { get; init; } = Size.Zero;

    /// <summary>True until the user zooms or pans: a resized view re-fits the document.</summary>
    public bool FollowsFit { get; init; } = true;

    /// <summary>View units per document pixel.</summary>
    public double PointsPerPixel => Zoom / BackingScale;

    public Point Center => new(ViewSize.Width / 2, ViewSize.Height / 2);

    /// <summary>Where a document of <paramref name="size"/> sits in the view.</summary>
    public Rect DocumentRect(Size size)
    {
        var scaled = size * PointsPerPixel;
        return new Rect(Center.X - (scaled.Width / 2) + Pan.Width, Center.Y - (scaled.Height / 2) + Pan.Height, scaled.Width, scaled.Height);
    }

    public Point DocumentPoint(Point view, Size documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return new Point((view.X - origin.X) / PointsPerPixel, (view.Y - origin.Y) / PointsPerPixel);
    }

    public Point ViewPoint(Point document, Size documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return new Point(origin.X + (document.X * PointsPerPixel), origin.Y + (document.Y * PointsPerPixel));
    }

    /// <summary>The whole document centred with a margin, or just flagged to fit once the view has a size.</summary>
    public CanvasViewport Fit(Size documentSize)
    {
        if (ViewSize.Width <= 0 || ViewSize.Height <= 0)
        {
            return this with { FollowsFit = true };
        }

        var zoom = Math.Min(Math.Max(1, ViewSize.Width - FitMargin) / documentSize.Width, Math.Max(1, ViewSize.Height - FitMargin) / documentSize.Height) * BackingScale;
        return this with { Zoom = Clamp(zoom), Pan = Size.Zero, FollowsFit = true };
    }

    /// <summary>A new view size or display scale; keeps the centre document point when not following fit.</summary>
    public CanvasViewport Resized(Size size, double backingScale, Size? documentSize)
    {
        var oldScale = PointsPerPixel;
        var resized = this with { ViewSize = size, BackingScale = Math.Max(1, backingScale) };
        if (FollowsFit && documentSize is { } document)
        {
            return resized.Fit(document);
        }

        var ratio = resized.PointsPerPixel / oldScale;
        return resized with { Pan = new Size(Pan.Width * ratio, Pan.Height * ratio) };
    }

    /// <summary>Zooms so the document pixel under <paramref name="anchor"/> stays under it.</summary>
    public CanvasViewport Zoomed(double value, Point anchor, Size documentSize)
    {
        if (!double.IsFinite(value))
        {
            return this;
        }

        var pixel = DocumentPoint(anchor, documentSize);
        var zoomed = this with { Zoom = Clamp(value), FollowsFit = false };
        var moved = zoomed.ViewPoint(pixel, documentSize);
        return zoomed with { Pan = new Size(Pan.Width + anchor.X - moved.X, Pan.Height + anchor.Y - moved.Y) };
    }

    public CanvasViewport Translated(Size delta) => this with { Pan = new Size(Pan.Width + delta.Width, Pan.Height + delta.Height), FollowsFit = false };

    private static double Clamp(double value) => Math.Min(MaxZoom, Math.Max(MinZoom, value));
}
