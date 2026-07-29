using System.Diagnostics;
using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class RuntimeResilienceTests
{
    [Fact]
    public void ErrorDetailsRedactSecretsFromEveryCopiedField()
    {
        var details = ErrorDialog.BuildDetails(
            "SecretKey=operation-secret",
            "https://storage.example/?X-Amz-Signature=signature-secret",
            "AmazonS3Exception",
            "403",
            "request-id",
            "SecretKey=message-secret Authorization: bearer-token",
            "https://cdn.example/?X-Amz-Signature=suggestion-secret");

        Assert.DoesNotContain("operation-secret", details, StringComparison.Ordinal);
        Assert.DoesNotContain("signature-secret", details, StringComparison.Ordinal);
        Assert.DoesNotContain("message-secret", details, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-token", details, StringComparison.Ordinal);
        Assert.DoesNotContain("suggestion-secret", details, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerRedactsRotatesAndRemovesExpiredFiles()
    {
        var root = TemporaryDirectory();
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.FromHours(8));
        try
        {
            var expired = Path.Combine(root, "s3explorer-2026-06-01.log");
            File.WriteAllText(expired, "expired");
            File.SetLastWriteTimeUtc(expired, now.UtcDateTime.AddDays(-40));
            var logger = new SimpleFileLogger(root, retentionDays: 30, maximumFileBytes: 256, () => now);

            for (var index = 0; index < 20; index++)
                logger.Error($"message-{index:D2} SecretKey=secret-{index:D2} " + new string('x', 48));

            var files = Directory.EnumerateFiles(root, "s3explorer-2026-07-30.log*").ToArray();
            Assert.True(files.Length >= 2);
            Assert.False(File.Exists(expired));
            var combined = string.Join("\n", files.Select(File.ReadAllText));
            Assert.DoesNotContain("secret-", combined, StringComparison.Ordinal);
            Assert.Contains("***", combined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ShutdownTimeoutCancelsExitWithoutWaitingForever()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            MainForm.AwaitShutdownStepAsync(
                pending.Task,
                "保存传输队列",
                TimeSpan.FromMilliseconds(50)));

        stopwatch.Stop();
        Assert.Contains("退出已取消", error.Message, StringComparison.Ordinal);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 20, 2000);
        pending.SetResult();
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
