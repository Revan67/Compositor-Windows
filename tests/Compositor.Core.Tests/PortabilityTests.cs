using System.Reflection;
using Compositor.Core.Raster;

namespace Compositor.Core.Tests;

/// <summary>
/// Core is the headless model and raster engine. If it ever picks up a UI framework the tests
/// stop being fast and the model stops being testable without a window.
/// </summary>
public sealed class PortabilityTests
{
    [Fact]
    public void CoreReferencesNoUiFramework()
    {
        var core = typeof(Checkerboard).Assembly;
        var references = core.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Avalonia", StringComparison.Ordinal) ||
            name.StartsWith("PresentationFramework", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.UI", StringComparison.Ordinal) ||
            name.StartsWith("System.Windows.Forms", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreUsesTheSkiaSharpVersionAvaloniaPins()
    {
        // Directory.Build.props pins SkiaSharpVersion to Avalonia's transitive version. A drift
        // here means two Skia assemblies at runtime and an unusable canvas lease.
        var skia = typeof(SkiaSharp.SKBitmap).Assembly.GetName().Version;
        Assert.NotNull(skia);
        Assert.Equal(3, skia.Major);
    }
}
