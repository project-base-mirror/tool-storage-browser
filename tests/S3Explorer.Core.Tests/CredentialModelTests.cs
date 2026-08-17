using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CredentialModelTests
{
    [Fact]
    public void Validate_AccessKeyProfile_WithoutSecret_Throws()
    {
        var profile = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "aliyun-oss",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_TEST",
            Secret = string.Empty
        };

        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Validate_CustomHeaderProfile_WithoutHeaderName_Throws()
    {
        var profile = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "cdn-generic",
            Provider = CredentialProviderKind.GenericHttp,
            Kind = CredentialKind.CustomHeader,
            Secret = "token"
        };

        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Compatibility_WithS3Provider_IsConsistent()
    {
        var profile = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "oss",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_TEST",
            Secret = "secret"
        };

        Assert.True(profile.IsCompatibleWith(S3ServiceType.AliyunOss));
        Assert.False(profile.IsCompatibleWith(S3ServiceType.Custom));
        Assert.False(profile.IsCompatibleWith(S3ServiceType.AmazonS3));
    }

    [Fact]
    public void Compatibility_WithCdnProvider_IsCheckedByProviderId()
    {
        var profile = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "cdn",
            Provider = CredentialProviderKind.GenericHttp,
            Kind = CredentialKind.BearerToken,
            Secret = "bearer-token"
        };

        Assert.True(profile.IsCompatibleWith(CdnProfile.GenericHttpProviderId));
        Assert.False(profile.IsCompatibleWith("cloudflare-r2"));
    }

    [Fact]
    public void Vault_ReportsDuplicateIdAndName()
    {
        var duplicateId = Guid.NewGuid();

        var errors = CredentialVault.ValidateUniqueness(
            [
                new CredentialProfile
                {
                    Id = duplicateId,
                    Name = "oss",
                    Provider = CredentialProviderKind.AlibabaCloud,
                    Kind = CredentialKind.AccessKeyPair,
                    AccessKeyId = "AKIA_1",
                    Secret = "secret"
                },
                new CredentialProfile
                {
                    Id = duplicateId,
                    Name = "oss-2",
                    Provider = CredentialProviderKind.TencentCloud,
                    Kind = CredentialKind.AccessKeyPair,
                    AccessKeyId = "AKIA_2",
                    Secret = "secret"
                },
                new CredentialProfile
                {
                    Id = Guid.NewGuid(),
                    Name = "oss",
                    Provider = CredentialProviderKind.Cloudflare,
                    Kind = CredentialKind.AccessKeyPair,
                    AccessKeyId = "AKIA_3",
                    Secret = "secret"
                }
            ]);

        Assert.Contains(errors, value => value.Contains("ID 重复", StringComparison.Ordinal));
        Assert.Contains(errors, value => value.Contains("名称重复", StringComparison.Ordinal));
    }

    [Fact]
    public void DisplayAndFingerprint_DoesNotLeakSecret()
    {
        var secret = "this-is-a-very-sensitive-secret";
        var profile = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "oss-profile",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "LTAIATestAccessKey",
            Secret = secret
        };

        Assert.DoesNotContain(secret, profile.Display, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, profile.Fingerprint, StringComparison.Ordinal);
        Assert.Contains("****sKey", profile.Fingerprint, StringComparison.Ordinal);
    }
}
