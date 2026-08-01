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
        Assert.False(capabilities.Cors.Supported);
        Assert.Contains("Community", capabilities.Cors.Reason, StringComparison.Ordinal);
        Assert.True(capabilities.Versioning.Supported);
        Assert.True(capabilities.Encryption.Supported);
        Assert.True(capabilities.KmsEncryption.Supported);
        Assert.True(capabilities.Tagging.Supported);
        Assert.True(capabilities.Lifecycle.Supported);
        Assert.False(capabilities.LifecycleStorageTransitions.Supported);
        Assert.False(capabilities.LifecycleMultipartCleanup.Supported);
        Assert.False(capabilities.ObjectLock.Supported);
        Assert.False(capabilities.PublicAccessBlock.Supported);
        Assert.False(capabilities.ObjectOwnership.Supported);
    }

    [Fact]
    public void CorsDocumentNormalizesMethodsAndCommaSeparatedValues()
    {
        var configuration = BucketCorsDocument.Validate(new BucketCorsConfiguration([
            new BucketCorsRule(" web ", ["https://example.com"], ["get", "HEAD"],
                ["Content-Type, X-Request-Id"], [], 600)
        ]));

        Assert.Equal(["GET", "HEAD"], configuration.Rules[0].AllowedMethods);
        Assert.Equal(["Content-Type", "X-Request-Id"], configuration.Rules[0].AllowedHeaders);
        Assert.Equal("web", configuration.Rules[0].Id);
        Assert.True(BucketCorsDocument.AreSemanticallyEquivalent(
            configuration, BucketCorsDocument.Parse(BucketCorsDocument.Serialize(configuration))));
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("")]
    public void CorsDocumentRejectsInvalidMethods(string method)
    {
        Assert.Throws<ArgumentException>(() => BucketCorsDocument.Validate(
            new BucketCorsConfiguration([new BucketCorsRule(null, ["*"], [method], [], [], null)])));
    }

    [Fact]
    public void CorsDocumentTreatsZeroMaxAgeAsOmitted()
    {
        var configuration = BucketCorsDocument.Validate(new BucketCorsConfiguration([
            new BucketCorsRule(null, ["*"], ["GET"], [], [], 0)
        ]));

        Assert.Null(configuration.Rules[0].MaxAgeSeconds);
    }

    [Fact]
    public void TagValidatorRejectsDuplicateKeys()
    {
        var exception = Assert.Throws<ArgumentException>(() => BucketTagValidator.Validate([
            new BucketTag("team", "a"), new BucketTag("team", "b")
        ]));

        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KmsEncryptionRequiresKeyId()
    {
        Assert.Throws<ArgumentException>(() =>
            new BucketEncryptionConfiguration(BucketEncryptionMode.SseKms).Validate(true));
    }

    [Fact]
    public void AmazonS3CapabilitiesEnableAdvancedAccessControls()
    {
        var capabilities = BucketCapabilityMatrix.For(S3ServiceType.AmazonS3);

        Assert.True(capabilities.PublicAccessBlock.Supported);
        Assert.True(capabilities.ObjectOwnership.Supported);
        Assert.True(capabilities.Lifecycle.Supported);
        Assert.True(capabilities.LifecycleStorageTransitions.Supported);
        Assert.True(capabilities.LifecycleMultipartCleanup.Supported);
        Assert.True(capabilities.ObjectLock.Supported);
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

    [Fact]
    public void LifecycleDocumentNormalizesRulesFiltersAndTransitions()
    {
        var configuration = new BucketLifecycleConfiguration([
            new BucketLifecycleRule(
                " archive ", true, "logs/",
                [new LifecycleTag(" team ", " platform ")],
                [new LifecycleTransition(90, LifecycleStorageClass.GlacierFlexibleRetrieval),
                 new LifecycleTransition(30, LifecycleStorageClass.StandardInfrequentAccess)],
                365,
                [new LifecycleTransition(60, LifecycleStorageClass.GlacierFlexibleRetrieval)],
                180,
                7)
        ]);

        var normalized = BucketLifecycleDocument.Validate(configuration);

        var rule = Assert.Single(normalized.Rules);
        Assert.Equal("archive", rule.Id);
        Assert.Equal("team", Assert.Single(rule.Tags).Key);
        Assert.Equal([30, 90], rule.Transitions.Select(value => value.Days));
        Assert.True(BucketLifecycleDocument.AreSemanticallyEquivalent(
            normalized,
            BucketLifecycleDocument.Parse(BucketLifecycleDocument.Serialize(normalized))));
    }

    [Fact]
    public void LifecycleDocumentRejectsConflictingFilters()
    {
        var configuration = new BucketLifecycleConfiguration([
            Rule("first", "logs/", expirationDays: 30),
            Rule("second", "logs/", expirationDays: 60)
        ]);

        var exception = Assert.Throws<ArgumentException>(() =>
            BucketLifecycleDocument.Validate(configuration));

        Assert.Contains("相同过滤条件", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleDocumentRejectsInvalidActionOrderAndUnsupportedTransitions()
    {
        Assert.Throws<ArgumentException>(() => BucketLifecycleDocument.Validate(
            new BucketLifecycleConfiguration([
                Rule("late-transition", null, expirationDays: 30) with
                {
                    Transitions = [new LifecycleTransition(30, LifecycleStorageClass.GlacierFlexibleRetrieval)]
                }
            ])));

        var unsupported = Assert.Throws<ArgumentException>(() => BucketLifecycleDocument.Validate(
            new BucketLifecycleConfiguration([
                Rule("tier", null, expirationDays: null) with
                {
                    Transitions = [new LifecycleTransition(30, LifecycleStorageClass.GlacierFlexibleRetrieval)]
                }
            ]),
            storageTransitionsSupported: false));
        Assert.Contains("Provider", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComplianceRetentionCanOnlyBeExtendedWithoutDowngrade()
    {
        var current = new ObjectLockSnapshot(
            ObjectRetentionMode.Compliance,
            DateTimeOffset.Parse("2030-01-01T00:00:00Z"),
            false,
            "version-1");

        Assert.Throws<ArgumentException>(() => new ObjectRetentionConfiguration(
            ObjectRetentionMode.Governance,
            DateTimeOffset.Parse("2031-01-01T00:00:00Z")).Validate(
                current, DateTimeOffset.Parse("2029-01-01T00:00:00Z")));
        Assert.Throws<ArgumentException>(() => new ObjectRetentionConfiguration(
            ObjectRetentionMode.Compliance,
            DateTimeOffset.Parse("2029-12-01T00:00:00Z")).Validate(
                current, DateTimeOffset.Parse("2029-01-01T00:00:00Z")));

        new ObjectRetentionConfiguration(
            ObjectRetentionMode.Compliance,
            DateTimeOffset.Parse("2031-01-01T00:00:00Z")).Validate(
                current, DateTimeOffset.Parse("2029-01-01T00:00:00Z"));
    }

    private static BucketLifecycleRule Rule(string id, string? prefix, int? expirationDays) => new(
        id, true, prefix, [], [], expirationDays, [], null, null);
}
