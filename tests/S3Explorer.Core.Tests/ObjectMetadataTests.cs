using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectMetadataTests
{
    [Fact]
    public void ObjectTagsAreNormalizedAndLimitedToTen()
    {
        var tags = ObjectTagValidator.Validate([new ObjectTag(" channel ", " stable ")]);

        Assert.Equal(new ObjectTag("channel", "stable"), Assert.Single(tags));
        Assert.Throws<ArgumentException>(() => ObjectTagValidator.Validate(
            Enumerable.Range(0, 11).Select(index => new ObjectTag($"key-{index}", "value"))));
    }

    [Fact]
    public void ObjectMetadataNormalizesAwsPrefixAndRejectsHeaderInjection()
    {
        var metadata = ObjectMetadataValidator.Validate(new Dictionary<string, string>
        {
            ["x-amz-meta-build"] = " 42 "
        });

        Assert.Equal("42", metadata["build"]);
        Assert.Throws<ArgumentException>(() => ObjectMetadataValidator.Validate(
            new Dictionary<string, string> { ["build"] = "42\r\nInjected: true" }));
    }

    [Fact]
    public void ObjectCapabilitiesRemainConservativeForUnverifiedProviders()
    {
        Assert.True(ObjectCapabilityMatrix.For(S3ServiceType.AmazonS3).Tagging.Supported);
        Assert.True(ObjectCapabilityMatrix.For(S3ServiceType.MinIO).MetadataRewrite.Supported);
        Assert.True(ObjectCapabilityMatrix.For(S3ServiceType.AliyunOss).Tagging.Supported);
        Assert.False(ObjectCapabilityMatrix.For(S3ServiceType.CloudflareR2).MetadataRewrite.Supported);
    }
}
