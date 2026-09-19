using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Project;

public enum ProjectError
{
    Invalid,
    Version,
    MissingImage,
    TooLarge,
    Encode,
}

public sealed class ProjectException(ProjectError error, int version = 0) : Exception(Describe(error, version))
{
    public ProjectError Error { get; } = error;

    private static string Describe(ProjectError error, int version) => error switch
    {
        ProjectError.Invalid => "This is not a valid Compositor project, or its metadata is damaged.",
        ProjectError.Version => $"This project uses format version {version}. This app supports version {ProjectStore.Version}.",
        ProjectError.MissingImage => "An image inside the project is missing or damaged. The current document has not been replaced.",
        ProjectError.TooLarge => "This project exceeds the supported canvas, layer, file-size, or 100-megapixel image limit.",
        _ => "An image could not be saved. The previous project has not been replaced.",
    };
}

/// <summary>A layer as written to <c>manifest.json</c>. Optional fields are omitted when default.</summary>
public sealed record ProjectLayerRecord
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsVisible { get; init; } = true;
    public required LayerTransform Transform { get; init; }
    public string? ImageFile { get; init; }
    public Guid? ParentId { get; init; }
    public bool? IsGroup { get; init; }
    public double? Opacity { get; init; }
    public LayerBlendMode? BlendMode { get; init; }
    public string? MaskFile { get; init; }
    public bool? MaskEnabled { get; init; }
    public Guid? MaskSourceId { get; init; }

    /// <summary>A mask moved apart from its layer: where it sits on the document.</summary>
    public LayerTransform? MaskPlacement { get; init; }

    /// <summary>Null is linked.</summary>
    public bool? MaskLinked { get; init; }

    [JsonIgnore]
    public bool IsFolder => IsGroup == true;
}

public sealed record ProjectManifest
{
    public string Format { get; init; } = ProjectStore.Format;
    public int Version { get; init; } = ProjectStore.Version;
    public string ColorSpace { get; init; } = "sRGB";
    public double? Resolution { get; init; }
    public required Guid DocumentId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public Guid? ActiveLayerId { get; init; }
    public required IReadOnlyList<ProjectLayerRecord> Layers { get; init; }
}

/// <summary>Everything a project file holds: the manifest plus decoded layer images and masks by layer id.</summary>
public sealed record ProjectSnapshot(ProjectManifest Manifest, IReadOnlyDictionary<Guid, ImportedImage> Images, IReadOnlyDictionary<Guid, ImportedImage> Masks)
{
    public LayerMask? MaskFor(ProjectLayerRecord layer) =>
        layer.MaskFile is not null && Masks.TryGetValue(layer.Id, out var asset)
            ? new LayerMask(asset) { IsEnabled = layer.MaskEnabled ?? true, Placement = layer.MaskPlacement, IsLinked = layer.MaskLinked ?? true }
            : null;

    /// <summary>The live document this snapshot describes.</summary>
    public CanvasDocument ToDocument()
    {
        var layers = Manifest.Layers.Select(record => new ImageLayer(record.Id, record.ImageFile is null ? null : Images[record.Id], record.Name, record.IsVisible, record.Transform) with
        {
            ParentId = record.ParentId,
            IsGroup = record.IsFolder,
            Opacity = record.Opacity ?? 1,
            BlendMode = record.BlendMode ?? LayerBlendMode.Normal,
            MaskSourceId = record.MaskSourceId,
            Mask = MaskFor(record),
        }).ToList();
        return new CanvasDocument(Manifest.Width, Manifest.Height, layers, Manifest.Resolution ?? 72, Manifest.DocumentId);
    }

