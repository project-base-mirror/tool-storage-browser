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
    DateTimeOffset ExportedAtUtc);

public sealed record ConnectionArchivePackage(
    IReadOnlyList<ConnectionProfile> Profiles,
    bool ContainsCredentials,
    DateTimeOffset ExportedAtUtc);

public sealed class ConnectionArchiveService
{
    public const string FileExtension = "s3connections";
    public const int MaximumProfileCount = 1000;
    public const int MaximumArchiveBytes = 16 * 1024 * 1024;
    public const int PasswordMinimumLength = 8;

    private const string FormatName = "s3explorer-connections";
    private const int FormatVersion = 2;
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
        string? password = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
            throw new ArgumentException("至少选择一个连接。", nameof(profiles));
        if (profiles.Count > MaximumProfileCount)
            throw new ArgumentOutOfRangeException(nameof(profiles), $"一次最多导出 {MaximumProfileCount} 个连接。");

        foreach (var profile in profiles)
            profile.ValidateConfiguration();

        if (includeCredentials && (password?.Length ?? 0) < PasswordMinimumLength)
            throw new ArgumentException($"迁移密码至少需要 {PasswordMinimumLength} 个字符。", nameof(password));

        var exportedAt = DateTimeOffset.UtcNow;
        var portableProfiles = profiles
            .Select(profile => PortableProfile.FromRuntime(profile, includeCredentials))
            .ToList();
        ArchiveEnvelope envelope;

        if (!includeCredentials)
        {
            envelope = new ArchiveEnvelope
            {
                Format = FormatName,
                Version = FormatVersion,
                ExportedAtUtc = exportedAt,
                ProfileCount = portableProfiles.Count,
                ContainsCredentials = false,
                Protection = NoProtection,
                Profiles = portableProfiles
            };
        }
        else
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new ArchivePayload { Profiles = portableProfiles }, _jsonOptions);
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
            envelope.ExportedAtUtc);
    }

    public ConnectionArchivePackage Import(ReadOnlySpan<byte> archive, string? password = null)
    {
        var envelope = ReadEnvelope(archive);
        ValidateEnvelope(envelope);

        List<PortableProfile> portableProfiles;
        if (string.Equals(envelope.Protection, NoProtection, StringComparison.Ordinal))
        {
            portableProfiles = envelope.Profiles!;
        }
        else
        {
            if (string.IsNullOrEmpty(password))
                throw new ConnectionArchivePasswordRequiredException();
            portableProfiles = DecryptProfiles(envelope, password);
        }

        if (portableProfiles.Count != envelope.ProfileCount)
            throw new InvalidDataException("连接包中的连接数量不一致。");

        var profiles = portableProfiles.Select(profile => profile.ToRuntime()).ToArray();
        foreach (var profile in profiles)
            profile.ValidateConfiguration();

        return new(profiles, envelope.ContainsCredentials, envelope.ExportedAtUtc);
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

    private List<PortableProfile> DecryptProfiles(ArchiveEnvelope envelope, string password)
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
            return payload.Profiles;
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
    {
        var candidate = $"{name} (导入)";
        var suffix = 2;
        while (profiles.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{name} (导入 {suffix++})";
        return candidate;
    }

    private sealed class ArchiveEnvelope
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset ExportedAtUtc { get; set; }
        public int ProfileCount { get; set; }
        public bool ContainsCredentials { get; set; }
        public string Protection { get; set; } = string.Empty;
        public List<PortableProfile>? Profiles { get; set; }
        public EncryptionMetadata? Encryption { get; set; }
        public string? EncryptedPayload { get; set; }
    }

    private sealed class ArchivePayload
    {
        public List<PortableProfile> Profiles { get; set; } = [];
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
                ? source.AwsProfileName.Trim()
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
            AwsProfileName = AwsProfileName,
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
