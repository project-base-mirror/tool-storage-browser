using System.Text;

namespace S3Explorer.Core;

public sealed record ObjectTag(string Key, string Value);

public static class ObjectTagValidator
{
    public static IReadOnlyList<ObjectTag> Validate(IEnumerable<ObjectTag>? tags)
    {
        var result = (tags ?? [])
            .Select(tag => new ObjectTag(tag.Key?.Trim() ?? string.Empty, tag.Value?.Trim() ?? string.Empty))
            .Where(tag => tag.Key.Length > 0 || tag.Value.Length > 0)
            .ToArray();
        if (result.Length > 10)
            throw new ArgumentException("对象最多允许 10 个 Tag。", nameof(tags));
        if (result.Any(tag => tag.Key.Length is < 1 or > 128))
            throw new ArgumentException("Tag Key 长度必须为 1–128 个字符。", nameof(tags));
        if (result.Any(tag => tag.Value.Length > 256))
            throw new ArgumentException("Tag Value 长度不能超过 256 个字符。", nameof(tags));
        if (result.Any(tag => tag.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Tag Key 不能使用保留前缀 aws:。", nameof(tags));
        var duplicate = result.GroupBy(tag => tag.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new ArgumentException($"Tag Key “{duplicate}”重复。", nameof(tags));
        return result;
    }
}

public sealed record ObjectWriteHeaders(
    string? ContentType = null,
    string? CacheControl = null,
    string? ContentEncoding = null,
    string? ContentDisposition = null,
    DateTimeOffset? ExpiresUtc = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<ObjectTag>? Tags = null)
{
    public static ObjectWriteHeaders Empty { get; } = new();

    public ObjectWriteHeaders ValidateAndNormalize()
    {
        var metadata = ObjectMetadataValidator.Validate(Metadata);
        var tags = ObjectTagValidator.Validate(Tags);
        return this with
        {
            ContentType = NormalizeHeader(ContentType, nameof(ContentType)),
            CacheControl = NormalizeHeader(CacheControl, nameof(CacheControl)),
            ContentEncoding = NormalizeHeader(ContentEncoding, nameof(ContentEncoding)),
            ContentDisposition = NormalizeHeader(ContentDisposition, nameof(ContentDisposition)),
            ExpiresUtc = ExpiresUtc?.ToUniversalTime(),
            Metadata = metadata,
            Tags = tags
        };
    }

    private static string? NormalizeHeader(string? value, string name)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException($"{name} 不能包含换行符。", name);
        return normalized;
    }
}

public static class ObjectMetadataValidator
{
    private const int MaximumMetadataBytes = 2 * 1024;

    public static IReadOnlyDictionary<string, string> Validate(
        IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            var key = NormalizeKey(pair.Key);
            var value = pair.Value?.Trim() ?? string.Empty;
            if (value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException($"Metadata “{key}”的值不能包含换行符。", nameof(metadata));
            if (!result.TryAdd(key, value))
                throw new ArgumentException($"Metadata Key “{key}”重复。", nameof(metadata));
        }

        var bytes = result.Sum(pair =>
            Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value));
        if (bytes > MaximumMetadataBytes)
            throw new ArgumentException("对象自定义 Metadata 的 Key 与 Value 总计不能超过 2 KiB。", nameof(metadata));
        return result;
    }

    private static string NormalizeKey(string? value)
    {
        var key = value?.Trim() ?? string.Empty;
        const string prefix = "x-amz-meta-";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            key = key[prefix.Length..];
        if (key.Length == 0)
            throw new ArgumentException("Metadata Key 不能为空。", nameof(value));
        if (key.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')))
            throw new ArgumentException($"Metadata Key “{key}”包含不支持的字符。", nameof(value));
        return key;
    }
}

public sealed record ObjectCapabilities(
    BucketFeatureSupport Tagging,
    BucketFeatureSupport MetadataRewrite,
    BucketFeatureSupport Acl,
    BucketFeatureSupport PresignedUrl,
    BucketFeatureSupport VersionOperations,
    BucketFeatureSupport ObjectLock)
{
    public BucketFeatureSupport MetadataCopy { get; init; } = BucketFeatureSupport.No(
        "此服务商尚未验证 Metadata 快照的非覆盖复制；原地替换仍不可用。");
}

public static class ObjectMetadataCopyPolicy
{
    public const long MaximumBytes = 5L * 1024 * 1024 * 1024;

    public static BucketFeatureSupport For(S3ServiceType provider, ObjectProperties source)
    {
        var capability = S3ProviderCapabilityRegistry.For(provider).Object.MetadataCopy;
        if (!capability.Supported) return capability;
        if (string.IsNullOrWhiteSpace(source.VersionId) || source.VersionId == "null" ||
            string.IsNullOrWhiteSpace(source.ETag))
            return BucketFeatureSupport.No("另存为新对象需要 AWS 非 null 的固定版本 ID 与 ETag；请先启用版本控制并重新上传或选择已有固定版本的对象。");
        if (source.Size is < 0 or > MaximumBytes)
            return BucketFeatureSupport.No("Metadata 快照另存目前仅支持不超过 5 GiB 的对象，不会降级为无法完整保留属性的分片复制。");
        if (source.MissingMetadataCount is > 0)
            return BucketFeatureSupport.No("服务端未返回全部 Metadata，不能安全地保存不完整快照。");
        if (!string.IsNullOrEmpty(source.ExpiresHeader) && source.ExpiresUtc is null)
            return BucketFeatureSupport.No("源对象的 Expires 无法无损解析，不能保存不完整的 Header 快照。");
        if (source.ServerSideEncryptionMethod != "AES256" ||
            !string.IsNullOrEmpty(source.EncryptionKeyId) ||
            !string.IsNullOrEmpty(source.CustomerEncryptionMethod) || source.BucketKeyEnabled == true)
            return BucketFeatureSupport.No("Metadata 快照另存仅支持明确的 SSE-S3 (AES256) 加密；KMS、SSE-C 和未知加密状态均不会复制。");
        if (!string.IsNullOrEmpty(source.ObjectLockMode) || source.ObjectLockRetainUntilDate is not null ||
            (!string.IsNullOrEmpty(source.ObjectLockLegalHoldStatus) && source.ObjectLockLegalHoldStatus != "OFF"))
            return BucketFeatureSupport.No("受 Object Lock / Retention / Legal Hold 保护的对象不能使用 Metadata 快照另存。");
        return capability;
    }

    public static void ValidateDestination(ObjectProperties source, string destinationKey)
    {
        if (string.IsNullOrEmpty(destinationKey) || Encoding.UTF8.GetByteCount(destinationKey) > 1024 ||
            destinationKey.IndexOf('\0') >= 0)
            throw new ArgumentException("目标 Key 必须为 1–1024 个 UTF-8 字节，且不能包含空字符。", nameof(destinationKey));
        if (string.Equals(source.Key, destinationKey, StringComparison.Ordinal))
            throw new ArgumentException("必须使用不同的新 Key；不会替换或删除源对象。", nameof(destinationKey));
    }
}
