namespace S3Explorer.Core;

public enum S3ServiceType
{
    AmazonS3,
    MinIO,
    CloudflareR2,
    BackblazeB2,
    AliyunOss,
    TencentCos,
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
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string SessionToken { get; init; } = string.Empty;
    public AddressingStyle AddressingStyle { get; init; } = AddressingStyle.Auto;
    public bool UseHttps { get; init; } = true;
    public bool IgnoreCertificateErrors { get; init; }
    public string DefaultStorageClass { get; init; } = "STANDARD";
    public int RequestTimeoutSeconds { get; init; } = 100;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("连接名称不能为空。", nameof(Name));
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint 必须是有效的 HTTP 或 HTTPS 地址。", nameof(Endpoint));
        if (string.IsNullOrWhiteSpace(Region))
            throw new ArgumentException("Region 不能为空。", nameof(Region));
        if (string.IsNullOrWhiteSpace(AccessKey))
            throw new ArgumentException("Access Key 不能为空。", nameof(AccessKey));
        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new ArgumentException("Secret Key 不能为空。", nameof(SecretKey));
        if (RequestTimeoutSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds), "请求超时必须在 5 到 3600 秒之间。");
    }

    public static ConnectionProfile CreatePreset(S3ServiceType type) => type switch
    {
        S3ServiceType.AmazonS3 => new() { ServiceType = type, Endpoint = "https://s3.amazonaws.com", Region = "us-east-1" },
        S3ServiceType.MinIO => new() { ServiceType = type, Endpoint = "http://127.0.0.1:9000", Region = "us-east-1", AddressingStyle = AddressingStyle.PathStyle, UseHttps = false },
        S3ServiceType.CloudflareR2 => new() { ServiceType = type, Endpoint = "https://<account-id>.r2.cloudflarestorage.com", Region = "auto", AddressingStyle = AddressingStyle.PathStyle },
        S3ServiceType.BackblazeB2 => new() { ServiceType = type, Endpoint = "https://s3.<region>.backblazeb2.com", Region = "us-west-004" },
        S3ServiceType.AliyunOss => new() { ServiceType = type, Endpoint = "https://oss-cn-hangzhou.aliyuncs.com", Region = "oss-cn-hangzhou" },
        S3ServiceType.TencentCos => new() { ServiceType = type, Endpoint = "https://cos.ap-guangzhou.myqcloud.com", Region = "ap-guangzhou" },
        _ => new() { ServiceType = type, Endpoint = "https://s3.example.com", Region = "us-east-1" }
    };
}
