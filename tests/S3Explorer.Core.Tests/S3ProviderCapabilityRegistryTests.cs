using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class S3ProviderCapabilityRegistryTests
{
    [Fact]
    public void RegistryCoversEveryProviderExactlyOnce()
    {
        var expected = Enum.GetValues<S3ServiceType>();
        var actual = S3ProviderCapabilityRegistry.All.Select(item => item.ServiceType).ToArray();

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void EveryCapabilityHasAnExplanation()
    {
        foreach (var provider in S3ProviderCapabilityRegistry.All)
        {
            foreach (var feature in Features(provider.Bucket).Concat(Features(provider.Object)))
                Assert.False(string.IsNullOrWhiteSpace(feature.Reason), $"{provider.ServiceType} contains a capability without a reason.");
        }
    }

    [Fact]
    public void AmazonObjectLockSeparatesBucketReadFromObjectWrites()
    {
        var capabilities = S3ProviderCapabilityRegistry.For(S3ServiceType.AmazonS3);

        Assert.True(capabilities.Bucket.ObjectLock.IsReadOnly);
        Assert.False(capabilities.Bucket.ObjectLock.CanWrite);
        Assert.True(capabilities.Object.ObjectLock.CanWrite);
    }

    [Fact]
    public void VersionOperationsAreLimitedToContinuouslyVerifiedProviders()
    {
        Assert.True(S3ProviderCapabilityRegistry.For(S3ServiceType.AmazonS3).Object.VersionOperations.Supported);
        Assert.True(S3ProviderCapabilityRegistry.For(S3ServiceType.MinIO).Object.VersionOperations.Supported);
        Assert.False(S3ProviderCapabilityRegistry.For(S3ServiceType.AliyunOss).Object.VersionOperations.Supported);
        Assert.False(S3ProviderCapabilityRegistry.For(S3ServiceType.Custom).Object.VersionOperations.Supported);
    }

    [Fact]
    public void PresignedUrlsRemainAvailableForEveryProvider()
    {
        Assert.All(S3ProviderCapabilityRegistry.All, provider =>
            Assert.True(provider.Object.PresignedUrl.Supported, provider.ServiceType.ToString()));
    }

    [Fact]
    public void AliyunUsesNativeMetadataRewriteWhileOtherCapabilitiesRemainConservative()
    {
        var capabilities = S3ProviderCapabilityRegistry.For(S3ServiceType.AliyunOss).Object;

        Assert.True(capabilities.Tagging.Supported);
        Assert.True(capabilities.MetadataRewrite.Supported);
        Assert.False(capabilities.Acl.Supported);
    }

    [Fact]
    public void AtomicDeleteCapabilityIsNotInventedForCompatibleProviders()
    {
        foreach (var provider in S3ProviderCapabilityRegistry.All)
            Assert.Equal(provider.ServiceType == S3ServiceType.AmazonS3, provider.ConditionalDelete.Supported);
    }

    [Fact]
    public void CompatibleMoveExplainsRecheckAndRemainingConcurrentWriteRisk()
    {
        var feature = S3ProviderCapabilityRegistry.For(S3ServiceType.SupabaseStorage).ConditionalDelete;
        Assert.False(feature.Supported);
        Assert.Contains("先复制", feature.Reason);
        Assert.Contains("竞争窗口", feature.Reason);
        Assert.Contains("停止", feature.Reason);
        Assert.Contains("不是原子重命名", feature.Reason);
        Assert.DoesNotContain("禁用", feature.Reason);
    }

    [Fact]
    public void MetadataRewriteIsAvailableWithExplicitNonAtomicWarning()
    {
        Assert.All(S3ProviderCapabilityRegistry.All, provider =>
            Assert.Equal(Enum.IsDefined(provider.ServiceType), provider.Object.MetadataRewrite.Supported));
    }

    [Fact]
    public void UnknownProviderIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            S3ProviderCapabilityRegistry.For((S3ServiceType)int.MaxValue));
    }

    private static IEnumerable<BucketFeatureSupport> Features(object capabilities) =>
        capabilities.GetType().GetProperties()
            .Where(property => property.PropertyType == typeof(BucketFeatureSupport))
            .Select(property => (BucketFeatureSupport)property.GetValue(capabilities)!);
}
