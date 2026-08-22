using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public enum ConnectionImportConflictStrategy
{
    Rename,
    Replace,
    Skip
}

public sealed record ConnectionArchiveInspection(
    int ProfileCount,
    bool ContainsCredentials,
    bool RequiresPassword,
    DateTimeOffset ExportedAtUtc,
    int CdnProfileCount = 0,
    int CredentialCount = 0);

public sealed record ConnectionArchivePackage(
    IReadOnlyList<ConnectionProfile> Profiles,
    bool ContainsCredentials,
    DateTimeOffset ExportedAtUtc,
    CdnConfiguration? CdnConfiguration = null,
    IReadOnlyList<CredentialProfile>? Credentials = null)
{
    public CdnConfiguration ImportedCdnConfiguration =>
        CdnConfiguration ?? S3Explorer.Core.CdnConfiguration.Empty;

    public IReadOnlyList<CredentialProfile> ImportedCredentials => Credentials ?? [];
}

public sealed record ConnectionArchiveMergeResult(
    IReadOnlyList<ConnectionProfile> Profiles,
    CdnConfiguration CdnConfiguration,
    IReadOnlyList<CredentialProfile> Credentials);

public sealed record ConnectionArchiveImportSelection(
    IReadOnlyCollection<Guid> StorageProfileIds,
    IReadOnlyCollection<Guid> CdnProfileIds);

public enum ConnectionArchiveImportStatus
{
    New,
    ExistingEquivalent,
    NameConflict
}

public sealed record ConnectionArchiveStoragePreview(
    Guid ImportedId,
    ConnectionArchiveImportStatus Status,
    Guid? ExistingId = null,
    string ExistingName = "");

public sealed record ConnectionArchiveCdnPreview(
    Guid ImportedId,
    ConnectionArchiveImportStatus Status,
    IReadOnlyList<Guid> RequiredStorageProfileIds,
    IReadOnlyList<Guid> MissingStorageProfileIds,
    Guid? ExistingId = null,
    string ExistingName = "");

public sealed record ConnectionArchiveImportPreview(
    IReadOnlyList<ConnectionArchiveStoragePreview> StorageProfiles,
    IReadOnlyList<ConnectionArchiveCdnPreview> CdnProfiles);

public sealed class ConnectionArchiveService
{
    public const string FileExtension = "s3connections";
    public const int MaximumProfileCount = 1000;
    public const int MaximumArchiveBytes = 16 * 1024 * 1024;
    public const int PasswordMinimumLength = 8;

