using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

/// <summary>最近一次成功列举到的 Bucket 快照。只包含 Bucket 名称和非秘密连接指纹。</summary>
internal sealed record BucketDiscoverySnapshot(
    Guid ProfileId,
    string ConnectionSignature,
    IReadOnlyList<string> Buckets,
    DateTimeOffset DiscoveredAtUtc);

/// <summary>Per-connection Bucket discovery cache. A null path creates an in-memory cache.</summary>
internal sealed class BucketDiscoveryCache
{
    private const int CurrentSchema = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly DurableJsonFile? _file;
    private readonly int _maxProfiles;
    private readonly int _maxBucketsPerProfile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<BucketDiscoverySnapshot> _entries = new();
    private bool _loaded;

    public BucketDiscoveryCache(
        string? path = null,
        int maxProfiles = 64,
        int maxBucketsPerProfile = 256)
    {
        if (maxProfiles < 1) throw new ArgumentOutOfRangeException(nameof(maxProfiles));
        if (maxBucketsPerProfile < 1) throw new ArgumentOutOfRangeException(nameof(maxBucketsPerProfile));
        _file = string.IsNullOrWhiteSpace(path) ? null : new DurableJsonFile(path);
        _maxProfiles = maxProfiles;
        _maxBucketsPerProfile = maxBucketsPerProfile;
    }

    public static string BuildConnectionSignature(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var material = string.Join("\n", new[]
        {
            profile.Id.ToString("D"),
            profile.ServiceType.ToString(),
            profile.Endpoint?.Trim() ?? string.Empty,
            profile.Region?.Trim() ?? string.Empty,
            profile.SignatureRegion?.Trim() ?? string.Empty,
            profile.CredentialSource.ToString(),
            profile.CredentialId?.ToString("D") ?? string.Empty,
            profile.AccessKey?.Trim() ?? string.Empty,
            profile.AwsProfileName?.Trim() ?? string.Empty,
            profile.AwsSourceProfileName?.Trim() ?? string.Empty,
            profile.AwsRoleArn?.Trim() ?? string.Empty,
            profile.AwsRoleSessionName?.Trim() ?? string.Empty,
            profile.AwsRoleSourceIdentity?.Trim() ?? string.Empty,
            profile.AwsExternalIdCredentialId?.ToString("D") ?? string.Empty,
            profile.AwsSessionDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.AwsWebIdentityTokenFile?.Trim() ?? string.Empty,
            profile.AddressingStyle.ToString(),
            profile.UseHttps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.CustomHostHeader?.Trim() ?? string.Empty
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public async Task<BucketDiscoverySnapshot?> GetAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var signature = BuildConnectionSignature(profile);
            return _entries.FirstOrDefault(entry =>
                entry.ProfileId == profile.Id &&
                string.Equals(entry.ConnectionSignature, signature, StringComparison.Ordinal));
        }
        finally { _gate.Release(); }
    }

    public async Task RecordSuccessfulDiscoveryAsync(
        ConnectionProfile profile,
        IEnumerable<string> buckets,
        DateTimeOffset? discoveredAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(buckets);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = NormalizeBuckets(buckets, _maxBucketsPerProfile);
            var entry = new BucketDiscoverySnapshot(profile.Id, BuildConnectionSignature(profile), normalized,
                discoveredAtUtc ?? DateTimeOffset.UtcNow);
            _entries.RemoveAll(existing => existing.ProfileId == entry.ProfileId);
            _entries.Insert(0, entry);
            _entries = _entries
                .OrderByDescending(item => item.DiscoveredAtUtc)
                .ThenBy(item => item.ProfileId)
                .Take(_maxProfiles)
                .ToList();
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BucketDiscoverySnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _entries.ToArray(); }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            if (_file is not null)
            {
                var document = await _file.LoadAsync(
                    static () => new BucketCacheDocument(),
                    JsonOptions,
                    ValidateDocument,
                    useDefaultWhenUnrecoverable: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _entries = document.Entries
                    .Where(static item => item.ProfileId != Guid.Empty && !string.IsNullOrWhiteSpace(item.ConnectionSignature))
                    .Select(item => item with
                    {
                        Buckets = NormalizeBuckets(
                            item.Buckets ?? Array.Empty<string>(),
                            _maxBucketsPerProfile)
                    })
                    .OrderByDescending(item => item.DiscoveredAtUtc).Take(_maxProfiles).ToList();
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    private Task SaveCoreAsync(CancellationToken cancellationToken) => _file is null
        ? Task.CompletedTask
        : _file.SaveAsync(
            new BucketCacheDocument { Entries = _entries },
            JsonOptions,
            ValidateDocument,
            cancellationToken);

    private static void ValidateDocument(BucketCacheDocument document)
    {
        if (document.Schema != CurrentSchema)
            throw new InvalidDataException($"不支持的 Bucket 缓存 Schema：{document.Schema}");
        if (document.Entries is null)
            throw new InvalidDataException("Bucket 缓存缺少 entries。");
    }

    private static IReadOnlyList<string> NormalizeBuckets(
        IEnumerable<string> buckets,
        int maximum) => buckets
        .Where(static bucket => !string.IsNullOrWhiteSpace(bucket))
        .Select(static bucket => bucket.Trim())
        .Where(static bucket =>
            bucket.Length <= 255 &&
            !bucket.Any(char.IsControl) &&
            !bucket.Contains('/') &&
            !bucket.Contains('\\'))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static bucket => bucket, StringComparer.Ordinal)
        .Take(maximum)
        .ToArray();

    private sealed class BucketCacheDocument
    {
        public int Schema { get; set; } = CurrentSchema;
        public IReadOnlyList<BucketDiscoverySnapshot> Entries { get; set; } = Array.Empty<BucketDiscoverySnapshot>();
    }
}
