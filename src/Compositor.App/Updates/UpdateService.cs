using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Compositor.App.Updates;

internal sealed record AppVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease) : IComparable<AppVersion>
{
    public bool IsPreRelease => PreRelease.Count != 0;

    public static bool TryParse(string? value, out AppVersion version)
    {
        version = new AppVersion(0, 0, 0, []);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimStart('v', 'V');
        var metadata = text.IndexOf('+', StringComparison.Ordinal);
        if (metadata >= 0)
        {
            text = text[..metadata];
        }

        var split = text.Split('-', 2);
        var numbers = split[0].Split('.');
        if (numbers.Length != 3 || !int.TryParse(numbers[0], CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(numbers[1], CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(numbers[2], CultureInfo.InvariantCulture, out var patch) || major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        var labels = split.Length == 2 ? split[1].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
        if (split.Length == 2 && labels.Length == 0)
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, labels);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var numeric = Major.CompareTo(other.Major);
        if (numeric == 0) numeric = Minor.CompareTo(other.Minor);
        if (numeric == 0) numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;
        if (!IsPreRelease || !other.IsPreRelease) return IsPreRelease == other.IsPreRelease ? 0 : IsPreRelease ? -1 : 1;

        for (var i = 0; i < Math.Max(PreRelease.Count, other.PreRelease.Count); i++)
        {
            if (i >= PreRelease.Count) return -1;
            if (i >= other.PreRelease.Count) return 1;
            var leftNumber = int.TryParse(PreRelease[i], CultureInfo.InvariantCulture, out var left);
            var rightNumber = int.TryParse(other.PreRelease[i], CultureInfo.InvariantCulture, out var right);
            if (leftNumber && rightNumber)
            {
                var result = left.CompareTo(right);
                if (result != 0) return result;
            }
            else if (leftNumber != rightNumber)
            {
                return leftNumber ? -1 : 1;
            }
            else
            {
                var result = string.Compare(PreRelease[i], other.PreRelease[i], StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
            }
        }

        return 0;
    }
}

internal sealed record UpdateAsset(string Name, Uri DownloadUrl);
internal sealed record UpdateRelease(string Tag, AppVersion Version, string Name, bool IsPreRelease, Uri PageUrl, UpdateAsset Package, UpdateAsset Checksum);

internal sealed class UpdateService
{
    private static readonly Uri ReleasesApi = new("https://api.github.com/repos/Revan67/Compositor-Windows/releases?per_page=20");
    private readonly HttpClient _client;
    private readonly AppVersion _currentVersion;
    private readonly string _stagingRoot;
    private readonly Architecture _architecture;

    public UpdateService(HttpClient? client = null, string? currentVersion = null, string? stagingRoot = null, Architecture? architecture = null)
    {
        _client = client ?? CreateClient();
        var versionText = currentVersion ?? typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!AppVersion.TryParse(versionText, out _currentVersion))
        {
            _currentVersion = new AppVersion(0, 0, 0, []);
        }
        _stagingRoot = stagingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "Updates");
        _architecture = architecture ?? RuntimeInformation.ProcessArchitecture;
    }

    public string CurrentVersion => $"{_currentVersion.Major}.{_currentVersion.Minor}.{_currentVersion.Patch}" +
        (_currentVersion.IsPreRelease ? $"-{string.Join('.', _currentVersion.PreRelease)}" : string.Empty);

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(ReleasesApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var architecture = _architecture == Architecture.Arm64 ? "arm64" : "x64";
        UpdateRelease? newest = null;

        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!TryBoolean(item, "draft", out var draft) || draft || !TryBoolean(item, "prerelease", out var prerelease)) continue;
            if (prerelease && !_currentVersion.IsPreRelease) continue;
            if (!TryString(item, "tag_name", out var tag)) continue;
            if (!AppVersion.TryParse(tag, out var version) || version.CompareTo(_currentVersion) <= 0) continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            UpdateAsset? package = null;
            var releaseAssets = new List<UpdateAsset>();
            foreach (var asset in assets.EnumerateArray())
            {
                if (!TryString(asset, "name", out var name) || Path.GetFileName(name) != name ||
                    !TryString(asset, "browser_download_url", out var urlText) || !Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                var updateAsset = new UpdateAsset(name, url);
                releaseAssets.Add(updateAsset);
                if (name.EndsWith($"-win-{architecture}.zip", StringComparison.OrdinalIgnoreCase)) package = updateAsset;
            }

            var checksum = package is null ? null : releaseAssets.FirstOrDefault(asset =>
                string.Equals(asset.Name, package.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (package is null || checksum is null) continue;
            if (!TryString(item, "html_url", out var pageText) || !Uri.TryCreate(pageText, UriKind.Absolute, out var pageUrl) || pageUrl.Scheme != Uri.UriSchemeHttps) continue;
            var nameText = TryString(item, "name", out var releaseName) ? releaseName : tag;
            var candidate = new UpdateRelease(tag, version, nameText, prerelease, pageUrl, package, checksum);
            if (newest is null || candidate.Version.CompareTo(newest.Version) > 0) newest = candidate;
        }

        return newest;
    }

    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(release.Package.Name) != release.Package.Name)
        {
            throw new InvalidDataException("The update package has an unsafe filename.");
        }

        var root = Path.Combine(_stagingRoot, SafeDirectoryName(release.Tag));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, release.Package.Name);
        var partial = destination + ".partial";

        var checksumText = await _client.GetStringAsync(release.Checksum.DownloadUrl, cancellationToken);
        var expected = ParseChecksum(checksumText, release.Package.Name);
        try
        {
            using var response = await _client.GetAsync(release.Package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    if (total > 0) progress?.Report((double)copied / total.Value);
                }
            }

            string actual;
            await using (var downloaded = File.OpenRead(partial))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken)).ToLowerInvariant();
            }
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded update did not match its published SHA-256 checksum.");
            }

            File.Move(partial, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    internal static string ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0].Length == 64 && fields.Skip(1).Any(field => string.Equals(field.TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase)) &&
                fields[0].All(Uri.IsHexDigit))
            {
                return fields[0].ToLowerInvariant();
            }
        }

        throw new InvalidDataException($"The published checksum does not contain an entry for {fileName}.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Compositor-Windows", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static bool TryBoolean(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = candidate.GetBoolean();
        return true;
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(candidate.GetString()))
        {
            return false;
        }

        value = candidate.GetString()!;
        return true;
    }

    private static string SafeDirectoryName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
