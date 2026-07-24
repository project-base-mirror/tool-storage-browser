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
            EnableMultipartCopy = false
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
