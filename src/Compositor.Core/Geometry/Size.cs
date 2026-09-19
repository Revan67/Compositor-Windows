using SkiaSharp;

namespace Compositor.Core.Geometry;

/// <summary>An extent in document pixels.</summary>
public readonly record struct Size(double Width, double Height)
{
    public static readonly Size Zero = new(0, 0);

    public static Size operator *(Size s, double factor) => new(s.Width * factor, s.Height * factor);

    public bool IsFinite => double.IsFinite(Width) && double.IsFinite(Height);

    public SKSize ToSK() => new((float)Width, (float)Height);

    public override string ToString() => $"{Width}×{Height}";
}
