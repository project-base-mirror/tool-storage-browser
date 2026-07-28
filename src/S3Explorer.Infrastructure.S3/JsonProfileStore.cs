using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly string _path;
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
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "profiles.json");
    }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return Array.Empty<ConnectionProfile>();

        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<ProfileDocument>(stream, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
            return Array.Empty<ConnectionProfile>();

        return document.Profiles.Select(profile => profile.ToRuntime(_protector)).ToArray();
    }

    public async Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var document = new ProfileDocument
        {
            Version = 3,
            Profiles = profiles.Select(profile => PersistedProfile.FromRuntime(profile, _protector)).ToList()
        };

        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _path, true);
    }

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
