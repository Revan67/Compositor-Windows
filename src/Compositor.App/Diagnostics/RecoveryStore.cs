using System.Text.Json;
using Compositor.Core.Project;

namespace Compositor.App.Diagnostics;

internal sealed record RecoveryInfo(string? SourcePath, DateTimeOffset SavedAtUtc);

/// <summary>A single crash-recovery slot, separate from and never substituted for the user's project.</summary>
internal static class RecoveryStore
{
    private const string ProjectName = "recovery.comp";
    private const string MetadataName = "recovery.json";

    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "Recovery");
    public static string ProjectPath => ProjectPathIn(DirectoryPath);
    public static string MetadataPath => MetadataPathIn(DirectoryPath);
    public static bool Exists => File.Exists(ProjectPath);

    public static void Save(ProjectSnapshot snapshot, string? sourcePath, DateTimeOffset? now = null, string? directory = null)
    {
        directory ??= DirectoryPath;
        Directory.CreateDirectory(directory);
        ProjectStore.Save(snapshot, ProjectPathIn(directory));
        var json = JsonSerializer.Serialize(new RecoveryInfo(sourcePath, now ?? DateTimeOffset.UtcNow));
        File.WriteAllText(MetadataPathIn(directory), json);
    }

    public static (ProjectSnapshot Snapshot, RecoveryInfo Info) Load(string? directory = null)
    {
        directory ??= DirectoryPath;
        var projectPath = ProjectPathIn(directory);
        var metadataPath = MetadataPathIn(directory);
        var snapshot = ProjectStore.Load(projectPath);
        var info = new RecoveryInfo(null, File.GetLastWriteTimeUtc(projectPath));
        try
        {
            if (File.Exists(metadataPath))
            {
                info = JsonSerializer.Deserialize<RecoveryInfo>(File.ReadAllText(metadataPath)) ?? info;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("Recovery", "Recovery metadata could not be read; project data remains usable", e);
        }

        return (snapshot, info);
    }

    public static void Clear(string? directory = null)
    {
        directory ??= DirectoryPath;
        foreach (var path in new[] { ProjectPathIn(directory), MetadataPathIn(directory) })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Recovery", $"Could not remove {path}", e);
            }
        }
    }

    private static string ProjectPathIn(string directory) => Path.Combine(directory, ProjectName);
    private static string MetadataPathIn(string directory) => Path.Combine(directory, MetadataName);
}
