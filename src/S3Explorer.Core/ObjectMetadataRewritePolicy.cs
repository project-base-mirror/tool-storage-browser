namespace S3Explorer.Core;

public static class ObjectMetadataRewritePolicy
{
    public const string Warning = "保存前会重新读取当前对象并检查内容与属性冲突；ETag 不是属性版本号，检查与写入之间仍有非原子窗口，可能覆盖并发修改。请暂停其他写入者后继续。ACL 启用时会保留读取到的 grants，但对象所有者可能变为执行写入者；并发 ACL / Tags 更改也可能受影响。此操作可能产生费用与新版本，应用不会主动重试或回滚（SDK / 服务端可能重试）。";

    public static string WarningFor(S3ServiceType provider) => provider switch
    {
        S3ServiceType.GoogleCloudStorage => "GCS 保存使用当前对象 generation / metageneration 的源、目标条件，冲突时停止，不降级为普通上传。仅支持可精确保留的 ACL 或明确开启统一 Bucket 访问的对象；Hold / Retention 以服务端拒绝为准。此操作可能产生请求费用，请核对保存结果。",
        S3ServiceType.AliyunOss => Warning + " OSS 使用原生协议保留 default/显式 ACL、Storage Class 与 SSE-OSS；仅支持未启用版本控制且无 WORM 的普通对象。",
        S3ServiceType.TencentCos => Warning + " COS 使用原生协议保留 ACL、Tags、Storage Class 与 SSE-COS；原生端点或对象保护状态无法确认时停止。",
        S3ServiceType.BackblazeB2 => Warning + " B2 的访问权限属于 Bucket，不写对象 ACL 或标签。源未报告加密时可能采用当前 Bucket 默认加密；不承诺原子属性保护。",
        S3ServiceType.SupabaseStorage => Warning + " Supabase 仅编辑 Content-Type、Cache-Control、Content-Encoding 和完整可读取的字符串 Metadata。服务端复制会先固定源内容再更新目标，没有目标版本 CAS；并发上传的新文件内容也可能被覆盖，请先停止其他写入者。对象 owner 可能变为当前 S3 身份，访问继续由项目 Bucket/RLS 规则控制。",
        _ => Warning
    };

    public static bool CanEditHeader(S3ServiceType provider, string name) =>
        provider != S3ServiceType.SupabaseStorage || name is "content-type" or "cache-control" or "content-encoding";

    public static void ValidateEdits(S3ServiceType provider, ObjectWriteHeaders headers)
    {
        if (provider != S3ServiceType.SupabaseStorage) return;
        if (!string.IsNullOrEmpty(headers.ContentDisposition) || headers.ExpiresUtc is not null ||
            !string.IsNullOrEmpty(headers.ContentLanguage) || !string.IsNullOrEmpty(headers.WebsiteRedirectLocation))
            throw new NotSupportedException("Supabase S3 CopyObject 尚未完整传递 Content-Disposition、Content-Language、Expires 或网站重定向；不能保存这些更改。");
        if (headers.Metadata?.Any(pair => pair.Value.Any(character => character > 127)) == true)
            throw new NotSupportedException("Supabase S3 HEAD 不能完整返回非 ASCII Metadata 值；请使用原生管理界面编辑此值。");
    }

    public static BucketFeatureSupport For(S3ServiceType provider, ObjectProperties source)
    {
        var capability = S3ProviderCapabilityRegistry.For(provider).Object.MetadataRewrite;
        if (!capability.Supported) return capability;
        if (string.IsNullOrEmpty(source.ETag)) return BucketFeatureSupport.No("缺少 ETag，无法检查内容冲突。");
        if (provider == S3ServiceType.GoogleCloudStorage &&
            (!IsPositiveDecimal(source.ProviderGeneration) || !IsPositiveDecimal(source.ProviderMetageneration)))
            return BucketFeatureSupport.No("GCS 未返回有效 generation / metageneration，不能进行条件 Metadata 更新。");
        if (!string.IsNullOrEmpty(source.ProviderRetentionUntil))
            return BucketFeatureSupport.No("对象报告原生 Retention，当前流程不会改变其保留状态。");
        if (source.ProviderObjectType is { Length: > 0 } objectType && !objectType.Equals("Normal", StringComparison.OrdinalIgnoreCase))
            return BucketFeatureSupport.No("当前仅编辑普通对象；Appendable / Symlink 等原生特殊类型不会转换为普通对象。");
        if (provider == S3ServiceType.SupabaseStorage &&
            (!string.IsNullOrEmpty(source.ContentDisposition) || !string.IsNullOrEmpty(source.ContentLanguage) ||
             !string.IsNullOrEmpty(source.WebsiteRedirectLocation) || source.ExpiresUtc is not null ||
             !string.IsNullOrEmpty(source.ServerSideEncryptionMethod)))
            return BucketFeatureSupport.No("此 Supabase 对象含 S3 CopyObject 无法完整往返的 Header 或显式加密属性；请用原生管理界面修改。");
        if (provider == S3ServiceType.BackblazeB2 && !string.IsNullOrEmpty(source.WebsiteRedirectLocation))
            return BucketFeatureSupport.No("B2 S3 复制不能保留网站重定向属性；不会静默删除该设置。");
        if (provider == S3ServiceType.BackblazeB2 && source.Size > 5_000_000_000L)
            return BucketFeatureSupport.No("B2 单次 Metadata 复制限 5 GB；不会自动降级为分片或下载回传。");
        if (source.Size is < 0 or > ObjectMetadataCopyPolicy.MaximumBytes)
            return BucketFeatureSupport.No("原地 Metadata 编辑仅支持不超过 5 GiB 的对象。");
        if (source.MissingMetadataCount is > 0 || (!string.IsNullOrEmpty(source.ExpiresHeader) && source.ExpiresUtc is null))
            return BucketFeatureSupport.No("源 Header / Metadata 不完整或无法解析，不能安全保留。");
        if ((!string.IsNullOrEmpty(source.ServerSideEncryptionMethod) && source.ServerSideEncryptionMethod != "AES256") ||
            !string.IsNullOrEmpty(source.EncryptionKeyId) || !string.IsNullOrEmpty(source.CustomerEncryptionMethod) || source.BucketKeyEnabled == true)
            return BucketFeatureSupport.No("KMS、SSE-C 或未知加密方式不能无损原地复制。");
        if (!string.IsNullOrEmpty(source.ObjectLockMode) || source.ObjectLockRetainUntilDate is not null ||
            (!string.IsNullOrEmpty(source.ObjectLockLegalHoldStatus) && source.ObjectLockLegalHoldStatus != "OFF"))
            return BucketFeatureSupport.No("Object Lock / Retention / Legal Hold 对象不能原地编辑 Metadata。");
        return capability;
    }

    private static bool IsPositiveDecimal(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(character => character is >= '0' and <= '9') && value.Any(character => character != '0');

    public static bool Matches(ObjectProperties expected, ObjectProperties current) =>
        expected with { Metadata = current.Metadata } == current &&
        expected.Metadata.Count == current.Metadata.Count &&
        expected.Metadata.All(pair => current.Metadata.TryGetValue(pair.Key, out var value) && pair.Value == value);
}
