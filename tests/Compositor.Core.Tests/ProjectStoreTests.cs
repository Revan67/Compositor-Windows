using System.IO.Compression;
using System.Text;
using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"compositor-projects-{Guid.NewGuid():N}");

    public ProjectStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static SKBitmap Solid(int w, int h, SKColor color, bool mask = false)
    {
        var bitmap = Bitmaps.Create(w, h, mask);
        bitmap.Erase(color);
        return bitmap;
    }

    private static ImportedImage Asset(SKBitmap image, string name) => new(image, ImageCodec.Thumbnail(image), name);

    /// <summary>A document using every field the format stores.</summary>
    private static (CanvasDocument Document, Guid Active) Everything()
    {
        var photo = new ImageLayer(Asset(Solid(6, 4, SKColors.Red), "Photo"), new Point(1, 2)) with
        {
            Transform = new LayerTransform(new Point(1, 2), new Size(12, 8), Rotation: 30, FlipX: true, Sampling: LayerSampling.Nearest),
            Opacity = 0.5,
            BlendMode = LayerBlendMode.Multiply,
        };
        var maskPixels = Solid(2, 2, SKColors.White, mask: true);
        maskPixels.SetPixel(1, 1, SKColors.Black);
        var masked = new ImageLayer(Asset(Solid(3, 3, SKColors.Blue), "Masked"), Point.Zero) with
        {
            Mask = new LayerMask(LayerMask.AssetFrom(maskPixels)) { IsEnabled = false, IsLinked = false, Placement = new LayerTransform(new Point(4, 4), new Size(2, 2)) },
        };
        var folder = new ImageLayer("Folder 1", new Size(16, 16)) with { IsGroup = true, Mask = LayerMask.Solid(revealing: true) };
        var inside = new ImageLayer("Blank", new Size(16, 16)) with { ParentId = folder.Id, IsVisible = false };
        var clipped = new ImageLayer(Asset(Solid(2, 2, SKColors.Lime), "Clipped"), new Point(5, 5)) with { MaskSourceId = masked.Id };
        var document = new CanvasDocument(16, 16, [photo, masked, clipped, folder, inside], resolution: 300);
        return (document, masked.Id);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var (document, active) = Everything();
        var path = PathFor("everything.comp");
        ProjectStore.Save(ProjectSnapshot.From(document, active), path);

        var loaded = ProjectStore.Load(path);
        Assert.Equal(ProjectStore.Format, loaded.Manifest.Format);
        Assert.Equal(ProjectStore.Version, loaded.Manifest.Version);
        Assert.Equal(active, loaded.Manifest.ActiveLayerId);
        var reopened = loaded.ToDocument();

        Assert.Equal(document.Id, reopened.Id);
        Assert.Equal((16, 16, 300.0), (reopened.Width, reopened.Height, reopened.Resolution));
        Assert.Equal(document.Layers.Count, reopened.Layers.Count);
        foreach (var (before, after) in document.Layers.Zip(reopened.Layers))
        {
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.IsVisible, after.IsVisible);
            Assert.Equal(before.Transform, after.Transform);
            Assert.Equal(before.ParentId, after.ParentId);
            Assert.Equal(before.IsGroup, after.IsGroup);
            Assert.Equal(before.Opacity, after.Opacity);
            Assert.Equal(before.BlendMode, after.BlendMode);
            Assert.Equal(before.MaskSourceId, after.MaskSourceId);
            Assert.Equal(before.Asset is null, after.Asset is null);
            if (before.Asset is { } asset)
            {
                Assert.True(Bitmaps.BytesEqual(asset.Image, after.Asset!.Image), $"{before.Name} pixels differ");
            }

            Assert.Equal(before.Mask is null, after.Mask is null);
            if (before.Mask is { } mask)
            {
                Assert.Equal(mask.IsEnabled, after.Mask!.IsEnabled);
                Assert.Equal(mask.IsLinked, after.Mask.IsLinked);
                Assert.Equal(mask.Placement, after.Mask.Placement);
                Assert.Equal(SKColorType.Gray8, after.Mask.Asset.Image.ColorType);
                Assert.True(Bitmaps.BytesEqual(mask.Asset.Image, after.Mask.Asset.Image), $"{before.Name} mask differs");
            }
        }

        // The reopened document renders identically.
        using var a = DocumentRenderer.Flatten(document);
        using var b = DocumentRenderer.Flatten(reopened);
        Assert.True(Bitmaps.BytesEqual(a, b));
    }

    [Fact]
    public void ManifestOmitsDefaultsAndUsesDocumentedNames()
    {
        var (document, _) = Everything();
        var path = PathFor("names.comp");
        ProjectStore.Save(ProjectSnapshot.From(document), path);

        using var archive = ZipFile.OpenRead(path);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("manifest.json", names);
        foreach (var layer in document.Layers)
        {
            Assert.Equal(layer.Asset is not null, names.Contains($"images/{layer.Id:D}.png"));
            Assert.Equal(layer.Mask is not null, names.Contains($"images/{layer.Id:D}.mask.png"));
        }

        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var json = reader.ReadToEnd();
        Assert.Contains("\"format\": \"com.compositor.windows.project\"", json);
        Assert.Contains("\"blendMode\": \"multiply\"", json);
        Assert.Contains("\"sampling\": \"nearest\"", json);
        // A blank, visible, normal layer carries only the required fields.
        Assert.DoesNotContain("\"opacity\": 1", json);
        Assert.DoesNotContain("\"isGroup\": false", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void ReadsAHandWrittenProject()
    {
        // Written from the format description, not by Save: the reader must accept the documented layout,
        // including optional fields left out and fields it does not know.
        var id = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var manifest = $$"""
            {
              "format": "com.compositor.windows.project",
              "version": 1,
              "colorSpace": "sRGB",
              "documentId": "{{Guid.NewGuid()}}",
              "width": 8,
              "height": 8,
              "futureField": { "ignored": true },
              "layers": [
                { "id": "{{folder}}", "name": "F", "isVisible": true, "isGroup": true,
                  "transform": { "origin": { "x": 0, "y": 0 }, "size": { "width": 8, "height": 8 } } },
                { "id": "{{id}}", "name": "Red", "isVisible": true, "parentId": "{{folder}}", "imageFile": "{{id}}.png",
                  "maskFile": "{{id}}.mask.png",
                  "transform": { "origin": { "x": 2, "y": 2 }, "size": { "width": 4, "height": 4 }, "rotation": 0, "flipX": false, "flipY": false, "sampling": "high" } }
              ]
            }
            """;
        var path = PathFor("hand.comp");
        // The mask is a plain 8-bit grayscale PNG written by hand, not by this app's encoder.
        WriteZip(path, manifest, ($"images/{id}.png", Png(Solid(4, 4, SKColors.Red))), ($"images/{id}.mask.png", GrayPng(4, 4, 255)));

        var document = ProjectStore.Load(path).ToDocument();
        Assert.Equal(72, document.Resolution);
        Assert.Equal(2, document.Layers.Count);
        var red = document.Layers[1];
        Assert.Equal(folder, red.ParentId);
        Assert.Equal(new LayerTransform(new Point(2, 2), new Size(4, 4)), red.Transform);
        Assert.True(red.Mask!.IsEnabled && red.Mask.IsLinked && red.Mask.Placement is null);
        using var flat = DocumentRenderer.Flatten(document);
        Assert.Equal(SKColors.Red, flat.GetPixel(3, 3));
        Assert.Equal(SKColors.Empty, flat.GetPixel(0, 0));
    }

    public static TheoryData<string, string, ProjectError> ManifestRejections
    {
        get
        {
            var id = Guid.NewGuid();
            var other = Guid.NewGuid();
            string Layer(string extra = "", Guid? layerId = null) =>
                $$"""{ "id": "{{layerId ?? id}}", "name": "L", "isVisible": true, "imageFile": "{{layerId ?? id}}.png", "transform": { "origin": { "x": 0, "y": 0 }, "size": { "width": 4, "height": 4 } }{{extra}} }""";
            string Manifest(string layers, string head = "\"format\": \"com.compositor.windows.project\", \"version\": 1, \"colorSpace\": \"sRGB\", \"width\": 8, \"height\": 8") =>
                $$"""{ {{head}}, "documentId": "{{Guid.NewGuid()}}", "layers": [ {{layers}} ] }""";
            return new TheoryData<string, string, ProjectError>
            {
                { "wrong format", Manifest(Layer(), "\"format\": \"com.compositor.project\", \"version\": 1, \"colorSpace\": \"sRGB\", \"width\": 8, \"height\": 8"), ProjectError.Invalid },
                { "future version", Manifest(Layer(), "\"format\": \"com.compositor.windows.project\", \"version\": 2, \"colorSpace\": \"sRGB\", \"width\": 8, \"height\": 8"), ProjectError.Version },
                { "colour space", Manifest(Layer(), "\"format\": \"com.compositor.windows.project\", \"version\": 1, \"colorSpace\": \"P3\", \"width\": 8, \"height\": 8"), ProjectError.Invalid },
                { "resolution", Manifest(Layer(), "\"format\": \"com.compositor.windows.project\", \"version\": 1, \"colorSpace\": \"sRGB\", \"width\": 8, \"height\": 8, \"resolution\": 0"), ProjectError.Invalid },
                { "canvas too large", Manifest(Layer(), "\"format\": \"com.compositor.windows.project\", \"version\": 1, \"colorSpace\": \"sRGB\", \"width\": 30001, \"height\": 8"), ProjectError.TooLarge },
                { "duplicate ids", Manifest(Layer() + "," + Layer()), ProjectError.Invalid },
                { "image file name", Manifest(Layer().Replace($"{id}.png", "other.png")), ProjectError.Invalid },
                { "folder with pixels", Manifest(Layer(", \"isGroup\": true")), ProjectError.Invalid },
                { "folder with opacity", Manifest(Layer(", \"isGroup\": true, \"opacity\": 0.5", other).Replace($"\"imageFile\": \"{other}.png\", ", string.Empty)), ProjectError.Invalid },
                { "opacity out of range", Manifest(Layer(", \"opacity\": 1.5")), ProjectError.Invalid },
                { "empty name", Manifest(Layer().Replace("\"name\": \"L\"", "\"name\": \"  \"")), ProjectError.Invalid },
                { "invalid transform", Manifest(Layer().Replace("\"width\": 4", "\"width\": 0")), ProjectError.Invalid },
                { "missing parent", Manifest(Layer($", \"parentId\": \"{other}\"")), ProjectError.Invalid },
                { "parent not a folder", Manifest(Layer($", \"parentId\": \"{other}\"") + "," + Layer(layerId: other)), ProjectError.Invalid },
                { "mask enabled without mask", Manifest(Layer(", \"maskEnabled\": true")), ProjectError.Invalid },
                { "mask placement without mask", Manifest(Layer(", \"maskPlacement\": { \"origin\": { \"x\": 0, \"y\": 0 }, \"size\": { \"width\": 1, \"height\": 1 } }")), ProjectError.Invalid },
                { "clip to self", Manifest(Layer($", \"maskSourceId\": \"{id}\"")), ProjectError.Invalid },
                { "clip to missing", Manifest(Layer($", \"maskSourceId\": \"{other}\"")), ProjectError.Invalid },
                { "active layer missing", Manifest(Layer()).Replace("\"layers\"", $"\"activeLayerId\": \"{other}\", \"layers\""), ProjectError.Invalid },
                { "not json", "{ not json", ProjectError.Invalid },
            };
        }
    }

    [Theory]
    [MemberData(nameof(ManifestRejections))]
    public void RejectsInvalidManifests(string label, string manifest, ProjectError expected)
    {
        var path = PathFor($"{label.Replace(' ', '-')}.comp");
        WriteZip(path, manifest);
        var error = Assert.Throws<ProjectException>(() => ProjectStore.Load(path));
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public void RejectsBadAssets()
    {
        var id = Guid.NewGuid();
        string Manifest(string extra = "") => $$"""{ "format": "com.compositor.windows.project", "version": 1, "colorSpace": "sRGB", "documentId": "{{Guid.NewGuid()}}", "width": 8, "height": 8, "layers": [ { "id": "{{id}}", "name": "L", "isVisible": true, "imageFile": "{{id}}.png"{{extra}}, "transform": { "origin": { "x": 0, "y": 0 }, "size": { "width": 4, "height": 4 } } } ] }""";

        var missing = PathFor("missing.comp");
        WriteZip(missing, Manifest());
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Load(missing)).Error);

        var notPng = PathFor("notpng.comp");
        using var jpegImage = Solid(4, 4, SKColors.Red).AsImage();
        WriteZip(notPng, Manifest(), ($"images/{id}.png", jpegImage.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray()));
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Load(notPng)).Error);

        var colourMask = PathFor("colourmask.comp");
        WriteZip(colourMask, Manifest($", \"maskFile\": \"{id}.mask.png\""), ($"images/{id}.png", Png(Solid(4, 4, SKColors.Red))), ($"images/{id}.mask.png", Png(Solid(4, 4, SKColors.White))));
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(colourMask)).Error);

        var garbage = PathFor("garbage.comp");
        File.WriteAllText(garbage, "not a zip");
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(garbage)).Error);
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(PathFor("absent.comp"))).Error);
    }

    [Fact]
    public void SaveIsAtomicAndReplacesInPlace()
    {
        var path = PathFor("atomic.comp");
        var first = new CanvasDocument(4, 4, [new ImageLayer(Asset(Solid(4, 4, SKColors.Red), "Red"), Point.Zero)]);
        ProjectStore.Save(ProjectSnapshot.From(first), path);

        // A snapshot whose manifest names an image it does not carry must leave the file untouched.
        var broken = ProjectSnapshot.From(new CanvasDocument(4, 4, [new ImageLayer(Asset(Solid(4, 4, SKColors.Blue), "Blue"), Point.Zero)]));
        broken = broken with { Images = new Dictionary<Guid, ImportedImage>() };
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Save(broken, path)).Error);
        Assert.Equal(SKColors.Red, ProjectStore.Load(path).ToDocument().Layers[0].Asset!.Image.GetPixel(0, 0));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        var second = new CanvasDocument(4, 4, [new ImageLayer(Asset(Solid(4, 4, SKColors.Blue), "Blue"), Point.Zero)]);
        ProjectStore.Save(ProjectSnapshot.From(second), path);
        Assert.Equal(SKColors.Blue, ProjectStore.Load(path).ToDocument().Layers[0].Asset!.Image.GetPixel(0, 0));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void SnapshotBackedLayersSaveTheirLazyPixels()
    {
        var raster = RasterSnapshot.Replacing(null, new Rect(0, 0, 4, 4), [new RasterPatch(new Rect(1, 1, 2, 2), Solid(2, 2, SKColors.Lime))], new Rect(0, 0, 4, 4));
        var painted = new ImageLayer(new ImportedImage(raster, raster.Thumbnail(), "Painted"), Point.Zero);
        var path = PathFor("sparse.comp");
        Assert.False(raster.HasMaterializedPixels);
        ProjectStore.Save(ProjectSnapshot.From(new CanvasDocument(4, 4, [painted])), path);
        var image = ProjectStore.Load(path).ToDocument().Layers[0].Asset!.Image;
        Assert.Equal(SKColors.Lime, image.GetPixel(1, 1));
        Assert.Equal(SKColors.Empty, image.GetPixel(0, 0));
    }

    /// <summary>A minimal grayscale PNG (colour type 0, 8-bit) assembled by hand, one filter byte per row.</summary>
    private static byte[] GrayPng(int width, int height, byte value)
    {
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
        {
            Array.Fill(raw, value, (y * (width + 1)) + 1, width);
        }

        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", deflated.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            s.Write(length);
            s.Write(typeBytes);
            s.Write(data);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32([.. typeBytes, .. data]));
            s.Write(crc);
        }
    }

    private static byte[] Png(SKBitmap bitmap)
    {
        using var image = bitmap.AsImage();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void WriteZip(string path, string manifest, params (string Name, byte[] Bytes)[] assets)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var entry = archive.CreateEntry("manifest.json").Open())
        {
            entry.Write(Encoding.UTF8.GetBytes(manifest));
        }

        foreach (var (name, bytes) in assets)
        {
            using var entry = archive.CreateEntry(name).Open();
            entry.Write(bytes);
        }
    }
}
