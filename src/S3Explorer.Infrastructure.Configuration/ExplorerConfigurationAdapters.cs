using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Configuration;

public sealed class ExplorerProfileStore(IExplorerConfigurationStore configurationStore) : IProfileStore
{
    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        (await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Storage.Profiles;

    public async Task SaveAsync(
        IReadOnlyCollection<ConnectionProfile> profiles,
        CancellationToken cancellationToken = default) =>
        await configurationStore.UpdateAsync(
            current => current with
            {
                Storage = new ConnectionProfileConfiguration(profiles.ToArray(), current.Storage.Groups).Normalize()
            },
            cancellationToken).ConfigureAwait(false);

    public async Task<ConnectionProfileConfiguration> LoadConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        (await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Storage;

    public async Task SaveConfigurationAsync(
        ConnectionProfileConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        await configurationStore.UpdateAsync(
            current => current with { Storage = configuration.Normalize() },
            cancellationToken).ConfigureAwait(false);
}

public sealed class ExplorerCdnConfigurationStore(IExplorerConfigurationStore configurationStore)
    : ICdnConfigurationStore
{
    public async Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
        (await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Cdn;

    public async Task SaveAsync(
        CdnConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        await configurationStore.UpdateAsync(
            current => current with { Cdn = configuration },
            cancellationToken).ConfigureAwait(false);
}

public sealed class ExplorerCredentialStore(IExplorerConfigurationStore configurationStore)
    : ICredentialStore
{
    public async Task<IReadOnlyList<CredentialProfile>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        (await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false)).CredentialVault;

    public async Task SaveAsync(
        IReadOnlyCollection<CredentialProfile> credentials,
        CancellationToken cancellationToken = default) =>
        await configurationStore.UpdateAsync(
            current => current with { CredentialVault = credentials.ToArray() },
            cancellationToken).ConfigureAwait(false);
}
