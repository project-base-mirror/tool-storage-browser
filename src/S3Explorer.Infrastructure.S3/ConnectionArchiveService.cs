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
    int CdnCredentialCount = 0);

public sealed record ConnectionArchivePackage(
    IReadOnlyList<ConnectionProfile> Profiles,
    bool ContainsCredentials,
    DateTimeOffset ExportedAtUtc,
    CdnConfiguration? CdnConfiguration = null,
    IReadOnlyList<CdnCredential>? CdnCredentials = null)
{
    public CdnConfiguration ImportedCdnConfiguration =>
        CdnConfiguration ?? S3Explorer.Core.CdnConfiguration.Empty;

    public IReadOnlyList<CdnCredential> ImportedCdnCredentials => CdnCredentials ?? [];
}

public sealed record ConnectionArchiveMergeResult(
    IReadOnlyList<ConnectionProfile> Profiles,
    CdnConfiguration CdnConfiguration,
    IReadOnlyList<CdnCredential> CdnCredentials);

public sealed class ConnectionArchiveService
{
    public const string FileExtension = "s3connections";
    public const int MaximumProfileCount = 1000;
    public const int MaximumArchiveBytes = 16 * 1024 * 1024;
    public const int PasswordMinimumLength = 8;

    private const string FormatName = "s3explorer-connections";
    private const int FormatVersion = 3;
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
        IReadOnlyCollection<CdnCredential>? cdnCredentials = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        cdnConfiguration ??= CdnConfiguration.Empty;
        cdnCredentials ??= [];
        if (profiles.Count == 0)
            throw new ArgumentException("至少选择一个连接。", nameof(profiles));
        if (profiles.Count > MaximumProfileCount)
            throw new ArgumentOutOfRangeException(nameof(profiles), $"一次最多导出 {MaximumProfileCount} 个连接。");

        foreach (var profile in profiles)
            profile.ValidateConfiguration();
        CdnConfigurationValidator.EnsureValid(cdnConfiguration, cdnCredentials);

        var containsStoredCredentials = includeCredentials && (
            profiles.Any(profile => profile.HasStoredCredentials) ||
            cdnCredentials.Any(credential =>
                credential.AuthenticationType != CdnAuthenticationType.None &&
                !string.IsNullOrEmpty(credential.Secret)));
        if (containsStoredCredentials && (password?.Length ?? 0) < PasswordMinimumLength)
            throw new ArgumentException($"迁移密码至少需要 {PasswordMinimumLength} 个字符。", nameof(password));

