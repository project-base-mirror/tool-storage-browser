namespace S3Explorer.Core;

public interface IExplorerConfigurationStore
{
    Task<ExplorerConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ExplorerConfiguration configuration, CancellationToken cancellationToken = default);
    Task<ExplorerConfiguration> UpdateAsync(
        Func<ExplorerConfiguration, ExplorerConfiguration> update,
        CancellationToken cancellationToken = default);
}

public sealed record ExplorerConfiguration(
    ConnectionProfileConfiguration Storage,
    CdnConfiguration Cdn,
    IReadOnlyList<CredentialProfile> CredentialVault)
{
    public const int CurrentSchema = 1;
    public static ExplorerConfiguration Empty { get; } = new(
        ConnectionProfileConfiguration.Empty,
        CdnConfiguration.Empty,
        []);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Storage);
        ArgumentNullException.ThrowIfNull(Cdn);
        ArgumentNullException.ThrowIfNull(CredentialVault);

        var credentials = new CredentialVault(CredentialVault);
        foreach (var profile in Storage.Profiles)
            ValidateStorageProfile(profile, credentials);

        CdnConfigurationValidator.EnsureValid(Cdn);
        foreach (var profile in Cdn.Profiles)
            ValidateCdnProfile(profile, credentials);

        foreach (var binding in Cdn.Bindings)
        {
            if (!Storage.Profiles.Any(profile => profile.Id == binding.StorageProfileId))
                throw new InvalidDataException($"CDN 关联引用了不存在的存储连接：{binding.StorageProfileId}");
        }
    }

    public ExplorerConfiguration ResolveCredentialReferences()
    {
        Validate();
        var credentials = new CredentialVault(CredentialVault);
        var profiles = Storage.Profiles
            .Select(profile => ResolveStorageProfile(profile, credentials))
            .ToArray();
        var resolved = this with
        {
            Storage = new ConnectionProfileConfiguration(profiles, Storage.Groups)
        };
        foreach (var profile in resolved.Storage.Profiles)
        {
            if (profile.CredentialSource == CredentialSourceKind.StoredKeys && profile.CredentialId is null)
                profile.ValidateConfiguration();
            else
                profile.Validate();
        }
        return resolved;
    }

    public ExplorerConfiguration ToPersistentSnapshot()
    {
        var persistent = this with
        {
            Storage = new ConnectionProfileConfiguration(
                Storage.Profiles.Select(profile => profile with
                {
                    AccessKey = string.Empty,
                    SecretKey = string.Empty,
                    SessionToken = string.Empty,
                    AwsExternalId = string.Empty
                }).ToArray(),
                Storage.Groups)
        };
        persistent.Validate();
        return persistent;
    }

    public CredentialProfile? FindCredential(Guid? id) =>
        id is Guid value
            ? CredentialVault.FirstOrDefault(credential => credential.Id == value)
            : null;

    private static void ValidateStorageProfile(ConnectionProfile profile, CredentialVault credentials)
    {
        try { profile.ValidateConfiguration(); }
        catch (ArgumentException exception) { throw new InvalidDataException(exception.Message, exception); }

        if (profile.CredentialSource == CredentialSourceKind.StoredKeys)
        {
            if (profile.CredentialId is not Guid credentialId)
            {
                if (!string.IsNullOrEmpty(profile.AccessKey) || !string.IsNullOrEmpty(profile.SecretKey) ||
                    !string.IsNullOrEmpty(profile.SessionToken))
                    throw new InvalidDataException($"存储连接“{profile.Name}”包含未纳入凭据中心的秘密值。");
                return;
            }
            var credential = credentials.FindById(credentialId)
                ?? throw new InvalidDataException($"存储连接“{profile.Name}”引用了不存在的凭据：{credentialId}");
            if (!credential.IsCompatibleWith(profile.ServiceType))
                throw new InvalidDataException($"凭据“{credential.Name}”与存储连接“{profile.Name}”的 Provider 不兼容。");
        }
        else if (profile.CredentialId is not null)
        {
            throw new InvalidDataException($"使用 AWS 外部凭据来源的连接“{profile.Name}”不能再关联 AccessKey 凭据。");
        }

        if (profile.AwsExternalIdCredentialId is Guid externalIdCredentialId)
        {
            if (profile.CredentialSource != CredentialSourceKind.AwsAssumeRole)
                throw new InvalidDataException($"只有 AssumeRole 连接可以关联 External ID 凭据：{profile.Name}");
            var credential = credentials.FindById(externalIdCredentialId)
                ?? throw new InvalidDataException($"连接“{profile.Name}”引用了不存在的 External ID 凭据。");
            if (credential.Provider != CredentialProviderKind.AmazonWebServices ||
                credential.Kind != CredentialKind.SecretValue)
                throw new InvalidDataException($"连接“{profile.Name}”的 External ID 必须引用 AWS SecretValue 凭据。");
        }
    }

    private static void ValidateCdnProfile(CdnProfile profile, CredentialVault credentials)
    {
        if (profile.CredentialId is not Guid credentialId)
            return;

        var credential = credentials.FindById(credentialId)
            ?? throw new InvalidDataException($"CDN 配置“{profile.Name}”引用了不存在的凭据：{credentialId}");
        if (!credential.IsCompatibleWith(profile.ProviderId))
            throw new InvalidDataException($"凭据“{credential.Name}”与 CDN Provider“{profile.ProviderId}”不兼容。");
    }

    private static ConnectionProfile ResolveStorageProfile(
        ConnectionProfile profile,
        CredentialVault credentials)
    {
        var result = profile;
        if (profile.CredentialSource == CredentialSourceKind.StoredKeys &&
            profile.CredentialId is Guid credentialId)
        {
            var credential = credentials.FindById(credentialId)!;
            result = result with
            {
                AccessKey = credential.AccessKeyId,
                SecretKey = credential.Secret,
                SessionToken = credential.SessionToken
            };
        }

        if (profile.AwsExternalIdCredentialId is Guid externalIdCredentialId)
            result = result with { AwsExternalId = credentials.FindById(externalIdCredentialId)!.Secret };
        return result;
    }
}
