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
            await store.SaveAsync([profile], TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("plain-secret", json);
            Assert.DoesNotContain("plain-session", json);

            var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
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
            await store.SaveAsync([profile], TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("awsSharedProfile", json, StringComparison.Ordinal);
            Assert.Contains("production-readonly", json, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-persist", json, StringComparison.Ordinal);

            var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task LegacyAmazonProfileWithCompatibleEndpointLoadsAsCustomWithoutLosingEndpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            {
              "version": 3,
              "profiles": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "legacy-compatible",
                  "serviceType": "amazonS3",
                  "endpoint": "https://oss-cn-shenzhen.aliyuncs.com",
                  "region": "auto",
                  "accessKey": "access",
                  "protectedSecretKey": "c2VjcmV0",
                  "addressingStyle": "auto",
                  "useHttps": true
                }
              ]
            }
            """, TestContext.Current.CancellationToken);
        var store = new JsonProfileStore(new FakeProtector(), path);

        try
        {
            var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));

            Assert.Equal(S3ServiceType.Custom, loaded.ServiceType);
            Assert.Equal("https://oss-cn-shenzhen.aliyuncs.com", loaded.Endpoint);
            Assert.Equal("auto", loaded.Region);
            Assert.Equal("secret", loaded.SecretKey);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SchemaFourRoundTripsGroupsAndProtectsAssumeRoleExternalId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        var store = new JsonProfileStore(new FakeProtector(), path);
        var group = new ConnectionGroup { Name = "Production", SortOrder = 2, IsExpanded = false };
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Audit role",
            GroupId = group.Id,
            SortOrder = 4,
            CredentialSource = CredentialSourceKind.AwsAssumeRole,
            AwsSourceProfileName = "bootstrap",
            AwsRoleArn = "arn:aws:iam::123456789012:role/Audit",
            AwsRoleSessionName = "s3explorer-audit",
            AwsRoleSourceIdentity = "operator-42",
            AwsExternalId = "plain-external-id",
            AwsSessionDurationSeconds = 1800
        };

        try
        {
            await store.SaveConfigurationAsync(new ConnectionProfileConfiguration([profile], [group]), TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("\"version\": 4", json, StringComparison.Ordinal);
            Assert.Contains("Production", json, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-external-id", json, StringComparison.Ordinal);

            var loaded = await store.LoadConfigurationAsync(TestContext.Current.CancellationToken);
            var loadedGroup = Assert.Single(loaded.Groups);
            var loadedProfile = Assert.Single(loaded.Profiles);
            Assert.Equal(group.Id, loadedGroup.Id);
            Assert.False(loadedGroup.IsExpanded);
            Assert.Equal(group.Id, loadedProfile.GroupId);
            Assert.Equal("plain-external-id", loadedProfile.AwsExternalId);
            Assert.Equal("operator-42", loadedProfile.AwsRoleSourceIdentity);
            Assert.Equal(1800, loadedProfile.AwsSessionDurationSeconds);
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
