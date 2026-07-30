using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class JsonProfileStore : IProfileStore, IRecoveryAwareStore
{
    private readonly DurableJsonFile _file;
    private readonly ICredentialProtector _protector;
    private IReadOnlyList<ConnectionGroup> _groups = [];
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
        => (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).Profiles;

    public async Task<ConnectionProfileConfiguration> LoadConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await _file.LoadAsync(
            static () => new ProfileDocument(),
            _jsonOptions,
            ValidateDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var configuration = ToConfiguration(document).Normalize();
        _groups = configuration.Groups;
        return configuration;
    }

    public async Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
        => await SaveConfigurationAsync(
            new ConnectionProfileConfiguration(profiles.ToArray(), _groups).Normalize(),
            cancellationToken).ConfigureAwait(false);

    public async Task SaveConfigurationAsync(
        ConnectionProfileConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration = configuration.Normalize();
        configuration.Validate();
        var document = new ProfileDocument
        {
            Version = 4,
            Groups = configuration.Groups.ToList(),
            Profiles = configuration.Profiles
                .Select(S3ProviderCatalog.RepairLegacyServiceType)
                .Select(profile => PersistedProfile.FromRuntime(profile, _protector))
                .ToList()
        };

        await _file.SaveAsync(document, _jsonOptions, ValidateDocument, cancellationToken)
            .ConfigureAwait(false);
        _groups = configuration.Groups;
    }

    private void ValidateDocument(ProfileDocument document)
    {
        if (document.Version is < 1 or > 4)
            throw new InvalidDataException($"不支持的连接配置文件版本：{document.Version}。");
        if (document.Profiles is null || document.Profiles.Any(profile => profile is null))
            throw new InvalidDataException("连接配置文件包含空集合或空记录。");
        if (document.Groups is null || document.Groups.Any(group => group is null))
            throw new InvalidDataException("连接配置文件包含空分组集合或空分组记录。");
        ToConfiguration(document).Validate();
    }

    private ConnectionProfileConfiguration ToConfiguration(ProfileDocument document) =>
        new(
            document.Profiles
                .Select(profile => S3ProviderCatalog.RepairLegacyServiceType(profile.ToRuntime(_protector)))
                .ToArray(),
            document.Version >= 4 ? document.Groups.ToArray() : []);

    private sealed class ProfileDocument
    {
        public int Version { get; set; } = 1;
        public List<ConnectionGroup> Groups { get; set; } = [];
        public List<PersistedProfile> Profiles { get; set; } = [];
    }

    private sealed class PersistedProfile
    {
        public Guid Id { get; set; }
        public Guid? GroupId { get; set; }
        public int SortOrder { get; set; }
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
        public string AwsSourceProfileName { get; set; } = string.Empty;
        public string AwsRoleArn { get; set; } = string.Empty;
        public string AwsRoleSessionName { get; set; } = string.Empty;
        public string AwsRoleSourceIdentity { get; set; } = string.Empty;
        public string ProtectedAwsExternalId { get; set; } = string.Empty;
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
            GroupId = source.GroupId,
            SortOrder = source.SortOrder,
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
            ProtectedAwsExternalId = source.CredentialSource == CredentialSourceKind.AwsAssumeRole
                ? protector.Protect(source.AwsExternalId)
                : string.Empty,
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
            ExternalBuckets = source.ExternalBuckets.ToList(),
            HealthStatus = source.HealthStatus,
            LastConnectionCheckedAtUtc = source.LastConnectionCheckedAtUtc,
            LastConnectionSucceededAtUtc = source.LastConnectionSucceededAtUtc
        };

        public ConnectionProfile ToRuntime(ICredentialProtector protector) => new()
        {
            Id = Id,
            GroupId = GroupId,
            SortOrder = Math.Max(0, SortOrder),
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
            AwsSourceProfileName = AwsSourceProfileName ?? string.Empty,
            AwsRoleArn = AwsRoleArn ?? string.Empty,
            AwsRoleSessionName = AwsRoleSessionName ?? string.Empty,
            AwsRoleSourceIdentity = AwsRoleSourceIdentity ?? string.Empty,
            AwsExternalId = protector.Unprotect(ProtectedAwsExternalId),
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