        var exportedAt = DateTimeOffset.UtcNow;
        var portableProfiles = profiles
            .Select(profile => PortableProfile.FromRuntime(profile, containsStoredCredentials))
            .ToList();
        var portableCdnProfiles = cdnConfiguration.Profiles
            .Select(profile => containsStoredCredentials ? profile : profile with { CredentialId = null })
            .ToList();
        var portableCdnBindings = cdnConfiguration.Bindings.ToList();
        var portableCdnCredentials = containsStoredCredentials
            ? cdnCredentials.ToList()
            : [];
        var payload = new ArchivePayload
        {
            Profiles = portableProfiles,
            CdnProfiles = portableCdnProfiles,
            CdnBindings = portableCdnBindings,
            CdnCredentials = portableCdnCredentials
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
                CdnCredentialCount = portableCdnCredentials.Count,
                ContainsCredentials = false,
                Protection = NoProtection,
                Profiles = portableProfiles,
                CdnProfiles = portableCdnProfiles,
                CdnBindings = portableCdnBindings
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
                CdnCredentialCount = portableCdnCredentials.Count,
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
                EncryptedPayload = Convert.ToBase64String(ciphertext)
            };
        }

        var result = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        if (result.Length > MaximumArchiveBytes)
            throw new InvalidOperationException($"连接包不能超过 {MaximumArchiveBytes / 1024 / 1024} MiB。");
        return result;
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
            envelope.CdnCredentialCount);
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
                CdnBindings = envelope.CdnBindings ?? []
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
            payload.CdnCredentials.Count != envelope.CdnCredentialCount)
            throw new InvalidDataException("连接包中的 CDN 配置或凭据数量不一致。");

        var profiles = payload.Profiles.Select(profile => profile.ToRuntime()).ToArray();
        foreach (var profile in profiles)
            profile.ValidateConfiguration();
        var cdnConfiguration = new CdnConfiguration(payload.CdnProfiles, payload.CdnBindings);
        CdnConfigurationValidator.EnsureValid(cdnConfiguration, payload.CdnCredentials);

        return new(
            profiles,
            envelope.ContainsCredentials,
            envelope.ExportedAtUtc,
            cdnConfiguration,
            payload.CdnCredentials);
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

    public ConnectionArchiveMergeResult MergePackage(
        IReadOnlyCollection<ConnectionProfile> existingProfiles,
        CdnConfiguration existingCdnConfiguration,
        IReadOnlyCollection<CdnCredential> existingCdnCredentials,
        ConnectionArchivePackage package,
        IReadOnlyCollection<Guid> selectedImportedProfileIds,
        bool importCredentials,
        ConnectionImportConflictStrategy conflictStrategy)
    {
        ArgumentNullException.ThrowIfNull(existingProfiles);
        ArgumentNullException.ThrowIfNull(existingCdnConfiguration);
        ArgumentNullException.ThrowIfNull(existingCdnCredentials);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(selectedImportedProfileIds);
        CdnConfigurationValidator.EnsureValid(existingCdnConfiguration, existingCdnCredentials);

        var selectedIds = selectedImportedProfileIds.ToHashSet();
        var packageIds = package.Profiles.Select(profile => profile.Id).ToHashSet();
        if (!selectedIds.IsSubsetOf(packageIds))
            throw new ArgumentException("所选连接不属于当前连接包。", nameof(selectedImportedProfileIds));

        var profiles = existingProfiles.ToList();
        var storageIdMap = new Dictionary<Guid, Guid>();
        foreach (var source in package.Profiles.Where(profile => selectedIds.Contains(profile.Id)))
        {
            source.ValidateConfiguration();
            var imported = importCredentials
                ? source
                : source with { AccessKey = string.Empty, SecretKey = string.Empty, SessionToken = string.Empty };
            var existingIndex = profiles.FindIndex(item =>
                string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                var added = imported with { Id = Guid.NewGuid() };
                profiles.Add(added);
                storageIdMap[source.Id] = added.Id;
                continue;
            }

            switch (conflictStrategy)
            {
                case ConnectionImportConflictStrategy.Skip:
                    break;
                case ConnectionImportConflictStrategy.Replace:
                    var replaced = imported with { Id = profiles[existingIndex].Id };
                    profiles[existingIndex] = replaced;
                    storageIdMap[source.Id] = replaced.Id;
                    break;
                case ConnectionImportConflictStrategy.Rename:
                    var renamed = imported with
                    {
                        Id = Guid.NewGuid(),
                        Name = CreateUniqueImportedName(imported.Name, profiles.Select(item => item.Name))
                    };
                    profiles.Add(renamed);
                    storageIdMap[source.Id] = renamed.Id;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(conflictStrategy));
            }
        }

        var importedConfiguration = package.ImportedCdnConfiguration;
        var importEntireCdnPackage = selectedIds.SetEquals(packageIds) &&
            storageIdMap.Count == package.Profiles.Count;
        var selectedBindings = importedConfiguration.Bindings
            .Where(binding => storageIdMap.ContainsKey(binding.StorageProfileId))
            .ToArray();
        var selectedCdnProfileIds = selectedBindings
            .Select(binding => binding.CdnProfileId)
            .ToHashSet();
        if (importEntireCdnPackage)
            selectedCdnProfileIds.UnionWith(importedConfiguration.Profiles.Select(profile => profile.Id));

        var importedCdnProfiles = importedConfiguration.Profiles
            .Where(profile => selectedCdnProfileIds.Contains(profile.Id))
            .ToArray();
        var selectedCredentialIds = importedCdnProfiles
            .Where(profile => profile.CredentialId.HasValue)
            .Select(profile => profile.CredentialId!.Value)
            .ToHashSet();
        if (importEntireCdnPackage)
            selectedCredentialIds.UnionWith(package.ImportedCdnCredentials.Select(credential => credential.Id));

        var credentials = existingCdnCredentials.ToList();
        var credentialIdMap = new Dictionary<Guid, Guid>();
        if (importCredentials)
        {
            foreach (var source in package.ImportedCdnCredentials
                         .Where(credential => selectedCredentialIds.Contains(credential.Id)))
            {
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

        var cdnProfiles = existingCdnConfiguration.Profiles.ToList();
        var cdnProfileIdMap = new Dictionary<Guid, Guid>();
        foreach (var source in importedCdnProfiles)
        {
            var credentialId = source.CredentialId is Guid sourceCredentialId &&
                               credentialIdMap.TryGetValue(sourceCredentialId, out var mappedCredentialId)
                ? mappedCredentialId
                : (Guid?)null;
            var imported = source with { CredentialId = credentialId };
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
            var exactIndex = bindings.FindIndex(binding => SameBinding(binding, imported));
            if (exactIndex >= 0)
            {
                if (conflictStrategy == ConnectionImportConflictStrategy.Replace)
                    bindings[exactIndex] = imported with { Id = bindings[exactIndex].Id };
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
        if (envelope.CdnProfileCount is < 0 or > MaximumProfileCount ||
            envelope.CdnCredentialCount is < 0 or > MaximumProfileCount)
            throw new InvalidDataException("连接包中的 CDN 配置或凭据数量无效。");
        if (envelope.Version < 3 &&
            (envelope.CdnProfileCount != 0 || envelope.CdnCredentialCount != 0 ||
             envelope.CdnProfiles is not null || envelope.CdnBindings is not null))
            throw new InvalidDataException("旧版连接包不能包含 CDN 配置。");
        if (envelope.ExportedAtUtc == default)
            throw new InvalidDataException("连接包缺少导出时间。");

        if (string.Equals(envelope.Protection, NoProtection, StringComparison.Ordinal))
        {
            if (envelope.ContainsCredentials || envelope.Profiles is null || envelope.Encryption is not null || envelope.EncryptedPayload is not null)
                throw new InvalidDataException("无凭据连接包结构无效。");
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
            if (payload.Profiles is null || payload.CdnProfiles is null ||
                payload.CdnBindings is null || payload.CdnCredentials is null)
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

    private static bool SameBinding(CdnBinding left, CdnBinding right) =>
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
        public bool ContainsCredentials { get; set; }
        public string Protection { get; set; } = string.Empty;
        public List<PortableProfile>? Profiles { get; set; }
        public List<CdnProfile>? CdnProfiles { get; set; }
        public List<CdnBinding>? CdnBindings { get; set; }
        public EncryptionMetadata? Encryption { get; set; }
        public string? EncryptedPayload { get; set; }
    }

    private sealed class ArchivePayload
    {
        public List<PortableProfile> Profiles { get; set; } = [];
        public List<CdnProfile> CdnProfiles { get; set; } = [];
        public List<CdnBinding> CdnBindings { get; set; } = [];
        public List<CdnCredential> CdnCredentials { get; set; } = [];
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
        public CredentialSourceKind CredentialSource { get; set; } = CredentialSourceKind.StoredKeys;
        public string AwsProfileName { get; set; } = string.Empty;
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

        public static PortableProfile FromRuntime(ConnectionProfile source, bool includeCredentials) => new()
        {
            Id = source.Id,
            Name = source.Name,
            ServiceType = source.ServiceType,
            Endpoint = source.Endpoint,
            Region = source.Region,
            SignatureRegion = source.SignatureRegion,
            AccessKey = includeCredentials && source.CredentialSource == CredentialSourceKind.StoredKeys
                ? source.AccessKey
                : null,
            SecretKey = includeCredentials && source.CredentialSource == CredentialSourceKind.StoredKeys
                ? source.SecretKey
                : null,
            SessionToken = includeCredentials && source.CredentialSource == CredentialSourceKind.StoredKeys && source.SessionToken.Length > 0
                ? source.SessionToken
                : null,
            CredentialSource = source.CredentialSource,
            AwsProfileName = source.CredentialSource == CredentialSourceKind.AwsSharedProfile
                ? (source.AwsProfileName ?? string.Empty).Trim()
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

        public ConnectionProfile ToRuntime() => new()
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
            CredentialSource = CredentialSource,
            AwsProfileName = AwsProfileName ?? string.Empty,
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
        };
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
