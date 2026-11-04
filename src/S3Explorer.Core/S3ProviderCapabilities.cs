namespace S3Explorer.Core;

public sealed record S3ProviderCapabilities(
    S3ServiceType ServiceType,
    BucketCapabilities Bucket,
    ObjectCapabilities Object)
{
    // A header being accepted is not evidence that the provider enforces it.
    // Automatic move is enabled only for the verified atomic delete contract.
    public BucketFeatureSupport ConditionalDelete => ServiceType == S3ServiceType.AmazonS3
        ? BucketFeatureSupport.Yes("AWS S3 支持 DeleteObject If-Match 条件。")
        : BucketFeatureSupport.No("此服务商未验证可靠的条件删除，已禁用自动移动；请复制后人工核对再删除源对象。");

}

public static class S3ProviderCapabilityRegistry
{
    private static BucketFeatureSupport UnsafeMetadataRewrite => BucketFeatureSupport.No(
        "暂不支持安全的原地 Metadata 替换：需保证对象并发身份并完整保留加密与未编辑属性。请使用服务商原生工具。");

    private static readonly IReadOnlyDictionary<S3ServiceType, S3ProviderCapabilities> Entries =
        Enum.GetValues<S3ServiceType>().ToDictionary(value => value, Create);
    private static readonly IReadOnlyCollection<S3ProviderCapabilities> RegisteredCapabilities =
        Entries.Values.ToArray();

    public static IReadOnlyCollection<S3ProviderCapabilities> All => RegisteredCapabilities;

    public static S3ProviderCapabilities For(S3ServiceType serviceType) =>
        Entries.TryGetValue(serviceType, out var capabilities)
            ? capabilities
            : throw new ArgumentOutOfRangeException(nameof(serviceType), serviceType, "未知的 S3 Provider 类型。");

    private static S3ProviderCapabilities Create(S3ServiceType serviceType) => serviceType switch
    {
        S3ServiceType.AmazonS3 => new(
            serviceType,
            new BucketCapabilities(
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.ReadOnly("当前客户端只读取 Bucket Object Lock 配置；对象 Retention 与 Legal Hold 可单独管理"),
                BucketFeatureSupport.No("当前客户端尚未实现 Bucket Logging 读取与编辑"),
                BucketFeatureSupport.Yes()),
            new ObjectCapabilities(
                BucketFeatureSupport.Yes(),
                UnsafeMetadataRewrite,
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes(),
                BucketFeatureSupport.Yes())),

        S3ServiceType.MinIO => new(
            serviceType,
            new BucketCapabilities(
                BucketFeatureSupport.Yes("MinIO 支持 Bucket Policy"),
                BucketFeatureSupport.Yes("MinIO ACL 仅支持 private；公开访问请使用 Bucket Policy"),
                BucketFeatureSupport.No("MinIO 不提供 AWS Public Access Block API"),
                BucketFeatureSupport.No("MinIO 不提供 AWS Object Ownership API"),
                BucketFeatureSupport.No("MinIO Community 不支持 Bucket 级 CORS；请使用服务端全局 CORS，AIStor 用户可改用自定义兼容类型"),
                BucketFeatureSupport.Yes("MinIO 支持 S3 Versioning API"),
                BucketFeatureSupport.Yes("MinIO 支持 Bucket 默认加密；服务端必须已配置密钥管理"),
                BucketFeatureSupport.Yes("MinIO 支持 SSE-KMS；保存前请确认服务端 KMS 与 Key 已就绪"),
                BucketFeatureSupport.Yes("MinIO 支持 S3 Bucket Tagging API"),
                BucketFeatureSupport.Yes("MinIO 已验证对象过期与非当前版本生命周期规则"),
                BucketFeatureSupport.No("MinIO 存储类型转换需要服务端远程分层配置，当前客户端未启用"),
                BucketFeatureSupport.No("当前锁定的 MinIO 版本不会可靠保存 AbortIncompleteMultipartUpload 规则"),
                BucketFeatureSupport.No("当前客户端尚未验证 MinIO Object Lock 创建与管理流程"),
                BucketFeatureSupport.No("MinIO 不提供 AWS Bucket Logging API"),
                BucketFeatureSupport.Yes()),
            new ObjectCapabilities(
                BucketFeatureSupport.Yes("MinIO 支持 S3 Object Tagging API"),
                UnsafeMetadataRewrite,
                BucketFeatureSupport.Yes("MinIO Object ACL 仅支持 private；公开访问请使用 Bucket Policy"),
                BucketFeatureSupport.Yes("使用当前连接的签名配置生成预签名 URL"),
                BucketFeatureSupport.Yes("MinIO 已验证对象版本浏览、下载、恢复与删除"),
                BucketFeatureSupport.No("当前客户端尚未验证 MinIO Object Lock 创建与管理流程"))),

        S3ServiceType.AliyunOss => Compatible(
            serviceType,
            new ObjectCapabilities(
                BucketFeatureSupport.Yes("阿里云 OSS S3 兼容接口支持对象 Tagging"),
                UnsafeMetadataRewrite,
                BucketFeatureSupport.No("阿里云 OSS S3 兼容接口的对象 ACL 尚未纳入持续验证"),
                BucketFeatureSupport.Yes("使用当前连接的签名配置生成预签名 URL"),
                BucketFeatureSupport.No("阿里云 OSS S3 兼容接口的对象版本操作尚未验证"),
                BucketFeatureSupport.No("阿里云 OSS S3 兼容接口的 Object Lock 操作尚未验证"))),

        _ => Compatible(serviceType, UnverifiedObjectCapabilities(serviceType))
    };

    private static S3ProviderCapabilities Compatible(
        S3ServiceType serviceType,
        ObjectCapabilities objectCapabilities)
    {
        var name = S3ProviderCatalog.Get(serviceType).DisplayName;
        return new S3ProviderCapabilities(
            serviceType,
            new BucketCapabilities(
                BucketFeatureSupport.No($"{name} 的 Bucket Policy API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Bucket ACL API 尚未验证"),
                BucketFeatureSupport.No("仅 AWS S3 支持 Public Access Block 控制项"),
                BucketFeatureSupport.No("仅 AWS S3 支持 Object Ownership 控制项"),
                BucketFeatureSupport.No($"{name} 的 Bucket CORS API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Bucket Versioning API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Bucket 加密配置 API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的 SSE-KMS 配置 API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Bucket Tagging API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的生命周期 API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的生命周期存储类型转换尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Multipart 生命周期清理尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Object Lock API 尚未验证"),
                BucketFeatureSupport.No($"{name} 的 Bucket Logging API 尚未验证"),
                BucketFeatureSupport.Yes("普通对象与未完成分片可安全清理；版本能力需扫描确认")),
            objectCapabilities);
    }

    private static ObjectCapabilities UnverifiedObjectCapabilities(S3ServiceType serviceType)
    {
        var name = S3ProviderCatalog.Get(serviceType).DisplayName;
        return new ObjectCapabilities(
            BucketFeatureSupport.No($"{name} 的对象 Tagging API 尚未验证"),
            BucketFeatureSupport.No($"{name} 的对象 Metadata 原地替换尚未验证"),
            BucketFeatureSupport.No($"{name} 的对象 ACL API 尚未验证"),
            BucketFeatureSupport.Yes("使用当前连接的签名配置生成预签名 URL"),
            BucketFeatureSupport.No($"{name} 的对象版本操作尚未验证"),
            BucketFeatureSupport.No($"{name} 的 Object Lock 操作尚未验证"));
    }
}
