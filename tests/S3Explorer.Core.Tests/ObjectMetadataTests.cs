using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectMetadataTests
{
    [Fact]
    public void SnapshotValidationPreservesDistinctPaddedTagsAndMetadataAcrossRepeatedValidation()
    {
        var original = new ObjectWriteHeaders(Metadata: new Dictionary<string, string> { ["keep"] = "  value  " },
            Tags: [new("team", " prod "), new(" team", " "), new(" ", "")])
        {
            ContentLanguage = " en-US ",
            WebsiteRedirectLocation = "/a b",
            PreserveExistingValues = true
        };
        var result = original.ValidateAndNormalize().ValidateAndNormalize();
        Assert.Equal(original.Tags, result.Tags);
        Assert.Equal("  value  ", result.Metadata!["keep"]);
        Assert.Equal(original.ContentLanguage, result.ContentLanguage);
        Assert.Equal(original.WebsiteRedirectLocation, result.WebsiteRedirectLocation);
        Assert.True(result.PreserveExistingValues);
        Assert.Throws<ArgumentException>(() => ObjectTagValidator.ValidatePreservingValues([new("", "")]));
        Assert.Throws<ArgumentException>(() => ObjectTagValidator.ValidatePreservingValues([new("team", "a"), new("team", "b")]));
    }

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
    public void MetadataCopyValidationPreservesValuesWithoutChangingUploadNormalization()
    {
        var input = new ObjectWriteHeaders(" text/plain ", " max-age=10 ", " gzip ", " inline ",
            Metadata: new Dictionary<string, string>
            {
                ["x-amz-meta-build"] = " 42 ",
                ["space"] = " \u00a0 ",
                ["empty"] = ""
            });
        var copy = input.ValidateForMetadataCopy();
        Assert.Equal(input.ContentType, copy.ContentType);
        Assert.Equal(input.CacheControl, copy.CacheControl);
        Assert.Equal(input.ContentEncoding, copy.ContentEncoding);
        Assert.Equal(input.ContentDisposition, copy.ContentDisposition);
        Assert.Equal(" 42 ", copy.Metadata!["build"]);
        Assert.Equal(" \u00a0 ", copy.Metadata["space"]);
        Assert.Equal("", copy.Metadata["empty"]);
        Assert.Equal("42", input.ValidateAndNormalize().Metadata!["build"]);
        Assert.Equal("text/plain", input.ValidateAndNormalize().ContentType);
    }

    [Fact]
    public void PreservedMetadataStillRejectsInjectionAndCountsUntrimmedBytes()
    {
        Assert.Throws<ArgumentException>(() => new ObjectWriteHeaders(CacheControl: "ok\r\nInjected: yes")
            .ValidateForMetadataCopy());
        Assert.Throws<ArgumentException>(() => ObjectMetadataValidator.ValidatePreservingValues(
            new Dictionary<string, string> { ["key"] = "\r\nInjected: yes" }));
        Assert.Throws<ArgumentException>(() => ObjectMetadataValidator.ValidatePreservingValues(
            new Dictionary<string, string> { ["key"] = new string(' ', 2048) }));
    }

    [Fact]
    public void MetadataReplacementHasExplicitProviderCapabilityAndWarning()
    {
        foreach (var provider in Enum.GetValues<S3ServiceType>())
        {
            var capability = S3ProviderCapabilityRegistry.For(provider).Object.MetadataRewrite;
            Assert.Equal(Enum.IsDefined(provider), capability.Supported);
            Assert.False(string.IsNullOrWhiteSpace(capability.Reason));
        }
    }

    [Fact]
    public void ObjectCapabilitiesRemainConservativeForUnverifiedProviders()
    {
        Assert.True(S3ProviderCapabilityRegistry.For(S3ServiceType.AmazonS3).Object.Tagging.Supported);
        Assert.True(S3ProviderCapabilityRegistry.For(S3ServiceType.MinIO).Object.MetadataRewrite.Supported);
        Assert.True(S3ProviderCapabilityRegistry.For(S3ServiceType.AliyunOss).Object.Tagging.Supported);
        Assert.True(S3ProviderCapabilityRegistry.For(S3ServiceType.CloudflareR2).Object.MetadataRewrite.Supported);
    }
}