    /// <summary>A snapshot of <paramref name="document"/>, ready to save.</summary>
    public static ProjectSnapshot From(CanvasDocument document, Guid? activeLayerId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var images = new Dictionary<Guid, ImportedImage>();
        var masks = new Dictionary<Guid, ImportedImage>();
        var records = new List<ProjectLayerRecord>(document.Layers.Count);
        foreach (var layer in document.Layers)
        {
            if (layer.Asset is { } asset)
            {
                images[layer.Id] = asset;
            }

            if (layer.Mask is { } mask)
            {
                masks[layer.Id] = mask.Asset;
            }

            records.Add(new ProjectLayerRecord
            {
                Id = layer.Id,
                Name = layer.Name,
                IsVisible = layer.IsVisible,
                Transform = layer.Transform,
                ImageFile = layer.Asset is null ? null : ProjectStore.ImageFileName(layer.Id),
                ParentId = layer.ParentId,
                IsGroup = layer.IsGroup ? true : null,
                Opacity = layer.Opacity == 1 ? null : layer.Opacity,
                BlendMode = layer.BlendMode == LayerBlendMode.Normal ? null : layer.BlendMode,
                MaskFile = layer.Mask is null ? null : ProjectStore.MaskFileName(layer.Id),
                MaskEnabled = layer.Mask is null ? null : layer.Mask.IsEnabled,
                MaskSourceId = layer.MaskSourceId,
                MaskPlacement = layer.Mask?.Placement,
                MaskLinked = layer.Mask is { IsLinked: false } ? false : null,
            });
        }

        var manifest = new ProjectManifest
        {
            DocumentId = document.Id,
            Width = document.Width,
            Height = document.Height,
            Resolution = document.Resolution,
            ActiveLayerId = activeLayerId,
            Layers = records,
        };
        return new ProjectSnapshot(manifest, images, masks);
    }
}

/// <summary>
/// The <c>.comp</c> file: a zip holding <c>manifest.json</c> and <c>images/&lt;layer id&gt;.png</c>
/// (plus <c>.mask.png</c>). Saving writes a sibling temp file and swaps it in, so a torn write never
/// replaces a good project. Loading validates everything before returning, so the live document is
/// only replaced by a project that was read completely.
/// </summary>
public static class ProjectStore
{
    public const string Format = "com.compositor.windows.project";
    public const int Version = 1;
    public const string Extension = ".comp";
    public const int MaxLayers = 10_000;
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const long MaxAssetBytes = 512L * 1024 * 1024;
    public const long PixelBudget = 100_000_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public static string ImageFileName(Guid id) => $"{id:D}.png";

    public static string MaskFileName(Guid id) => $"{id:D}.mask.png";

