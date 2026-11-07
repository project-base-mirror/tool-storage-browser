using Xunit;
namespace S3Explorer.Core.Tests;

public sealed class MetadataProviderEditPolicyTests
{
    private static ObjectProperties Plain => new("bucket", "key", 42, null, "etag", "text/plain", null, null, new Dictionary<string, string>());

    [Fact]
    public void SupabaseOrdinaryMetadataIsEditableButUnsupportedHeadersAreNotSilentlyDropped()
    {
        Assert.True(ObjectMetadataRewritePolicy.For(S3ServiceType.SupabaseStorage, Plain).Supported);
        Assert.False(ObjectMetadataRewritePolicy.For(S3ServiceType.SupabaseStorage, Plain with { ExpiresUtc = DateTimeOffset.UtcNow }).Supported);
        Assert.False(ObjectMetadataRewritePolicy.CanEditHeader(S3ServiceType.SupabaseStorage, "expires"));
        Assert.False(ObjectMetadataRewritePolicy.CanEditHeader(S3ServiceType.SupabaseStorage, "content-disposition"));
        Assert.True(ObjectMetadataRewritePolicy.CanEditHeader(S3ServiceType.SupabaseStorage, "cache-control"));
        Assert.Throws<NotSupportedException>(() => ObjectMetadataRewritePolicy.ValidateEdits(S3ServiceType.SupabaseStorage, new(ContentDisposition: "inline")));
        Assert.Throws<NotSupportedException>(() => ObjectMetadataRewritePolicy.ValidateEdits(S3ServiceType.SupabaseStorage,
            new(Metadata: new Dictionary<string, string> { ["non-ascii"] = "内容" })));
    }

    [Fact]
    public void GcsRequiresBothExactPositiveRevisionTokens()
    {
        Assert.False(ObjectMetadataRewritePolicy.For(S3ServiceType.GoogleCloudStorage, Plain).Supported);
        Assert.True(ObjectMetadataRewritePolicy.For(S3ServiceType.GoogleCloudStorage, Plain with { ProviderGeneration = "1234567890123456789", ProviderMetageneration = "9" }).Supported);
        Assert.False(ObjectMetadataRewritePolicy.For(S3ServiceType.GoogleCloudStorage, Plain with { ProviderGeneration = "1", ProviderMetageneration = "0" }).Supported);
    }
}
