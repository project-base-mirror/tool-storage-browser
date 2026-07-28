using Amazon.Runtime;
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
    public void DefaultChainReportsTheActualResolvedSource()
    {
        var resolver = CreateResolver(defaultChain: () => new TestEnvironmentCredentials());

        var result = resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsDefaultChain));

        Assert.Equal(CredentialSourceKind.AwsEnvironmentVariables, result.ActualSource);
        Assert.Contains("环境变量", result.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedTokenSourcesAreNotSilentlyAcceptedInThisPhase()
    {
        var resolver = CreateResolver(defaultChain: () => new TestSSOCredentials());

        var exception = Assert.Throws<AwsCredentialResolutionException>(() =>
            resolver.Resolve(ExternalProfile(CredentialSourceKind.AwsDefaultChain)));

        Assert.Contains("SSO", exception.Message, StringComparison.Ordinal);
        Assert.Contains("高级身份", exception.Message, StringComparison.Ordinal);
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
