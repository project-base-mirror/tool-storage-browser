using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class S3ClientFactoryTests
{
    [Fact]
    public void AdvancedCompatibilityOptionsMapToSdkConfiguration()
    {
        var profile = new ConnectionProfile
        {
            Name = "Gateway",
            ServiceType = S3ServiceType.Custom,
            Endpoint = "https://127.0.0.1:9443/api/s3/",
            Region = "display-region",
            SignatureRegion = "signing-region",
            AccessKey = "access",
            SecretKey = "secret",
            AddressingStyle = AddressingStyle.PathStyle,
            IgnoreCertificateErrors = true,
            CustomHostHeader = "storage.internal:9443",
            FollowTemporaryRedirects = false
        };

        var snapshot = new S3ClientFactory().Describe(profile);

        Assert.Equal("https://127.0.0.1:9443/api/s3", snapshot.ServiceUrl);
        Assert.Equal("signing-region", snapshot.AuthenticationRegion);
        Assert.True(snapshot.ForcePathStyle);
        Assert.True(snapshot.DisableHostPrefixInjection);
        Assert.False(snapshot.AllowAutoRedirect);
        Assert.True(snapshot.IgnoreCertificateErrors);
        Assert.Equal("storage.internal:9443", snapshot.CustomHostHeader);
    }

    [Fact]
    public void EndpointControlsRoutingWhenRegionIsEmpty()
    {
        var profile = new ConnectionProfile
        {
            Name = "Endpoint only",
            ServiceType = S3ServiceType.Custom,
            Endpoint = "https://storage.example.test/base",
            Region = string.Empty,
            AccessKey = "access",
            SecretKey = "secret"
        };

        var snapshot = new S3ClientFactory().Describe(profile);

        Assert.Equal("https://storage.example.test/base", snapshot.ServiceUrl);
        Assert.Equal("us-east-1", snapshot.AuthenticationRegion);
    }

    [Fact]
    public void AmazonAutoRegionUsesGlobalEndpointAndSafeSigningRegion()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Amazon auto",
            AccessKey = "access",
            SecretKey = "secret"
        };

        var config = new S3ClientFactory().CreateConfig(profile);

        Assert.Equal("https://s3.amazonaws.com/", config.ServiceURL);
        Assert.Equal("us-east-1", config.AuthenticationRegion);
        Assert.Null(config.RegionEndpoint);
    }

    [Fact]
    public void MinioAutoAddressingForcesPathStyleForBucketOperations()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = "MinIO",
            AccessKey = "access",
            SecretKey = "secret",
            AddressingStyle = AddressingStyle.Auto
        };

        var snapshot = new S3ClientFactory().Describe(profile);

        Assert.True(snapshot.ForcePathStyle);
        Assert.True(snapshot.DisableHostPrefixInjection);
    }

    [Fact]
    public void MinioEndpointBasePathIsNormalizedToApiRoot()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = "MinIO proxy path",
            Endpoint = "https://storage.example.test:9443/minio/",
            AccessKey = "access",
            SecretKey = "secret"
        };

        var snapshot = new S3ClientFactory().Describe(profile);

        var serviceUri = new Uri(snapshot.ServiceUrl);
        Assert.Equal("storage.example.test", serviceUri.Host);
        Assert.Equal(9443, serviceUri.Port);
        Assert.Equal("/", serviceUri.AbsolutePath);
        Assert.True(snapshot.ForcePathStyle);
    }

    [Fact]
    public void SessionTokenSelectsSessionCredentials()
    {
        var profile = new ConnectionProfile
        {
            Name = "Temporary",
            Endpoint = "https://s3.example.test",
            Region = "us-east-1",
            AccessKey = "access",
            SecretKey = "secret",
            SessionToken = "token"
        };

        Assert.True(new S3ClientFactory().Describe(profile).UsesSessionCredentials);
        Assert.False(new S3ClientFactory().Describe(profile with { SessionToken = string.Empty }).UsesSessionCredentials);
    }

    [Fact]
    public void VirtualHostedStyleDoesNotForcePathAddressing()
    {
        var profile = new ConnectionProfile
        {
            Name = "Virtual",
            Endpoint = "https://s3.example.test",
            Region = "us-east-1",
            AccessKey = "access",
            SecretKey = "secret",
            AddressingStyle = AddressingStyle.VirtualHosted
        };

        var snapshot = new S3ClientFactory().Describe(profile);

        Assert.False(snapshot.ForcePathStyle);
        Assert.False(snapshot.DisableHostPrefixInjection);
    }

    [Fact]
    public void SnapshotDescribesRequestedExternalCredentialSourceWithoutResolvingIt()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "AWS profile",
            CredentialSource = CredentialSourceKind.AwsSharedProfile,
            AwsProfileName = "readonly"
        };

        var snapshot = new S3ClientFactory().Describe(profile);

        Assert.Equal("AWS shared profile：readonly", snapshot.CredentialSource);
    }
}
