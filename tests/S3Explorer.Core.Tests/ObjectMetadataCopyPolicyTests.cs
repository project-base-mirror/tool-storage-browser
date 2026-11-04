using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectMetadataCopyPolicyTests
{
    private static ObjectProperties Source => new("bucket", " key ", 1, null, "etag", "text/plain", null,
        "v1", new Dictionary<string, string>()) { ServerSideEncryptionMethod = "AES256" };

    [Fact]
    public void OnlyAwsHasAnExplicitSeparateCopyCapability()
    {
        foreach (var provider in Enum.GetValues<S3ServiceType>())
        {
            Assert.False(S3ProviderCapabilityRegistry.For(provider).Object.MetadataRewrite.Supported);
            Assert.Equal(provider == S3ServiceType.AmazonS3, ObjectMetadataCopyPolicy.For(provider, Source).Supported);
        }
    }

    [Fact]
    public void ProtectedUnknownOrUnrepresentableSourcesAreNotEligible()
    {
        ObjectProperties[] unsafeSources =
        [
            Source with { ServerSideEncryptionMethod = null },
            Source with { ServerSideEncryptionMethod = "aws:kms" },
            Source with { CustomerEncryptionMethod = "AES256" },
            Source with { EncryptionKeyId = "kms-key" },
            Source with { BucketKeyEnabled = true },
            Source with { VersionId = null },
            Source with { VersionId = "null" },
            Source with { ETag = "" },
            Source with { MissingMetadataCount = 1 },
            Source with { ExpiresHeader = "not-a-date" },
            Source with { ObjectLockMode = "GOVERNANCE" },
            Source with { ObjectLockLegalHoldStatus = "ON" },
            Source with { ObjectLockLegalHoldStatus = "unknown" },
            Source with { ObjectLockRetainUntilDate = DateTimeOffset.UtcNow },
            Source with { Size = ObjectMetadataCopyPolicy.MaximumBytes + 1 },
            Source with { Size = -1 }
        ];
        Assert.All(unsafeSources, source =>
        {
            var result = ObjectMetadataCopyPolicy.For(S3ServiceType.AmazonS3, source);
            Assert.False(result.Supported);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        });
    }

    [Fact]
    public void DestinationValidationPreservesExactKeyIdentity()
    {
        ObjectMetadataCopyPolicy.ValidateDestination(Source, "/new//copy ");
        ObjectMetadataCopyPolicy.ValidateDestination(Source, "key");
        Assert.Throws<ArgumentException>(() => ObjectMetadataCopyPolicy.ValidateDestination(Source, Source.Key));
        Assert.Throws<ArgumentException>(() => ObjectMetadataCopyPolicy.ValidateDestination(Source, ""));
        Assert.Throws<ArgumentException>(() => ObjectMetadataCopyPolicy.ValidateDestination(Source, "bad\0key"));
        Assert.Throws<ArgumentException>(() => ObjectMetadataCopyPolicy.ValidateDestination(Source, new string('中', 342)));
    }
}
