using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ProviderMatrixTests
{
    [Fact]
    public void Matrix_contains_required_and_optional_s3_compatible_providers()
    {
        Assert.Equal(8, ProviderMatrixCase.All.Count);
        Assert.True(ProviderMatrixCase.All.Single(item => item.Id == "minio").Required);
        Assert.Contains(ProviderMatrixCase.All, item => item.ServiceType == S3ServiceType.TencentCos);
        Assert.Contains(ProviderMatrixCase.All, item => item.ServiceType == S3ServiceType.AliyunOss);
        Assert.Contains(ProviderMatrixCase.All, item => item.ServiceType == S3ServiceType.CloudflareR2);
        Assert.Contains(ProviderMatrixCase.All, item => item.ServiceType == S3ServiceType.BackblazeB2);
        Assert.Contains(ProviderMatrixCase.All, item => item.ServiceType == S3ServiceType.GoogleCloudStorage);
        Assert.Contains(ProviderMatrixCase.All, item => item.ServiceType == S3ServiceType.SupabaseStorage);
    }

    [Fact]
    public void Missing_credentials_are_reported_as_not_configured()
    {
        var configuration = ProviderMatrixCase.All.Single(item => item.Id == "aws").Resolve();

        if (!configuration.IsConfigured)
        {
            var report = configuration.ToReportJson(ProviderMatrixStatus.NotConfigured);
            Assert.Contains("\"status\":\"NotConfigured\"", report);
            Assert.DoesNotContain("SecretKey", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SessionToken", report, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Matrix_profiles_preserve_provider_addressing_defaults()
    {
        var minio = ProviderMatrixCase.All.Single(item => item.Id == "minio");
        var r2 = ProviderMatrixCase.All.Single(item => item.Id == "cloudflare-r2");

        Assert.Equal(AddressingStyle.PathStyle, minio.AddressingStyle);
        Assert.Equal("auto", r2.DefaultRegion);
        Assert.Equal(AddressingStyle.PathStyle, r2.AddressingStyle);
    }
}
