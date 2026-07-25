using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class BucketManagementTests
{
    [Fact]
    public void PolicyValidatorNormalizesValidPolicy()
    {
        var normalized = BucketPolicyDocument.ValidateAndNormalize(
            "{\"Version\":\"2012-10-17\",\"Statement\":[]}");

        Assert.Contains("\"Statement\"", normalized, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyComparisonIgnoresObjectPropertyOrder()
    {
        const string first = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":[\"s3:ListBucket\"],\"Resource\":[\"arn:aws:s3:::bucket\"]}]}";
        const string reordered = "{\"Statement\":[{\"Resource\":[\"arn:aws:s3:::bucket\"],\"Action\":[\"s3:ListBucket\"],\"Effect\":\"Allow\"}],\"Version\":\"2012-10-17\"}";

        Assert.True(BucketPolicyDocument.AreSemanticallyEquivalent(first, reordered));
    }

    [Fact]
    public void PolicyComparisonAcceptsMinioNormalization()
    {
        const string submitted = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Sid\":\"ListBucket\",\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":[\"s3:ListBucket\"],\"Resource\":[\"arn:aws:s3:::bucket\"]}]}";
        const string normalized = "{\"Statement\":{\"Resource\":\"arn:aws:s3:::bucket\",\"Action\":\"s3:ListBucket\",\"Principal\":{\"AWS\":[\"*\"]},\"Effect\":\"Allow\"},\"Version\":\"2012-10-17\"}";

        Assert.True(BucketPolicyDocument.AreSemanticallyEquivalent(submitted, normalized));
    }

    [Fact]
    public void PolicyComparisonTreatsPolicyArraysAsUnorderedSets()
    {
        const string first = "{\"Statement\":[{\"Effect\":\"Allow\",\"Action\":[\"s3:GetObject\",\"s3:ListBucket\"]}]}";
        const string second = "{\"Statement\":[{\"Action\":[\"s3:ListBucket\",\"s3:GetObject\"],\"Effect\":\"Allow\"}]}";

        Assert.True(BucketPolicyDocument.AreSemanticallyEquivalent(first, second));
    }

    [Fact]
    public void PolicyComparisonDetectsDifferentValues()
    {
        const string first = "{\"Statement\":[{\"Effect\":\"Allow\"}]}";
        const string second = "{\"Statement\":[{\"Effect\":\"Deny\"}]}";

        Assert.False(BucketPolicyDocument.AreSemanticallyEquivalent(first, second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"Version\":\"2012-10-17\"}")]
    [InlineData("{not-json}")]
    public void PolicyValidatorRejectsUnsafeDocuments(string json)
    {
        Assert.Throws<ArgumentException>(() =>
            BucketPolicyDocument.ValidateAndNormalize(json));
    }

    [Fact]
    public void MinioCapabilitiesExposeVerifiedFeaturesOnly()
    {
        var capabilities = BucketCapabilityMatrix.For(S3ServiceType.MinIO);

        Assert.True(capabilities.Policy.Supported);
        Assert.True(capabilities.Acl.Supported);
        Assert.True(capabilities.EmptyBucket.Supported);
        Assert.False(capabilities.PublicAccessBlock.Supported);
        Assert.False(capabilities.ObjectOwnership.Supported);
    }

    [Fact]
    public void AmazonS3CapabilitiesEnableAdvancedAccessControls()
    {
        var capabilities = BucketCapabilityMatrix.For(S3ServiceType.AmazonS3);

        Assert.True(capabilities.PublicAccessBlock.Supported);
        Assert.True(capabilities.ObjectOwnership.Supported);
    }

    [Fact]
    public void PublicAccessBlockReportsFullyBlockedOnlyWhenAllFlagsAreSet()
    {
        Assert.True(new BucketPublicAccessBlockSnapshot(true, true, true, true).FullyBlocked);
        Assert.False(new BucketPublicAccessBlockSnapshot(true, true, true, false).FullyBlocked);
    }

    [Fact]
    public void UnverifiedCompatibleProviderDoesNotEnableManagementRequests()
    {
        var capabilities = BucketCapabilityMatrix.For(S3ServiceType.AliyunOss);

        Assert.False(capabilities.Policy.Supported);
        Assert.False(capabilities.Acl.Supported);
        Assert.True(capabilities.EmptyBucket.Supported);
    }

    [Fact]
    public void EmptySummaryCountsAllRemoteEntries()
    {
        var summary = new BucketEmptySummary(2, 3, 4, 5, 1024, true);

        Assert.Equal(14, summary.TotalRemoteEntries);
        Assert.False(summary.IsEmpty);
    }
}
