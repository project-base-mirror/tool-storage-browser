using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CdnModelTests
{
    [Fact]
    public void ResolvesLongestPrefixAndDefaultProfile()
    {
        var storageId = Guid.NewGuid();
        var rootProfile = Profile("root", "https://root.example");
        var assetProfile = Profile("assets", "https://assets.example/base");
        var alternate = Profile("alternate", "https://alt.example");
        var configuration = new CdnConfiguration(
            [rootProfile, assetProfile, alternate],
            [
                Binding(storageId, rootProfile.Id, "", "", true),
                Binding(storageId, alternate.Id, "assets/", "mirror/", false),
                Binding(storageId, assetProfile.Id, "assets/", "static/", true)
            ]);

        var targets = CdnUrlMapper.ResolveAll(
            configuration,
            storageId,
            "site",
            "assets/js/app 1.js");

        Assert.Equal(2, targets.Count);
        Assert.Equal(assetProfile.Id, targets[0].Profile.Id);
        Assert.Equal(
            "https://assets.example/base/static/js/app%201.js",
            targets[0].Url.AbsoluteUri);
        Assert.Equal(alternate.Id, targets[1].Profile.Id);
    }

    [Fact]
    public void DoesNotUseLessSpecificBindingWhenSpecificBindingExists()
    {
        var storageId = Guid.NewGuid();
        var root = Profile("root", "https://root.example");
        var nested = Profile("nested", "https://nested.example");
        var configuration = new CdnConfiguration(
            [root, nested],
            [
                Binding(storageId, root.Id, "", "", true),
                Binding(storageId, nested.Id, "a/b/", "", true)
            ]);

        var target = CdnUrlMapper.ResolveDefault(
            configuration,
            storageId,
            "site",
            "a/b/file.txt");

        Assert.NotNull(target);
        Assert.Equal(nested.Id, target.Profile.Id);
    }

    [Fact]
    public void ReturnsNoTargetOutsideBinding()
    {
        var storageId = Guid.NewGuid();
        var profile = Profile("cdn", "https://cdn.example");
        var configuration = new CdnConfiguration(
            [profile],
            [Binding(storageId, profile.Id, "public/", "", true)]);

        Assert.Empty(
            CdnUrlMapper.ResolveAll(
                configuration,
                storageId,
                "site",
                "private/file.txt"));
    }

    [Fact]
    public void ValidatorRejectsTwoDefaultsAtSameScope()
    {
        var storageId = Guid.NewGuid();
        var first = Profile("first", "https://first.example");
        var second = Profile("second", "https://second.example");
        var configuration = new CdnConfiguration(
            [first, second],
            [
                Binding(storageId, first.Id, "assets", "", true),
                Binding(storageId, second.Id, "assets/", "", true)
            ]);

        var errors = CdnConfigurationValidator.Validate(configuration);

        Assert.Contains(
            errors,
            value => value.Contains("只能有一个默认 CDN", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsMissingCredentialReference()
    {
        var profile = Profile("cdn", "https://cdn.example") with
        {
            CredentialId = Guid.NewGuid()
        };

        var errors = CdnConfigurationValidator.Validate(
            new CdnConfiguration([profile], []),
            []);

        Assert.Contains(
            errors,
            value => value.Contains("不存在的凭据", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://cdn.example/base?token=in-config")]
    [InlineData("https://user:password@cdn.example/base")]
    [InlineData("https://cdn.example/base#fragment")]
    public void ValidatorRejectsUnsafeBaseUrlComponents(string baseUrl)
    {
        var errors = CdnConfigurationValidator.Validate(
            new CdnConfiguration([Profile("cdn", baseUrl)], []));

        Assert.Contains(
            errors,
            value => value.Contains("基础 URL", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsInvalidCustomHeaderAndMultilineSecret()
    {
        var credential = new CdnCredential
        {
            Name = "invalid",
            AuthenticationType = CdnAuthenticationType.CustomHeader,
            HeaderName = "X Token",
            Secret = "first\r\nsecond"
        };

        var errors = CdnConfigurationValidator.Validate(
            CdnConfiguration.Empty,
            [credential]);

        Assert.Contains(errors, value => value.Contains("Header", StringComparison.Ordinal));
        Assert.Contains(errors, value => value.Contains("换行符", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsOversizedProfileNotes()
    {
        var profile = Profile("cdn", "https://cdn.example") with
        {
            Notes = new string('n', CdnProfile.MaximumNotesLength + 1)
        };

        var errors = CdnConfigurationValidator.Validate(
            new CdnConfiguration([profile], []));

        Assert.Contains(errors, value => value.Contains("备注", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsInvalidUploadAutomationPolicies()
    {
        var storageId = Guid.NewGuid();
        var profile = Profile("cdn", "https://cdn.example");
        var invalidNewObject = Binding(storageId, profile.Id, "", "", true) with
        {
            NewObjectAction = CdnUploadAction.Purge
        };
        var invalidOverwrite = Binding(storageId, profile.Id, "other/", "", true) with
        {
            OverwriteAction = CdnUploadAction.PurgeThenWarmup
        };

        var errors = CdnConfigurationValidator.Validate(
            new CdnConfiguration([profile], [invalidNewObject, invalidOverwrite]));

        Assert.Contains(errors, value => value.Contains("新对象自动化动作无效", StringComparison.Ordinal));
        Assert.Contains(errors, value => value.Contains("需要 CDN 配置", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorAcceptsExplicitUploadAutomationPolicies()
    {
        var storageId = Guid.NewGuid();
        var profile = Profile("cdn", "https://cdn.example") with
        {
            PurgeEndpointTemplate = "https://api.example/purge?url={url}"
        };
        var binding = Binding(storageId, profile.Id, "", "", true) with
        {
            NewObjectAction = CdnUploadAction.Warmup,
            OverwriteAction = CdnUploadAction.PurgeThenWarmup
        };

        Assert.Empty(CdnConfigurationValidator.Validate(
            new CdnConfiguration([profile], [binding])));
    }

    [Theory]
    [InlineData(CdnCertificateProblems.None, 60, "正常")]
    [InlineData(CdnCertificateProblems.None, 7, "即将到期")]
    [InlineData(CdnCertificateProblems.Expired, -3, "已过期")]
    [InlineData(CdnCertificateProblems.NameMismatch, 60, "域名不匹配")]
    [InlineData(CdnCertificateProblems.UntrustedChain, 60, "证书链不受信任")]
    public void CertificateResultSummarizesValidity(
        CdnCertificateProblems problems,
        int daysRemaining,
        string expectedStatus)
    {
        var checkedAt = new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
        var result = new CdnCertificateCheckResult(
            new Uri("https://cdn.example"),
            checkedAt,
            checkedAt.AddDays(-30),
            checkedAt.AddDays(daysRemaining),
            "CN=cdn.example",
            "CN=issuer",
            "ABC123",
            "Tls13",
            problems,
            []);

        Assert.Equal(daysRemaining, result.DaysRemaining);
        Assert.Contains(expectedStatus, result.StatusText, StringComparison.Ordinal);
    }

    private static CdnProfile Profile(string name, string url) => new()
    {
        Name = name,
        BaseUrl = url
    };

    private static CdnBinding Binding(
        Guid storageId,
        Guid cdnId,
        string sourcePrefix,
        string targetPrefix,
        bool isDefault) => new()
        {
            StorageProfileId = storageId,
            Bucket = "site",
            SourcePrefix = sourcePrefix,
            CdnProfileId = cdnId,
            CdnPathPrefix = targetPrefix,
            IsDefault = isDefault
        };
}