    public static void Save(ProjectSnapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot.Manifest);
        foreach (var layer in snapshot.Manifest.Layers)
        {
            if ((layer.ImageFile is not null && !snapshot.Images.ContainsKey(layer.Id)) || (layer.MaskFile is not null && !snapshot.Masks.ContainsKey(layer.Id)))
            {
                throw new ProjectException(ProjectError.MissingImage);
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ProjectException(ProjectError.Invalid);
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open())
                {
                    JsonSerializer.Serialize(manifest, snapshot.Manifest, Json);
                }

                foreach (var layer in snapshot.Manifest.Layers)
                {
                    if (layer.ImageFile is { } imageFile)
                    {
                        WriteAsset(archive, "images/" + imageFile, snapshot.Images[layer.Id].Image);
                    }

                    if (layer.MaskFile is { } maskFile)
                    {
                        WriteAsset(archive, "images/" + maskFile, snapshot.Masks[layer.Id].Image);
                    }
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static ProjectSnapshot Load(string path)
    {
        using var archive = OpenArchive(path);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new ProjectException(ProjectError.Invalid);
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        byte[] metadata;
        using (var stream = manifestEntry.Open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            metadata = buffer.ToArray();
        }

        ProjectManifest manifest;
        try
        {
            var header = JsonSerializer.Deserialize<Header>(metadata, Json) ?? throw new ProjectException(ProjectError.Invalid);
            if (header.Format != Format)
            {
                throw new ProjectException(ProjectError.Invalid);
            }

            if (header.Version != Version)
            {
                throw new ProjectException(ProjectError.Version, header.Version);
            }

            manifest = JsonSerializer.Deserialize<ProjectManifest>(metadata, Json) ?? throw new ProjectException(ProjectError.Invalid);
        }
        catch (JsonException)
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        Validate(manifest);
        var images = new Dictionary<Guid, ImportedImage>();
        var masks = new Dictionary<Guid, ImportedImage>();
        long pixels = 0;
        long maskPixels = 0;
        foreach (var layer in manifest.Layers)
        {
            if (layer.ImageFile is { } imageFile)
            {
                images[layer.Id] = ReadAsset(archive, "images/" + imageFile, layer.Name, mask: false, ref pixels);
            }

            if (layer.MaskFile is { } maskFile)
            {
                masks[layer.Id] = ReadAsset(archive, "images/" + maskFile, "Layer Mask", mask: true, ref maskPixels);
            }
        }

        return new ProjectSnapshot(manifest, images, masks);
    }

    /// <summary>Everything the manifest alone can be checked for, before any image is decoded.</summary>
    public static void Validate(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Format != Format)
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        if (manifest.Version != Version)
        {
            throw new ProjectException(ProjectError.Version, manifest.Version);
        }

        if (manifest.ColorSpace != "sRGB")
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        if (manifest.Resolution is { } resolution && (!double.IsFinite(resolution) || resolution is < 1 or > 9600))
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        if (manifest.Width is < 1 or > CanvasDocument.MaxDimension || manifest.Height is < 1 or > CanvasDocument.MaxDimension || manifest.Layers.Count > MaxLayers)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        var ids = new HashSet<Guid>();
        foreach (var layer in manifest.Layers)
        {
            var opacity = layer.Opacity ?? 1;
            var blend = layer.BlendMode ?? LayerBlendMode.Normal;
            var valid = ids.Add(layer.Id)
                && layer.Transform.IsValid
                && !string.IsNullOrWhiteSpace(layer.Name)
                && System.Text.Encoding.UTF8.GetByteCount(layer.Name) <= 16_384
                && (layer.ImageFile is null || layer.ImageFile == ImageFileName(layer.Id))
                && (layer.MaskFile is null || layer.MaskFile == MaskFileName(layer.Id))
                && (layer.MaskEnabled is null || layer.MaskFile is not null)
                && (layer.MaskLinked is null || layer.MaskFile is not null)
                && (layer.MaskPlacement is not { } placement || (placement.IsValid && layer.MaskFile is not null))
                && double.IsFinite(opacity) && opacity is >= 0 and <= 1
                && (!layer.IsFolder || (opacity == 1 && blend == LayerBlendMode.Normal))
                && Enum.IsDefined(blend);
            if (!valid)
            {
                throw new ProjectException(ProjectError.Invalid);
            }
        }

        if (manifest.ActiveLayerId is { } active && !ids.Contains(active))
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        // Hierarchy and clipping links are checked on the same model the renderer uses.
        var model = manifest.Layers.Select(r => new ImageLayer(r.Id, r.ImageFile is null ? null : Placeholder, r.Name, r.IsVisible, r.Transform) with
        {
            ParentId = r.ParentId,
            IsGroup = r.IsFolder,
            MaskSourceId = r.MaskSourceId,
        }).ToList();
        try
        {
            LayerHierarchy.Validate(model);
            LayerHierarchy.ValidateMaskLinks(model);
        }
        catch (InvalidProjectException)
        {
            throw new ProjectException(ProjectError.Invalid);
        }
    }

    private static readonly ImportedImage Placeholder = new(Bitmaps.Create(1, 1, false), Bitmaps.Create(1, 1, false), "placeholder");

    private sealed record Header(string Format, int Version);

    private static ZipArchive OpenArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ProjectException(ProjectError.Invalid);
        }
    }

    private static void WriteAsset(ZipArchive archive, string name, SKBitmap image)
    {
        using var wrapped = image.AsImage();
        using var data = wrapped.Encode(SKEncodedImageFormat.Png, 100) ?? throw new ProjectException(ProjectError.Encode);
        // PNGs do not compress further; stored entries keep saves fast and loads seekable.
        using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        data.SaveTo(entry);
    }

    private static ImportedImage ReadAsset(ZipArchive archive, string name, string layerName, bool mask, ref long used)
    {
        var entry = archive.GetEntry(name) ?? throw new ProjectException(ProjectError.MissingImage);
        if (entry.Length > MaxAssetBytes)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        byte[] bytes;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream(checked((int)entry.Length)))
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1)
        {
            throw new ProjectException(ProjectError.MissingImage);
        }

        var info = codec.Info;
        if (info.Width is < 1 or > CanvasDocument.MaxDimension || info.Height is < 1 or > CanvasDocument.MaxDimension || (long)info.Width * info.Height > PixelBudget - used)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        used += (long)info.Width * info.Height;

        if (mask && info.ColorType != SKColorType.Gray8)
        {
            // A mask is stored as an 8-bit gray PNG; anything else is not a mask.
            throw new ProjectException(ProjectError.Invalid);
        }

        var bitmap = new SKBitmap(Bitmaps.Info(info.Width, info.Height, mask));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new ProjectException(ProjectError.MissingImage);
        }

        return new ImportedImage(bitmap, ImageCodec.Thumbnail(bitmap), layerName);
    }
}
