using S3Explorer.App;
using System.Net;
using System.Text;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class GitHubUpdateCheckerTests
{
    [Fact]
    public void ParsesPagesManifest()
    {
        const string payload = """
        {
          "schemaVersion": 1,
          "tagName": "v0.5.5",
          "version": "0.5.5",
          "releasePage": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/v0.5.5",
          "downloadUrl": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.5.5/S3Explorer-v0.5.5-win-x64.zip",
          "notes": "Reliable update checks",
          "publishedAt": "2026-07-28T00:00:00Z"
        }
        """;

        var release = GitHubUpdateChecker.ParseManifest(payload);

        Assert.Equal("v0.5.5", release.TagName);
        Assert.Equal(new Version(0, 5, 5), release.Version);
        Assert.Equal(UpdateReleaseSource.PagesManifest, release.Source);
        Assert.False(release.IsFromCache);
        Assert.EndsWith("/S3Explorer-v0.5.5-win-x64.zip", release.PreferredDownload!.AbsoluteUri);
    }

    [Theory]
    [InlineData(0, "S3Explorer-v0.6.10-win-x64.zip")]
    [InlineData(1, "S3Explorer-v0.6.10-win-x64-self-contained.zip")]
    [InlineData(2, "S3Explorer-v0.6.10-win-x64-framework-dependent-setup.msi")]
    [InlineData(3, "S3Explorer-v0.6.10-win-x64-setup.msi")]
    public void SelectsMatchingPackageFromVersionTwoManifest(int kindValue, string expectedName)
    {
        var kind = (UpdatePackageKind)kindValue;
        const string payload = """
        {
          "schemaVersion": 2,
          "tagName": "v0.6.10",
          "version": "0.6.10",
          "releasePage": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/v0.6.10",
          "downloads": {
            "portableFrameworkDependent": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.6.10/S3Explorer-v0.6.10-win-x64.zip",
            "portableSelfContained": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.6.10/S3Explorer-v0.6.10-win-x64-self-contained.zip",
            "installerFrameworkDependent": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.6.10/S3Explorer-v0.6.10-win-x64-framework-dependent-setup.msi",
            "installerSelfContained": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.6.10/S3Explorer-v0.6.10-win-x64-setup.msi"
          }
        }
        """;

        var release = GitHubUpdateChecker.ParseManifest(payload, kind);

        Assert.Equal(kind, release.RecommendedPackage);
        Assert.EndsWith(expectedName, release.PreferredDownload!.AbsoluteUri);
    }

    [Fact]
    public void RejectsManifestWhenVersionAndTagDiffer()
    {
        const string payload = """
        {
          "schemaVersion": 1,
          "tagName": "v0.5.5",
          "version": "0.5.4",
          "releasePage": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/v0.5.5"
        }
        """;

        Assert.Throws<InvalidDataException>(() => GitHubUpdateChecker.ParseManifest(payload));
    }

    [Fact]
    public async Task UsesPagesBeforeGitHubApiAndWritesCache()
    {
        var cachePath = TemporaryCachePath();
        var handler = new QueueHandler(JsonResponse(HttpStatusCode.OK, Manifest("v0.5.5")));
        try
        {
            using var client = new HttpClient(handler);
            using var checker = new GitHubUpdateChecker(client, cachePath, TimeSpan.FromSeconds(2));

            var release = await checker.GetLatestAsync();

            Assert.Equal(UpdateReleaseSource.PagesManifest, release.Source);
            Assert.True(File.Exists(cachePath));
            Assert.Single(handler.Requests);
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public async Task FallsBackToGitHubApiWhenPagesManifestIsUnavailable()
    {
        var cachePath = TemporaryCachePath();
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound),
            JsonResponse(HttpStatusCode.OK, Release("v0.5.5")));
        try
        {
            using var client = new HttpClient(handler);
            using var checker = new GitHubUpdateChecker(client, cachePath, TimeSpan.FromSeconds(2));

            var release = await checker.GetLatestAsync();

            Assert.Equal(UpdateReleaseSource.GitHubApi, release.Source);
            Assert.Equal([ProjectLinks.UpdateManifest, ProjectLinks.LatestReleaseApi],
                handler.Requests.Select(uri => uri.AbsoluteUri));
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public async Task ReturnsLastSuccessfulCacheWhenBothOnlineChannelsFail()
    {
        var cachePath = TemporaryCachePath();
        try
        {
            using (var seedClient = new HttpClient(new QueueHandler(
                       JsonResponse(HttpStatusCode.OK, Manifest("v0.5.5")))))
            using (var seedChecker = new GitHubUpdateChecker(seedClient, cachePath, TimeSpan.FromSeconds(2)))
                await seedChecker.GetLatestAsync();

            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString());
            var handler = new QueueHandler(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                limited);
            using var client = new HttpClient(handler);
            using var checker = new GitHubUpdateChecker(client, cachePath, TimeSpan.FromSeconds(2));

            var release = await checker.GetLatestAsync();

            Assert.True(release.IsFromCache);
            Assert.Equal(UpdateReleaseSource.Cache, release.Source);
            Assert.NotNull(release.CachedAtUtc);
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public async Task ReportsRateLimitWhenNoCacheExists()
    {
        var cachePath = TemporaryCachePath();
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound),
            limited);
        try
        {
            using var client = new HttpClient(handler);
            using var checker = new GitHubUpdateChecker(client, cachePath, TimeSpan.FromSeconds(2));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => checker.GetLatestAsync());

            Assert.Contains("HTTP 429", exception.Message);
            Assert.Contains("3 分钟后重试", exception.Message);
        }
        finally
        {
            DeleteCacheDirectory(cachePath);
        }
    }

    [Fact]
    public void ParsesLatestReleaseAndSelectsFrameworkDependentAsset()
    {
        const string payload = """
        {
          "tag_name": "v0.5.3",
          "html_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/v0.5.3",
          "body": "Release notes",
          "published_at": "2026-07-27T03:00:00Z",
          "assets": [
            {
              "name": "S3Explorer-win-x64.zip",
              "browser_download_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.5.1/S3Explorer-win-x64.zip"
            },
            {
              "name": "S3Explorer-v0.5.3-win-x64-self-contained.zip",
              "browser_download_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.5.3/S3Explorer-v0.5.3-win-x64-self-contained.zip"
            },
            {
              "name": "S3Explorer-v0.5.3-win-x64.zip",
              "browser_download_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.5.3/S3Explorer-v0.5.3-win-x64.zip"
            }
          ]
        }
        """;

        var release = GitHubUpdateChecker.ParseRelease(payload);

        Assert.Equal("v0.5.3", release.TagName);
        Assert.Equal(new Version(0, 5, 3), release.Version);
        Assert.Equal("Release notes", release.Notes);
        Assert.EndsWith("/S3Explorer-v0.5.3-win-x64.zip", release.PreferredDownload!.AbsoluteUri);
        Assert.True(release.IsNewerThan(new Version(0, 5, 0, 0)));
        Assert.False(release.IsNewerThan(new Version(0, 5, 3, 0)));
    }

    [Theory]
    [InlineData(3, "S3Explorer-v0.6.10-win-x64-setup.msi")]
    [InlineData(2, "S3Explorer-v0.6.10-win-x64-framework-dependent-setup.msi")]
    public void SelectsMatchingInstallerFromGitHubRelease(int kindValue, string expectedName)
    {
        var kind = (UpdatePackageKind)kindValue;
        var payload = $$"""
        {
          "tag_name": "v0.6.10",
          "html_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/v0.6.10",
          "assets": [
            {
              "name": "S3Explorer-v0.6.10-win-x64.zip",
              "browser_download_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.6.10/S3Explorer-v0.6.10-win-x64.zip"
            },
            {
              "name": "{{expectedName}}",
              "browser_download_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.6.10/{{expectedName}}"
            }
          ]
        }
        """;

        var release = GitHubUpdateChecker.ParseRelease(payload, kind);

        Assert.Equal(kind, release.RecommendedPackage);
        Assert.EndsWith(expectedName, release.PreferredDownload!.AbsoluteUri);
    }

    [Theory]
    [InlineData("self-contained", 3)]
    [InlineData("framework-dependent", 2)]
    [InlineData("unknown", 0)]
    public void MapsInstallerFlavor(string value, int expectedValue) =>
        Assert.Equal((UpdatePackageKind)expectedValue, UpdatePackageDetector.FromInstallerFlavor(value));

    [Fact]
    public void LegacyInstallerIsRecognizedByProgramFilesLocation()
    {
        Assert.True(UpdatePackageDetector.IsUnderDirectory(
            @"C:\Program Files\S3 Explorer\S3Explorer.exe",
            @"C:\Program Files"));
        Assert.False(UpdatePackageDetector.IsUnderDirectory(
            @"D:\Portable\S3Explorer.exe",
            @"C:\Program Files"));
    }

    [Fact]
    public void FallsBackToLegacyUnversionedAssetName()
    {
        const string payload = """
        {
          "tag_name": "v0.5.2",
          "html_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/v0.5.2",
          "assets": [{
            "name": "S3Explorer-win-x64.zip",
            "browser_download_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/v0.5.2/S3Explorer-win-x64.zip"
          }]
        }
        """;

        var release = GitHubUpdateChecker.ParseRelease(payload);

        Assert.EndsWith("/S3Explorer-win-x64.zip", release.PreferredDownload!.AbsoluteUri);
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3.0", true)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3-beta", false)]
    [InlineData("latest", false)]
    public void AcceptsOnlyStableNumericReleaseTags(string value, bool expected)
    {
        Assert.Equal(expected, GitHubUpdateChecker.TryParseReleaseVersion(value, out _));
    }

    [Fact]
    public void RejectsNonHttpsReleaseLinks()
    {
        const string payload = """
        { "tag_name": "v0.5.1", "html_url": "file:///tmp/update.exe", "assets": [] }
        """;

        Assert.Throws<InvalidDataException>(() => GitHubUpdateChecker.ParseRelease(payload));
    }

    private static string Manifest(string tag) => $$"""
        {
          "schemaVersion": 1,
          "tagName": "{{tag}}",
          "version": "{{tag.TrimStart('v')}}",
          "releasePage": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/{{tag}}",
          "downloadUrl": "https://github.com/project-base-mirror/tool-storage-browser/releases/download/{{tag}}/S3Explorer-{{tag}}-win-x64.zip",
          "notes": "Release notes"
        }
        """;

    private static string Release(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/project-base-mirror/tool-storage-browser/releases/tag/{{tag}}",
          "body": "Release notes",
          "assets": []
        }
        """;

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static string TemporaryCachePath() => Path.Combine(
        Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"), "update-cache.json");

    private static void DeleteCacheDirectory(string cachePath)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
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
            if (_responses.Count == 0)
                throw new InvalidOperationException("No fake HTTP response remains.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
