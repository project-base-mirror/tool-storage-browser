using Amazon.Runtime;
using System.Reflection;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class AwsCredentialResolverTests
{
    [Fact]
    public void StoredSessionCredentialsRemainAnExplicitSource()
    {
        var resolver = CreateResolver();
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Stored",
            AccessKey = "access",
            SecretKey = "secret",
            SessionToken = "session"
        };

        var result = resolver.Resolve(profile);

        Assert.IsType<SessionAWSCredentials>(result.Credentials);
        Assert.Equal(CredentialSourceKind.StoredKeys, result.ActualSource);
        Assert.Contains("Session Token", result.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void LockedSharedProfileDoesNotFallThroughToAnotherSource()
    {
        var requestedName = string.Empty;
        var resolver = CreateResolver(
            sharedProfile: name =>
            {
                requestedName = name;
                return new BasicAWSCredentials("access", "secret");
            },
            environment: () => throw new Xunit.Sdk.XunitException("environment must not run"));
        var profile = ExternalProfile(CredentialSourceKind.AwsSharedProfile) with { AwsProfileName = "audit" };

        var result = resolver.Resolve(profile);

        Assert.Equal("audit", requestedName);
        Assert.Equal(CredentialSourceKind.AwsSharedProfile, result.ActualSource);
        Assert.Equal("AWS shared profile：audit", result.DisplayName);
    }

    [Fact]
    public void MissingSharedProfileHasActionableDiagnostic()
    {
        var resolver = CreateResolver(sharedProfile: _ => null);
        var profile = ExternalProfile(CredentialSourceKind.AwsSharedProfile) with { AwsProfileName = "missing" };

        var exception = Assert.Throws<AwsCredentialResolutionException>(() => resolver.Resolve(profile));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvironmentSourceRequiresCompleteAwsVariablesBeforeFactoryRuns()
    {
        var factoryCalled = false;
        var resolver = CreateResolver(
            environment: () =>
            {
                factoryCalled = true;
                return new BasicAWSCredentials("access", "secret");
            });

        var exception = Assert.Throws<AwsCredentialResolutionException>(() =>
            resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsEnvironmentVariables)));

        Assert.False(factoryCalled);
        Assert.Contains("AWS_ACCESS_KEY_ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerSourceRequiresRoleEndpointVariables()
    {
        var resolver = CreateResolver();

        var exception = Assert.Throws<AwsCredentialResolutionException>(() =>
            resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsContainerRole)));

        Assert.Contains("AWS_CONTAINER_CREDENTIALS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFactoryDiagnosticsRedactAdvancedTokens()
    {
        var resolver = CreateResolver(
            environment: () => throw new InvalidOperationException("access_token=token-secret external_id=external-secret"),
            environmentVariables: new Dictionary<string, string?>
            {
                ["AWS_ACCESS_KEY_ID"] = "access",
                ["AWS_SECRET_ACCESS_KEY"] = "secret"
            });

        var exception = Assert.Throws<AwsCredentialResolutionException>(() =>
            resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsEnvironmentVariables)));

        Assert.DoesNotContain("token-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("external-secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultChainReportsTheActualResolvedSource()
    {
        var resolver = CreateResolver(defaultChain: () => new TestEnvironmentCredentials());

        var result = resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsDefaultChain));

        Assert.Equal(CredentialSourceKind.AwsEnvironmentVariables, result.ActualSource);
        Assert.Contains("环境变量", result.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultChainClassifiesSsoAndRequiresAnExplicitLoginTrigger()
    {
        var resolver = CreateResolver(defaultChain: () => new TestSSOCredentials());

        var result = resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsDefaultChain));

        Assert.Equal(CredentialSourceKind.AwsSso, result.ActualSource);
        Assert.True(result.Identity.UserLoginMayBeRequired);
        Assert.Contains("SSO", result.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitSsoEnablesBrowserVerificationOnlyForInteractiveResolution()
    {
        var sso = new SSOAWSCredentials(
            "123456789012", "us-east-1", "ReadOnly", "https://example.awsapps.com/start",
            new SSOAWSCredentialsOptions());
        var resolver = CreateResolver(sharedProfile: _ => sso);
        var profile = ExternalProfile(CredentialSourceKind.AwsSso) with { AwsProfileName = "company" };

        var background = resolver.Resolve(profile);
        Assert.False(sso.Options.SupportsGettingNewToken);
        Assert.Null(sso.Options.SsoVerificationCallback);
        Assert.True(background.Identity.UserLoginMayBeRequired);

        var interactive = resolver.Resolve(profile, allowInteractiveSso: true);
        Assert.True(sso.Options.SupportsGettingNewToken);
        Assert.NotNull(sso.Options.SsoVerificationCallback);
        Assert.False(interactive.Identity.UserLoginMayBeRequired);
        Assert.Contains("123456789012", interactive.Identity.SourceIdentity, StringComparison.Ordinal);
        Assert.Contains("ReadOnly", interactive.Identity.SourceIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void AssumeRoleKeepsSourceRoleAndExternalIdStateDiagnosableWithoutExposingExternalId()
    {
        var resolver = CreateResolver();
        var profile = ExternalProfile(CredentialSourceKind.AwsAssumeRole) with
        {
            AwsSourceProfileName = "bootstrap",
            AwsRoleArn = "arn:aws:iam::123456789012:role/Audit",
            AwsRoleSessionName = "s3explorer-audit",
            AwsRoleSourceIdentity = "operator-42",
            AwsExternalId = "customer-secret-id",
            AwsSessionDurationSeconds = 1800
        };

        var result = resolver.Resolve(profile);
        var credentials = Assert.IsType<AssumeRoleAWSCredentials>(result.Credentials);

        Assert.Equal("customer-secret-id", credentials.Options.ExternalId);
        Assert.Equal("operator-42", credentials.Options.SourceIdentity);
        Assert.Equal(1800, credentials.Options.DurationSeconds);
        Assert.Equal(profile.AwsRoleArn, result.Identity.TargetRoleArn);
        Assert.True(result.Identity.ExternalIdConfigured);
        Assert.DoesNotContain("customer-secret-id", result.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("customer-secret-id", result.Identity.SourceIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void WebIdentityPassesOnlyTheTokenFileReferenceToTheSdk()
    {
        var resolver = CreateResolver();
        var tokenFile = Path.GetFullPath("web-identity-token.jwt");
        var profile = ExternalProfile(CredentialSourceKind.AwsWebIdentity) with
        {
            AwsRoleArn = "arn:aws:iam::123456789012:role/Workload",
            AwsRoleSessionName = "s3explorer-workload",
            AwsWebIdentityTokenFile = tokenFile,
            AwsSessionDurationSeconds = 1800
        };

        var result = resolver.Resolve(profile);
        var credentials = Assert.IsType<AssumeRoleWithWebIdentityCredentials>(result.Credentials);

        Assert.Equal(tokenFile, credentials.WebIdentityTokenFile);
        Assert.Equal(profile.AwsRoleArn, result.Identity.TargetRoleArn);
        Assert.DoesNotContain(tokenFile, result.Identity.SourceIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshingCredentialExpirationCanBeReportedAfterTheSdkCreatesASession()
    {
        var credentials = new AssumeRoleAWSCredentials(
            new BasicAWSCredentials("access", "secret"),
            "arn:aws:iam::123456789012:role/Audit",
            "s3explorer-audit");
        var expiration = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        var stateField = typeof(RefreshingAWSCredentials).GetField(
            "currentState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(
            stateField.FieldType,
            new ImmutableCredentials("temporary", "temporary-secret", "temporary-token"),
            expiration)!;
        stateField.SetValue(credentials, state);

        var result = AwsCredentialResolver.TryGetSessionExpiration(credentials);

        Assert.Equal(new DateTimeOffset(expiration), result);
    }

    private static ConnectionProfile ExternalProfile(CredentialSourceKind source) =>
        ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "External",
            CredentialSource = source
        };

    private static AwsCredentialResolver CreateResolver(
        Func<string, AWSCredentials?>? sharedProfile = null,
        Func<AWSCredentials>? environment = null,
        Func<AWSCredentials>? container = null,
        Func<AWSCredentials>? instance = null,
        Func<AWSCredentials>? defaultChain = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null) =>
        new(
            sharedProfile ?? (_ => new BasicAWSCredentials("access", "secret")),
            environment ?? (() => new BasicAWSCredentials("access", "secret")),
            container ?? (() => new BasicAWSCredentials("access", "secret")),
            instance ?? (() => new BasicAWSCredentials("access", "secret")),
            defaultChain ?? (() => new BasicAWSCredentials("access", "secret")),
            name => environmentVariables is not null && environmentVariables.TryGetValue(name, out var value)
                ? value
                : null);

    private sealed class TestEnvironmentCredentials : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => new("access", "secret", null);
    }

    private sealed class TestSSOCredentials : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => new("access", "secret", "session");
    }
}
