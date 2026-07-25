using System.Net;
using Amazon.S3;
using S3Explorer.Core;
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

    [Theory]
    [InlineData(S3ServiceType.MinIO, "us-east-1")]
    [InlineData(S3ServiceType.BackblazeB2, "us-west-004")]
    [InlineData(S3ServiceType.AliyunOss, "oss-cn-shenzhen")]
    [InlineData(S3ServiceType.Custom, "custom-region")]
    public void CompatibleProvidersOmitBucketLocationConstraint(S3ServiceType serviceType, string region)
    {
        var profile = new ConnectionProfile { ServiceType = serviceType };
        var request = S3CompatibilityPolicy.CreateBucketRequest(profile, "test-bucket", region);

        Assert.Null(request.BucketRegionName);
    }

    [Fact]
    public void AmazonS3IncludesNonDefaultBucketLocationConstraint()
    {
        var profile = new ConnectionProfile { ServiceType = S3ServiceType.AmazonS3 };

        Assert.Equal("eu-west-1",
            S3CompatibilityPolicy.CreateBucketRequest(profile, "test-bucket", " eu-west-1 " ).BucketRegionName);
        Assert.Null(S3CompatibilityPolicy.CreateBucketRequest(profile, "test-bucket", "us-east-1").BucketRegionName);
    }

    [Fact]
    public void MinioConsoleResponseIsClassifiedAsApiPortError()
    {
        var profile = new ConnectionProfile { ServiceType = S3ServiceType.MinIO };
        var exception = new AmazonS3Exception("S3 API Requests must be made to API port.");

        Assert.True(S3CompatibilityPolicy.IsMinioApiPortError(profile, exception));
        Assert.False(S3CompatibilityPolicy.IsMinioApiPortError(
            profile with { ServiceType = S3ServiceType.Custom },
            exception));
    }

    [Fact]
    public void MinioNotFoundWithoutRequestIdIsClassifiedAsEndpointRoutingError()
    {
        var profile = new ConnectionProfile { ServiceType = S3ServiceType.MinIO };
        var exception = new AmazonS3Exception("not found")
        {
            StatusCode = HttpStatusCode.NotFound,
            ErrorCode = "NotFound"
        };

        Assert.True(S3CompatibilityPolicy.IsMinioEndpointRoutingError(profile, exception));
        Assert.False(S3CompatibilityPolicy.IsMinioEndpointRoutingError(
            profile with { ServiceType = S3ServiceType.Custom },
            exception));
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
