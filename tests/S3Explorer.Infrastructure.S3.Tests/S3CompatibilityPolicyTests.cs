using System.Net;
using Amazon.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class S3CompatibilityPolicyTests
{
    [Fact]
    public void ForbiddenListBucketsIsAcceptedAsRestrictedConnection()
    {
        var exception = new AmazonS3Exception("denied")
        {
            StatusCode = HttpStatusCode.Forbidden,
            ErrorCode = "AccessDenied"
        };

        Assert.True(S3CompatibilityPolicy.IsRestrictedListBuckets(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed")]
    [InlineData(HttpStatusCode.NotImplemented, "NotImplemented")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    public void UnsupportedBatchDeleteFallsBackToSingleDeletes(HttpStatusCode status, string code)
    {
        var exception = new AmazonS3Exception(code)
        {
            StatusCode = status,
            ErrorCode = code
        };

        Assert.True(S3CompatibilityPolicy.ShouldFallbackToSingleDelete(exception));
    }

    [Fact]
    public void MultipartCopyPartSizeStaysWithinS3Limits()
    {
        var sixTibibytes = 6L * 1024 * 1024 * 1024 * 1024;
        var partSize = S3CompatibilityPolicy.CalculateCopyPartSize(sixTibibytes);

        Assert.InRange(partSize, 64L * 1024 * 1024, 5L * 1024 * 1024 * 1024);
        Assert.True((sixTibibytes + partSize - 1) / partSize <= 10_000);
        Assert.Equal(0, partSize % (8L * 1024 * 1024));
    }

    [Fact]
    public void EntityTooLargeRequiresMultipartCopy()
    {
        var exception = new AmazonS3Exception("too large")
        {
            StatusCode = HttpStatusCode.BadRequest,
            ErrorCode = "EntityTooLarge"
        };

        Assert.True(S3CompatibilityPolicy.RequiresMultipartCopy(exception));
    }
}
