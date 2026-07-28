namespace S3Explorer.Core;

public enum S3ServiceType
{
    AmazonS3,
    MinIO,
    CloudflareR2,
    BackblazeB2,
    AliyunOss,
    TencentCos,
    GoogleCloudStorage,
    SupabaseStorage,
    Custom
}

public enum AddressingStyle
{
    Auto,
    VirtualHosted,
    PathStyle
}

public enum ConnectionHealthStatus
{
    Unknown,
    Healthy,
    Failed
}

public enum CredentialSourceKind
{
    StoredKeys,
    AwsSharedProfile,
    AwsEnvironmentVariables,
    AwsContainerRole,
    AwsInstanceRole,
    AwsDefaultChain
}

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public S3ServiceType ServiceType { get; init; } = S3ServiceType.AmazonS3;
    public string Endpoint { get; init; } = "https://s3.amazonaws.com";
    public string Region { get; init; } = "us-east-1";
    public string SignatureRegion { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string SessionToken { get; init; } = string.Empty;
    public CredentialSourceKind CredentialSource { get; init; } = CredentialSourceKind.StoredKeys;
    public string AwsProfileName { get; init; } = string.Empty;
    public bool UsesTemporarySessionCredentials =>
        CredentialSource == CredentialSourceKind.StoredKeys &&
        !string.IsNullOrWhiteSpace(SessionToken);
    public bool HasStoredCredentials =>
        CredentialSource == CredentialSourceKind.StoredKeys &&
        !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey);
    public bool HasCredentialConfiguration => CredentialSource switch
    {
        CredentialSourceKind.StoredKeys => HasStoredCredentials,
        CredentialSourceKind.AwsSharedProfile => !string.IsNullOrWhiteSpace(AwsProfileName),
        _ => true
    };
    public bool UsesExternalAwsCredentials => CredentialSource != CredentialSourceKind.StoredKeys;
    public string CredentialSourceDisplayName => CredentialSource switch
    {
        CredentialSourceKind.StoredKeys => UsesTemporarySessionCredentials
            ? "已保存密钥（Session Token）"
            : "已保存密钥",
        CredentialSourceKind.AwsSharedProfile => $"AWS shared profile：{(AwsProfileName ?? string.Empty).Trim()}",
        CredentialSourceKind.AwsEnvironmentVariables => "AWS 环境变量",
        CredentialSourceKind.AwsContainerRole => "AWS 容器角色",
        CredentialSourceKind.AwsInstanceRole => "AWS EC2 实例角色",
        CredentialSourceKind.AwsDefaultChain => "AWS 默认凭据链",
        _ => CredentialSource.ToString()
    };
    public AddressingStyle AddressingStyle { get; init; } = AddressingStyle.Auto;
    public bool UseHttps { get; init; } = true;
    public bool IgnoreCertificateErrors { get; init; }
    public string CustomHostHeader { get; init; } = string.Empty;
    public bool FollowTemporaryRedirects { get; init; } = true;
    public bool EnableMultiObjectDelete { get; init; } = true;
    public bool EnableMultipartCopy { get; init; } = true;
    public string DefaultStorageClass { get; init; } = "STANDARD";
    public int RequestTimeoutSeconds { get; init; } = 100;
    public int ConnectionTimeoutSeconds { get; init; } = 10;
    public string DefaultBucket { get; init; } = string.Empty;
    public IReadOnlyList<string> ExternalBuckets { get; init; } = Array.Empty<string>();
    public ConnectionHealthStatus HealthStatus { get; init; }
    public DateTimeOffset? LastConnectionCheckedAtUtc { get; init; }
    public DateTimeOffset? LastConnectionSucceededAtUtc { get; init; }

    public string EffectiveSignatureRegion
    {
        get
        {
            var signatureRegion = SignatureRegion?.Trim() ?? string.Empty;
            if (signatureRegion.Length > 0 && !string.Equals(signatureRegion, "auto", StringComparison.OrdinalIgnoreCase))
                return signatureRegion;

            var region = Region?.Trim() ?? string.Empty;
            if (region.Length > 0 && !string.Equals(region, "auto", StringComparison.OrdinalIgnoreCase))
                return region;

            return S3ProviderCatalog.Get(ServiceType).EffectiveDefaultSigningRegion;
        }
    }

    public IReadOnlyList<string> KnownBuckets =>
        new[] { DefaultBucket }
            .Concat(ExternalBuckets ?? Array.Empty<string>())
            .Select(NormalizeKnownBucket)
            .Where(bucket => bucket.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public Uri NormalizedEndpoint => EndpointCompatibility.NormalizeEndpoint(Endpoint);

    public void Validate()
    {
        ValidateConfiguration();
        if (CredentialSource == CredentialSourceKind.StoredKeys)
        {
            if (string.IsNullOrWhiteSpace(AccessKey))
                throw new ArgumentException("Access Key 不能为空。", nameof(AccessKey));
            if (string.IsNullOrWhiteSpace(SecretKey))
                throw new ArgumentException("Secret Key 不能为空。", nameof(SecretKey));
        }
    }

    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("连接名称不能为空。", nameof(Name));

        var endpoint = EndpointCompatibility.NormalizeEndpoint(Endpoint);
        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Endpoint 不能包含查询参数或片段。", nameof(Endpoint));
        EndpointCompatibility.ValidateForService(ServiceType, endpoint);
        if (string.IsNullOrWhiteSpace(EffectiveSignatureRegion))
            throw new ArgumentException("签名 Region 不能为空。", nameof(SignatureRegion));
        if (CredentialSource != CredentialSourceKind.StoredKeys && ServiceType != S3ServiceType.AmazonS3)
            throw new ArgumentException(
                "AWS 外部凭据来源仅适用于 Amazon S3；S3-compatible 连接必须使用明确保存的密钥。",
                nameof(CredentialSource));
        if (CredentialSource == CredentialSourceKind.AwsSharedProfile)
        {
            var profileName = AwsProfileName?.Trim() ?? string.Empty;
            if (profileName.Length == 0)
                throw new ArgumentException("选择 AWS shared profile 时必须填写 Profile 名称。", nameof(AwsProfileName));
            if (profileName.Any(char.IsControl))
                throw new ArgumentException("AWS Profile 名称不能包含控制字符。", nameof(AwsProfileName));
        }
        if (RequestTimeoutSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds), "请求超时必须在 5 到 3600 秒之间。");
        if (ConnectionTimeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeoutSeconds), "连接超时必须在 1 到 120 秒之间。");
        foreach (var bucket in KnownBuckets)
            ValidateKnownBucket(bucket);

        EndpointCompatibility.ValidateHostHeader(CustomHostHeader);
    }

    private static string NormalizeKnownBucket(string? bucket) => bucket?.Trim() ?? string.Empty;

    private static void ValidateKnownBucket(string bucket)
    {
        if (bucket.Any(char.IsControl) || bucket.Contains('/') || bucket.Contains('\\'))
            throw new ArgumentException($"Bucket 名称无效：{bucket}", nameof(ExternalBuckets));
    }

    public static ConnectionProfile CreatePreset(S3ServiceType type)
    {
        var definition = S3ProviderCatalog.Get(type);
        return new ConnectionProfile
        {
            ServiceType = type,
            Endpoint = definition.DefaultEndpoint,
            Region = definition.DefaultRegion,
            SignatureRegion = definition.EffectiveDefaultSigningRegion,
            AddressingStyle = definition.DefaultAddressingStyle,
            UseHttps = definition.DefaultUseHttps
        };
    }
}

