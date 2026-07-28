using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public async Task StoreDoesNotWritePlaintextSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        var store = new JsonProfileStore(new FakeProtector(), path);
        var profile = new ConnectionProfile
        {
            Name = "MinIO",
            ServiceType = S3ServiceType.MinIO,
            Endpoint = "http://127.0.0.1:9000",
            Region = "us-east-1",
            SignatureRegion = "custom-signing-region",
            AccessKey = "access",
            SecretKey = "plain-secret",
            SessionToken = "plain-session",
            AddressingStyle = AddressingStyle.PathStyle,
            UseHttps = false,
            IgnoreCertificateErrors = true,
            CustomHostHeader = "storage.internal:9000",
            FollowTemporaryRedirects = false,
            EnableMultiObjectDelete = false,
            EnableMultipartCopy = false,
            HealthStatus = ConnectionHealthStatus.Healthy,
            LastConnectionCheckedAtUtc = new DateTimeOffset(2026, 7, 27, 8, 30, 0, TimeSpan.Zero),
            LastConnectionSucceededAtUtc = new DateTimeOffset(2026, 7, 27, 8, 30, 0, TimeSpan.Zero)
        };

        try
        {
            await store.SaveAsync([profile]);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("plain-secret", json);
            Assert.DoesNotContain("plain-session", json);

            var loaded = Assert.Single(await store.LoadAsync());
            Assert.Equal("plain-secret", loaded.SecretKey);
            Assert.Equal("custom-signing-region", loaded.SignatureRegion);
            Assert.Equal("storage.internal:9000", loaded.CustomHostHeader);
            Assert.True(loaded.IgnoreCertificateErrors);
            Assert.False(loaded.FollowTemporaryRedirects);
            Assert.False(loaded.EnableMultiObjectDelete);
            Assert.False(loaded.EnableMultipartCopy);
            Assert.Equal(ConnectionHealthStatus.Healthy, loaded.HealthStatus);
            Assert.Equal(profile.LastConnectionCheckedAtUtc, loaded.LastConnectionCheckedAtUtc);
            Assert.Equal(profile.LastConnectionSucceededAtUtc, loaded.LastConnectionSucceededAtUtc);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExternalCredentialSourcePersistsOnlyNonSensitiveReference()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        var store = new JsonProfileStore(new FakeProtector(), path);
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Shared profile",
            CredentialSource = CredentialSourceKind.AwsSharedProfile,
            AwsProfileName = "production-readonly",
            AccessKey = "must-not-persist-access",
            SecretKey = "must-not-persist-secret",
            SessionToken = "must-not-persist-session"
        };

        try
        {
            await store.SaveAsync([profile]);
            var json = await File.ReadAllTextAsync(path);

            Assert.Contains("awsSharedProfile", json, StringComparison.Ordinal);
            Assert.Contains("production-readonly", json, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-persist", json, StringComparison.Ordinal);

            var loaded = Assert.Single(await store.LoadAsync());
            Assert.Equal(CredentialSourceKind.AwsSharedProfile, loaded.CredentialSource);
            Assert.Equal("production-readonly", loaded.AwsProfileName);
            Assert.Empty(loaded.AccessKey);
            Assert.Empty(loaded.SecretKey);
            Assert.Empty(loaded.SessionToken);
            loaded.Validate();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private sealed class FakeProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => string.IsNullOrEmpty(ciphertext)
            ? string.Empty
            : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
