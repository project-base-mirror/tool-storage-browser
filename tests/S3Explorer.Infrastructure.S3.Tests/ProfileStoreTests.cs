using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

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
            AccessKey = "access",
            SecretKey = "plain-secret",
            SessionToken = "plain-session",
            AddressingStyle = AddressingStyle.PathStyle,
            UseHttps = false
        };

        try
        {
            await store.SaveAsync([profile]);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("plain-secret", json);
            Assert.DoesNotContain("plain-session", json);

            var loaded = await store.LoadAsync();
            Assert.Equal("plain-secret", Assert.Single(loaded).SecretKey);
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