public static class EndpointCompatibility
{
    public static Uri NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint 必须是有效的 HTTP 或 HTTPS 地址。", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Endpoint 必须包含主机名。", nameof(endpoint));

        var builder = new UriBuilder(uri)
        {
            Path = NormalizeBasePath(uri.AbsolutePath)
        };
        return builder.Uri;
    }

    public static string NormalizeServiceUrl(string endpoint)
    {
        var uri = NormalizeEndpoint(endpoint);
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string NormalizeServiceUrl(S3ServiceType serviceType, string endpoint)
    {
        var uri = NormalizeEndpoint(endpoint);
        if (serviceType == S3ServiceType.MinIO && uri.AbsolutePath != "/")
        {
            uri = new UriBuilder(uri)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static void ValidateForService(S3ServiceType serviceType, Uri endpoint)
    {
        if (serviceType != S3ServiceType.MinIO)
            return;

        var firstSegment = endpoint.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (endpoint.Port == 9001 ||
            string.Equals(firstSegment, "browser", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, "login", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "MinIO Endpoint 必须指向 S3 API 端口（默认 9000），不能使用 Console 端口或 /browser、/login 地址。",
                nameof(endpoint));
        }
    }

    public static void ValidateHostHeader(string hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
            return;

        var value = hostHeader.Trim();
        if (value.Any(char.IsWhiteSpace) || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("自定义 Host Header 只能包含主机名和可选端口。", nameof(hostHeader));

        if (!Uri.TryCreate($"http://{value}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.AbsolutePath != "/")
            throw new ArgumentException("自定义 Host Header 格式无效。", nameof(hostHeader));
    }

    private static string NormalizeBasePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segments);
    }
}
