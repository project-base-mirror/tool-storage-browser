using System.Runtime.Versioning;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Configuration;
using Xunit;

namespace S3Explorer.Infrastructure.Configuration.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationRecoveryTests
{
    [Fact]
    public async Task RestoreValidatesAnIsolatedCopyAndKeepsSourceAndQuarantine()
    {
        var root = Temp();
        var sourceRoot = Path.Combine(root, "source");
        var targetRoot = Path.Combine(root, "target");
        var token = TestContext.Current.CancellationToken;
        try
        {
            var protector = new FakeProtector();
            var credential = new CredentialProfile
            {
                Name = "recovered credential", Provider = CredentialProviderKind.GenericHttp,
                Kind = CredentialKind.BearerToken, Secret = "test-secret"
            };
            var source = await ExplorerConfigurationStore.CreateOrReplaceAsync(sourceRoot,
                ExplorerConfiguration.Empty with { CredentialVault = [credential] }, protector, token);
            var sourceBytes = await File.ReadAllBytesAsync(source.Path, token);
            Directory.CreateDirectory(targetRoot);
            var preserved = Path.Combine(targetRoot, "configuration.json.corrupt-test");
            await File.WriteAllTextAsync(preserved, "original corrupted bytes", token);

            var recovered = await ConfigurationRecovery.RestoreFromFileAsync(targetRoot, source.Path, protector, token);

            Assert.Equal(credential, Assert.Single((await recovered.LoadAsync(token)).CredentialVault));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source.Path, token));
            Assert.Equal("original corrupted bytes", await File.ReadAllTextAsync(preserved, token));
            Assert.Empty(Directory.EnumerateDirectories(targetRoot, ".configuration-recovery-*"));
            var reopened = await ExplorerConfigurationStore.OpenAsync(targetRoot, protector, token);
            Assert.Equal(credential, Assert.Single((await reopened.LoadAsync(token)).CredentialVault));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidRestoreDoesNotReplaceExistingConfigurationOrChangeSource()
    {
        var root = Temp();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var protector = new FakeProtector();
            var target = await ExplorerConfigurationStore.CreateOrReplaceAsync(root, ExplorerConfiguration.Empty, protector, token);
            var before = await File.ReadAllBytesAsync(target.Path, token);
            var sourcePath = Path.Combine(root, "invalid-backup.json");
            await File.WriteAllTextAsync(sourcePath, "null", token);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ConfigurationRecovery.RestoreFromFileAsync(root, sourcePath, protector, token));

            Assert.Equal(before, await File.ReadAllBytesAsync(target.Path, token));
            Assert.Equal("null", await File.ReadAllTextAsync(sourcePath, token));
            Assert.False(File.Exists(target.Path + ".bak"));
            Assert.Empty(Directory.EnumerateDirectories(root, ".configuration-recovery-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitResetCreatesUsableEmptyConfigurationWithoutDeletingPreservedFiles()
    {
        var root = Temp();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var preserved = Path.Combine(root, "configuration.json.corrupt-test");
            await File.WriteAllTextAsync(preserved, "original", token);
            await ExplorerConfigurationStore.CreateOrReplaceAsync(root, ExplorerConfiguration.Empty, new FakeProtector(), token);
            var opened = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), token);
            Assert.Empty((await opened.LoadAsync(token)).Storage.Profiles);
            Assert.Equal("original", await File.ReadAllTextAsync(preserved, token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledRestoreDoesNotCreateAConfiguration()
    {
        var root = Temp();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ConfigurationRecovery.RestoreFromFileAsync(root, "unused", new FakeProtector(), cancellation.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "s3explorer-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProtector : IConfigurationPayloadProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
