using System.Text.Json;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class JsonCdnConfigurationStore : ICdnConfigurationStore, IRecoveryAwareStore
{
    private readonly DurableJsonFile _file;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public JsonCdnConfigurationStore(string? path = null)
    {
        _file = new DurableJsonFile(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "cdn-config.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var document = await _file.LoadAsync(
            static () => new CdnConfigurationDocument(),
            Options,
            ValidateDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new CdnConfiguration(document.Profiles, document.Bindings);
    }

    public async Task SaveAsync(
        CdnConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await _file.SaveAsync(
            new CdnConfigurationDocument
            {
                Profiles = [.. configuration.Profiles],
                Bindings = [.. configuration.Bindings]
            },
            Options,
            ValidateDocument,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateDocument(CdnConfigurationDocument document)
    {
        EnsureSupportedVersion(document.Version, "CDN 配置");
        if (document.Profiles is null || document.Bindings is null ||
            document.Profiles.Any(value => value is null) ||
            document.Bindings.Any(value => value is null))
            throw new InvalidDataException("CDN 配置文件包含空集合或空记录。");
        CdnConfigurationValidator.EnsureValid(new CdnConfiguration(document.Profiles, document.Bindings));
    }

    private static void EnsureSupportedVersion(int version, string documentName)
    {
        if (version != 1)
            throw new InvalidDataException($"不支持的{documentName}文件版本：{version}。");
    }

    private sealed record CdnConfigurationDocument
    {
        public int Version { get; init; } = 1;
        public List<CdnProfile> Profiles { get; init; } = [];
        public List<CdnBinding> Bindings { get; init; } = [];
    }
}

public sealed class JsonCdnCredentialStore : ICdnCredentialStore, IRecoveryAwareStore
{
    private readonly ICredentialProtector _protector;
    private readonly DurableJsonFile _file;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public JsonCdnCredentialStore(ICredentialProtector protector, string? path = null)
    {
        _protector = protector;
        _file = new DurableJsonFile(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "cdn-credentials.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<IReadOnlyList<CdnCredential>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await _file.LoadAsync(
            static () => new CdnCredentialDocument(),
            Options,
            ValidateDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToRuntime(document);
    }

    public async Task SaveAsync(
        IReadOnlyCollection<CdnCredential> credentials,
        CancellationToken cancellationToken = default)
    {
        var document = new CdnCredentialDocument
        {
            Credentials = credentials.Select(value => new StoredCredential
            {
                Id = value.Id,
                Name = value.Name,
                AuthenticationType = value.AuthenticationType,
                HeaderName = value.HeaderName,
                ProtectedSecret = _protector.Protect(value.Secret)
            }).ToList()
        };
        await _file.SaveAsync(document, Options, ValidateDocument, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ValidateDocument(CdnCredentialDocument document)
    {
        EnsureSupportedVersion(document.Version, "CDN 凭据");
        if (document.Credentials is null || document.Credentials.Any(value => value is null))
            throw new InvalidDataException("CDN 凭据文件包含空集合或空记录。");
        var validation = CdnConfigurationValidator.Validate(CdnConfiguration.Empty, ToRuntime(document));
        if (validation.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, validation));
    }

    private CdnCredential[] ToRuntime(CdnCredentialDocument document) =>
        document.Credentials.Select(value => new CdnCredential
        {
            Id = value.Id,
            Name = value.Name,
            AuthenticationType = value.AuthenticationType,
            HeaderName = value.HeaderName,
            Secret = _protector.Unprotect(value.ProtectedSecret)
        }).ToArray();

    private static void EnsureSupportedVersion(int version, string documentName)
    {
        if (version != 1)
            throw new InvalidDataException($"不支持的{documentName}文件版本：{version}。");
    }

    private sealed record CdnCredentialDocument
    {
        public int Version { get; init; } = 1;
        public List<StoredCredential> Credentials { get; init; } = [];
    }

    private sealed record StoredCredential
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public CdnAuthenticationType AuthenticationType { get; init; }
        public string HeaderName { get; init; } = string.Empty;
        public string ProtectedSecret { get; init; } = string.Empty;
    }
}
