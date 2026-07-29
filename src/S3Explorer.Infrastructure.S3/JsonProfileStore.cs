using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class JsonProfileStore : IProfileStore, IRecoveryAwareStore
{
    private readonly DurableJsonFile _file;
    private readonly ICredentialProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JsonProfileStore(ICredentialProtector protector, string? path = null)
    {
        _protector = protector;
        _file = new DurableJsonFile(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "profiles.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var document = await _file.LoadAsync(
            static () => new ProfileDocument(),
            _jsonOptions,
            ValidateDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToRuntime(document);
    }

    public async Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        var document = new ProfileDocument
        {
            Version = 3,
            Profiles = profiles
                .Select(S3ProviderCatalog.RepairLegacyServiceType)
                .Select(profile => PersistedProfile.FromRuntime(profile, _protector))
                .ToList()
        };

        await _file.SaveAsync(document, _jsonOptions, ValidateDocument, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ValidateDocument(ProfileDocument document)
    {
        if (document.Version is < 1 or > 3)
            throw new InvalidDataException($"不支持的连接配置文件版本：{document.Version}。");
        if (document.Profiles is null || document.Profiles.Any(profile => profile is null))
            throw new InvalidDataException("连接配置文件包含空集合或空记录。");

        var profiles = ToRuntime(document);
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            profile.Validate();
            if (!ids.Add(profile.Id))
                throw new InvalidDataException($"连接 ID 重复：{profile.Id}。");
            if (!names.Add(profile.Name.Trim()))
                throw new InvalidDataException($"连接名称重复：{profile.Name}。");
        }
    }

    private ConnectionProfile[] ToRuntime(ProfileDocument document) =>
        document.Profiles
            .Select(profile => S3ProviderCatalog.RepairLegacyServiceType(profile.ToRuntime(_protector)))
            .ToArray();

    private sealed class ProfileDocument
    {
        public int Version { get; set; } = 1;
        public List<PersistedProfile> Profiles { get; set; } = [];
    }

    private sealed class PersistedProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public S3ServiceType ServiceType { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string SignatureRegion { get; set; } = string.Empty;
        public string AccessKey { get; set; } = string.Empty;
        public string ProtectedSecretKey { get; set; } = string.Empty;
        public string ProtectedSessionToken { get; set; } = string.Empty;
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
        public int RequestTimeoutSeconds { get; set; }
        public int ConnectionTimeoutSeconds { get; set; } = 10;
        public string DefaultBucket { get; set; } = string.Empty;
        public List<string> ExternalBuckets { get; set; } = [];
        public ConnectionHealthStatus HealthStatus { get; set; }
        public DateTimeOffset? LastConnectionCheckedAtUtc { get; set; }
        public DateTimeOffset? LastConnectionSucceededAtUtc { get; set; }

        public static PersistedProfile FromRuntime(ConnectionProfile source, ICredentialProtector protector) => new()
        {
            Id = source.Id,
            Name = source.Name,
            ServiceType = source.ServiceType,
            Endpoint = source.Endpoint,
            Region = source.Region,
            SignatureRegion = source.SignatureRegion,
            AccessKey = source.CredentialSource == CredentialSourceKind.StoredKeys ? source.AccessKey : string.Empty,
            ProtectedSecretKey = source.CredentialSource == CredentialSourceKind.StoredKeys
                ? protector.Protect(source.SecretKey)
                : string.Empty,
            ProtectedSessionToken = source.CredentialSource == CredentialSourceKind.StoredKeys
                ? protector.Protect(source.SessionToken)
                : string.Empty,
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
            ExternalBuckets = source.ExternalBuckets.ToList(),
            HealthStatus = source.HealthStatus,
            LastConnectionCheckedAtUtc = source.LastConnectionCheckedAtUtc,
            LastConnectionSucceededAtUtc = source.LastConnectionSucceededAtUtc
        };

        public ConnectionProfile ToRuntime(ICredentialProtector protector) => new()
        {
            Id = Id,
            Name = Name,
            ServiceType = ServiceType,
            Endpoint = Endpoint,
            Region = Region,
            SignatureRegion = SignatureRegion,
            AccessKey = AccessKey,
            SecretKey = protector.Unprotect(ProtectedSecretKey),
            SessionToken = protector.Unprotect(ProtectedSessionToken),
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
            RequestTimeoutSeconds = RequestTimeoutSeconds <= 0 ? 100 : RequestTimeoutSeconds,
            ConnectionTimeoutSeconds = ConnectionTimeoutSeconds <= 0 ? 10 : ConnectionTimeoutSeconds,
            DefaultBucket = DefaultBucket,
            ExternalBuckets = ExternalBuckets ?? [],
            HealthStatus = HealthStatus,
            LastConnectionCheckedAtUtc = LastConnectionCheckedAtUtc,
            LastConnectionSucceededAtUtc = LastConnectionSucceededAtUtc
        };
    }
}
