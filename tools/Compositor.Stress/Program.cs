using System.Diagnostics;
using System.Text.Json;
using Compositor.Core.Document;
using Compositor.Core.Geometry;
using Compositor.Core.Project;
using Compositor.Core.Raster;
using Compositor.Core.Rendering;
using SkiaSharp;

var full = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
var side = full ? 4096 : 2048;
var updates = full ? 80 : 40;
var brushSize = full ? 800 : 400;
var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault();
var timings = new List<double>(updates);
var process = Process.GetCurrentProcess();
var session = new EditorSession();
session.CreateDocument(side, side, emptyLayer: true);
var layer = session.ActiveLayer!;
using var stroke = new BrushStroke(layer, null, BrushMode.Paint, brushSize, 0.8, new SKColor(0x24, 0x78, 0xD4), hardness: 0.35);
var total = Stopwatch.StartNew();

for (var index = 0; index < updates; index++)
{
    var x = side * (0.1 + (0.8 * index / Math.Max(1, updates - 1)));
    var y = side * (0.5 + (0.25 * Math.Sin(index * Math.PI * 4 / updates)));
    var update = Stopwatch.StartNew();
    stroke.Add(new Point(x, y));
    var previewAsset = new ImportedImage(stroke.Preview, stroke.Preview, layer.Name);
    var preview = session.Document! with { Layers = session.Layers.Select(item => item.Id == layer.Id ? item with { Asset = previewAsset } : item).ToList() };
    using var rendered = DocumentRenderer.Flatten(preview);
    update.Stop();
    timings.Add(update.Elapsed.TotalMilliseconds);
}

var commit = Stopwatch.StartNew();
session.ReplaceLayerAsset(layer.Id, stroke.Commit(layer.Name), layer.Transform, "Stress Stroke");
commit.Stop();
var tempDirectory = Path.Combine(Path.GetTempPath(), $"compositor-stress-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDirectory);
var projectPath = Path.Combine(tempDirectory, "stress.comp");
try
{
    var save = Stopwatch.StartNew();
    ProjectStore.Save(ProjectSnapshot.From(session.Document!, session.ActiveLayerId), projectPath);
    save.Stop();
    var reopen = Stopwatch.StartNew();
    var loaded = ProjectStore.Load(projectPath).ToDocument();
    reopen.Stop();
    using var before = DocumentRenderer.Flatten(session.Document!);
    using var after = DocumentRenderer.Flatten(loaded);
    if (!Bitmaps.BytesEqual(before, after))
    {
        throw new InvalidOperationException("Save/reopen changed the rendered pixels.");
    }

    timings.Sort();
    process.Refresh();
    var result = new
    {
        profile = full ? "full" : "quick",
        canvas = $"{side}x{side}",
        updates,
        brushSize,
        previewMedianMs = Percentile(timings, 0.5),
        previewP95Ms = Percentile(timings, 0.95),
        previewMaxMs = timings[^1],
        commitMs = commit.Elapsed.TotalMilliseconds,
        saveMs = save.Elapsed.TotalMilliseconds,
        reopenMs = reopen.Elapsed.TotalMilliseconds,
        totalMs = total.Elapsed.TotalMilliseconds,
        workingSetMiB = process.WorkingSet64 / 1024.0 / 1024.0,
        managedMiB = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0,
        projectMiB = new FileInfo(projectPath).Length / 1024.0 / 1024.0,
        pixelRoundTrip = "pass",
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output is not null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(output, json);
    }
}
finally
{
    Directory.Delete(tempDirectory, recursive: true);
}

static double Percentile(IReadOnlyList<double> sorted, double percentile)
{
    var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
    return Math.Round(sorted[index], 2);
}