    private const string FormatName = "s3explorer-connections";
    private const int FormatVersion = 5;
    private const int MinimumSupportedFormatVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int KdfIterations = 210_000;
    private const string NoProtection = "none";
    private const string PasswordProtection = "password-aes256-gcm";
    private static readonly byte[] AdditionalData = Encoding.UTF8.GetBytes("S3Explorer.ConnectionArchive.v1");

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public byte[] Export(
        IReadOnlyCollection<ConnectionProfile> profiles,
        bool includeCredentials = false,
        string? password = null,
        CdnConfiguration? cdnConfiguration = null,
        IReadOnlyCollection<CredentialProfile>? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        cdnConfiguration ??= CdnConfiguration.Empty;
        credentials ??= [];
        if (profiles.Count == 0)
            throw new ArgumentException("至少选择一个连接。", nameof(profiles));
        if (profiles.Count > MaximumProfileCount)
            throw new ArgumentOutOfRangeException(nameof(profiles), $"一次最多导出 {MaximumProfileCount} 个连接。");

        foreach (var profile in profiles)
            profile.ValidateConfiguration();
        CdnConfigurationValidator.EnsureValid(cdnConfiguration, credentials);
        EnsureArchiveRelationshipsAreComplete(profiles, cdnConfiguration);

        var unifiedCredentials = credentials;
        var referencedCredentialIds = profiles
            .SelectMany(profile => new[] { profile.CredentialId, profile.AwsExternalIdCredentialId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(cdnConfiguration.Profiles.Where(profile => profile.ControlCredentialId.HasValue).Select(profile => profile.ControlCredentialId!.Value))
            .ToHashSet();
        var selectedUnifiedCredentials = unifiedCredentials
            .Where(credential => referencedCredentialIds.Contains(credential.Id))
            .ToArray();
        var containsStoredCredentials = includeCredentials &&
            (selectedUnifiedCredentials.Any(credential => !string.IsNullOrEmpty(credential.Secret)) ||
             cdnConfiguration.Profiles.Any(profile =>
                 profile.ContentAuthentication.AuthenticationType != CdnAuthenticationType.None &&
                 !string.IsNullOrEmpty(profile.ContentAuthentication.Secret)));
        if (containsStoredCredentials && (password?.Length ?? 0) < PasswordMinimumLength)
            throw new ArgumentException($"迁移密码至少需要 {PasswordMinimumLength} 个字符。", nameof(password));

        var exportedAt = DateTimeOffset.UtcNow;
        var portableProfiles = profiles
            .Select(profile =>
            {
                var portable = PortableProfile.FromRuntime(profile);
                if (!containsStoredCredentials)
                {
                    portable.CredentialId = null;
                    portable.AwsExternalIdCredentialId = null;
                }
                return portable;
            })
            .ToList();
        var portableCdnProfiles = cdnConfiguration.Profiles
            .Select(profile => PortableCdnProfile.FromRuntime(profile, containsStoredCredentials))
            .ToList();
        var portableCdnBindings = cdnConfiguration.Bindings.ToList();
        var portableCredentialProfiles = containsStoredCredentials
            ? selectedUnifiedCredentials.ToList()
            : [];
        var payload = new ArchivePayload
        {
            Profiles = portableProfiles,
            CdnProfiles = portableCdnProfiles,
            CdnBindings = portableCdnBindings,
            CredentialProfiles = portableCredentialProfiles
        };
        ArchiveEnvelope envelope;

        if (!containsStoredCredentials)
        {
            envelope = new ArchiveEnvelope
            {
                Format = FormatName,
                Version = FormatVersion,
                ExportedAtUtc = exportedAt,
                ProfileCount = portableProfiles.Count,
                CdnProfileCount = portableCdnProfiles.Count,
                CredentialCount = portableCredentialProfiles.Count,
                ContainsCredentials = false,
                Protection = NoProtection,
                Profiles = portableProfiles,
                CdnProfiles = portableCdnProfiles,
                CdnBindings = portableCdnBindings,
                CredentialProfiles = []
            };
        }
        else
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                _jsonOptions);
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var tag = new byte[TagSize];
            var ciphertext = new byte[plaintext.Length];
            var key = Rfc2898DeriveBytes.Pbkdf2(
                password!, salt, KdfIterations, HashAlgorithmName.SHA256, KeySize);
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }

            envelope = new ArchiveEnvelope
            {
                Format = FormatName,
                Version = FormatVersion,
                ExportedAtUtc = exportedAt,
                ProfileCount = portableProfiles.Count,
                CdnProfileCount = portableCdnProfiles.Count,
                CredentialCount = portableCredentialProfiles.Count,
                ContainsCredentials = true,
                Protection = PasswordProtection,
                Encryption = new EncryptionMetadata
                {
                    Algorithm = "AES-256-GCM",
                    Kdf = "PBKDF2-SHA256",
                    Iterations = KdfIterations,
                    Salt = Convert.ToBase64String(salt),
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag)
                },
                EncryptedPayload = Convert.ToBase64String(ciphertext),
                CredentialProfiles = null
            };
        }

        var result = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        if (result.Length > MaximumArchiveBytes)
            throw new InvalidOperationException($"连接包不能超过 {MaximumArchiveBytes / 1024 / 1024} MiB。");
        return result;
    }

    /// <summary>Exports the canonical configuration graph, preserving shared credential IDs.</summary>
    public byte[] Export(
        ExplorerConfiguration configuration,
        bool includeCredentials = false,
        string? password = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        return Export(
            configuration.Storage.Profiles,
            includeCredentials,
            password,
            configuration.Cdn,
            credentials: configuration.CredentialVault);
    }

    public ConnectionArchiveInspection Inspect(ReadOnlySpan<byte> archive)
    {
        var envelope = ReadEnvelope(archive);
        ValidateEnvelope(envelope);
        return new(
            envelope.ProfileCount,
            envelope.ContainsCredentials,
            string.Equals(envelope.Protection, PasswordProtection, StringComparison.Ordinal),
            envelope.ExportedAtUtc,
            envelope.CdnProfileCount,
            envelope.Version >= 4 ? envelope.CredentialCount : envelope.CdnCredentialCount);
    }

    public ConnectionArchivePackage Import(ReadOnlySpan<byte> archive, string? password = null)
    {
        var envelope = ReadEnvelope(archive);
        ValidateEnvelope(envelope);

        ArchivePayload payload;
        if (string.Equals(envelope.Protection, NoProtection, StringComparison.Ordinal))
        {
            payload = new ArchivePayload
            {
                Profiles = envelope.Profiles!,
                CdnProfiles = envelope.CdnProfiles ?? [],
                CdnBindings = envelope.CdnBindings ?? [],
                CredentialProfiles = envelope.CredentialProfiles ?? []
            };
        }
        else
        {
            if (string.IsNullOrEmpty(password))
                throw new ConnectionArchivePasswordRequiredException();
            payload = DecryptPayload(envelope, password);
        }

        if (payload.Profiles.Count != envelope.ProfileCount)
            throw new InvalidDataException("连接包中的连接数量不一致。");
        if (payload.CdnProfiles.Count != envelope.CdnProfileCount ||
            (envelope.Version >= 4
                ? payload.CredentialProfiles.Count
                : payload.CdnCredentials.Count) != (envelope.Version >= 4 ? envelope.CredentialCount : envelope.CdnCredentialCount))
            throw new InvalidDataException("连接包中的 CDN 配置或凭据数量不一致。");

        var profiles = payload.Profiles.Select(profile => profile.ToRuntime()).ToArray();
        CdnConfiguration cdnConfiguration;
        IReadOnlyList<CredentialProfile> importedCredentials;
        if (envelope.Version >= 5)
        {
            if (profiles.Any(profile =>
                    !string.IsNullOrEmpty(profile.AccessKey) ||
                    !string.IsNullOrEmpty(profile.SecretKey) ||
                    !string.IsNullOrEmpty(profile.SessionToken) ||
                    !string.IsNullOrEmpty(profile.AwsExternalId)))
                throw new InvalidDataException("v5 连接包不能在对象存储配置中内嵌秘密值。");
            cdnConfiguration = new CdnConfiguration(
                payload.CdnProfiles.Select(profile => profile.ToRuntime()).ToArray(),
                payload.CdnBindings);
            importedCredentials = payload.CredentialProfiles;
        }
        else if (envelope.Version == 4)
        {
            if (profiles.Any(profile =>
                    !string.IsNullOrEmpty(profile.AccessKey) ||
                    !string.IsNullOrEmpty(profile.SecretKey) ||
                    !string.IsNullOrEmpty(profile.SessionToken) ||
                    !string.IsNullOrEmpty(profile.AwsExternalId)))
                throw new InvalidDataException("v4 连接包不能在对象存储配置中内嵌秘密值。");
            (cdnConfiguration, importedCredentials) = MigrateVersion4CdnProfiles(
                payload.CdnProfiles,
                payload.CdnBindings,
                payload.CredentialProfiles);
        }
        else
        {
            (profiles, cdnConfiguration, importedCredentials) = MigrateLegacyArchiveCredentials(
                profiles,
                payload.CdnProfiles,
                payload.CdnBindings,
                payload.CdnCredentials);
        }

        EnsureArchiveRelationshipsAreComplete(profiles, cdnConfiguration);
        var resolved = new ExplorerConfiguration(
            new ConnectionProfileConfiguration(profiles, []),
            cdnConfiguration,
            importedCredentials).ResolveCredentialReferences();

        return new(
            resolved.Storage.Profiles,
            envelope.ContainsCredentials,
            envelope.ExportedAtUtc,
            resolved.Cdn,
            resolved.CredentialVault);
    }

    public IReadOnlyList<ConnectionProfile> Merge(
        IReadOnlyCollection<ConnectionProfile> existingProfiles,
        IReadOnlyCollection<ConnectionProfile> importedProfiles,
        bool importCredentials,
        ConnectionImportConflictStrategy conflictStrategy)
    {
        ArgumentNullException.ThrowIfNull(existingProfiles);
        ArgumentNullException.ThrowIfNull(importedProfiles);

        var result = existingProfiles.ToList();
        foreach (var source in importedProfiles)
        {
            source.ValidateConfiguration();
            var imported = importCredentials
                ? source
                : source with { AccessKey = string.Empty, SecretKey = string.Empty, SessionToken = string.Empty };
            if (result.Any(existing => StorageProfilesEquivalent(existing, imported, importCredentials)))
                continue;
            var existingIndex = result.FindIndex(item =>
                string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                result.Add(imported with { Id = Guid.NewGuid() });
                continue;
            }

            switch (conflictStrategy)
            {
                case ConnectionImportConflictStrategy.Skip:
                    break;
                case ConnectionImportConflictStrategy.Replace:
                    result[existingIndex] = imported with { Id = result[existingIndex].Id };
                    break;
                case ConnectionImportConflictStrategy.Rename:
                    result.Add(imported with
                    {
                        Id = Guid.NewGuid(),
                        Name = CreateUniqueImportedName(imported.Name, result)
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(conflictStrategy));
            }
        }

        return result;
    }

    public ConnectionArchiveImportPreview PreviewPackage(
        IReadOnlyCollection<ConnectionProfile> existingProfiles,
        CdnConfiguration existingCdnConfiguration,
        IReadOnlyCollection<CredentialProfile> existingCredentials,
        ConnectionArchivePackage package,
        bool importStorageCredentials = false,
        bool importCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(existingProfiles);
        ArgumentNullException.ThrowIfNull(existingCdnConfiguration);
        ArgumentNullException.ThrowIfNull(existingCredentials);
        ArgumentNullException.ThrowIfNull(package);

        var storage = package.Profiles.Select(source =>
        {
            var imported = PortableStorage(source, importStorageCredentials);
            var exact = existingProfiles.FirstOrDefault(existing =>
                StorageProfilesEquivalent(existing, imported, importStorageCredentials));
            var sameName = existingProfiles.FirstOrDefault(existing =>
                string.Equals(existing.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            return new ConnectionArchiveStoragePreview(
                source.Id,
                exact is not null
                    ? ConnectionArchiveImportStatus.ExistingEquivalent
                    : sameName is not null
                        ? ConnectionArchiveImportStatus.NameConflict
                        : ConnectionArchiveImportStatus.New,
                exact?.Id ?? sameName?.Id,
                exact?.Name ?? sameName?.Name ?? string.Empty);
        }).ToArray();

        var credentialIdMap = new Dictionary<Guid, Guid>();
        if (importCredentials)
        {
            foreach (var source in package.ImportedCredentials)
            {
                var exact = existingCredentials.FirstOrDefault(existing =>
                    CredentialProfilesEquivalent(existing, source));
                if (exact is not null) credentialIdMap[source.Id] = exact.Id;
            }
        }

        var configuration = package.ImportedCdnConfiguration;
        var cdn = configuration.Profiles.Select(source =>
        {
            var credentialId = importCredentials && source.ControlCredentialId is Guid sourceCredentialId &&
                               credentialIdMap.TryGetValue(sourceCredentialId, out var mappedCredentialId)
                ? mappedCredentialId
                : (Guid?)null;
            var imported = source with
            {
                ControlCredentialId = credentialId,
                ContentAuthentication = importCredentials
                    ? source.ContentAuthentication
                    : CdnHttpAuthentication.Anonymous
            };
            var exact = existingCdnConfiguration.Profiles.FirstOrDefault(existing =>
                CdnProfilesEquivalent(existing, imported, importCredentials));
            var sameName = existingCdnConfiguration.Profiles.FirstOrDefault(existing =>
                string.Equals(existing.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            var requiredStorage = configuration.Bindings
                .Where(binding => binding.CdnProfileId == source.Id)
                .Select(binding => binding.StorageProfileId)
                .Distinct()
                .ToArray();
            var missingStorage = requiredStorage.Where(sourceStorageId =>
            {
                var sourceStorage = package.Profiles.FirstOrDefault(profile => profile.Id == sourceStorageId);
                return sourceStorage is not null && !existingProfiles.Any(existing =>
                    StorageProfilesEquivalent(existing, PortableStorage(sourceStorage, false), false));
            }).ToArray();
            return new ConnectionArchiveCdnPreview(
                source.Id,
                exact is not null
                    ? ConnectionArchiveImportStatus.ExistingEquivalent
                    : sameName is not null
                        ? ConnectionArchiveImportStatus.NameConflict
                        : ConnectionArchiveImportStatus.New,
                requiredStorage,
                missingStorage,
                exact?.Id ?? sameName?.Id,
                exact?.Name ?? sameName?.Name ?? string.Empty);
        }).ToArray();

        return new ConnectionArchiveImportPreview(storage, cdn);
    }

    public ConnectionArchiveMergeResult MergePackage(
        IReadOnlyCollection<ConnectionProfile> existingProfiles,
        CdnConfiguration existingCdnConfiguration,
        IReadOnlyCollection<CredentialProfile> existingCredentials,
        ConnectionArchivePackage package,
        IReadOnlyCollection<Guid> selectedImportedProfileIds,
        bool importCredentials,
        ConnectionImportConflictStrategy conflictStrategy)
    {
        var selectedStorageIds = selectedImportedProfileIds.ToHashSet();
        var selectedCdnIds = package.ImportedCdnConfiguration.Bindings
            .Where(binding => selectedStorageIds.Contains(binding.StorageProfileId))
            .Select(binding => binding.CdnProfileId)
            .ToHashSet();
        if (selectedStorageIds.SetEquals(package.Profiles.Select(profile => profile.Id)))
            selectedCdnIds.UnionWith(package.ImportedCdnConfiguration.Profiles.Select(profile => profile.Id));
        return MergePackage(
            existingProfiles,
            existingCdnConfiguration,
            existingCredentials,
            package,
            new ConnectionArchiveImportSelection(selectedStorageIds, selectedCdnIds),
            importCredentials,
            importCredentials,
            conflictStrategy);
    }

    public ConnectionArchiveMergeResult MergePackage(
        IReadOnlyCollection<ConnectionProfile> existingProfiles,
        CdnConfiguration existingCdnConfiguration,
        IReadOnlyCollection<CredentialProfile> existingCredentials,
        ConnectionArchivePackage package,
        ConnectionArchiveImportSelection selection,
        bool importStorageCredentials,
        bool importCredentials,
        ConnectionImportConflictStrategy conflictStrategy,
        Guid? targetGroupId = null)
    {
        ArgumentNullException.ThrowIfNull(existingProfiles);
        ArgumentNullException.ThrowIfNull(existingCdnConfiguration);
        ArgumentNullException.ThrowIfNull(existingCredentials);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(selection);
        CdnConfigurationValidator.EnsureValid(existingCdnConfiguration, existingCredentials);

        var selectedStorageIds = selection.StorageProfileIds.ToHashSet();
        var selectedCdnIds = selection.CdnProfileIds.ToHashSet();
        var packageStorageIds = package.Profiles.Select(profile => profile.Id).ToHashSet();
        var packageCdnIds = package.ImportedCdnConfiguration.Profiles.Select(profile => profile.Id).ToHashSet();
        if (!selectedStorageIds.IsSubsetOf(packageStorageIds))
            throw new ArgumentException("所选对象存储连接不属于当前连接包。", nameof(selection));
        if (!selectedCdnIds.IsSubsetOf(packageCdnIds))
            throw new ArgumentException("所选 CDN 配置不属于当前连接包。", nameof(selection));

        var importedConfiguration = package.ImportedCdnConfiguration;
        var selectedBindings = importedConfiguration.Bindings
            .Where(binding => selectedCdnIds.Contains(binding.CdnProfileId))
            .ToArray();
        var importedCdnProfiles = importedConfiguration.Profiles
            .Where(profile => selectedCdnIds.Contains(profile.Id))
            .ToArray();
        var selectedStorageProfiles = package.Profiles
            .Where(profile => selectedStorageIds.Contains(profile.Id))
            .ToArray();
        var storageCredentialIds = selectedStorageProfiles
            .SelectMany(profile => new[] { profile.CredentialId, profile.AwsExternalIdCredentialId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var cdnCredentialIds = importedCdnProfiles
            .Where(profile => profile.ControlCredentialId.HasValue)
            .Select(profile => profile.ControlCredentialId!.Value)
            .ToHashSet();
        var credentialIdsToImport = (importStorageCredentials ? storageCredentialIds : [])
            .Concat(importCredentials ? cdnCredentialIds : [])
            .ToHashSet();

        var credentials = existingCredentials.ToList();
        var credentialIdMap = new Dictionary<Guid, Guid>();
        if (importStorageCredentials || importCredentials)
        {
            foreach (var source in package.ImportedCredentials
                         .Where(credential => credentialIdsToImport.Contains(credential.Id)))
            {
                var exact = credentials.FirstOrDefault(existing => CredentialProfilesEquivalent(existing, source));
                if (exact is not null)
                {
                    credentialIdMap[source.Id] = exact.Id;
                    continue;
                }

                var existingIndex = credentials.FindIndex(item =>
                    string.Equals(item.Name, source.Name, StringComparison.OrdinalIgnoreCase));
                if (existingIndex < 0)
                {
                    var added = source with { Id = Guid.NewGuid() };
                    credentials.Add(added);
                    credentialIdMap[source.Id] = added.Id;
                    continue;
                }

                switch (conflictStrategy)
                {
                    case ConnectionImportConflictStrategy.Skip:
                        break;
                    case ConnectionImportConflictStrategy.Replace:
                        var replaced = source with { Id = credentials[existingIndex].Id };
                        credentials[existingIndex] = replaced;
                        credentialIdMap[source.Id] = replaced.Id;
                        break;
                    case ConnectionImportConflictStrategy.Rename:
                        var renamed = source with
                        {
                            Id = Guid.NewGuid(),
                            Name = CreateUniqueImportedName(source.Name, credentials.Select(item => item.Name))
                        };
                        credentials.Add(renamed);
                        credentialIdMap[source.Id] = renamed.Id;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(conflictStrategy));
                }
            }
        }
        var missingCredentialIds = credentialIdsToImport
            .Where(id => !credentialIdMap.ContainsKey(id))
            .ToArray();
        if (missingCredentialIds.Length > 0)
        {
            var names = package.ImportedCredentials
                .Where(value => missingCredentialIds.Contains(value.Id))
                .Select(value => value.Name)
                .DefaultIfEmpty(string.Join(", ", missingCredentialIds))
                .ToArray();
            throw new InvalidDataException(
                $"所选配置依赖的凭据未能导入：{string.Join("、", names)}。" +
                "如果同名凭据采用“跳过”策略，请改用自动重命名或覆盖；也可以取消导入统一凭据。");
        }

        var profiles = existingProfiles.ToList();
        var nextTargetOrder = profiles
            .Where(profile => profile.GroupId == targetGroupId)
            .Select(profile => profile.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var storageIdMap = new Dictionary<Guid, Guid>();
        foreach (var source in selectedStorageProfiles)
        {
            source.ValidateConfiguration();
            var imported = PortableStorage(source, importStorageCredentials) with
            {
                CredentialId = importStorageCredentials && source.CredentialId is Guid credentialId &&
                    credentialIdMap.TryGetValue(credentialId, out var mappedCredentialId)
                    ? mappedCredentialId
                    : null,
                AwsExternalIdCredentialId = importStorageCredentials && source.AwsExternalIdCredentialId is Guid externalIdCredentialId &&
                    credentialIdMap.TryGetValue(externalIdCredentialId, out var mappedExternalIdCredentialId)
                    ? mappedExternalIdCredentialId
                    : null
            };
            var exact = profiles.FirstOrDefault(existing =>
                StorageProfilesEquivalent(existing, imported, importStorageCredentials));
            if (exact is not null)
            {
                storageIdMap[source.Id] = exact.Id;
                continue;
            }

            var existingIndex = profiles.FindIndex(item =>
                string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                var added = imported with { Id = Guid.NewGuid(), GroupId = targetGroupId, SortOrder = nextTargetOrder++ };
                profiles.Add(added);
                storageIdMap[source.Id] = added.Id;
                continue;
            }

            switch (conflictStrategy)
            {
                case ConnectionImportConflictStrategy.Skip:
                    break;
                case ConnectionImportConflictStrategy.Replace:
                    var replaced = imported with
                    {
                        Id = profiles[existingIndex].Id,
                        GroupId = targetGroupId,
                        SortOrder = nextTargetOrder++
                    };
                    profiles[existingIndex] = replaced;
                    storageIdMap[source.Id] = replaced.Id;
                    break;
                case ConnectionImportConflictStrategy.Rename:
                    var renamed = imported with
                    {
                        Id = Guid.NewGuid(),
                        Name = CreateUniqueImportedName(imported.Name, profiles.Select(item => item.Name)),
                        GroupId = targetGroupId,
                        SortOrder = nextTargetOrder++
                    };
                    profiles.Add(renamed);
                    storageIdMap[source.Id] = renamed.Id;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(conflictStrategy));
            }
        }

        foreach (var dependencyId in selectedBindings.Select(binding => binding.StorageProfileId).Distinct())
        {
            if (storageIdMap.ContainsKey(dependencyId)) continue;
            var source = package.Profiles.FirstOrDefault(profile => profile.Id == dependencyId);
            if (source is null) continue;
            var imported = PortableStorage(source, false);
            var exact = profiles.FirstOrDefault(existing =>
                StorageProfilesEquivalent(existing, imported, false));
            if (exact is not null)
            {
                storageIdMap[source.Id] = exact.Id;
                continue;
            }
            if (!selectedStorageIds.Contains(dependencyId))
                throw new InvalidDataException(
                    $"CDN 关联依赖对象存储连接“{source.Name}”，但该连接未被选择且本地没有等价连接。");
        }

        var cdnProfiles = existingCdnConfiguration.Profiles.ToList();
        var cdnProfileIdMap = new Dictionary<Guid, Guid>();
        foreach (var source in importedCdnProfiles)
        {
            var credentialId = source.ControlCredentialId is Guid sourceCredentialId &&
                               credentialIdMap.TryGetValue(sourceCredentialId, out var mappedCredentialId)
                ? mappedCredentialId
                : (Guid?)null;
            var imported = source with
            {
                ControlCredentialId = credentialId,
                ContentAuthentication = importCredentials
                    ? source.ContentAuthentication
                    : CdnHttpAuthentication.Anonymous
            };
            var exact = cdnProfiles.FirstOrDefault(existing =>
                CdnProfilesEquivalent(existing, imported, importCredentials));
            if (exact is not null)
            {
                cdnProfileIdMap[source.Id] = exact.Id;
                continue;
            }

            var existingIndex = cdnProfiles.FindIndex(item =>
                string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                var added = imported with { Id = Guid.NewGuid() };
                cdnProfiles.Add(added);
                cdnProfileIdMap[source.Id] = added.Id;
                continue;
            }

            switch (conflictStrategy)
            {
                case ConnectionImportConflictStrategy.Skip:
                    break;
                case ConnectionImportConflictStrategy.Replace:
                    var replaced = imported with { Id = cdnProfiles[existingIndex].Id };
                    cdnProfiles[existingIndex] = replaced;
                    cdnProfileIdMap[source.Id] = replaced.Id;
                    break;
                case ConnectionImportConflictStrategy.Rename:
                    var renamed = imported with
                    {
                        Id = Guid.NewGuid(),
                        Name = CreateUniqueImportedName(imported.Name, cdnProfiles.Select(item => item.Name))
                    };
                    cdnProfiles.Add(renamed);
                    cdnProfileIdMap[source.Id] = renamed.Id;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(conflictStrategy));
            }
        }

        var bindings = existingCdnConfiguration.Bindings.ToList();
        foreach (var source in selectedBindings)
        {
            if (!storageIdMap.TryGetValue(source.StorageProfileId, out var storageProfileId) ||
                !cdnProfileIdMap.TryGetValue(source.CdnProfileId, out var cdnProfileId))
                continue;

            var imported = source with
            {
                Id = Guid.NewGuid(),
                StorageProfileId = storageProfileId,
                CdnProfileId = cdnProfileId
            };
            if (bindings.Any(binding => BindingsEquivalent(binding, imported)))
                continue;

            var identityIndex = bindings.FindIndex(binding => SameBindingIdentity(binding, imported));
            if (identityIndex >= 0)
            {
                if (conflictStrategy == ConnectionImportConflictStrategy.Replace)
                    bindings[identityIndex] = imported with { Id = bindings[identityIndex].Id };
                continue;
            }

            if (imported.Enabled && imported.IsDefault)
            {
                var conflictingDefaults = bindings
                    .Where(binding => binding.Enabled && binding.IsDefault && SameBindingLocation(binding, imported))
                    .ToArray();
                if (conflictingDefaults.Length > 0)
                {
                    if (conflictStrategy == ConnectionImportConflictStrategy.Replace)
                        bindings.RemoveAll(binding => conflictingDefaults.Any(conflict => conflict.Id == binding.Id));
                    else
                        imported = imported with { IsDefault = false };
                }
            }
            bindings.Add(imported);
        }

        var mergedConfiguration = new CdnConfiguration(cdnProfiles, bindings);
        CdnConfigurationValidator.EnsureValid(mergedConfiguration, credentials);
        return new ConnectionArchiveMergeResult(profiles, mergedConfiguration, credentials);
    }

    private ArchiveEnvelope ReadEnvelope(ReadOnlySpan<byte> archive)
    {
        if (archive.Length == 0)
            throw new InvalidDataException("连接包为空。");
        if (archive.Length > MaximumArchiveBytes)
            throw new InvalidDataException($"连接包不能超过 {MaximumArchiveBytes / 1024 / 1024} MiB。");
        try
        {
            return JsonSerializer.Deserialize<ArchiveEnvelope>(archive, _jsonOptions)
                ?? throw new InvalidDataException("连接包内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("连接包不是有效的 JSON 文件。", exception);
        }
    }

    private static void ValidateEnvelope(ArchiveEnvelope envelope)
    {
        if (!string.Equals(envelope.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("文件不是 S3 Explorer 连接包。");
        if (envelope.Version is < MinimumSupportedFormatVersion or > FormatVersion)
            throw new InvalidDataException($"不支持连接包版本 {envelope.Version}。");
        if (envelope.ProfileCount is <= 0 or > MaximumProfileCount)
            throw new InvalidDataException("连接包中的连接数量无效。");
        var credentialCount = envelope.Version >= 4 ? envelope.CredentialCount : envelope.CdnCredentialCount;
        if (envelope.CdnProfileCount is < 0 or > MaximumProfileCount ||
            credentialCount is < 0 or > MaximumProfileCount)
            throw new InvalidDataException("连接包中的 CDN 配置或凭据数量无效。");
        if (envelope.Version < 3 &&
            (envelope.CdnProfileCount != 0 || credentialCount != 0 ||
             envelope.CdnProfiles is not null || envelope.CdnBindings is not null))
            throw new InvalidDataException("旧版连接包不能包含 CDN 配置。");
        if (envelope.ExportedAtUtc == default)
            throw new InvalidDataException("连接包缺少导出时间。");

        if (string.Equals(envelope.Protection, NoProtection, StringComparison.Ordinal))
        {
            if (envelope.ContainsCredentials || envelope.Profiles is null || envelope.Encryption is not null || envelope.EncryptedPayload is not null)
                throw new InvalidDataException("无凭据连接包结构无效。");
            EnsureUnprotectedEnvelopeContainsNoCredentials(envelope);
            return;
        }

        if (!string.Equals(envelope.Protection, PasswordProtection, StringComparison.Ordinal) ||
            !envelope.ContainsCredentials || envelope.Profiles is not null || envelope.Encryption is null ||
            string.IsNullOrWhiteSpace(envelope.EncryptedPayload))
            throw new InvalidDataException("连接包加密信息无效。");

        if (!string.Equals(envelope.Encryption.Algorithm, "AES-256-GCM", StringComparison.Ordinal) ||
            !string.Equals(envelope.Encryption.Kdf, "PBKDF2-SHA256", StringComparison.Ordinal) ||
            envelope.Encryption.Iterations != KdfIterations)
            throw new InvalidDataException("连接包使用了不支持的加密参数。");
    }

    private static void EnsureUnprotectedEnvelopeContainsNoCredentials(ArchiveEnvelope envelope)
    {
        if (envelope.CredentialCount != 0 || envelope.CdnCredentialCount != 0 ||
            (envelope.CredentialProfiles?.Count ?? 0) != 0)
            throw new InvalidDataException("无凭据连接包不能包含凭据条目。");

        if (envelope.Profiles!.Any(profile =>
                profile is null ||
                profile.CredentialId.HasValue ||
                profile.AwsExternalIdCredentialId.HasValue ||
                !string.IsNullOrEmpty(profile.AccessKey) ||
                !string.IsNullOrEmpty(profile.SecretKey) ||
                !string.IsNullOrEmpty(profile.SessionToken) ||
                !string.IsNullOrEmpty(profile.AwsExternalId)))
            throw new InvalidDataException("无凭据连接包不能包含对象存储凭据或凭据引用。");

        if (envelope.CdnProfiles?.Any(profile =>
                profile is null ||
                profile.CredentialId.HasValue ||
                profile.ControlCredentialId.HasValue ||
                !string.IsNullOrEmpty(profile.ContentAuthentication?.Secret)) == true)
            throw new InvalidDataException("无凭据连接包不能包含 CDN 凭据或凭据引用。");
    }

    private ArchivePayload DecryptPayload(ArchiveEnvelope envelope, string password)
    {
        var metadata = envelope.Encryption!;
        byte[] salt;
        byte[] nonce;
        byte[] tag;
        byte[] ciphertext;
        try
        {
            salt = Convert.FromBase64String(metadata.Salt);
            nonce = Convert.FromBase64String(metadata.Nonce);
            tag = Convert.FromBase64String(metadata.Tag);
            ciphertext = Convert.FromBase64String(envelope.EncryptedPayload!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("连接包的加密数据格式无效。", exception);
        }

        if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize || ciphertext.Length == 0)
            throw new InvalidDataException("连接包的加密参数长度无效。");

        var plaintext = new byte[ciphertext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, metadata.Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalData);
            var payload = JsonSerializer.Deserialize<ArchivePayload>(plaintext, _jsonOptions)
                ?? throw new InvalidDataException("连接包的加密内容为空。");
            if (payload.Profiles is null || payload.CdnProfiles is null || payload.CdnBindings is null ||
                (envelope.Version < 4 && payload.CdnCredentials is null) ||
                (envelope.Version >= 4 && payload.CredentialProfiles is null))
                throw new InvalidDataException("连接包的加密内容包含空集合。");
            return payload;
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new ConnectionArchiveAuthenticationException(exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("连接包的加密内容无效。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string CreateUniqueImportedName(string name, IReadOnlyCollection<ConnectionProfile> profiles)
        => CreateUniqueImportedName(name, profiles.Select(profile => profile.Name));

    private static string CreateUniqueImportedName(string name, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = $"{name} (导入)";
        var suffix = 2;
        while (names.Contains(candidate))
            candidate = $"{name} (导入 {suffix++})";
        return candidate;
    }

    private static ConnectionProfile PortableStorage(ConnectionProfile source, bool includeCredentials) =>
        includeCredentials
            ? source
            : source with
            {
                AccessKey = string.Empty,
                SecretKey = string.Empty,
                SessionToken = string.Empty,
                AwsExternalId = string.Empty
            };

    private static bool StorageProfilesEquivalent(
        ConnectionProfile left,
        ConnectionProfile right,
        bool compareStoredCredentials)
    {
        var same = left.ServiceType == right.ServiceType &&
            string.Equals(NormalizeEndpoint(left), NormalizeEndpoint(right), StringComparison.Ordinal) &&
            string.Equals(NormalizeRegion(left.Region), NormalizeRegion(right.Region), StringComparison.Ordinal) &&
            string.Equals(left.EffectiveSignatureRegion, right.EffectiveSignatureRegion, StringComparison.OrdinalIgnoreCase) &&
            left.CredentialSource == right.CredentialSource &&
            string.Equals(left.AwsProfileName?.Trim(), right.AwsProfileName?.Trim(), StringComparison.Ordinal) &&
            string.Equals(left.AwsSourceProfileName?.Trim(), right.AwsSourceProfileName?.Trim(), StringComparison.Ordinal) &&
            string.Equals(left.AwsRoleArn?.Trim(), right.AwsRoleArn?.Trim(), StringComparison.Ordinal) &&
            string.Equals(left.AwsRoleSessionName?.Trim(), right.AwsRoleSessionName?.Trim(), StringComparison.Ordinal) &&
            string.Equals(left.AwsRoleSourceIdentity?.Trim(), right.AwsRoleSourceIdentity?.Trim(), StringComparison.Ordinal) &&
            left.AwsSessionDurationSeconds == right.AwsSessionDurationSeconds &&
            string.Equals(left.AwsWebIdentityTokenFile?.Trim(), right.AwsWebIdentityTokenFile?.Trim(), StringComparison.Ordinal) &&
            left.AddressingStyle == right.AddressingStyle &&
            left.UseHttps == right.UseHttps &&
            left.IgnoreCertificateErrors == right.IgnoreCertificateErrors &&
            string.Equals(left.CustomHostHeader?.Trim(), right.CustomHostHeader?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            left.FollowTemporaryRedirects == right.FollowTemporaryRedirects &&
            left.EnableMultiObjectDelete == right.EnableMultiObjectDelete &&
            left.EnableMultipartCopy == right.EnableMultipartCopy &&
            string.Equals(left.DefaultStorageClass?.Trim(), right.DefaultStorageClass?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            left.RequestTimeoutSeconds == right.RequestTimeoutSeconds &&
            left.ConnectionTimeoutSeconds == right.ConnectionTimeoutSeconds &&
            string.Equals(left.DefaultBucket?.Trim(), right.DefaultBucket?.Trim(), StringComparison.Ordinal) &&
            NormalizeBuckets(left.ExternalBuckets).SequenceEqual(NormalizeBuckets(right.ExternalBuckets), StringComparer.Ordinal);
        if (!same || !compareStoredCredentials)
            return same;
        if (left.CredentialSource == CredentialSourceKind.StoredKeys)
            return string.Equals(left.AccessKey, right.AccessKey, StringComparison.Ordinal) &&
                   string.Equals(left.SecretKey, right.SecretKey, StringComparison.Ordinal) &&
                   string.Equals(left.SessionToken, right.SessionToken, StringComparison.Ordinal);
        return left.CredentialSource != CredentialSourceKind.AwsAssumeRole ||
               string.Equals(left.AwsExternalId, right.AwsExternalId, StringComparison.Ordinal);
    }

    private static bool CredentialProfilesEquivalent(CredentialProfile left, CredentialProfile right)
    {
        return left.Provider == right.Provider &&
            left.Kind == right.Kind &&
            string.Equals(left.AccessKeyId, right.AccessKeyId, StringComparison.Ordinal) &&
            string.Equals(left.Secret, right.Secret, StringComparison.Ordinal) &&
            string.Equals(left.SessionToken, right.SessionToken, StringComparison.Ordinal) &&
            string.Equals(left.HeaderName?.Trim(), right.HeaderName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static CredentialProfile? ToCredentialProfile(CdnCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.AuthenticationType switch
        {
            CdnAuthenticationType.BearerToken => new CredentialProfile
            {
                Id = credential.Id,
                Name = credential.Name,
                Provider = CredentialProviderKind.GenericHttp,
                Kind = CredentialKind.BearerToken,
                Secret = credential.Secret
            },
            CdnAuthenticationType.CustomHeader => new CredentialProfile
            {
                Id = credential.Id,
                Name = credential.Name,
                Provider = CredentialProviderKind.GenericHttp,
                Kind = CredentialKind.CustomHeader,
                HeaderName = credential.HeaderName ?? string.Empty,
                Secret = credential.Secret
            },
            _ => null
        };
    }

    private static CdnHttpAuthentication ToContentAuthentication(CdnCredential? credential) =>
        credential?.AuthenticationType switch
        {
            CdnAuthenticationType.BearerToken => new CdnHttpAuthentication
            {
                AuthenticationType = CdnAuthenticationType.BearerToken,
                Secret = credential.Secret
            },
            CdnAuthenticationType.CustomHeader => new CdnHttpAuthentication
            {
                AuthenticationType = CdnAuthenticationType.CustomHeader,
                HeaderName = credential.HeaderName,
                Secret = credential.Secret
            },
            _ => CdnHttpAuthentication.Anonymous
        };

    private static CdnHttpAuthentication ToContentAuthentication(CredentialProfile? credential) =>
        credential?.Kind switch
        {
            CredentialKind.BearerToken when credential.Provider == CredentialProviderKind.GenericHttp =>
                new CdnHttpAuthentication
                {
                    AuthenticationType = CdnAuthenticationType.BearerToken,
                    Secret = credential.Secret
                },
            CredentialKind.CustomHeader when credential.Provider == CredentialProviderKind.GenericHttp =>
                new CdnHttpAuthentication
                {
                    AuthenticationType = CdnAuthenticationType.CustomHeader,
                    HeaderName = credential.HeaderName,
                    Secret = credential.Secret
                },
            _ => CdnHttpAuthentication.Anonymous
        };

    private static (CdnConfiguration Cdn, IReadOnlyList<CredentialProfile> Credentials) MigrateVersion4CdnProfiles(
        IReadOnlyList<PortableCdnProfile> sourceProfiles,
        IReadOnlyList<CdnBinding> bindings,
        IReadOnlyList<CredentialProfile> sourceCredentials)
    {
        var credentialsById = sourceCredentials.ToDictionary(value => value.Id);
        var legacyGenericIds = new HashSet<Guid>();
        var retainedControlIds = new HashSet<Guid>();
        var profiles = sourceProfiles.Select(source =>
        {
            var legacyCredential = source.CredentialId is Guid id && credentialsById.TryGetValue(id, out var value)
                ? value
                : null;
            var isAliyun = string.Equals(
                source.ProviderId,
                CdnProfile.AlibabaCloudProviderId,
                StringComparison.OrdinalIgnoreCase);
            var controlCredentialId = isAliyun || !string.IsNullOrWhiteSpace(source.PurgeEndpointTemplate)
                ? source.CredentialId
                : null;
            if (controlCredentialId is Guid controlId)
                retainedControlIds.Add(controlId);
            if (!isAliyun && source.CredentialId is Guid genericId)
                legacyGenericIds.Add(genericId);

            return source.ToRuntime() with
            {
                ContentAuthentication = isAliyun
                    ? CdnHttpAuthentication.Anonymous
                    : ToContentAuthentication(legacyCredential),
                ControlCredentialId = controlCredentialId
            };
        }).ToArray();

        var credentials = sourceCredentials
            .Where(credential =>
                !legacyGenericIds.Contains(credential.Id) ||
                retainedControlIds.Contains(credential.Id))
            .ToArray();
        return (new CdnConfiguration(profiles, bindings), credentials);
    }

    private static (
        ConnectionProfile[] Profiles,
        CdnConfiguration Cdn,
        IReadOnlyList<CredentialProfile> Credentials) MigrateLegacyArchiveCredentials(
            IReadOnlyList<ConnectionProfile> sourceProfiles,
            IReadOnlyList<PortableCdnProfile> sourceCdnProfiles,
            IReadOnlyList<CdnBinding> sourceBindings,
            IReadOnlyList<CdnCredential> sourceCdnCredentials)
    {
        var credentials = new List<CredentialProfile>();
        var profiles = sourceProfiles.ToArray();
        for (var index = 0; index < profiles.Length; index++)
        {
            var profile = profiles[index];
            if (profile.CredentialSource == CredentialSourceKind.StoredKeys &&
                (!string.IsNullOrEmpty(profile.AccessKey) || !string.IsNullOrEmpty(profile.SecretKey)))
            {
                var credentialId = AddLegacyArchiveCredential(credentials, new CredentialProfile
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Provider = CredentialProviderFor(profile.ServiceType),
                    Kind = CredentialKind.AccessKeyPair,
                    AccessKeyId = profile.AccessKey,
                    Secret = profile.SecretKey,
                    SessionToken = profile.SessionToken
                }, "storage");
                profiles[index] = profile with { CredentialId = credentialId };
                profile = profiles[index];
            }

            if (profile.CredentialSource == CredentialSourceKind.AwsAssumeRole &&
                !string.IsNullOrWhiteSpace(profile.AwsExternalId))
            {
                var credentialId = AddLegacyArchiveCredential(credentials, new CredentialProfile
                {
                    Id = CreateDerivedCredentialId(profile.Id, "archive-external-id"),
                    Name = profile.Name + " - AWS External ID",
                    Provider = CredentialProviderKind.AmazonWebServices,
                    Kind = CredentialKind.SecretValue,
                    Secret = profile.AwsExternalId
                }, "external-id");
                profiles[index] = profile with { AwsExternalIdCredentialId = credentialId };
            }
        }

        var legacyCredentials = sourceCdnCredentials.ToDictionary(value => value.Id);
        var cdnProfiles = sourceCdnProfiles.Select(source =>
        {
            var legacy = source.CredentialId is Guid legacyId && legacyCredentials.TryGetValue(legacyId, out var value)
                ? value
                : null;
            Guid? controlCredentialId = null;
            if (legacy is not null && !string.IsNullOrWhiteSpace(source.PurgeEndpointTemplate))
            {
                var controlCredential = ToCredentialProfile(legacy)
                    ?? throw new InvalidDataException($"旧连接包 CDN 配置“{source.Name}”包含无法迁移的控制面认证。");
                controlCredentialId = AddLegacyArchiveCredential(credentials, controlCredential, "cdn-control");
            }
            return source.ToRuntime() with
            {
                ContentAuthentication = ToContentAuthentication(legacy),
                ControlCredentialId = controlCredentialId
            };
        }).ToArray();
        var cdn = new CdnConfiguration(cdnProfiles, sourceBindings);
        return (profiles, cdn, credentials);
    }

    private static Guid AddLegacyArchiveCredential(
        ICollection<CredentialProfile> credentials,
        CredentialProfile source,
        string purpose)
    {
        var id = source.Id;
        var suffix = 2;
        while (credentials.Any(value => value.Id == id))
            id = CreateDerivedCredentialId(source.Id, $"archive-{purpose}-{suffix++}");

        var baseName = source.Name.Trim();
        var name = baseName;
        suffix = 2;
        while (credentials.Any(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        credentials.Add(source with { Id = id, Name = name });
        return id;
    }

    private static Guid CreateDerivedCredentialId(Guid sourceId, string purpose)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId:N}:{purpose}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static CredentialProviderKind CredentialProviderFor(S3ServiceType serviceType) => serviceType switch
    {
        S3ServiceType.AmazonS3 => CredentialProviderKind.AmazonWebServices,
        S3ServiceType.AliyunOss => CredentialProviderKind.AlibabaCloud,
        S3ServiceType.TencentCos => CredentialProviderKind.TencentCloud,
        S3ServiceType.CloudflareR2 => CredentialProviderKind.Cloudflare,
        S3ServiceType.BackblazeB2 => CredentialProviderKind.Backblaze,
        S3ServiceType.GoogleCloudStorage => CredentialProviderKind.GoogleCloud,
        S3ServiceType.SupabaseStorage => CredentialProviderKind.Supabase,
        _ => CredentialProviderKind.S3Compatible
    };

    private static bool CdnProfilesEquivalent(
        CdnProfile left,
        CdnProfile right,
        bool compareCredential) =>
        string.Equals(NormalizeCdnUrl(left.BaseUrl), NormalizeCdnUrl(right.BaseUrl), StringComparison.Ordinal) &&
        string.Equals(left.ProviderId?.Trim(), right.ProviderId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        (!compareCredential ||
         (left.ControlCredentialId == right.ControlCredentialId &&
          left.ContentAuthentication.AuthenticationType == right.ContentAuthentication.AuthenticationType &&
          string.Equals(left.ContentAuthentication.HeaderName?.Trim(), right.ContentAuthentication.HeaderName?.Trim(), StringComparison.OrdinalIgnoreCase) &&
          string.Equals(left.ContentAuthentication.Secret, right.ContentAuthentication.Secret, StringComparison.Ordinal))) &&
        left.WarmupMode == right.WarmupMode &&
        left.WarmupRangeBytes == right.WarmupRangeBytes &&
        string.Equals(left.PurgeEndpointTemplate?.Trim(), right.PurgeEndpointTemplate?.Trim(), StringComparison.Ordinal) &&
        string.Equals(left.PurgeHttpMethod?.Trim(), right.PurgeHttpMethod?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.PurgeBodyTemplate, right.PurgeBodyTemplate, StringComparison.Ordinal) &&
        string.Equals(left.PurgeContentType?.Trim(), right.PurgeContentType?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.TimeoutSeconds == right.TimeoutSeconds &&
        left.FollowRedirects == right.FollowRedirects &&
        left.Enabled == right.Enabled;

    private static void EnsureArchiveRelationshipsAreComplete(
        IReadOnlyCollection<ConnectionProfile> profiles,
        CdnConfiguration cdnConfiguration)
    {
        var storageIds = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (profile.Id == Guid.Empty)
                throw new InvalidDataException($"对象存储连接“{profile.Name}”的 ID 不能为空。");
            if (!storageIds.Add(profile.Id))
                throw new InvalidDataException($"对象存储连接 ID 重复：{profile.Id}");
        }

        foreach (var binding in cdnConfiguration.Bindings)
        {
            if (!storageIds.Contains(binding.StorageProfileId))
            {
                throw new InvalidDataException(
                    $"Bucket“{binding.Bucket}”的 CDN 关联引用了不在连接包内的对象存储连接：{binding.StorageProfileId}");
            }
        }
    }

    private static string NormalizeEndpoint(ConnectionProfile profile)
    {
        var normalized = EndpointCompatibility.NormalizeServiceUrl(profile.ServiceType, profile.Endpoint);
        var uri = new Uri(normalized, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Path = uri.AbsolutePath.TrimEnd('/')
        };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static string NormalizeCdnUrl(string value)
    {
        var uri = new Uri(value.Trim(), UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Path = uri.AbsolutePath.TrimEnd('/')
        };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static string NormalizeRegion(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : normalized.ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeBuckets(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Select(value => value?.Trim() ?? string.Empty)
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private static bool BindingsEquivalent(CdnBinding left, CdnBinding right) =>
        SameBindingIdentity(left, right) &&
        string.Equals(
            CdnUrlMapper.NormalizePrefix(left.CdnPathPrefix),
            CdnUrlMapper.NormalizePrefix(right.CdnPathPrefix),
            StringComparison.Ordinal) &&
        left.NewObjectAction == right.NewObjectAction &&
        left.OverwriteAction == right.OverwriteAction &&
        left.IsDefault == right.IsDefault &&
        left.Enabled == right.Enabled;

    private static bool SameBindingIdentity(CdnBinding left, CdnBinding right) =>
        left.StorageProfileId == right.StorageProfileId &&
        left.CdnProfileId == right.CdnProfileId &&
        string.Equals(left.Bucket, right.Bucket, StringComparison.Ordinal) &&
        string.Equals(
            CdnUrlMapper.NormalizePrefix(left.SourcePrefix),
            CdnUrlMapper.NormalizePrefix(right.SourcePrefix),
            StringComparison.Ordinal);

    private static bool SameBindingLocation(CdnBinding left, CdnBinding right) =>
        left.StorageProfileId == right.StorageProfileId &&
        string.Equals(left.Bucket, right.Bucket, StringComparison.Ordinal) &&
        string.Equals(
            CdnUrlMapper.NormalizePrefix(left.SourcePrefix),
            CdnUrlMapper.NormalizePrefix(right.SourcePrefix),
            StringComparison.Ordinal);

    private sealed class ArchiveEnvelope
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset ExportedAtUtc { get; set; }
        public int ProfileCount { get; set; }
        public int CdnProfileCount { get; set; }
        public int CdnCredentialCount { get; set; }
        public int CredentialCount { get; set; }
        public bool ContainsCredentials { get; set; }
        public string Protection { get; set; } = string.Empty;
        public List<PortableProfile>? Profiles { get; set; }
        public List<PortableCdnProfile>? CdnProfiles { get; set; }
        public List<CdnBinding>? CdnBindings { get; set; }
        public List<CredentialProfile>? CredentialProfiles { get; set; }
        public EncryptionMetadata? Encryption { get; set; }
        public string? EncryptedPayload { get; set; }
    }

    private sealed class ArchivePayload
    {
        public List<PortableProfile> Profiles { get; set; } = [];
        public List<PortableCdnProfile> CdnProfiles { get; set; } = [];
        public List<CdnBinding> CdnBindings { get; set; } = [];
        public List<CdnCredential> CdnCredentials { get; set; } = [];
        public List<CredentialProfile> CredentialProfiles { get; set; } = [];
    }

    private sealed class EncryptionMetadata
    {
        public string Algorithm { get; set; } = string.Empty;
        public string Kdf { get; set; } = string.Empty;
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    /// <summary>
    /// Stable archive boundary for CDN profiles. CredentialId exists only to decode v3/v4
    /// packages; v5 writes ContentAuthentication and ControlCredentialId instead.
    /// </summary>
    private sealed class PortableCdnProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string ProviderId { get; set; } = CdnProfile.GenericHttpProviderId;
        public string BaseUrl { get; set; } = string.Empty;
        public Guid? CredentialId { get; set; }
        public CdnHttpAuthentication ContentAuthentication { get; set; } = CdnHttpAuthentication.Anonymous;
        public Guid? ControlCredentialId { get; set; }
        public CdnWarmupMode WarmupMode { get; set; } = CdnWarmupMode.RangeGet;
        public long WarmupRangeBytes { get; set; } = 1024 * 1024;
        public string PurgeEndpointTemplate { get; set; } = string.Empty;
        public string PurgeHttpMethod { get; set; } = "POST";
        public string PurgeBodyTemplate { get; set; } = string.Empty;
        public string PurgeContentType { get; set; } = "application/json";
        public int TimeoutSeconds { get; set; } = 100;
        public bool FollowRedirects { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public CdnCertificateCheckResult? LastCertificateCheck { get; set; }

        public static PortableCdnProfile FromRuntime(CdnProfile profile, bool includeCredentials) => new()
        {
            Id = profile.Id,
            Name = profile.Name,
            Notes = profile.Notes,
            ProviderId = profile.ProviderId,
            BaseUrl = profile.BaseUrl,
            ContentAuthentication = includeCredentials
                ? profile.ContentAuthentication
                : CdnHttpAuthentication.Anonymous,
            ControlCredentialId = includeCredentials ? profile.ControlCredentialId : null,
            WarmupMode = profile.WarmupMode,
            WarmupRangeBytes = profile.WarmupRangeBytes,
            PurgeEndpointTemplate = profile.PurgeEndpointTemplate,
            PurgeHttpMethod = profile.PurgeHttpMethod,
            PurgeBodyTemplate = profile.PurgeBodyTemplate,
            PurgeContentType = profile.PurgeContentType,
            TimeoutSeconds = profile.TimeoutSeconds,
            FollowRedirects = profile.FollowRedirects,
            Enabled = profile.Enabled,
            LastCertificateCheck = profile.LastCertificateCheck
        };

        public CdnProfile ToRuntime() => new()
        {
            Id = Id,
            Name = Name,
            Notes = Notes,
            ProviderId = ProviderId,
            BaseUrl = BaseUrl,
            ContentAuthentication = ContentAuthentication ?? CdnHttpAuthentication.Anonymous,
            ControlCredentialId = ControlCredentialId,
            WarmupMode = WarmupMode,
            WarmupRangeBytes = WarmupRangeBytes,
            PurgeEndpointTemplate = PurgeEndpointTemplate,
            PurgeHttpMethod = PurgeHttpMethod,
            PurgeBodyTemplate = PurgeBodyTemplate,
            PurgeContentType = PurgeContentType,
            TimeoutSeconds = TimeoutSeconds,
            FollowRedirects = FollowRedirects,
            Enabled = Enabled,
            LastCertificateCheck = LastCertificateCheck
        };
    }

    private sealed class PortableProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public S3ServiceType ServiceType { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string SignatureRegion { get; set; } = string.Empty;
        public string? AccessKey { get; set; }
        public string? SecretKey { get; set; }
        public string? SessionToken { get; set; }
        public Guid? CredentialId { get; set; }
        public Guid? AwsExternalIdCredentialId { get; set; }
        public CredentialSourceKind CredentialSource { get; set; } = CredentialSourceKind.StoredKeys;
        public string AwsProfileName { get; set; } = string.Empty;
        public string AwsSourceProfileName { get; set; } = string.Empty;
        public string AwsRoleArn { get; set; } = string.Empty;
        public string AwsRoleSessionName { get; set; } = string.Empty;
        public string AwsRoleSourceIdentity { get; set; } = string.Empty;
        public string? AwsExternalId { get; set; }
        public int AwsSessionDurationSeconds { get; set; } = 3600;
        public string AwsWebIdentityTokenFile { get; set; } = string.Empty;
        public AddressingStyle AddressingStyle { get; set; }
        public bool UseHttps { get; set; }
        public bool IgnoreCertificateErrors { get; set; }
        public string CustomHostHeader { get; set; } = string.Empty;
        public bool FollowTemporaryRedirects { get; set; } = true;
        public bool EnableMultiObjectDelete { get; set; } = true;
        public bool EnableMultipartCopy { get; set; } = true;
        public string DefaultStorageClass { get; set; } = "STANDARD";
        public int RequestTimeoutSeconds { get; set; } = 100;
        public int ConnectionTimeoutSeconds { get; set; } = 10;
        public string DefaultBucket { get; set; } = string.Empty;
        public List<string> ExternalBuckets { get; set; } = [];

        public static PortableProfile FromRuntime(ConnectionProfile source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            ServiceType = source.ServiceType,
            Endpoint = source.Endpoint,
            Region = source.Region,
            SignatureRegion = source.SignatureRegion,
            AccessKey = null,
            SecretKey = null,
            SessionToken = null,
            CredentialId = source.CredentialId,
            AwsExternalIdCredentialId = source.AwsExternalIdCredentialId,
            CredentialSource = source.CredentialSource,
            AwsProfileName = source.CredentialSource is CredentialSourceKind.AwsSharedProfile or CredentialSourceKind.AwsSso
                ? (source.AwsProfileName ?? string.Empty).Trim()
                : string.Empty,
            AwsSourceProfileName = source.CredentialSource == CredentialSourceKind.AwsAssumeRole
                ? (source.AwsSourceProfileName ?? string.Empty).Trim()
                : string.Empty,
            AwsRoleArn = source.CredentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                ? (source.AwsRoleArn ?? string.Empty).Trim()
                : string.Empty,
            AwsRoleSessionName = source.CredentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                ? (source.AwsRoleSessionName ?? string.Empty).Trim()
                : string.Empty,
            AwsRoleSourceIdentity = source.CredentialSource == CredentialSourceKind.AwsAssumeRole
                ? (source.AwsRoleSourceIdentity ?? string.Empty).Trim()
                : string.Empty,
            AwsExternalId = null,
            AwsSessionDurationSeconds = source.CredentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                ? source.AwsSessionDurationSeconds
                : 3600,
            AwsWebIdentityTokenFile = source.CredentialSource == CredentialSourceKind.AwsWebIdentity
                ? (source.AwsWebIdentityTokenFile ?? string.Empty).Trim()
                : string.Empty,
            AddressingStyle = source.AddressingStyle,
            UseHttps = source.UseHttps,
            IgnoreCertificateErrors = source.IgnoreCertificateErrors,
            CustomHostHeader = source.CustomHostHeader,
            FollowTemporaryRedirects = source.FollowTemporaryRedirects,
            EnableMultiObjectDelete = source.EnableMultiObjectDelete,
            EnableMultipartCopy = source.EnableMultipartCopy,
            DefaultStorageClass = source.DefaultStorageClass,
            RequestTimeoutSeconds = source.RequestTimeoutSeconds,
            ConnectionTimeoutSeconds = source.ConnectionTimeoutSeconds,
            DefaultBucket = source.DefaultBucket,
            ExternalBuckets = source.ExternalBuckets.ToList()
        };

        public ConnectionProfile ToRuntime() =>
            S3ProviderCatalog.RepairLegacyServiceType(new ConnectionProfile
            {
                Id = Id,
                Name = Name,
                ServiceType = ServiceType,
                Endpoint = Endpoint,
                Region = Region,
                SignatureRegion = SignatureRegion,
                AccessKey = AccessKey ?? string.Empty,
                SecretKey = SecretKey ?? string.Empty,
                SessionToken = SessionToken ?? string.Empty,
                CredentialId = CredentialId,
                AwsExternalIdCredentialId = AwsExternalIdCredentialId,
                CredentialSource = CredentialSource,
                AwsProfileName = AwsProfileName ?? string.Empty,
                AwsSourceProfileName = AwsSourceProfileName ?? string.Empty,
                AwsRoleArn = AwsRoleArn ?? string.Empty,
                AwsRoleSessionName = AwsRoleSessionName ?? string.Empty,
                AwsRoleSourceIdentity = AwsRoleSourceIdentity ?? string.Empty,
                AwsExternalId = AwsExternalId ?? string.Empty,
                AwsSessionDurationSeconds = AwsSessionDurationSeconds <= 0 ? 3600 : AwsSessionDurationSeconds,
                AwsWebIdentityTokenFile = AwsWebIdentityTokenFile ?? string.Empty,
                AddressingStyle = AddressingStyle,
                UseHttps = UseHttps,
                IgnoreCertificateErrors = IgnoreCertificateErrors,
                CustomHostHeader = CustomHostHeader,
                FollowTemporaryRedirects = FollowTemporaryRedirects,
                EnableMultiObjectDelete = EnableMultiObjectDelete,
                EnableMultipartCopy = EnableMultipartCopy,
                DefaultStorageClass = DefaultStorageClass,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
                DefaultBucket = DefaultBucket,
                ExternalBuckets = ExternalBuckets ?? []
            });
    }
}

public sealed class ConnectionArchivePasswordRequiredException : Exception
{
    public ConnectionArchivePasswordRequiredException()
        : base("该连接包包含凭据，需要输入迁移密码。") { }
}

public sealed class ConnectionArchiveAuthenticationException : Exception
{
    public ConnectionArchiveAuthenticationException(Exception innerException)
        : base("迁移密码错误，或连接包已损坏。", innerException) { }
}
