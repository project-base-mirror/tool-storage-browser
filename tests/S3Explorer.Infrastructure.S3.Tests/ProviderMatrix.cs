using S3Explorer.Core;
using System.Text.Json;

namespace S3Explorer.Infrastructure.S3.Tests;

internal enum ProviderMatrixStatus
{
    NotConfigured,
    Skipped,
    Passed,
    Failed
}

internal sealed record ProviderMatrixCase(
    string Id,
    string DisplayName,
    S3ServiceType ServiceType,
    string EnvironmentPrefix,
    string DefaultEndpoint,
    string DefaultRegion,
    AddressingStyle AddressingStyle,
    bool Required)
{
    public static IReadOnlyList<ProviderMatrixCase> All { get; } =
    [
        new("minio", "MinIO", S3ServiceType.MinIO, "S3EXPLORER_MINIO", "http://127.0.0.1:9000", "us-east-1", AddressingStyle.PathStyle, true),
        new("aws", "Amazon S3", S3ServiceType.AmazonS3, "S3EXPLORER_AWS", "https://s3.amazonaws.com", "us-east-1", AddressingStyle.Auto, false),
        new("tencent-cos", "Tencent COS", S3ServiceType.TencentCos, "S3EXPLORER_TENCENT_COS", "", "ap-guangzhou", AddressingStyle.Auto, false),
        new("aliyun-oss", "Aliyun OSS S3", S3ServiceType.AliyunOss, "S3EXPLORER_ALIYUN_OSS", "", "oss-cn-hangzhou", AddressingStyle.Auto, false),
        new("cloudflare-r2", "Cloudflare R2", S3ServiceType.CloudflareR2, "S3EXPLORER_CLOUDFLARE_R2", "", "auto", AddressingStyle.PathStyle, false),
        new("backblaze-b2", "Backblaze B2", S3ServiceType.BackblazeB2, "S3EXPLORER_BACKBLAZE_B2", "", "us-west-004", AddressingStyle.Auto, false),
        new("google-cloud-storage", "Google Cloud Storage", S3ServiceType.GoogleCloudStorage, "S3EXPLORER_GCS", "https://storage.googleapis.com", "auto", AddressingStyle.PathStyle, false),
        new("supabase-storage", "Supabase Storage", S3ServiceType.SupabaseStorage, "S3EXPLORER_SUPABASE", "", "us-east-1", AddressingStyle.PathStyle, false)
    ];

    public static ProviderMatrixCase Selected()
    {
        var selected = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER")?.Trim();
        return string.IsNullOrEmpty(selected)
            ? All[0]
            : All.Single(item => string.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase));
    }

    public ProviderMatrixConfiguration Resolve()
    {
        string? Read(string name) => Environment.GetEnvironmentVariable($"{EnvironmentPrefix}_{name}");

        var storedProfileName = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROFILE")?.Trim();
        if (!string.IsNullOrWhiteSpace(storedProfileName))
        {
            var profiles = new JsonProfileStore(new DpapiCredentialProtector())
                .LoadAsync().GetAwaiter().GetResult();
            var stored = profiles.SingleOrDefault(profile =>
                string.Equals(profile.Name, storedProfileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"未找到矩阵测试连接：{storedProfileName}");
            if (stored.ServiceType != ServiceType)
                throw new InvalidOperationException(
                    $"矩阵测试连接 {storedProfileName} 的类型是 {stored.ServiceType}，预期 {ServiceType}。");
            return new ProviderMatrixConfiguration(
                this,
                true,
                stored.Endpoint,
                stored.Region,
                stored.AccessKey,
                stored.SecretKey,
                stored.SessionToken,
                stored.IgnoreCertificateErrors,
                stored.DefaultBucket,
                stored);
        }

        var endpoint = Read("ENDPOINT");
        var accessKey = Read("ACCESS_KEY");
        var secretKey = Read("SECRET_KEY");
        var region = Read("REGION");

        if (Id == "minio")
        {
            endpoint ??= Environment.GetEnvironmentVariable("S3EXPLORER_TEST_ENDPOINT");
            accessKey ??= Environment.GetEnvironmentVariable("S3EXPLORER_TEST_ACCESS_KEY");
            secretKey ??= Environment.GetEnvironmentVariable("S3EXPLORER_TEST_SECRET_KEY");
            region ??= Environment.GetEnvironmentVariable("S3EXPLORER_TEST_REGION");
        }

        endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        region = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        var configured =
            !string.IsNullOrWhiteSpace(endpoint) &&
            !string.IsNullOrWhiteSpace(accessKey) &&
            !string.IsNullOrWhiteSpace(secretKey);

        return new ProviderMatrixConfiguration(
            this,
            configured,
            endpoint,
            region,
            accessKey ?? string.Empty,
            secretKey ?? string.Empty,
            Read("SESSION_TOKEN") ?? string.Empty,
            bool.TryParse(Read("IGNORE_CERTIFICATE_ERRORS"), out var ignoreCertificateErrors) && ignoreCertificateErrors,
            Read("KNOWN_BUCKET"),
            null);
    }
}

internal sealed record ProviderMatrixConfiguration(
    ProviderMatrixCase Case,
    bool IsConfigured,
    string Endpoint,
    string Region,
    string AccessKey,
    string SecretKey,
    string SessionToken,
    bool IgnoreCertificateErrors,
    string? KnownBucket,
    ConnectionProfile? StoredProfile)
{
    public ConnectionProfile CreateProfile(bool enableMultiObjectDelete = true, bool enableMultipartCopy = true) =>
        (StoredProfile ?? new ConnectionProfile
        {
            Name = $"Matrix {Case.DisplayName}",
            ServiceType = Case.ServiceType,
            Endpoint = Endpoint,
            Region = Region,
            SignatureRegion = Region,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            SessionToken = SessionToken,
            AddressingStyle = Case.AddressingStyle,
            UseHttps = Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            IgnoreCertificateErrors = IgnoreCertificateErrors
        }) with
        {
            EnableMultiObjectDelete = enableMultiObjectDelete,
            EnableMultipartCopy = enableMultipartCopy
        };

    public string ToReportJson(ProviderMatrixStatus status, string? message = null) =>
        JsonSerializer.Serialize(new
        {
            provider = Case.Id,
            displayName = Case.DisplayName,
            required = Case.Required,
            status = status.ToString(),
            endpointConfigured = !string.IsNullOrWhiteSpace(Endpoint),
            credentialsConfigured = IsConfigured,
            message
        });
}
