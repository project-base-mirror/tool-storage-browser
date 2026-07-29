using S3Explorer.Cli;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class CliTransferSettingsTests
{
    [Fact]
    public void DefaultsMatchDesktopTransferDefaults()
    {
        var settings = CliTransferSettings.Parse(CliArguments.Parse(["upload"]));

        Assert.Equal(4, settings.Transfers);
        Assert.Equal(4, settings.MultipartConcurrency);
        Assert.Equal(64L * 1024 * 1024, settings.MultipartThresholdBytes);
        Assert.Equal(16L * 1024 * 1024, settings.PartSizeBytes);
        Assert.Equal(0, settings.UploadBytesPerSecond);
        Assert.Equal(0, settings.DownloadBytesPerSecond);
    }

    [Fact]
    public void ExplicitValuesAreConvertedToBytes()
    {
        var settings = CliTransferSettings.Parse(CliArguments.Parse([
            "upload",
            "--transfers", "8",
            "--multipart-concurrency", "6",
            "--multipart-threshold", "128",
            "--part-size", "32",
            "--upload-limit", "1024",
            "--download-limit", "2048"]));

        Assert.Equal(8, settings.Transfers);
        Assert.Equal(6, settings.MultipartConcurrency);
        Assert.Equal(128L * 1024 * 1024, settings.MultipartThresholdBytes);
        Assert.Equal(32L * 1024 * 1024, settings.PartSizeBytes);
        Assert.Equal(1024L * 1024, settings.UploadBytesPerSecond);
        Assert.Equal(2048L * 1024, settings.DownloadBytesPerSecond);
    }

    [Theory]
    [InlineData("--transfers", "0")]
    [InlineData("--multipart-concurrency", "33")]
    [InlineData("--multipart-threshold", "4")]
    [InlineData("--part-size", "5121")]
    [InlineData("--upload-limit", "-1")]
    public void InvalidValuesAreRejected(string option, string value)
    {
        Assert.Throws<CliUsageException>(() =>
            CliTransferSettings.Parse(CliArguments.Parse(["upload", option, value])));
    }

    [Fact]
    public async Task BulkOperationsRespectConfiguredConcurrency()
    {
        var runtime = CliTransferRuntime.Create(
            CliArguments.Parse(["upload", "--transfers", "2"]));
        var active = 0;
        var maximum = 0;

        await runtime.ForEachAsync(
            Enumerable.Range(0, 8),
            async (_, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                await Task.Delay(20, cancellationToken);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);

        Assert.Equal(2, maximum);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        do
        {
            observed = maximum;
            if (observed >= value) return;
        } while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
    }
}
