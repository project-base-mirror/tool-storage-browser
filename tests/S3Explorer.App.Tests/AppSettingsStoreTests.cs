using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public async Task InvalidSemanticValuesAreNormalizedBeforeRuntimeUse()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "windowWidth": 1,
                  "windowHeight": 1,
                  "objectPageSize": 0,
                  "objectCacheLimit": 0,
                  "concurrentTransfers": 0,
                  "multipartConcurrency": 0,
                  "multipartThresholdMb": 0,
                  "partSizeMb": 0,
                  "retryCount": -1,
                  "retryDelaySeconds": -1,
                  "uploadLimitKibPerSecond": -1,
                  "objectColumnWidths": [1],
                  "sortColumn": 99
                }
                """);

            var settings = await new AppSettingsStore(path).LoadAsync();

            Assert.Equal(960, settings.WindowWidth);
            Assert.Equal(600, settings.WindowHeight);
            Assert.Equal(1, settings.ObjectPageSize);
            Assert.Equal(1000, settings.ObjectCacheLimit);
            Assert.Equal(1, settings.ConcurrentTransfers);
            Assert.Equal(1, settings.MultipartConcurrency);
            Assert.Equal(5, settings.MultipartThresholdMb);
            Assert.Equal(5, settings.PartSizeMb);
            Assert.Equal(0, settings.RetryCount);
            Assert.Equal(0, settings.RetryDelaySeconds);
            Assert.Equal(0, settings.UploadLimitKibPerSecond);
            Assert.Equal(5, settings.ObjectColumnWidths.Length);
            Assert.Equal(4, settings.SortColumn);
            new TransferRuntimeConfiguration().Apply(settings);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MalformedSettingsArePreservedAndDefaultsAreReportedAsRecovery()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, "{truncated");
        try
        {
            var store = new AppSettingsStore(path);

            var settings = await store.LoadAsync();

            Assert.Equal(1280, settings.WindowWidth);
            Assert.True(store.LastRecovery?.UsedDefault);
            Assert.Single(Directory.EnumerateFiles(root, "settings.json.corrupt-*-primary-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
