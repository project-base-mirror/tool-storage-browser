using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Core;

public enum LifecycleStorageClass
{
    StandardInfrequentAccess,
    OneZoneInfrequentAccess,
    IntelligentTiering,
    GlacierInstantRetrieval,
    GlacierFlexibleRetrieval,
    DeepArchive
}

public sealed record LifecycleTag(string Key, string Value);

public sealed record LifecycleTransition(int Days, LifecycleStorageClass StorageClass);

public sealed record BucketLifecycleRule(
    string Id,
    bool Enabled,
    string? Prefix,
    IReadOnlyList<LifecycleTag> Tags,
    IReadOnlyList<LifecycleTransition> Transitions,
    int? ExpirationDays,
    IReadOnlyList<LifecycleTransition> NoncurrentVersionTransitions,
    int? NoncurrentVersionExpirationDays,
    int? AbortIncompleteMultipartUploadDays);

public sealed record BucketLifecycleConfiguration(IReadOnlyList<BucketLifecycleRule> Rules);

public static class BucketLifecycleDocument
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static BucketLifecycleConfiguration Validate(
        BucketLifecycleConfiguration configuration,
        bool storageTransitionsSupported = true,
        bool multipartCleanupSupported = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Rules.Count > 1000)
            throw new ArgumentException("生命周期最多允许 1000 条规则。", nameof(configuration));

        var normalized = configuration.Rules.Select((rule, index) =>
            ValidateRule(rule, index, storageTransitionsSupported, multipartCleanupSupported)).ToArray();

        var duplicateId = normalized.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null)
            throw new ArgumentException($"生命周期规则 ID “{duplicateId}”重复。", nameof(configuration));

        var duplicateFilter = normalized.GroupBy(FilterSignature, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFilter is not null)
        {
            var ids = string.Join("、", duplicateFilter.Select(rule => rule.Id));
            throw new ArgumentException($"生命周期规则 {ids} 使用相同过滤条件，会产生冲突。", nameof(configuration));
        }

        return new BucketLifecycleConfiguration(
            normalized.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray());
    }

    public static string Serialize(
        BucketLifecycleConfiguration configuration,
        bool storageTransitionsSupported = true,
        bool multipartCleanupSupported = true) =>
        JsonSerializer.Serialize(Validate(configuration, storageTransitionsSupported, multipartCleanupSupported), JsonOptions);

    public static BucketLifecycleConfiguration Parse(
        string json,
        bool storageTransitionsSupported = true,
        bool multipartCleanupSupported = true)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new BucketLifecycleConfiguration([]);
        try
        {
            var configuration = JsonSerializer.Deserialize<BucketLifecycleConfiguration>(json, JsonOptions)
                ?? throw new ArgumentException("生命周期 JSON 不能为空。", nameof(json));
            return Validate(configuration, storageTransitionsSupported, multipartCleanupSupported);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"生命周期 JSON 无效：{exception.Message}", nameof(json), exception);
        }
    }

    public static bool AreSemanticallyEquivalent(
        BucketLifecycleConfiguration left,
        BucketLifecycleConfiguration right,
        bool storageTransitionsSupported = true,
        bool multipartCleanupSupported = true) =>
        string.Equals(
            Serialize(left, storageTransitionsSupported, multipartCleanupSupported),
            Serialize(right, storageTransitionsSupported, multipartCleanupSupported),
            StringComparison.Ordinal);

    private static BucketLifecycleRule ValidateRule(
        BucketLifecycleRule rule,
        int index,
        bool storageTransitionsSupported,
        bool multipartCleanupSupported)
    {
        var number = index + 1;
        var id = rule.Id?.Trim() ?? string.Empty;
        if (id.Length is < 1 or > 255)
            throw new ArgumentException($"第 {number} 条生命周期规则 ID 长度必须为 1–255 个字符。", nameof(rule));

        var tags = (rule.Tags ?? []).Select(tag =>
                new LifecycleTag(tag.Key?.Trim() ?? string.Empty, tag.Value?.Trim() ?? string.Empty))
            .OrderBy(tag => tag.Key, StringComparer.Ordinal)
            .ToArray();
        if (tags.Length > 10)
            throw new ArgumentException($"生命周期规则 “{id}”最多允许 10 个过滤 Tag。", nameof(rule));
        if (tags.Any(tag => tag.Key.Length is < 1 or > 128))
            throw new ArgumentException($"生命周期规则 “{id}”的 Tag Key 长度必须为 1–128 个字符。", nameof(rule));
        if (tags.Any(tag => tag.Value.Length > 256))
            throw new ArgumentException($"生命周期规则 “{id}”的 Tag Value 长度不能超过 256 个字符。", nameof(rule));
        var duplicateTag = tags.GroupBy(tag => tag.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateTag is not null)
            throw new ArgumentException($"生命周期规则 “{id}”的 Tag Key “{duplicateTag}”重复。", nameof(rule));

        var transitions = ValidateTransitions(rule.Transitions ?? [], id, "当前版本", storageTransitionsSupported);
        var noncurrentTransitions = ValidateTransitions(
            rule.NoncurrentVersionTransitions ?? [], id, "非当前版本", storageTransitionsSupported);

        ValidatePositive(rule.ExpirationDays, id, "过期天数");
        ValidatePositive(rule.NoncurrentVersionExpirationDays, id, "非当前版本过期天数");
        ValidatePositive(rule.AbortIncompleteMultipartUploadDays, id, "未完成 Multipart 清理天数");
        if (rule.AbortIncompleteMultipartUploadDays is not null && !multipartCleanupSupported)
            throw new ArgumentException("当前 Provider 未启用生命周期自动清理未完成 Multipart 的能力。", nameof(rule));

        if (rule.ExpirationDays is not null && transitions.Count > 0 &&
            transitions.Max(value => value.Days) >= rule.ExpirationDays)
            throw new ArgumentException($"生命周期规则 “{id}”的存储类型转换必须早于对象过期。", nameof(rule));
        if (rule.NoncurrentVersionExpirationDays is not null && noncurrentTransitions.Count > 0 &&
            noncurrentTransitions.Max(value => value.Days) >= rule.NoncurrentVersionExpirationDays)
            throw new ArgumentException($"生命周期规则 “{id}”的非当前版本转换必须早于非当前版本过期。", nameof(rule));

        if (transitions.Count == 0 && rule.ExpirationDays is null &&
            noncurrentTransitions.Count == 0 && rule.NoncurrentVersionExpirationDays is null &&
            rule.AbortIncompleteMultipartUploadDays is null)
            throw new ArgumentException($"生命周期规则 “{id}”至少需要一个转换、过期或 Multipart 清理操作。", nameof(rule));

        return rule with
        {
            Id = id,
            Prefix = string.IsNullOrEmpty(rule.Prefix) ? null : rule.Prefix,
            Tags = tags,
            Transitions = transitions,
            NoncurrentVersionTransitions = noncurrentTransitions
        };
    }

    private static IReadOnlyList<LifecycleTransition> ValidateTransitions(
        IEnumerable<LifecycleTransition> source,
        string ruleId,
        string label,
        bool supported)
    {
        var values = source.OrderBy(value => value.Days).ThenBy(value => value.StorageClass).ToArray();
        if (values.Length > 0 && !supported)
            throw new ArgumentException($"当前 Provider 未启用{label}存储类型转换能力。", nameof(source));
        if (values.Any(value => value.Days <= 0))
            throw new ArgumentException($"生命周期规则 “{ruleId}”的{label}转换天数必须大于 0。", nameof(source));
        if (values.GroupBy(value => value.StorageClass).Any(group => group.Count() > 1))
            throw new ArgumentException($"生命周期规则 “{ruleId}”不能重复转换到同一存储类型。", nameof(source));
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index - 1].Days >= values[index].Days)
                throw new ArgumentException($"生命周期规则 “{ruleId}”的{label}转换天数必须严格递增。", nameof(source));
        }
        return values;
    }

    private static void ValidatePositive(int? value, string ruleId, string label)
    {
        if (value is <= 0)
            throw new ArgumentException($"生命周期规则 “{ruleId}”的{label}必须大于 0。", label);
    }

    private static string FilterSignature(BucketLifecycleRule rule) =>
        $"{rule.Prefix ?? string.Empty}\u001f{string.Join("\u001e", rule.Tags.Select(tag => $"{tag.Key}\u001d{tag.Value}"))}";

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public enum ObjectRetentionMode
{
    Governance,
    Compliance
}

