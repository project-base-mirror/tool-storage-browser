using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class S3ProviderCatalogTests
{
    [Theory]
    [InlineData(S3ServiceType.MinIO, "us-east-1")]
    [InlineData(S3ServiceType.CloudflareR2, "auto")]
    [InlineData(S3ServiceType.GoogleCloudStorage, "auto")]
    [InlineData(S3ServiceType.SupabaseStorage, "us-east-1")]
    public void Providers_without_region_input_use_safe_signing_default(S3ServiceType type, string expected)
    {
        var definition = S3ProviderCatalog.Get(type);

        Assert.Equal(RegionInputMode.Hidden, definition.RegionInput);
        Assert.Equal(expected, S3ProviderCatalog.ResolveSigningRegion(type, "ignored"));
    }

    [Fact]
    public void Compatible_account_category_groups_equivalent_credential_forms()
    {
        var compatibleTypes = S3ProviderCatalog.CompatibleProviders.Select(item => item.ServiceType).ToHashSet();

        Assert.Contains(S3ServiceType.MinIO, compatibleTypes);
        Assert.Contains(S3ServiceType.CloudflareR2, compatibleTypes);
        Assert.Contains(S3ServiceType.Custom, compatibleTypes);
        Assert.DoesNotContain(S3ServiceType.AmazonS3, compatibleTypes);
        Assert.DoesNotContain(S3ServiceType.GoogleCloudStorage, compatibleTypes);
    }

    [Fact]
    public void Preset_and_catalog_remain_consistent()
    {
        foreach (var definition in S3ProviderCatalog.All)
        {
            var preset = ConnectionProfile.CreatePreset(definition.ServiceType);
            Assert.Equal(definition.DefaultEndpoint, preset.Endpoint);
            Assert.Equal(definition.DefaultRegion, preset.Region);
            Assert.Equal(definition.DefaultAddressingStyle, preset.AddressingStyle);
            Assert.Equal(definition.DefaultUseHttps, preset.UseHttps);
            Assert.Equal(definition.EffectiveDefaultSigningRegion, preset.EffectiveSignatureRegion);
        }
    }

    [Fact]
    public void Amazon_preset_displays_auto_but_signs_with_safe_default()
    {
        var preset = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);

        Assert.Equal("auto", preset.Region);
        Assert.Equal("us-east-1", preset.EffectiveSignatureRegion);
        Assert.Equal("us-east-1", S3ProviderCatalog.ResolveSigningRegion(S3ServiceType.AmazonS3, "auto"));
    }
}
