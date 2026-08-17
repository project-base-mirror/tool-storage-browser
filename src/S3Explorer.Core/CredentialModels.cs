namespace S3Explorer.Core;

public enum CredentialProviderKind
{
    S3Compatible,
    AmazonWebServices,
    AlibabaCloud,
    TencentCloud,
    Cloudflare,
    Backblaze,
    GoogleCloud,
    Supabase,
    GenericHttp
}

public enum CredentialKind
{
    AccessKeyPair,
    BearerToken,
    CustomHeader,
    SecretValue
}

public enum PermissionCheckState
{
    Passed,
    Denied,
    Unsupported,
    Indeterminate,
    Skipped
}

[Flags]
public enum StoragePermissionOperation
{
    Read = 1,
    Publish = 2,
    Mirror = 4,
    PutObjectAcl = 8
}

public sealed record StoragePermissionCheckRequest(
    ConnectionProfile Profile,
    string Bucket,
    string Prefix,
    StoragePermissionOperation Operation,
    bool AllowMutation = false);

public interface IStoragePermissionChecker
{
    Task<PermissionCheckResult> CheckAsync(
        StoragePermissionCheckRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CredentialProfile
{
    public const int MaximumNameLength = 200;
    public static readonly string GenericHttpCdnProviderId = CdnProfile.GenericHttpProviderId;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string StableId => Id.ToString("N");
    public string Name { get; init; } = string.Empty;
    public CredentialProviderKind Provider { get; init; } = CredentialProviderKind.S3Compatible;
    public CredentialKind Kind { get; init; } = CredentialKind.AccessKeyPair;
    public string AccessKeyId { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public string SessionToken { get; init; } = string.Empty;
    public string HeaderName { get; init; } = string.Empty;

    public string Display => $"{Name} ({Kind} / {Provider})";
    public string Fingerprint => Provider switch
    {
        CredentialProviderKind.S3Compatible => BuildAccessKeyFingerprint("S3-Compatible"),
        CredentialProviderKind.AmazonWebServices => Kind == CredentialKind.SecretValue
            ? "AWS secret:configured"
            : BuildAccessKeyFingerprint("AmazonS3"),
        CredentialProviderKind.AlibabaCloud => BuildAccessKeyFingerprint("Aliyun OSS"),
        CredentialProviderKind.TencentCloud => BuildAccessKeyFingerprint("Tencent COS"),
        CredentialProviderKind.Cloudflare => BuildAccessKeyFingerprint("Cloudflare R2"),
        CredentialProviderKind.Backblaze => BuildAccessKeyFingerprint("Backblaze B2"),
        CredentialProviderKind.GoogleCloud => BuildAccessKeyFingerprint("Google Cloud Storage"),
        CredentialProviderKind.Supabase => BuildAccessKeyFingerprint("Supabase Storage"),
        _ => Kind == CredentialKind.CustomHeader
            ? $"header:{HeaderName}"
            : "token:configured"
    };

    public void Validate()
    {
        if (Id == Guid.Empty)
            throw new ArgumentException("凭据 ID 不能为空。", nameof(Id));

        var trimmedName = Name?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0)
            throw new ArgumentException("凭据名称不能为空。", nameof(Name));
        if (trimmedName.Length > MaximumNameLength)
            throw new ArgumentException($"凭据名称不能超过 {MaximumNameLength} 个字符。", nameof(Name));
        if (trimmedName.Any(char.IsControl))
            throw new ArgumentException("凭据名称不能包含控制字符。", nameof(Name));

        if (!Enum.IsDefined(Provider))
            throw new ArgumentException("凭据提供方无效。", nameof(Provider));
        if (!Enum.IsDefined(Kind))
            throw new ArgumentException("凭据类型无效。", nameof(Kind));

        if (!IsKindCompatibleWithProvider(Provider, Kind))
            throw new ArgumentException(
                $"{Provider} 不支持 {Kind} 凭据类型。",
                nameof(Kind));

        var normalizedSessionToken = SessionToken?.Trim() ?? string.Empty;
        if (normalizedSessionToken.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("SessionToken 不能包含换行符。", nameof(SessionToken));

        if (!IsValidHeaderName(HeaderName))
        {
            if (Kind == CredentialKind.CustomHeader)
                throw new ArgumentException("CustomHeader 需要有效的 Header 名称。", nameof(HeaderName));
            if (!string.IsNullOrWhiteSpace(HeaderName))
                throw new ArgumentException("HeaderName 仅在 CustomHeader 类型下可使用。", nameof(HeaderName));
        }

        switch (Kind)
        {
            case CredentialKind.AccessKeyPair:
                if (string.IsNullOrWhiteSpace(AccessKeyId))
                    throw new ArgumentException("AccessKeyId 不能为空。", nameof(AccessKeyId));
                if (string.IsNullOrWhiteSpace(Secret))
                    throw new ArgumentException("Secret 不能为空。", nameof(Secret));
                if (AccessKeyId.IndexOfAny(['\r', '\n']) >= 0 ||
                    Secret.IndexOfAny(['\r', '\n']) >= 0)
                    throw new ArgumentException("AccessKeyId/Secret 不能包含换行符。", nameof(Secret));
                break;

            case CredentialKind.BearerToken:
                if (string.IsNullOrWhiteSpace(Secret))
                    throw new ArgumentException("BearerToken 需要 Secret。", nameof(Secret));
                if (Secret.IndexOfAny(['\r', '\n']) >= 0)
                    throw new ArgumentException("Secret 不能包含换行符。", nameof(Secret));
                break;

            case CredentialKind.CustomHeader:
                if (string.IsNullOrWhiteSpace(HeaderName))
                    throw new ArgumentException("CustomHeader 需要 HeaderName。", nameof(HeaderName));
                if (!IsValidHeaderName(HeaderName))
                    throw new ArgumentException("HeaderName 格式不正确。", nameof(HeaderName));
                if (string.IsNullOrWhiteSpace(Secret))
                    throw new ArgumentException("CustomHeader 需要 Secret。", nameof(Secret));
                if (Secret.IndexOfAny(['\r', '\n']) >= 0)
                    throw new ArgumentException("Secret 不能包含换行符。", nameof(Secret));
                break;

            case CredentialKind.SecretValue:
                if (string.IsNullOrWhiteSpace(Secret))
                    throw new ArgumentException("SecretValue 需要 Secret。", nameof(Secret));
                if (Secret.IndexOfAny(['\r', '\n']) >= 0)
                    throw new ArgumentException("Secret 不能包含换行符。", nameof(Secret));
                if (!string.IsNullOrWhiteSpace(AccessKeyId) || !string.IsNullOrWhiteSpace(HeaderName) ||
                    !string.IsNullOrWhiteSpace(SessionToken))
                    throw new ArgumentException("SecretValue 不能包含 AccessKeyId、HeaderName 或 SessionToken。", nameof(Secret));
                break;
        }

        if (!string.IsNullOrWhiteSpace(AccessKeyId) && AccessKeyId.Any(char.IsWhiteSpace))
            throw new ArgumentException("AccessKeyId 不能包含空白字符。", nameof(AccessKeyId));
    }

    public bool IsCompatibleWith(S3ServiceType serviceType)
    {
        return serviceType switch
        {
            S3ServiceType.AmazonS3 =>
                Provider == CredentialProviderKind.AmazonWebServices && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.MinIO or S3ServiceType.Custom =>
                Provider == CredentialProviderKind.S3Compatible && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.CloudflareR2 => Provider == CredentialProviderKind.Cloudflare && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.BackblazeB2 => Provider == CredentialProviderKind.Backblaze && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.AliyunOss => Provider == CredentialProviderKind.AlibabaCloud && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.TencentCos => Provider == CredentialProviderKind.TencentCloud && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.SupabaseStorage => Provider == CredentialProviderKind.Supabase && Kind == CredentialKind.AccessKeyPair,
            S3ServiceType.GoogleCloudStorage => Provider == CredentialProviderKind.GoogleCloud && Kind == CredentialKind.AccessKeyPair,
            _ => false
        };
    }

    public bool IsCompatibleWith(string cdnProviderId)
    {
        if (string.IsNullOrWhiteSpace(cdnProviderId))
            return false;

        var providerId = cdnProviderId.Trim();
        return providerId.Equals(GenericHttpCdnProviderId, StringComparison.OrdinalIgnoreCase)
            ? Provider == CredentialProviderKind.GenericHttp
            : providerId.Equals(CdnProfile.AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase) &&
              Provider == CredentialProviderKind.AlibabaCloud &&
              Kind == CredentialKind.AccessKeyPair;
    }

    private static bool IsKindCompatibleWithProvider(
        CredentialProviderKind provider,
        CredentialKind kind) => provider switch
    {
        CredentialProviderKind.S3Compatible or
            CredentialProviderKind.AlibabaCloud or
            CredentialProviderKind.TencentCloud or
            CredentialProviderKind.Cloudflare or
            CredentialProviderKind.Backblaze or
            CredentialProviderKind.GoogleCloud or
            CredentialProviderKind.Supabase => kind == CredentialKind.AccessKeyPair,

        CredentialProviderKind.AmazonWebServices =>
            kind is CredentialKind.AccessKeyPair or CredentialKind.SecretValue,

        CredentialProviderKind.GenericHttp => kind == CredentialKind.BearerToken || kind == CredentialKind.CustomHeader,
        _ => false
    };

    private string BuildAccessKeyFingerprint(string providerLabel)
    {
        var safeAccessKey = MaskAccessKeyTail(AccessKeyId);
        return string.IsNullOrWhiteSpace(safeAccessKey)
            ? providerLabel
            : $"{providerLabel}:{safeAccessKey}";
    }

    private static string MaskAccessKeyTail(string accessKey)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
            return string.Empty;

        var trimmed = accessKey.Trim();
        if (trimmed.Length <= 4)
            return "****" + trimmed;

        return "****" + trimmed[^4..];
    }

    private static bool IsValidHeaderName(string? headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
            return false;

        const string allowedSymbols = "!#$%&'*+-.^_`|~";
        return headerName.All(character =>
            character is >= '0' and <= '9' ||
            character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z' ||
            allowedSymbols.Contains(character));
    }
}

public sealed class CredentialVault
{
    private readonly IReadOnlyDictionary<Guid, CredentialProfile> _byId;
    private readonly IReadOnlyDictionary<string, CredentialProfile> _byName;

    public CredentialVault(IEnumerable<CredentialProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var allProfiles = profiles.ToArray();
        foreach (var profile in allProfiles)
            profile.Validate();
        var errors = ValidateUniqueness(allProfiles);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        Profiles = allProfiles;
        _byId = Profiles.ToDictionary(profile => profile.Id);
        _byName = Profiles.ToDictionary(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CredentialProfile> Profiles { get; }

    public CredentialProfile? FindById(Guid id) =>
        _byId.TryGetValue(id, out var result) ? result : null;

    public CredentialProfile? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _byName.TryGetValue(name.Trim(), out var result) ? result : null;
    }

    public static IReadOnlyList<string> ValidateUniqueness(IEnumerable<CredentialProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var errors = new List<string>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            if (profile is null)
            {
                errors.Add("存在空凭据。");
                continue;
            }

            if (!ids.Add(profile.Id))
                errors.Add($"凭据 ID 重复：{profile.Id}");

            var name = profile.Name?.Trim() ?? string.Empty;
            if (!names.Add(name))
                errors.Add($"凭据名称重复：{name}");
        }

        return errors;
    }
}

public interface ICredentialStore
{
    Task<IReadOnlyList<CredentialProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        IReadOnlyCollection<CredentialProfile> credentials,
        CancellationToken cancellationToken = default);
}

public sealed record PermissionCheck(
    string Subject,
    string Name,
    PermissionCheckState State,
    string Message = "")
{
    public bool Required { get; init; } = true;
    public int? StatusCode { get; init; }
    public string ProviderCode { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
}

public sealed record PermissionCheckResult(
    Guid CredentialId,
    IReadOnlyList<PermissionCheck> Checks)
{
    public string TargetScope { get; init; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public PermissionCheckResult(Guid credentialId)
        : this(credentialId, Array.Empty<PermissionCheck>())
    {
    }

    public bool Passed => Checks.Count > 0 && Checks.All(check => check.State == PermissionCheckState.Passed);
    public bool HasIssues => Checks.Any(check => check.State != PermissionCheckState.Passed);

    public int CountByState(PermissionCheckState state) =>
        Checks.Count(check => check.State == state);
}

public sealed record PermissionCheckReport(
    IReadOnlyList<PermissionCheckResult> Results)
{
    public PermissionCheckReport()
        : this(Array.Empty<PermissionCheckResult>())
    {
    }

    public int TotalChecks => Results.Sum(result => result.Checks.Count);
    public int PassedCount => Results.Sum(result => result.CountByState(PermissionCheckState.Passed));
    public int DeniedCount => Results.Sum(result => result.CountByState(PermissionCheckState.Denied));
    public int UnsupportedCount => Results.Sum(result => result.CountByState(PermissionCheckState.Unsupported));
    public int IndeterminateCount => Results.Sum(result => result.CountByState(PermissionCheckState.Indeterminate));
    public int SkippedCount => Results.Sum(result => result.CountByState(PermissionCheckState.Skipped));
    public bool AllPassed => Results.Count > 0 && Results.All(result => result.Passed);
    public bool HasDenied => DeniedCount > 0;
}
