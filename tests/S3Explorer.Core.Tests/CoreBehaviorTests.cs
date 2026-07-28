using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Theory]
    [InlineData("s3://AWS-Prod/game-assets/config/", "AWS-Prod", "game-assets", "config/")]
    [InlineData("s3://MinIO/backup/", "MinIO", "backup", "")]
    [InlineData("s3://Account/", "Account", null, "")]
    public void S3UriParses(string input, string profile, string? bucket, string prefix)
    {
        var location = S3Location.Parse(input);
        Assert.Equal(profile, location.Profile);
        Assert.Equal(bucket, location.Bucket);
        Assert.Equal(prefix, location.Prefix);
    }

    [Fact]
    public void ParentNavigationKeepsBucket()
    {
        var location = S3Location.Parse("s3://A/bucket/a/b/c/");
        Assert.Equal("s3://A/bucket/a/b/", location.Parent().ToString());
    }

    [Theory]
    [InlineData("a/b/c/", "a/b/")]
    [InlineData("folder/", "")]
    [InlineData("", "")]
    public void ParentPrefixStaysWithinBucket(string prefix, string expected) =>
        Assert.Equal(expected, S3Path.ParentPrefix(prefix));

    [Fact]
    public void FolderMarkerUsesSlashAndAllowsUnicode()
    {
        Assert.Equal("root/中文/", S3Path.FolderMarker("root/", "中文"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1310720, "1.25 MiB")]
    public void FileSizesAreFormatted(long bytes, string expected) =>
        Assert.Equal(expected, FileSizeFormatter.Format(bytes));

    [Fact]
    public void SensitiveFieldsAreRedacted()
    {
        var value = "SecretKey=abc Authorization: bearer-token https://x/?X-Amz-Signature=123";
        var redacted = SensitiveDataRedactor.Redact(value);
        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("bearer-token", redacted);
        Assert.DoesNotContain("Signature=123", redacted);
    }

    [Theory]
    [InlineData("SlowDown", 503, true)]
    [InlineData("AccessDenied", 403, false)]
    [InlineData(null, 429, true)]
    public void RetryClassificationWorks(string? code, int? status, bool expected) =>
        Assert.Equal(expected, RetryClassifier.ShouldRetry(code, status));

    [Fact]
    public void EndpointNormalizationPreservesPortAndBasePath()
    {
        var endpoint = EndpointCompatibility.NormalizeServiceUrl("https://storage.example.test:9443/api/s3///");
        Assert.Equal("https://storage.example.test:9443/api/s3", endpoint);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9001")]
    [InlineData("http://127.0.0.1:19000/browser")]
    [InlineData("http://127.0.0.1:19000/login")]
    public void MinioConsoleEndpointIsRejected(string endpoint)
    {
        var profile = new ConnectionProfile
        {
            Name = "MinIO",
            ServiceType = S3ServiceType.MinIO,
            Endpoint = endpoint,
            Region = "us-east-1",
            AccessKey = "access",
            SecretKey = "secret"
        };

        var exception = Assert.Throws<ArgumentException>(() => profile.Validate());
        Assert.Contains("S3 API", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MinioRemappedApiPortIsAccepted()
    {
        var profile = new ConnectionProfile
        {
            Name = "MinIO",
            ServiceType = S3ServiceType.MinIO,
            Endpoint = "http://127.0.0.1:19000",
            Region = "us-east-1",
            AccessKey = "access",
            SecretKey = "secret"
        };

        profile.Validate();
    }

    [Fact]
    public void SignatureRegionUsesExplicitOverrideThenRegionThenProviderDefault()
    {
        var profile = new ConnectionProfile { Region = "ap-guangzhou" };
        Assert.Equal("ap-guangzhou", profile.EffectiveSignatureRegion);
        Assert.Equal("us-east-1", (profile with { SignatureRegion = " auto ", Region = "auto" }).EffectiveSignatureRegion);
        Assert.Equal("us-east-1", (profile with { Region = string.Empty }).EffectiveSignatureRegion);
        Assert.Equal("auto", (profile with { ServiceType = S3ServiceType.CloudflareR2, Region = string.Empty }).EffectiveSignatureRegion);
    }

    [Fact]
    public void KnownBucketsIncludeDefaultAndDistinctExternalBuckets()
    {
        var profile = new ConnectionProfile
        {
            DefaultBucket = " primary " ,
            ExternalBuckets = ["archive", "primary", " archive "]
        };

        Assert.Equal(["primary", "archive"], profile.KnownBuckets);
    }

    [Fact]
    public void BackblazePresetUsesValidConcreteEndpoint()
    {
        var preset = ConnectionProfile.CreatePreset(S3ServiceType.BackblazeB2);
        Assert.True(Uri.TryCreate(preset.Endpoint, UriKind.Absolute, out var endpoint));
        Assert.Equal(Uri.UriSchemeHttps, endpoint!.Scheme);
        Assert.DoesNotContain('<', preset.Endpoint);
    }

    [Fact]
    public void ConnectionTimeoutDefaultsToTenSecondsAndRegionMayBeEmpty()
    {
        var profile = new ConnectionProfile
        {
            Name = "Custom",
            Endpoint = "https://s3.example.test",
            Region = string.Empty,
            AccessKey = "access",
            SecretKey = "secret"
        };

        Assert.Equal(10, profile.ConnectionTimeoutSeconds);
        profile.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (profile with { ConnectionTimeoutSeconds = 0 }).Validate());
    }

    [Fact]
    public void SessionTokenSelectsTemporaryCredentialsWithoutExposingItsValue()
    {
        var permanent = new ConnectionProfile { SessionToken = string.Empty };
        var temporary = permanent with { SessionToken = "temporary-token" };

        Assert.False(permanent.UsesTemporarySessionCredentials);
        Assert.True(temporary.UsesTemporarySessionCredentials);
    }

    [Fact]
    public void ExternalAwsCredentialSourceDoesNotUseBlankKeysAsImplicitState()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "AWS environment",
            CredentialSource = CredentialSourceKind.AwsEnvironmentVariables
        };

        profile.Validate();

        Assert.True(profile.HasCredentialConfiguration);
        Assert.True(profile.UsesExternalAwsCredentials);
        Assert.False(profile.HasStoredCredentials);
        Assert.Equal("AWS 环境变量", profile.CredentialSourceDisplayName);
    }

    [Fact]
    public void SharedProfileRequiresAnExplicitProfileName()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "AWS profile",
            CredentialSource = CredentialSourceKind.AwsSharedProfile
        };

        var exception = Assert.Throws<ArgumentException>(() => profile.Validate());

        Assert.Contains("Profile 名称", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S3CompatibleConnectionCannotReadAwsExternalCredentialChain()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = "MinIO",
            CredentialSource = CredentialSourceKind.AwsDefaultChain
        };

        var exception = Assert.Throws<ArgumentException>(() => profile.Validate());

        Assert.Contains("S3-compatible", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad host/path")]
    [InlineData("bad host")]
    [InlineData("example.test/path")]
    public void InvalidCustomHostHeaderIsRejected(string value) =>
        Assert.Throws<ArgumentException>(() => EndpointCompatibility.ValidateHostHeader(value));

    [Fact]
    public void OptionalProvidersHaveS3CompatiblePresets()
    {
        Assert.Contains("/storage/v1/s3", ConnectionProfile.CreatePreset(S3ServiceType.SupabaseStorage).Endpoint);
        Assert.Equal("https://storage.googleapis.com", ConnectionProfile.CreatePreset(S3ServiceType.GoogleCloudStorage).Endpoint);
    }

    [Fact]
    public void OperationCancellationReplacesAndCancelsPreviousToken()
    {
        using var cancellation = new OperationCancellation();

        var first = cancellation.StartNew();
        var second = cancellation.StartNew();

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);

        cancellation.CancelCurrent();
        Assert.True(second.IsCancellationRequested);

        cancellation.CancelCurrent();
        var third = cancellation.CurrentOrStart();
        Assert.False(third.IsCancellationRequested);
    }

    [Fact]
    public void OperationCancellationSupportsRepeatedConcurrentReplacement()
    {
        using var cancellation = new OperationCancellation();
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Parallel.For(0, 500, index =>
        {
            try
            {
                if (index % 3 == 0)
                    cancellation.CancelCurrent();
                else
                    _ = cancellation.StartNew();
            }
            catch (Exception exception)
            {
                exceptions.Enqueue(exception);
            }
        });

        Assert.Empty(exceptions);
    }
}
