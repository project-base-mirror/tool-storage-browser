using System.Net;
using System.Security.Cryptography;
using System.Text;
using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class UpdateDownloadServiceTests
{
    [Fact]
    public async Task DownloadsInstallerAndVerifiesReleaseChecksum()
    {
        var root = TemporaryDirectory();
        var packageBytes = Encoding.UTF8.GetBytes("verified-msi-payload");
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var release = Release();
        var handler = new QueueHandler(
            Response($"{hash} *{release.PreferredAssetName}\n", "text/plain"),
            Response(packageBytes, "application/octet-stream"));
        try
        {
            using var client = new HttpClient(handler);
            using var service = new UpdateDownloadService(client, root);

            var package = await service.DownloadAsync(
                release, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(hash, package.Sha256);
            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(package.PackagePath, TestContext.Current.CancellationToken));
            Assert.Equal([release.ChecksumsDownload!, release.PreferredDownload!], handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RejectsChecksumMismatchAndRemovesPartialDownload()
    {
        var root = TemporaryDirectory();
        var release = Release();
        var handler = new QueueHandler(
            Response($"{new string('a', 64)} *{release.PreferredAssetName}\n", "text/plain"),
            Response(Encoding.UTF8.GetBytes("wrong-content"), "application/octet-stream"));
        try
        {
            using var client = new HttpClient(handler);
            using var service = new UpdateDownloadService(client, root);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(
                release, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(root, "*.msi", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RejectsInstallerOutsideProjectRelease()
    {
        var release = Release() with
        {
            PreferredDownload = new Uri("https://example.com/S3Explorer-v0.7.2-win-x64-setup.msi")
        };

        Assert.Throws<InvalidDataException>(() => UpdateDownloadService.ValidateRelease(release));
    }

    [Fact]
    public void ParsesOnlyExactChecksumEntry()
    {
        var expected = new string('b', 64);
        var payload = $"{new string('a', 64)} *other.msi\n{expected} *target.msi\n";

        Assert.Equal(expected, UpdateDownloadService.ParseExpectedSha256(payload, "target.msi"));
        Assert.Throws<InvalidDataException>(() =>
            UpdateDownloadService.ParseExpectedSha256(payload, "missing.msi"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DownloadsPublishedReleaseWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("S3EXPLORER_UPDATE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        const string tag = "v0.7.2";
        const string asset = "S3Explorer-v0.7.2-win-x64-framework-dependent-setup.msi";
        var release = new GitHubReleaseInfo(
            tag,
            new Version(0, 7, 2),
            new Uri($"https://github.com/project-base-mirror/tool-storage-browser/releases/tag/{tag}"),
            new Uri($"https://github.com/project-base-mirror/tool-storage-browser/releases/download/{tag}/{asset}"),
            "integration",
            null,
            UpdatePackageKind.InstallerFrameworkDependent,
            UpdateReleaseSource.GitHubApi,
            null,
            new Uri($"https://github.com/project-base-mirror/tool-storage-browser/releases/download/{tag}/SHA256SUMS.txt"),
            asset);
        var root = TemporaryDirectory();
        try
        {
            using var service = new UpdateDownloadService(downloadRoot: root);

            var package = await service.DownloadAsync(
                release, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(package.Bytes > 1024 * 1024);
            Assert.Equal(64, package.Sha256.Length);
            Assert.True(File.Exists(package.PackagePath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParsesValidatedUpdaterResult()
    {
        var root = TemporaryDirectory();
        try
        {
            var logPath = Path.Combine(root, "install.log");
            var payload = $$"""
            {
              "schemaVersion": 1,
              "status": "completed",
              "targetVersion": "0.7.2",
              "installerExitCode": 0,
              "message": "ok",
              "logPath": "{{logPath.Replace("\\", "\\\\")}}",
              "completedAtUtc": "2026-08-05T01:02:03Z"
            }
            """;

            var result = UpdateInstallerLauncher.ParseResult(payload);

            Assert.True(result.Succeeded);
            Assert.Equal(new Version(0, 7, 2), result.TargetVersion);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static GitHubReleaseInfo Release()
    {
        const string tag = "v0.7.2";
        const string asset = "S3Explorer-v0.7.2-win-x64-setup.msi";
        return new GitHubReleaseInfo(
            tag,
            new Version(0, 7, 2),
            new Uri($"https://github.com/project-base-mirror/tool-storage-browser/releases/tag/{tag}"),
            new Uri($"https://github.com/project-base-mirror/tool-storage-browser/releases/download/{tag}/{asset}"),
            "notes",
            DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            UpdatePackageKind.InstallerSelfContained,
            UpdateReleaseSource.PagesManifest,
            null,
            new Uri($"https://github.com/project-base-mirror/tool-storage-browser/releases/download/{tag}/SHA256SUMS.txt"),
            asset);
    }

    private static HttpResponseMessage Response(string content, string mediaType) =>
        Response(Encoding.UTF8.GetBytes(content), mediaType);

    private static HttpResponseMessage Response(byte[] content, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "S3Explorer.Update.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
