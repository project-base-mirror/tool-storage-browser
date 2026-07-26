namespace S3Explorer.Core;

public enum S3AccountCategory
{
    AmazonS3,
    S3Compatible,
    GoogleCloudStorage
}

public enum RegionInputMode
{
    Hidden,
    Optional,
    Required
}

public sealed record S3ProviderDefinition(
    S3ServiceType ServiceType,
    S3AccountCategory Category,
    string DisplayName,
    RegionInputMode RegionInput,
    string DefaultRegion,
    string DefaultEndpoint,
    AddressingStyle DefaultAddressingStyle,
    bool DefaultUseHttps = true,
    string? DefaultSigningRegion = null)
{
    public string EffectiveDefaultSigningRegion => DefaultSigningRegion ?? DefaultRegion;
}

public static class S3ProviderCatalog
{
    private static readonly IReadOnlyDictionary<S3ServiceType, S3ProviderDefinition> Definitions =
        new[]
        {
            new S3ProviderDefinition(
                S3ServiceType.AmazonS3, S3AccountCategory.AmazonS3, "Amazon S3",
                RegionInputMode.Required, "auto", "https://s3.amazonaws.com", AddressingStyle.Auto,
                DefaultSigningRegion: "us-east-1"),
            new S3ProviderDefinition(
                S3ServiceType.MinIO, S3AccountCategory.S3Compatible, "MinIO",
                RegionInputMode.Hidden, "us-east-1", "http://127.0.0.1:9000", AddressingStyle.PathStyle, false),
            new S3ProviderDefinition(
                S3ServiceType.CloudflareR2, S3AccountCategory.S3Compatible, "Cloudflare R2",
                RegionInputMode.Hidden, "auto", "https://<account-id>.r2.cloudflarestorage.com", AddressingStyle.PathStyle),
            new S3ProviderDefinition(
                S3ServiceType.BackblazeB2, S3AccountCategory.S3Compatible, "Backblaze B2",
                RegionInputMode.Required, "us-west-004", "https://s3.us-west-004.backblazeb2.com", AddressingStyle.Auto),
            new S3ProviderDefinition(
                S3ServiceType.AliyunOss, S3AccountCategory.S3Compatible, "阿里云 OSS",
                RegionInputMode.Required, "oss-cn-hangzhou", "https://oss-cn-hangzhou.aliyuncs.com", AddressingStyle.Auto),
            new S3ProviderDefinition(
                S3ServiceType.TencentCos, S3AccountCategory.S3Compatible, "腾讯云 COS",
                RegionInputMode.Required, "ap-guangzhou", "https://cos.ap-guangzhou.myqcloud.com", AddressingStyle.Auto),
            new S3ProviderDefinition(
                S3ServiceType.SupabaseStorage, S3AccountCategory.S3Compatible, "Supabase Storage",
                RegionInputMode.Hidden, "us-east-1", "https://<project-ref>.supabase.co/storage/v1/s3", AddressingStyle.PathStyle),
            new S3ProviderDefinition(
                S3ServiceType.Custom, S3AccountCategory.S3Compatible, "其他 S3 兼容存储",
                RegionInputMode.Optional, "us-east-1", "https://s3.example.com", AddressingStyle.Auto),
            new S3ProviderDefinition(
                S3ServiceType.GoogleCloudStorage, S3AccountCategory.GoogleCloudStorage, "Google Cloud Storage",
                RegionInputMode.Hidden, "auto", "https://storage.googleapis.com", AddressingStyle.PathStyle)
        }.ToDictionary(item => item.ServiceType);

    public static IReadOnlyList<S3ProviderDefinition> All { get; } =
        Definitions.Values
            .OrderBy(item => item.Category)
            .ThenBy(item => item.ServiceType == S3ServiceType.Custom ? 1 : 0)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .ToArray();

    public static IReadOnlyList<S3ProviderDefinition> CompatibleProviders { get; } =
        All.Where(item => item.Category == S3AccountCategory.S3Compatible).ToArray();

    public static S3ProviderDefinition Get(S3ServiceType serviceType) =>
        Definitions.TryGetValue(serviceType, out var definition)
            ? definition
            : Definitions[S3ServiceType.Custom];

    public static S3ServiceType DefaultServiceType(S3AccountCategory category) => category switch
    {
        S3AccountCategory.AmazonS3 => S3ServiceType.AmazonS3,
        S3AccountCategory.GoogleCloudStorage => S3ServiceType.GoogleCloudStorage,
        _ => S3ServiceType.Custom
    };

    public static string CategoryDisplayName(S3AccountCategory category) => category switch
    {
        S3AccountCategory.AmazonS3 => "Amazon S3",
        S3AccountCategory.S3Compatible => "S3 兼容存储",
        S3AccountCategory.GoogleCloudStorage => "Google Cloud Storage",
        _ => category.ToString()
    };

    public static string ResolveSigningRegion(S3ServiceType serviceType, string? userInput)
    {
        var definition = Get(serviceType);
        if (definition.RegionInput == RegionInputMode.Hidden)
            return definition.EffectiveDefaultSigningRegion;

        var value = userInput?.Trim() ?? string.Empty;
        if (value.Length == 0 || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            return definition.EffectiveDefaultSigningRegion;
        return value;
    }
}
