using Compositor.App.Diagnostics;
using Compositor.Core.Document;
using Compositor.Core.Project;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.App.Tests;

public sealed class RecoveryStoreTests
{
    [Fact]
    public void RecoveryRoundTripsProjectAndMetadataAndCanBeCleared()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"compositor-recovery-{Guid.NewGuid():N}");
        try
        {
            var session = new EditorSession();
            session.CreateDocument(96, 64, emptyLayer: true);
            var savedAt = new DateTimeOffset(2026, 9, 22, 12, 30, 0, TimeSpan.Zero);
            var source = @"C:\art\example.comp";

            RecoveryStore.Save(ProjectSnapshot.From(session.Document!, session.ActiveLayerId), source, savedAt, directory);
            var (snapshot, info) = RecoveryStore.Load(directory);

            Assert.Equal((96, 64), (snapshot.Manifest.Width, snapshot.Manifest.Height));
            Assert.Equal(source, info.SourcePath);
            Assert.Equal(savedAt, info.SavedAtUtc);
            RecoveryStore.Clear(directory);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RecoveryPreservesLayerPixels()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"compositor-recovery-{Guid.NewGuid():N}");
        try
        {
            using var pixels = new SKBitmap(8, 6);
            pixels.Erase(SKColors.CornflowerBlue);
            using var thumbnail = new SKBitmap(1, 1);
            thumbnail.Erase(SKColors.CornflowerBlue);
            var asset = new ImportedImage(pixels, thumbnail, "Working image");
            var layer = new ImageLayer(asset, Point.Zero);
            var document = new CanvasDocument(8, 6, [layer]);

            RecoveryStore.Save(ProjectSnapshot.From(document, layer.Id), null, directory: directory);
            var recovered = RecoveryStore.Load(directory).Snapshot.ToDocument();

            Assert.Equal(SKColors.CornflowerBlue, recovered.Layers.Single().Asset!.Image.GetPixel(4, 3));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
