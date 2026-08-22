using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// Writes a complete configuration without first loading or migrating the target directory.
    /// This is intended for authoritative snapshot replacement where a corrupt previous target
    /// must not block a valid incoming configuration.
    /// </summary>
    public static async Task<ExplorerConfigurationStore> CreateOrReplaceAsync(
        string dataRoot,
        ExplorerConfiguration configuration,
        IConfigurationPayloadProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(configuration);
        var store = new ExplorerConfigurationStore(
            dataRoot,
            protector ?? new DpapiConfigurationPayloadProtector());
        await store.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        return store;
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
        var json = _protector.Unprotect(envelope.EncryptedPayload);
        if (envelope.Schema == 1)
        {
            var migrated = MigrateSchema1(json);
            migrated.Validate();
            await SaveCoreAsync(migrated, cancellationToken).ConfigureAwait(false);
            return migrated.ResolveCredentialReferences();
        }
        if (envelope.Schema != ExplorerConfiguration.CurrentSchema) throw new InvalidDataException($"不支持的统一配置 Schema：{envelope.Schema}");
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
        if (envelope.Schema is not (1 or ExplorerConfiguration.CurrentSchema)) throw new InvalidDataException($"不支持的统一配置 Schema：{envelope.Schema}");
        if (string.IsNullOrWhiteSpace(envelope.EncryptedPayload)) throw new InvalidDataException("统一配置缺少加密载荷。");
    }

    private ExplorerConfiguration MigrateSchema1(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("旧统一配置载荷为空。");
        var credentials = root["credentialVault"]?.AsArray();
        var profiles = root["cdn"]?["profiles"]?.AsArray();
        if (credentials is null || profiles is null)
            throw new InvalidDataException("旧统一配置缺少 CDN 配置或凭据目录。");

        var credentialNodes = credentials
            .OfType<JsonObject>()
            .Select(node => (Node: node, Id: ReadGuid(node["id"])))
            .Where(value => value.Id is not null)
            .ToDictionary(value => value.Id!.Value, value => value.Node);
        var retainedControlIds = new HashSet<Guid>();
        var legacyGenericIds = new HashSet<Guid>();

        foreach (var node in profiles.OfType<JsonObject>())
        {
            var legacyId = ReadGuid(node["credentialId"]);
            var provider = ReadString(node["providerId"]);
            var isAliyun = string.Equals(provider, CdnProfile.AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase);
            var purgeConfigured = !string.IsNullOrWhiteSpace(ReadString(node["purgeEndpointTemplate"]));
            var content = isAliyun
                ? CdnHttpAuthentication.Anonymous
                : ContentFromCredential(legacyId is Guid id && credentialNodes.TryGetValue(id, out var credential) ? credential : null);

            node["contentAuthentication"] = JsonSerializer.SerializeToNode(content, Options);
            node.Remove("credentialId");
            node.Remove("controlCredentialId");
            if (legacyId is Guid controlId && (isAliyun || purgeConfigured))
            {
                node["controlCredentialId"] = controlId.ToString("D");
                retainedControlIds.Add(controlId);
            }
            if (!isAliyun && legacyId is Guid genericId)
                legacyGenericIds.Add(genericId);
        }

        for (var index = credentials.Count - 1; index >= 0; index--)
        {
            if (credentials[index] is not JsonObject credential ||
                ReadEnum<CredentialProviderKind>(credential["provider"]) != CredentialProviderKind.GenericHttp)
                continue;
            var id = ReadGuid(credential["id"]);
            if (id is Guid genericId && legacyGenericIds.Contains(genericId) && !retainedControlIds.Contains(genericId))
                credentials.RemoveAt(index);
        }

        var migrated = JsonSerializer.Deserialize<ExplorerConfiguration>(
            root.ToJsonString(Options), Options)
            ?? throw new InvalidDataException("Schema 1 配置迁移后载荷为空。");
        return migrated;
    }

    private static CdnHttpAuthentication ContentFromCredential(JsonObject? credential)
    {
        if (credential is null || ReadEnum<CredentialProviderKind>(credential["provider"]) != CredentialProviderKind.GenericHttp)
            return CdnHttpAuthentication.Anonymous;
        var kind = ReadEnum<CredentialKind>(credential["kind"]);
        return kind switch
        {
            CredentialKind.BearerToken => new CdnHttpAuthentication
            {
                AuthenticationType = CdnAuthenticationType.BearerToken,
                Secret = ReadString(credential["secret"])
            },
            CredentialKind.CustomHeader => new CdnHttpAuthentication
            {
                AuthenticationType = CdnAuthenticationType.CustomHeader,
                HeaderName = ReadString(credential["headerName"]),
                Secret = ReadString(credential["secret"])
            },
            _ => CdnHttpAuthentication.Anonymous
        };
    }

    private static string ReadString(JsonNode? node)
    {
        try { return node?.GetValue<string>() ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private static Guid? ReadGuid(JsonNode? node) =>
        Guid.TryParse(ReadString(node), out var value) ? value : null;

    private static T ReadEnum<T>(JsonNode? node) where T : struct, Enum
    {
        var text = ReadString(node);
        if (Enum.TryParse<T>(text, ignoreCase: true, out var value)) return value;
        try
        {
            var number = node?.GetValue<int>();
            return number is int integer && Enum.IsDefined(typeof(T), integer)
                ? (T)Enum.ToObject(typeof(T), integer)
                : default;
        }
        catch (InvalidOperationException) { return default; }
    }

    private async Task<ExplorerConfiguration> LoadLegacyAsync(CancellationToken cancellationToken)
    {
        var profilePath = System.IO.Path.Combine(_dataRoot, "profiles.json");
        var cdnPath = System.IO.Path.Combine(_dataRoot, "cdn-config.json");
        var credentialPath = System.IO.Path.Combine(_dataRoot, "cdn-credentials.json");
        var profiles = await new JsonProfileStore(new DpapiCredentialProtector(), profilePath).LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var cdnCredentials = await new JsonCdnCredentialStore(new DpapiCdnCredentialProtector(), credentialPath).LoadAsync(cancellationToken).ConfigureAwait(false);
        var cdn = await LoadLegacyCdnConfigurationAsync(cdnPath, cdnCredentials, cancellationToken).ConfigureAwait(false);
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
        var controlCredentialIdMap = new Dictionary<Guid, Guid>();
        foreach (var credential in cdnCredentials.Where(value => cdn.Profiles.Any(profile => profile.ControlCredentialId == value.Id)))
        {
            if (credential.AuthenticationType == CdnAuthenticationType.None) continue;
            var migratedId = AddMigratedCredential(vault, new CredentialProfile
            {
                Id = credential.Id,
                Name = credential.Name,
                Provider = CredentialProviderKind.GenericHttp,
                Kind = credential.AuthenticationType == CdnAuthenticationType.CustomHeader
                    ? CredentialKind.CustomHeader
                    : CredentialKind.BearerToken,
                HeaderName = credential.HeaderName,
                Secret = credential.Secret
            }, "cdn-control");
            controlCredentialIdMap[credential.Id] = migratedId;
        }
        var migratedCdn = cdn with
        {
            Profiles = cdn.Profiles.Select(profile =>
                profile.ControlCredentialId is Guid id && controlCredentialIdMap.TryGetValue(id, out var migratedId)
                    ? profile with { ControlCredentialId = migratedId }
                    : profile).ToArray()
        };
        var result = new ExplorerConfiguration(
            new ConnectionProfileConfiguration(migratedProfiles, profiles.Groups),
            migratedCdn,
            vault);
        result.Validate();
        return result;
    }

    private static async Task<CdnConfiguration> LoadLegacyCdnConfigurationAsync(
        string path,
        IReadOnlyList<CdnCredential> credentials,
        CancellationToken cancellationToken)
    {
        var configuration = await new JsonCdnConfigurationStore(path)
            .LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path) || configuration.Profiles.Count == 0)
            return configuration;

        var raw = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject();
        var rawProfiles = raw?["profiles"]?.AsArray();
        if (rawProfiles is null) return configuration;
        var legacyCredentials = credentials.ToDictionary(value => value.Id);
        var transformed = configuration.Profiles.Select(profile =>
        {
            var rawProfile = rawProfiles
                .OfType<JsonObject>()
                .FirstOrDefault(node => ReadGuid(node["id"]) == profile.Id);
            var legacyId = ReadGuid(rawProfile?["credentialId"]);
            var legacy = legacyId is Guid id && legacyCredentials.TryGetValue(id, out var value) ? value : null;
            var needsControl = legacyId is not null &&
                (string.Equals(profile.ProviderId, CdnProfile.AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrWhiteSpace(profile.PurgeEndpointTemplate));
            return profile with
            {
                ContentAuthentication = legacy is null
                    ? CdnHttpAuthentication.Anonymous
                    : new CdnHttpAuthentication
                    {
                        AuthenticationType = legacy.AuthenticationType,
                        HeaderName = legacy.HeaderName,
                        Secret = legacy.Secret
                    },
                ControlCredentialId = needsControl && legacy?.AuthenticationType != CdnAuthenticationType.None
                    ? legacyId
                    : null
            };
        }).ToArray();
        return new CdnConfiguration(transformed, configuration.Bindings);
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
