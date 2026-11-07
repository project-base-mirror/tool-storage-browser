using System.Text;

namespace S3Explorer.Core;

public sealed record ObjectTag(string Key, string Value);

public static class ObjectTagValidator
{
    public static IReadOnlyList<ObjectTag> Validate(IEnumerable<ObjectTag>? tags) => Validate(tags, preserveValues: false);

    public static IReadOnlyList<ObjectTag> ValidatePreservingValues(IEnumerable<ObjectTag>? tags) => Validate(tags, preserveValues: true);

    private static IReadOnlyList<ObjectTag> Validate(IEnumerable<ObjectTag>? tags, bool preserveValues)
    {
        var result = (tags ?? [])
            .Select(tag => new ObjectTag((preserveValues ? tag.Key : tag.Key?.Trim()) ?? string.Empty,
                (preserveValues ? tag.Value : tag.Value?.Trim()) ?? string.Empty))
            .Where(tag => preserveValues || tag.Key.Length > 0 || tag.Value.Length > 0)
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

    // Snapshots retain exact values across validation and persistence, including tag keys.
    public bool PreserveExistingValues { get; init; }
    public string? ContentLanguage { get; init; }
    public string? WebsiteRedirectLocation { get; init; }

    public ObjectWriteHeaders ValidateAndNormalize() => Validate(PreserveExistingValues);

    public ObjectWriteHeaders ValidatePreservingValues() => Validate(preserveValues: true);

    // A metadata copy edits an existing snapshot, not newly entered upload defaults.
    // Trimming here would silently change unrelated values merely by pressing Save.
    public ObjectWriteHeaders ValidateForMetadataCopy() => Validate(preserveValues: true);

    private ObjectWriteHeaders Validate(bool preserveValues)
    {
        var metadata = preserveValues
            ? ObjectMetadataValidator.ValidatePreservingValues(Metadata)
            : ObjectMetadataValidator.Validate(Metadata);
        var tags = preserveValues ? ObjectTagValidator.ValidatePreservingValues(Tags) : ObjectTagValidator.Validate(Tags);
        return this with
        {
            PreserveExistingValues = preserveValues,
            ContentLanguage = NormalizeHeader(ContentLanguage, nameof(ContentLanguage), preserveValues),
            WebsiteRedirectLocation = NormalizeHeader(WebsiteRedirectLocation, nameof(WebsiteRedirectLocation), preserveValues),
            ContentType = NormalizeHeader(ContentType, nameof(ContentType), preserveValues),
            CacheControl = NormalizeHeader(CacheControl, nameof(CacheControl), preserveValues),
            ContentEncoding = NormalizeHeader(ContentEncoding, nameof(ContentEncoding), preserveValues),
            ContentDisposition = NormalizeHeader(ContentDisposition, nameof(ContentDisposition), preserveValues),
            ExpiresUtc = ExpiresUtc?.ToUniversalTime(),
            Metadata = metadata,
            Tags = tags
        };
    }

    private static string? NormalizeHeader(string? value, string name, bool preserveValues)
    {
        var normalized = preserveValues
            ? (string.IsNullOrEmpty(value) ? null : value)
            : (string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        if (normalized?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException($"{name} 不能包含换行符。", name);
        return normalized;
    }
}

public static class ObjectMetadataValidator
{
    private const int MaximumMetadataBytes = 2 * 1024;

    public static IReadOnlyDictionary<string, string> Validate(
        IReadOnlyDictionary<string, string>? metadata) => Validate(metadata, preserveValues: false);

    public static IReadOnlyDictionary<string, string> ValidatePreservingValues(
        IReadOnlyDictionary<string, string>? metadata) => Validate(metadata, preserveValues: true);

    private static IReadOnlyDictionary<string, string> Validate(
        IReadOnlyDictionary<string, string>? metadata, bool preserveValues)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            var key = NormalizeKey(pair.Key);
            var value = (preserveValues ? pair.Value : pair.Value?.Trim()) ?? string.Empty;
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
        "此服务商尚未验证 Metadata 快照的非覆盖另存。");
}

public static class ObjectMetadataCopyPolicy
{
    public const long MaximumBytes = 5L * 1024 * 1024 * 1024;

    public static string SuggestDestinationKey(string sourceKey)
    {
        var candidate = WithSuffix(".metadata-copy");
        // A full-length key might already equal its truncated-prefix suggestion.
        return candidate == sourceKey ? WithSuffix(".metadata-copy-2") : candidate;

        string WithSuffix(string suffix)
        {
            var prefix = new StringBuilder();
            var remainingBytes = 1024 - Encoding.UTF8.GetByteCount(suffix);
            foreach (var rune in sourceKey.EnumerateRunes())
            {
                if (rune.Utf8SequenceLength > remainingBytes) break;
                prefix.Append(rune.ToString());
                remainingBytes -= rune.Utf8SequenceLength;
            }
            return prefix.Append(suffix).ToString();
        }
    }

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
