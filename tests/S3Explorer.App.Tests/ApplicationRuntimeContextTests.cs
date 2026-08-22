using S3Explorer.App;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using S3Explorer.Infrastructure.Configuration;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ApplicationRuntimeContextTests
{
    [Fact]
    public void DevelopmentRuntimeIsIsolatedFromInstalledApplication()
    {
        var roaming = Path.Combine(Path.GetTempPath(), "s3explorer-roaming");
        var local = Path.Combine(Path.GetTempPath(), "s3explorer-local");

        var development = ApplicationRuntimeContext.Resolve(
            new AutomationOptions(),
            developmentMode: true,
            roaming,
            local);
        var production = ApplicationRuntimeContext.Resolve(
            new AutomationOptions(),
            developmentMode: false,
            roaming,
            local);

        Assert.True(development.DevelopmentMode);
        Assert.NotEqual(production.InstanceKey, development.InstanceKey);
        Assert.NotEqual(production.DataRoot, development.DataRoot);
        Assert.Equal(ApplicationRuntimeContext.DevelopmentInstanceKey, development.InstanceKey);
        Assert.Equal(Path.Combine(roaming, ApplicationRuntimeContext.DevelopmentDataDirectoryName), development.DataRoot);
        Assert.Equal(Path.Combine(local, ApplicationRuntimeContext.DevelopmentDataDirectoryName), development.LocalDataRoot);
        Assert.Equal(SingleInstanceCoordinator.ApplicationInstanceKey, production.InstanceKey);
        Assert.Equal(Path.Combine(roaming, ApplicationRuntimeContext.ProductionDataDirectoryName), production.DataRoot);
        Assert.Equal(Path.Combine(local, ApplicationRuntimeContext.ProductionDataDirectoryName), production.LocalDataRoot);
    }

    [Fact]
    public void AutomationRuntimeKeepsItsExplicitIsolationContract()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "s3explorer-automation-runtime"));
        var options = AutomationOptions.Parse(
        [
            "--automation-state", Path.Combine(root, "state.json"),
            "--automation-data-dir", Path.Combine(root, "data"),
            "--automation-instance-key", "automation.test"
        ]);

        var runtime = ApplicationRuntimeContext.Resolve(options, developmentMode: true, root, root);

        Assert.False(runtime.DevelopmentMode);
        Assert.Equal("automation.test", runtime.InstanceKey);
        Assert.Equal(Path.Combine(root, "data"), runtime.DataRoot);
        Assert.Equal(runtime.DataRoot, runtime.LocalDataRoot);
    }

    [Fact]
    public async Task DevelopmentSnapshotCopiesUnifiedConfigurationWithoutPlaintext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-snapshot-" + Guid.NewGuid().ToString("N"));
        var production = Path.Combine(root, "production");
        var development = Path.Combine(root, "development");
        var protector = new FakeProtector();
        var credential = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "production-key",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "production-ak",
            Secret = "production-secret"
        };
        var source = await ExplorerConfigurationStore.OpenAsync(production, protector, cancellationToken);
        await source.SaveAsync(new ExplorerConfiguration(
            new ConnectionProfileConfiguration(
                [ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
                {
                    Name = "production-connection",
                    CredentialId = credential.Id
                }], []),
            CdnConfiguration.Empty,
            [credential]), cancellationToken);
        var productionEnvelope = await File.ReadAllTextAsync(source.Path, cancellationToken);

        Assert.True(await DevelopmentConfigurationSnapshot.RefreshAsync(production, development, protector, cancellationToken));

        var copied = await ExplorerConfigurationStore.OpenAsync(development, protector, cancellationToken);
        var configuration = await copied.LoadAsync(cancellationToken);
        Assert.Equal("production-key", configuration.CredentialVault.Single().Name);
        Assert.Equal("production-secret", configuration.CredentialVault.Single().Secret);
        Assert.DoesNotContain("production-secret", await File.ReadAllTextAsync(copied.Path, cancellationToken), StringComparison.Ordinal);
        Assert.Equal(productionEnvelope, await File.ReadAllTextAsync(source.Path, cancellationToken));
        Assert.False(File.Exists(Path.Combine(development, "settings.json")));
        Assert.False(File.Exists(Path.Combine(development, "transfers.json")));
    }

    [Fact]
    public async Task DevelopmentSnapshotReplacesCorruptDebugConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-snapshot-" + Guid.NewGuid().ToString("N"));
        var production = Path.Combine(root, "production");
        var development = Path.Combine(root, "development");
        var protector = new FakeProtector();
        var credential = new CredentialProfile
        {
            Id = Guid.NewGuid(), Name = "production", Provider = CredentialProviderKind.GenericHttp,
            Kind = CredentialKind.BearerToken, Secret = "production-secret"
        };
        await ExplorerConfigurationStore.CreateOrReplaceAsync(
            production,
            new ExplorerConfiguration(
                new ConnectionProfileConfiguration([], []),
                CdnConfiguration.Empty,
                [credential]),
            protector,
            cancellationToken);
        Directory.CreateDirectory(development);
        await File.WriteAllTextAsync(Path.Combine(development, "configuration.json"), "{broken", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(development, "configuration.json.bak"), "{also-broken", cancellationToken);

        Assert.True(await DevelopmentConfigurationSnapshot.RefreshAsync(
            production,
            development,
            protector,
            cancellationToken));

        var refreshed = await ExplorerConfigurationStore.OpenAsync(development, protector, cancellationToken);
        Assert.Equal("production", (await refreshed.LoadAsync(cancellationToken)).CredentialVault.Single().Name);
    }

    [Fact]
    public async Task UnrecoverableProductionConfigurationPreservesPreviousDebugSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-snapshot-" + Guid.NewGuid().ToString("N"));
        var production = Path.Combine(root, "production");
        var development = Path.Combine(root, "development");
        var protector = new FakeProtector();
        var existing = await ExplorerConfigurationStore.OpenAsync(development, protector, cancellationToken);
        var existingCredential = new CredentialProfile
        {
            Id = Guid.NewGuid(), Name = "existing", Provider = CredentialProviderKind.GenericHttp,
            Kind = CredentialKind.BearerToken, Secret = "existing-secret"
        };
        await existing.SaveAsync(new ExplorerConfiguration(
            new ConnectionProfileConfiguration([], []), CdnConfiguration.Empty, [existingCredential]), cancellationToken);

        Directory.CreateDirectory(production);
        await File.WriteAllTextAsync(Path.Combine(production, "configuration.json"), "{broken", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(production, "configuration.json.bak"), "{also-broken", cancellationToken);

        Assert.False(await DevelopmentConfigurationSnapshot.RefreshAsync(production, development, protector, cancellationToken));
        var preserved = await ExplorerConfigurationStore.OpenAsync(development, protector, cancellationToken);
        Assert.Equal("existing", (await preserved.LoadAsync(cancellationToken)).CredentialVault.Single().Name);
    }

    private sealed class FakeProtector : IConfigurationPayloadProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
