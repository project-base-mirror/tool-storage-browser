using System.Runtime.Versioning;
using System.Text;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Configuration;
using Xunit;

namespace S3Explorer.Infrastructure.Configuration.Tests;

[SupportedOSPlatform("windows")]
public sealed class LegacyArchiveTests
{
    [Fact]
    public async Task ArchiveFailureDoesNotBlockHealthyConfigurationAndRetriesNextOpen()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "S3Explorer.ArchiveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var protector = new TestProtector();
            var store = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with { Name = "Keep me" };
            await store.UpdateAsync(current => current with { Storage = new([profile], []) }, token);
            // A file at the archive-directory path prevents all legacy moves.
            var archive = Path.Combine(root, "legacy-archive");
            await File.WriteAllTextAsync(archive, "occupied", token);
            var legacy = Path.Combine(root, "profiles.json");
            await File.WriteAllTextAsync(legacy, "obsolete legacy configuration", token);

            var reopened = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            Assert.Equal("Keep me", Assert.Single((await reopened.LoadAsync(token)).Storage.Profiles).Name);
            Assert.Contains("profiles.json", reopened.LegacyArchiveWarning!);
            Assert.True(File.Exists(legacy));

            File.Delete(archive);
            var retried = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            Assert.Null(retried.LegacyArchiveWarning);
            Assert.False(File.Exists(legacy));
            Assert.Single(Directory.GetFiles(archive, "profiles.json", SearchOption.AllDirectories));
            Assert.Equal("Keep me", Assert.Single((await retried.LoadAsync(token)).Storage.Profiles).Name);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestProtector : IConfigurationPayloadProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