public sealed record BucketObjectLockSnapshot(
    bool Enabled,
    ObjectRetentionMode? DefaultRetentionMode = null,
    int? DefaultRetentionDays = null,
    int? DefaultRetentionYears = null)
{
    public string Summary => !Enabled
        ? "未启用"
        : DefaultRetentionMode is null
            ? "已启用；未配置默认保留期"
            : $"已启用；默认 {DefaultRetentionMode} " +
              (DefaultRetentionDays is not null
                  ? $"{DefaultRetentionDays} 天"
                  : $"{DefaultRetentionYears} 年");
}

public sealed record ObjectLockSnapshot(
    ObjectRetentionMode? RetentionMode,
    DateTimeOffset? RetainUntilDate,
    bool LegalHoldEnabled,
    string? VersionId)
{
    public bool HasRetention => RetentionMode is not null && RetainUntilDate is not null;
}

public sealed record ObjectRetentionConfiguration(
    ObjectRetentionMode Mode,
    DateTimeOffset RetainUntilDate)
{
    public void Validate(ObjectLockSnapshot? current = null, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        if (RetainUntilDate <= currentTime)
            throw new ArgumentException("Retention 截止时间必须晚于当前时间。", nameof(RetainUntilDate));
        if (current?.RetentionMode == ObjectRetentionMode.Compliance)
        {
            if (Mode != ObjectRetentionMode.Compliance)
                throw new ArgumentException("Compliance Retention 不能改为 Governance。", nameof(Mode));
            if (current.RetainUntilDate is not null && RetainUntilDate < current.RetainUntilDate)
                throw new ArgumentException("Compliance Retention 只能延长，不能缩短。", nameof(RetainUntilDate));
        }
    }
}
