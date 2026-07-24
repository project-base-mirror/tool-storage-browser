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
    public bool UsesTemporarySessionCredentials => !string.IsNullOrWhiteSpace(SessionToken);
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

    public string EffectiveSignatureRegion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SignatureRegion))
                return SignatureRegion.Trim();
            if (!string.IsNullOrWhiteSpace(Region))
                return Region.Trim();
            return ServiceType is S3ServiceType.CloudflareR2 or S3ServiceType.GoogleCloudStorage
                ? "auto"
                : "us-east-1";
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
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("连接名称不能为空。", nameof(Name));

        var endpoint = EndpointCompatibility.NormalizeEndpoint(Endpoint);
        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Endpoint 不能包含查询参数或片段。", nameof(Endpoint));
        if (string.IsNullOrWhiteSpace(EffectiveSignatureRegion))
            throw new ArgumentException("签名 Region 不能为空。", nameof(SignatureRegion));
        if (string.IsNullOrWhiteSpace(AccessKey))
            throw new ArgumentException("Access Key 不能为空。", nameof(AccessKey));
        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new ArgumentException("Secret Key 不能为空。", nameof(SecretKey));
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

    public static ConnectionProfile CreatePreset(S3ServiceType type) => type switch
    {
        S3ServiceType.AmazonS3 => new() { ServiceType = type, Endpoint = "https://s3.amazonaws.com", Region = "us-east-1" },
        S3ServiceType.MinIO => new() { ServiceType = type, Endpoint = "http://127.0.0.1:9000", Region = "us-east-1", AddressingStyle = AddressingStyle.PathStyle, UseHttps = false },
        S3ServiceType.CloudflareR2 => new() { ServiceType = type, Endpoint = "https://<account-id>.r2.cloudflarestorage.com", Region = "auto", SignatureRegion = "auto", AddressingStyle = AddressingStyle.PathStyle },
        S3ServiceType.BackblazeB2 => new() { ServiceType = type, Endpoint = "https://s3.us-west-004.backblazeb2.com", Region = "us-west-004" },
        S3ServiceType.AliyunOss => new() { ServiceType = type, Endpoint = "https://oss-cn-hangzhou.aliyuncs.com", Region = "oss-cn-hangzhou" },
        S3ServiceType.TencentCos => new() { ServiceType = type, Endpoint = "https://cos.ap-guangzhou.myqcloud.com", Region = "ap-guangzhou" },
        S3ServiceType.GoogleCloudStorage => new() { ServiceType = type, Endpoint = "https://storage.googleapis.com", Region = "auto", SignatureRegion = "auto", AddressingStyle = AddressingStyle.PathStyle },
        S3ServiceType.SupabaseStorage => new() { ServiceType = type, Endpoint = "https://<project-ref>.supabase.co/storage/v1/s3", Region = "us-east-1", AddressingStyle = AddressingStyle.PathStyle },
        _ => new() { ServiceType = type, Endpoint = "https://s3.example.com", Region = "us-east-1" }
    };
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
