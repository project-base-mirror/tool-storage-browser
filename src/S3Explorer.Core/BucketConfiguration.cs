using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Core;

public enum BucketVersioningState
{
    Disabled,
    Enabled,
    Suspended
}

public enum BucketEncryptionMode
{
    None,
    SseS3,
    SseKms
}

public sealed record BucketEncryptionConfiguration(
    BucketEncryptionMode Mode,
    string? KmsKeyId = null)
{
    public string Summary => Mode switch
    {
        BucketEncryptionMode.None => "未配置",
        BucketEncryptionMode.SseS3 => "SSE-S3（AES256）",
        BucketEncryptionMode.SseKms => string.IsNullOrWhiteSpace(KmsKeyId)
            ? "SSE-KMS"
            : $"SSE-KMS（{KmsKeyId}）",
        _ => Mode.ToString()
    };

    public void Validate(bool kmsSupported)
    {
        if (Mode == BucketEncryptionMode.SseKms && !kmsSupported)
            throw new ArgumentException("当前连接类型未启用 SSE-KMS 配置能力。");
        if (Mode == BucketEncryptionMode.SseKms && string.IsNullOrWhiteSpace(KmsKeyId))
            throw new ArgumentException("SSE-KMS 必须填写 KMS Key ID。", nameof(KmsKeyId));
    }
}

public sealed record BucketTag(string Key, string Value);

public static class BucketTagValidator
{
    public static IReadOnlyList<BucketTag> Validate(IEnumerable<BucketTag> tags)
    {
        var result = tags
            .Select(tag => new BucketTag(tag.Key.Trim(), tag.Value.Trim()))
            .Where(tag => tag.Key.Length > 0 || tag.Value.Length > 0)
            .ToArray();
        if (result.Length > 50)
            throw new ArgumentException("Bucket 最多允许 50 个 Tag。", nameof(tags));
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

public sealed record BucketCorsRule(
    string? Id,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string> AllowedHeaders,
    IReadOnlyList<string> ExposeHeaders,
    int? MaxAgeSeconds);

public sealed record BucketCorsConfiguration(IReadOnlyList<BucketCorsRule> Rules);

public static class BucketCorsDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> AllowedMethods =
        new(["GET", "PUT", "POST", "DELETE", "HEAD"], StringComparer.Ordinal);

    public static BucketCorsConfiguration Validate(BucketCorsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Rules.Count > 100)
            throw new ArgumentException("CORS 最多允许 100 条规则。", nameof(configuration));
        var normalized = configuration.Rules.Select((rule, index) =>
        {
            var origins = Normalize(rule.AllowedOrigins);
            var methods = Normalize(rule.AllowedMethods, value => value.ToUpperInvariant());
            var allowedHeaders = Normalize(rule.AllowedHeaders);
            var exposeHeaders = Normalize(rule.ExposeHeaders);
            if (origins.Count == 0)
                throw new ArgumentException($"第 {index + 1} 条 CORS 规则至少需要一个 Allowed Origin。", nameof(configuration));
            if (methods.Count == 0 || methods.Any(method => !AllowedMethods.Contains(method)))
                throw new ArgumentException($"第 {index + 1} 条 CORS 规则的方法只能是 GET、PUT、POST、DELETE 或 HEAD。", nameof(configuration));
            if (rule.MaxAgeSeconds is < 0)
                throw new ArgumentException($"第 {index + 1} 条 CORS 规则的 Max Age 不能为负数。", nameof(configuration));
            return new BucketCorsRule(
                string.IsNullOrWhiteSpace(rule.Id) ? null : rule.Id.Trim(),
                origins, methods, allowedHeaders, exposeHeaders,
                rule.MaxAgeSeconds is > 0 ? rule.MaxAgeSeconds : null);
        }).ToArray();
        return new BucketCorsConfiguration(normalized);
    }

    public static string Serialize(BucketCorsConfiguration configuration) =>
        JsonSerializer.Serialize(Validate(configuration), JsonOptions);

    public static BucketCorsConfiguration Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new BucketCorsConfiguration([]);
        try
        {
            var value = JsonSerializer.Deserialize<BucketCorsConfiguration>(json, JsonOptions)
                ?? throw new ArgumentException("CORS JSON 不能为空。", nameof(json));
            return Validate(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"CORS JSON 无效：{exception.Message}", nameof(json), exception);
        }
    }

    public static bool AreSemanticallyEquivalent(
        BucketCorsConfiguration left, BucketCorsConfiguration right) =>
        string.Equals(Serialize(left), Serialize(right), StringComparison.Ordinal);

    private static IReadOnlyList<string> Normalize(
        IEnumerable<string>? values, Func<string, string>? transform = null) =>
        (values ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Select(value => transform?.Invoke(value) ?? value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
