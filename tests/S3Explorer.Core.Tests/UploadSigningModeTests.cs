using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class UploadSigningModeTests
{
    [Theory]
    [InlineData(S3ServiceType.AmazonS3)]
    [InlineData(S3ServiceType.MinIO)]
    [InlineData(S3ServiceType.CloudflareR2)]
    [InlineData(S3ServiceType.Custom)]
    public void NewAndLegacyProfilesDefaultToAuto(S3ServiceType type)
    {
        Assert.Equal(UploadSigningMode.Auto, ConnectionProfile.CreatePreset(type).UploadSigningMode);
        var profile = JsonSerializer.Deserialize<ConnectionProfile>("{}")!;
        Assert.Equal(UploadSigningMode.Auto, profile.UploadSigningMode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void UnknownNumericModesAreRejected(int mode)
    {
        var profile = ValidProfile() with { UploadSigningMode = (UploadSigningMode)mode };
        Assert.Throws<ArgumentOutOfRangeException>(profile.ValidateConfiguration);
    }

    [Theory]
    [InlineData(UploadSigningMode.Auto)]
    [InlineData(UploadSigningMode.Streaming)]
    public void HttpKeepsSdkModesAvailable(UploadSigningMode mode) =>
        (ValidProfile() with { Endpoint = "http://localhost:9000", UploadSigningMode = mode }).ValidateConfiguration();

    [Fact]
    public void UnsignedPayloadRequiresActualHttpsEndpoint()
    {
        var profile = ValidProfile() with { UploadSigningMode = UploadSigningMode.UnsignedPayload };
        Assert.Throws<ArgumentException>(() => (profile with { Endpoint = "http://localhost:9000", UseHttps = true }).ValidateConfiguration());
        (profile with { UseHttps = false }).ValidateConfiguration(); // Endpoint controls the SDK transport.
    }

    [Theory]
    [InlineData(UploadSigningMode.Auto)]
    [InlineData(UploadSigningMode.Streaming)]
    [InlineData(UploadSigningMode.UnsignedPayload)]
    public void SerializationAndRecordClonesPreserveMode(UploadSigningMode mode)
    {
        var profile = ValidProfile() with { UploadSigningMode = mode };
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var loaded = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(profile, options), options)!;
        Assert.Equal(mode, loaded.UploadSigningMode);
        Assert.Equal(mode, (loaded with { Name = "clone" }).UploadSigningMode);
        Assert.Equal(mode, new ConnectionProfileConfiguration([loaded], []).Normalize().Profiles[0].UploadSigningMode);
    }

    private static ConnectionProfile ValidProfile() => new()
    {
        Name = "test", ServiceType = S3ServiceType.Custom, Endpoint = "https://storage.example.test",
        AccessKey = "fake-access", SecretKey = "fake-secret"
    };
}
