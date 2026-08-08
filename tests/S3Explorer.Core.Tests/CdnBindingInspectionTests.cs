using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CdnBindingInspectionTests
{
    [Fact]
    public async Task InspectionRecursesToFirstObjectAndProbesMappedUrl()
    {
        var profile = new CdnProfile { Name = "cdn", BaseUrl = "https://cdn.example.com/base" };
        var binding = new CdnBinding
        {
            StorageProfileId = Guid.NewGuid(),
            Bucket = "site",
            SourcePrefix = "deploy/",
            CdnProfileId = profile.Id,
            CdnPathPrefix = "public/"
        };
        var requestedPrefixes = new List<string>();
        Uri? probedUrl = null;

        var result = await CdnBindingInspector.InspectAsync(
            profile,
            binding,
            (prefix, _, _) =>
            {
                requestedPrefixes.Add(prefix);
                return Task.FromResult(prefix == "deploy/"
                    ? new PagedObjectResult(
                        [new S3ObjectEntry("deploy/game/", "game", 0, true, null, string.Empty)],
                        null,
                        false)
                    : new PagedObjectResult(
                        [new S3ObjectEntry("deploy/game/config.json", "config.json", 12, false, DateTimeOffset.UtcNow, "STANDARD")],
                        null,
                        false));
            },
            (url, _) =>
            {
                probedUrl = url;
                return Task.FromResult(new CdnProbeResult(
                    url, url, 200, "OK", TimeSpan.Zero, TimeSpan.Zero, 0, 12,
                    "application/json", "HIT", new Dictionary<string, string>()));
            }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["deploy/", "deploy/game/"], requestedPrefixes);
        Assert.Equal("deploy/game/config.json", result.SourceObject.Key);
        Assert.Equal("https://cdn.example.com/base/public/game/config.json", probedUrl!.AbsoluteUri);
        Assert.Equal("HIT", result.Probe.CacheStatus);
    }

    [Fact]
    public async Task InspectionReturnsNullWithoutSendingHeadWhenPrefixHasNoFiles()
    {
        var profile = new CdnProfile { Name = "cdn", BaseUrl = "https://cdn.example.com" };
        var binding = new CdnBinding
        {
            StorageProfileId = Guid.NewGuid(),
            Bucket = "empty",
            CdnProfileId = profile.Id
        };
        var probeCalled = false;

        var result = await CdnBindingInspector.InspectAsync(
            profile,
            binding,
            (_, _, _) => Task.FromResult(new PagedObjectResult([], null, false)),
            (url, _) =>
            {
                probeCalled = true;
                throw new InvalidOperationException(url.AbsoluteUri);
            }, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(probeCalled);
    }
}
