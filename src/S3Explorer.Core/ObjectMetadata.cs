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
    BucketFeatureSupport MetadataRewrite);

public static class ObjectCapabilityMatrix
{
    public static ObjectCapabilities For(S3ServiceType serviceType) => serviceType switch
    {
        S3ServiceType.AmazonS3 => new(BucketFeatureSupport.Yes(), BucketFeatureSupport.Yes()),
        S3ServiceType.MinIO => new(
            BucketFeatureSupport.Yes("MinIO 支持 S3 Object Tagging API"),
            BucketFeatureSupport.Yes("MinIO 支持通过原地 Copy 替换对象 Metadata")),
        S3ServiceType.AliyunOss => new(
            BucketFeatureSupport.Yes("阿里云 OSS S3 兼容接口支持对象 Tagging"),
            BucketFeatureSupport.Yes("阿里云 OSS S3 兼容接口支持原地 Copy Metadata")),
        _ => new(
            BucketFeatureSupport.No("该兼容服务的对象 Tagging API 尚未验证"),
            BucketFeatureSupport.No("该兼容服务的对象 Metadata 原地替换尚未验证"))
    };
}
