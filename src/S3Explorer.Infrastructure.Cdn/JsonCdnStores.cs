using System.Text.Json;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class JsonCdnConfigurationStore : ICdnConfigurationStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public JsonCdnConfigurationStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "cdn-config.json");
    }

    public async Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return CdnConfiguration.Empty;
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<CdnConfigurationDocument>(
                    stream,
                    Options,
                    cancellationToken)
                ?? new CdnConfigurationDocument();
            EnsureSupportedVersion(document.Version, "CDN 配置");
            if (document.Profiles is null || document.Bindings is null ||
                document.Profiles.Any(value => value is null) ||
                document.Bindings.Any(value => value is null))
                throw new InvalidDataException("CDN 配置文件包含空集合或空记录。");
            var configuration = new CdnConfiguration(document.Profiles, document.Bindings);
            CdnConfigurationValidator.EnsureValid(configuration);
            return configuration;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        CdnConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        CdnConfigurationValidator.EnsureValid(configuration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectory();
            var temporaryPath = _path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new CdnConfigurationDocument
                    {
                        Profiles = [.. configuration.Profiles],
                        Bindings = [.. configuration.Bindings]
                    },
                    Options,
                    cancellationToken);
            }
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
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

public sealed class JsonCdnCredentialStore : ICdnCredentialStore
{
    private readonly ICredentialProtector _protector;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public JsonCdnCredentialStore(ICredentialProtector protector, string? path = null)
    {
        _protector = protector;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "cdn-credentials.json");
    }

    public async Task<IReadOnlyList<CdnCredential>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<CdnCredentialDocument>(
                    stream,
                    Options,
                    cancellationToken)
                ?? new CdnCredentialDocument();
            EnsureSupportedVersion(document.Version, "CDN 凭据");
            if (document.Credentials is null || document.Credentials.Any(value => value is null))
                throw new InvalidDataException("CDN 凭据文件包含空集合或空记录。");
            var credentials = document.Credentials
                .Select(value => new CdnCredential
                {
                    Id = value.Id,
                    Name = value.Name,
                    AuthenticationType = value.AuthenticationType,
                    HeaderName = value.HeaderName,
                    Secret = _protector.Unprotect(value.ProtectedSecret)
                })
                .ToArray();
            var validation = CdnConfigurationValidator.Validate(CdnConfiguration.Empty, credentials);
            if (validation.Count > 0)
                throw new InvalidDataException(string.Join(Environment.NewLine, validation));
            return credentials;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<CdnCredential> credentials,
        CancellationToken cancellationToken = default)
    {
        var validation = CdnConfigurationValidator.Validate(
            CdnConfiguration.Empty,
            credentials);
        if (validation.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, validation));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectory();
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
            var temporaryPath = _path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    Options,
                    cancellationToken);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

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
