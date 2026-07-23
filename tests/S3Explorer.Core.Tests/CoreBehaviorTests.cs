using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Theory]
    [InlineData("s3://AWS-Prod/game-assets/config/", "AWS-Prod", "game-assets", "config/")]
    [InlineData("s3://MinIO/backup/", "MinIO", "backup", "")]
    [InlineData("s3://Account/", "Account", null, "")]
    public void S3UriParses(string input, string profile, string? bucket, string prefix)
    {
        var location = S3Location.Parse(input);
        Assert.Equal(profile, location.Profile);
        Assert.Equal(bucket, location.Bucket);
        Assert.Equal(prefix, location.Prefix);
    }

    [Fact]
    public void ParentNavigationKeepsBucket()
    {
        var location = S3Location.Parse("s3://A/bucket/a/b/c/");
        Assert.Equal("s3://A/bucket/a/b/", location.Parent().ToString());
    }

    [Fact]
    public void FolderMarkerUsesSlashAndAllowsUnicode()
    {
        Assert.Equal("root/中文/", S3Path.FolderMarker("root/", "中文"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1310720, "1.25 MiB")]
    public void FileSizesAreFormatted(long bytes, string expected) =>
        Assert.Equal(expected, FileSizeFormatter.Format(bytes));

    [Fact]
    public void SensitiveFieldsAreRedacted()
    {
        var value = "SecretKey=abc Authorization: bearer-token https://x/?X-Amz-Signature=123";
        var redacted = SensitiveDataRedactor.Redact(value);
        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("bearer-token", redacted);
        Assert.DoesNotContain("Signature=123", redacted);
    }

    [Theory]
    [InlineData("SlowDown", 503, true)]
    [InlineData("AccessDenied", 403, false)]
    [InlineData(null, 429, true)]
    public void RetryClassificationWorks(string? code, int? status, bool expected) =>
        Assert.Equal(expected, RetryClassifier.ShouldRetry(code, status));
}
