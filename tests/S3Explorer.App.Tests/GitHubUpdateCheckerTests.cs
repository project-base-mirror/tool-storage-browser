using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class GitHubUpdateCheckerTests
{
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
}
