using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Compositor.App.Updates;

namespace Compositor.App.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void SemanticVersionsOrderPrereleasesBeforeStable()
    {
        Assert.True(AppVersion.TryParse("v0.9.0-beta.2", out var beta2));
        Assert.True(AppVersion.TryParse("0.9.0-beta.10+build", out var beta10));
        Assert.True(AppVersion.TryParse("0.9.0", out var stable));
        Assert.True(beta2.CompareTo(beta10) < 0);
        Assert.True(beta10.CompareTo(stable) < 0);
        Assert.False(AppVersion.TryParse("0.9", out _));
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "arm64")]
    public async Task PrereleaseBuildFindsNewerPrereleaseForRequestedArchitecture(Architecture architecture, string arch)
    {
        var json = Releases(
            Release("v0.9.0-beta.1", prerelease: true, arch),
            Release("v0.9.0-beta.2", prerelease: true, arch));
        using var client = new HttpClient(new StubHandler(_ => Json(json)));
        var service = new UpdateService(client, "0.9.0-beta.1", architecture: architecture);

        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("v0.9.0-beta.2", update.Tag);
        Assert.EndsWith($"-win-{arch}.zip", update.Package.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableBuildIgnoresPrereleases()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var json = Releases(Release("v1.1.0-beta.1", prerelease: true, arch));
        using var client = new HttpClient(new StubHandler(_ => Json(json)));

        Assert.Null(await new UpdateService(client, "1.0.0").CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StableBuildSelectsNewerStableRelease()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var json = Releases(Release("v1.1.0-beta.1", prerelease: true, arch), Release("v1.0.1", prerelease: false, arch));
        using var client = new HttpClient(new StubHandler(_ => Json(json)));

        var update = await new UpdateService(client, "1.0.0").CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("v1.0.1", update.Tag);
        Assert.False(update.IsPreRelease);
    }

    [Fact]
    public async Task PackageRequiresItsOwnMatchingChecksumAsset()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var package = $"Compositor-0.9.0-beta.2-win-{arch}.zip";
        var json = $$"""
            [{"tag_name":"v0.9.0-beta.2","name":"Beta 2","draft":false,"prerelease":true,"html_url":"https://example.test/release","assets":[
              {"name":"{{package}}","browser_download_url":"https://example.test/update.zip"},
              {"name":"different-win-{{arch}}.zip.sha256","browser_download_url":"https://example.test/different.sha256"}
            ]}]
            """;
        using var client = new HttpClient(new StubHandler(_ => Json(json)));

        Assert.Null(await new UpdateService(client, "0.9.0-beta.1").CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadMustMatchPublishedChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), $"compositor-update-{Guid.NewGuid():N}");
        var payload = Encoding.UTF8.GetBytes("verified package");
        var name = "Compositor-0.9.0-beta.2-win-x64.zip";
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{checksum}  {name}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new UpdateService(client, "0.9.0-beta.1", root);
        Assert.True(AppVersion.TryParse("0.9.0-beta.2", out var version));
        var release = new UpdateRelease("v0.9.0-beta.2", version, "Beta 2", true, new Uri("https://example.test/release"),
            new UpdateAsset(name, new Uri("https://example.test/update.zip")), new UpdateAsset(name + ".sha256", new Uri("https://example.test/update.zip.sha256")));

        try
        {
            var path = await service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path + ".partial"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ChecksumMustNameTheSelectedPackage()
    {
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseChecksum(new string('a', 64) + "  other.zip", "selected.zip"));
    }

    [Fact]
    public async Task ChecksumMismatchRemovesPartialDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"compositor-update-{Guid.NewGuid():N}");
        var payload = Encoding.UTF8.GetBytes("tampered package");
        var name = "Compositor-0.9.0-beta.2-win-x64.zip";
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('a', 64)}  {name}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new UpdateService(client, "0.9.0-beta.1", root);
        Assert.True(AppVersion.TryParse("0.9.0-beta.2", out var version));
        var release = new UpdateRelease("v0.9.0-beta.2", version, "Beta 2", true, new Uri("https://example.test/release"),
            new UpdateAsset(name, new Uri("https://example.test/update.zip")), new UpdateAsset(name + ".sha256", new Uri("https://example.test/update.zip.sha256")));

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string Releases(params string[] releases) => $"[{string.Join(',', releases)}]";

    private static string Release(string tag, bool prerelease, string arch) => $$"""
        {"tag_name":"{{tag}}","name":"{{tag}}","draft":false,"prerelease":{{prerelease.ToString().ToLowerInvariant()}},"html_url":"https://example.test/{{tag}}","assets":[
          {"name":"Compositor-{{tag.TrimStart('v')}}-win-{{arch}}.zip","browser_download_url":"https://example.test/{{tag}}.zip"},
          {"name":"Compositor-{{tag.TrimStart('v')}}-win-{{arch}}.zip.sha256","browser_download_url":"https://example.test/{{tag}}.zip.sha256"}
        ]}
        """;

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
