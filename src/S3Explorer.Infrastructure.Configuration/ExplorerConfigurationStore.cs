using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.Infrastructure.Configuration;

[SupportedOSPlatform("windows")]
public sealed class ExplorerConfigurationStore : IExplorerConfigurationStore, IRecoveryAwareStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly DurableJsonFile _file;
    private readonly IConfigurationPayloadProtector _protector;
    private readonly string _dataRoot;
    private readonly string _archiveRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonStoreRecoveryInfo? _lastRecovery;
    public JsonStoreRecoveryInfo? LastRecovery => _lastRecovery;
    public string Path => _file.Path;

    private sealed record Envelope(int Schema, string EncryptedPayload);

    private ExplorerConfigurationStore(string dataRoot, IConfigurationPayloadProtector protector)
    {
        _dataRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataRoot));
        _archiveRoot = System.IO.Path.Combine(_dataRoot, "legacy-archive");
        _protector = protector;
        _file = new DurableJsonFile(System.IO.Path.Combine(_dataRoot, "configuration.json"));
    }

    public static async Task<ExplorerConfigurationStore> OpenAsync(
        string dataRoot,
        IConfigurationPayloadProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var store = new ExplorerConfigurationStore(dataRoot, protector ?? new DpapiConfigurationPayloadProtector());
        using var semaphore = store.CreateProcessSemaphore();
        WaitForProcessLock(semaphore, cancellationToken);
        try
        {
            if (File.Exists(store._file.Path) || File.Exists(store._file.Path + ".bak"))
            {
                await store.LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                store.ArchiveLegacyFiles();
                return store;
            }

            var legacy = await store.LoadLegacyAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveCoreAsync(legacy, cancellationToken).ConfigureAwait(false);
            store.ArchiveLegacyFiles();
            await store.LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return store;
        }
        finally { semaphore.Release(); }
    }

    public async Task<ExplorerConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var semaphore = CreateProcessSemaphore();
        try
        {
            WaitForProcessLock(semaphore, cancellationToken);
            try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        }
        finally { _gate.Release(); }
    }

    private async Task<ExplorerConfiguration> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var envelope = await _file.LoadAsync(static () => new Envelope(ExplorerConfiguration.CurrentSchema, ""), Options, ValidateEnvelope, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_file.LastRecovery is { } recovery)
            _lastRecovery = recovery;
        if (envelope.Schema != ExplorerConfiguration.CurrentSchema) throw new InvalidDataException($"不支持的统一配置 Schema：{envelope.Schema}");
        var json = _protector.Unprotect(envelope.EncryptedPayload);
        var configuration = JsonSerializer.Deserialize<ExplorerConfiguration>(json, Options)
            ?? throw new InvalidDataException("统一配置载荷为空。");
        return configuration.ResolveCredentialReferences();
    }

    public async Task SaveAsync(ExplorerConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var semaphore = CreateProcessSemaphore();
        try
        {
            WaitForProcessLock(semaphore, cancellationToken);
            try { await SaveCoreAsync(configuration, cancellationToken).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        }
        finally { _gate.Release(); }
    }

    private async Task SaveCoreAsync(ExplorerConfiguration configuration, CancellationToken cancellationToken)
    {
        var persistent = configuration.ToPersistentSnapshot();
        var json = JsonSerializer.Serialize(persistent, Options);
        var envelope = new Envelope(ExplorerConfiguration.CurrentSchema, _protector.Protect(json));
        await _file.SaveAsync(envelope, Options, ValidateEnvelope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExplorerConfiguration> UpdateAsync(Func<ExplorerConfiguration, ExplorerConfiguration> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var semaphore = CreateProcessSemaphore();
        try
        {
            WaitForProcessLock(semaphore, cancellationToken);
            try
            {
                var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                var next = update(current) ?? throw new InvalidOperationException("配置更新函数返回 null。");
                await SaveCoreAsync(next, cancellationToken).ConfigureAwait(false);
                return next.ResolveCredentialReferences();
            }
            finally { semaphore.Release(); }
        }
        finally { _gate.Release(); }
    }

    private static void ValidateEnvelope(Envelope envelope)
    {
        if (envelope.Schema != ExplorerConfiguration.CurrentSchema) throw new InvalidDataException($"不支持的统一配置 Schema：{envelope.Schema}");
        if (string.IsNullOrWhiteSpace(envelope.EncryptedPayload)) throw new InvalidDataException("统一配置缺少加密载荷。");
    }

    private async Task<ExplorerConfiguration> LoadLegacyAsync(CancellationToken cancellationToken)
    {
        var profilePath = System.IO.Path.Combine(_dataRoot, "profiles.json");
        var cdnPath = System.IO.Path.Combine(_dataRoot, "cdn-config.json");
        var credentialPath = System.IO.Path.Combine(_dataRoot, "cdn-credentials.json");
        var profiles = await new JsonProfileStore(new DpapiCredentialProtector(), profilePath).LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var cdn = await new JsonCdnConfigurationStore(cdnPath).LoadAsync(cancellationToken).ConfigureAwait(false);
        var cdnCredentials = await new JsonCdnCredentialStore(new DpapiCdnCredentialProtector(), credentialPath).LoadAsync(cancellationToken).ConfigureAwait(false);
        var vault = new List<CredentialProfile>();
        var migratedProfiles = profiles.Profiles.Select(profile =>
        {
            if (profile.CredentialSource != CredentialSourceKind.StoredKeys ||
                (string.IsNullOrWhiteSpace(profile.AccessKey) && string.IsNullOrWhiteSpace(profile.SecretKey))) return profile;
            var credentialId = AddMigratedCredential(vault, new CredentialProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Provider = Provider(profile.ServiceType),
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = profile.AccessKey,
                Secret = profile.SecretKey,
                SessionToken = profile.SessionToken
            }, "storage");
            return profile with { CredentialId = credentialId, AccessKey = string.Empty, SecretKey = string.Empty, SessionToken = string.Empty };
        }).ToArray();
        for (var index = 0; index < migratedProfiles.Length; index++)
        {
            var profile = migratedProfiles[index];
            if (profile.CredentialSource != CredentialSourceKind.AwsAssumeRole ||
                string.IsNullOrWhiteSpace(profile.AwsExternalId)) continue;
            var credentialId = AddMigratedCredential(vault, new CredentialProfile
            {
                Id = CreateDerivedCredentialId(profile.Id, "aws-external-id"),
                Name = profile.Name + " - AWS External ID",
                Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.SecretValue,
                Secret = profile.AwsExternalId
            }, "aws-external-id");
            migratedProfiles[index] = profile with
            {
                AwsExternalIdCredentialId = credentialId,
                AwsExternalId = string.Empty
            };
        }
        var cdnCredentialIdMap = new Dictionary<Guid, Guid?>();
        foreach (var credential in cdnCredentials)
        {
            if (credential.AuthenticationType == CdnAuthenticationType.None)
            {
                cdnCredentialIdMap[credential.Id] = null;
                continue;
            }
            cdnCredentialIdMap[credential.Id] = AddMigratedCredential(vault, new CredentialProfile
            {
                Id = credential.Id,
                Name = credential.Name,
                Provider = CredentialProviderKind.GenericHttp,
                Kind = credential.AuthenticationType == CdnAuthenticationType.CustomHeader
                    ? CredentialKind.CustomHeader
                    : CredentialKind.BearerToken,
                HeaderName = credential.HeaderName,
                Secret = credential.Secret
            }, "cdn");
        }
        var migratedCdn = cdn with
        {
            Profiles = cdn.Profiles.Select(profile => profile.CredentialId is Guid legacyCredentialId &&
                    cdnCredentialIdMap.TryGetValue(legacyCredentialId, out var migratedCredentialId)
                ? profile with { CredentialId = migratedCredentialId }
                : profile).ToArray()
        };
        var result = new ExplorerConfiguration(
            new ConnectionProfileConfiguration(migratedProfiles, profiles.Groups),
            migratedCdn,
            vault);
        result.Validate();
        return result;
    }

    private void ArchiveLegacyFiles()
    {
        var files = new[]
            {
                "profiles.json", "profiles.json.bak", "profiles.json.tmp",
                "cdn-config.json", "cdn-config.json.bak", "cdn-config.json.tmp",
                "cdn-credentials.json", "cdn-credentials.json.bak", "cdn-credentials.json.tmp",
                "configuration-transaction.json", "configuration-transaction.json.bak", "configuration-transaction.json.tmp"
            }
            .Select(name => System.IO.Path.Combine(_dataRoot, name)).Where(File.Exists).ToArray();
        if (files.Length == 0) return;
        var directory = System.IO.Path.Combine(_archiveRoot, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff'Z'")); Directory.CreateDirectory(directory);
        foreach (var file in files) File.Move(file, System.IO.Path.Combine(directory, System.IO.Path.GetFileName(file)));
    }

    private static CredentialProviderKind Provider(S3ServiceType type) => type switch
    {
        S3ServiceType.AliyunOss => CredentialProviderKind.AlibabaCloud, S3ServiceType.AmazonS3 => CredentialProviderKind.AmazonWebServices, S3ServiceType.MinIO or S3ServiceType.Custom => CredentialProviderKind.S3Compatible,
        S3ServiceType.CloudflareR2 => CredentialProviderKind.Cloudflare, S3ServiceType.TencentCos => CredentialProviderKind.TencentCloud, S3ServiceType.BackblazeB2 => CredentialProviderKind.Backblaze,
        S3ServiceType.GoogleCloudStorage => CredentialProviderKind.GoogleCloud, S3ServiceType.SupabaseStorage => CredentialProviderKind.Supabase, _ => CredentialProviderKind.S3Compatible
    };

    private Semaphore CreateProcessSemaphore() => new(1, 1, CreateProcessSemaphoreName(_dataRoot));

    private static void WaitForProcessLock(Semaphore semaphore, CancellationToken cancellationToken)
    {
        var waitResult = WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle]);
        if (waitResult == 1) throw new OperationCanceledException(cancellationToken);
    }

    private static string CreateProcessSemaphoreName(string path) => "Local\\S3Explorer.Configuration." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant();

    private static Guid CreateDerivedCredentialId(Guid profileId, string purpose)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{profileId:N}:{purpose}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid AddMigratedCredential(
        ICollection<CredentialProfile> vault,
        CredentialProfile source,
        string purpose)
    {
        var id = source.Id;
        var suffix = 2;
        while (vault.Any(value => value.Id == id))
            id = CreateDerivedCredentialId(source.Id, $"{purpose}-{suffix++}");

        var name = source.Name.Trim();
        var baseName = name;
        suffix = 2;
        while (vault.Any(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";

        vault.Add(source with { Id = id, Name = name });
        return id;
    }
}
