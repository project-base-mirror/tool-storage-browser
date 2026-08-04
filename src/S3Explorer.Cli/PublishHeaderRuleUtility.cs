using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using S3Explorer.Contracts;
using S3Explorer.Core;

namespace S3Explorer.Cli;

public static class PublishHeaderRuleUtility
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<PublishHeaderRuleSet> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("发布 Header 规则文件不存在。", path);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var rules = await JsonSerializer.DeserializeAsync<PublishHeaderRuleSet>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("发布 Header 规则文件不能为空。");
        Validate(rules);
        return rules;
    }

    public static void Validate(PublishHeaderRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.SchemaVersion != PublishHeaderRuleSet.CurrentSchemaVersion)
            throw new InvalidDataException($"不支持的发布 Header 规则版本：{rules.SchemaVersion}。");
        if (rules.Rules is null)
            throw new InvalidDataException("发布 Header 规则的 rules 不能为空。");
        _ = Normalize(rules.Defaults);
        foreach (var rule in rules.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern))
                throw new InvalidDataException("发布 Header 规则的 pattern 不能为空。");
            if (rule.Pattern.IndexOfAny(['\r', '\n']) >= 0)
                throw new InvalidDataException("发布 Header 规则的 pattern 不能包含换行符。");
            _ = CreateMatcher(rule.Pattern);
            _ = Normalize(rule.Headers);
        }
    }

    public static PublishObjectHeaders? Resolve(PublishHeaderRuleSet? rules, string relativePath)
    {
        if (rules is null) return null;
        relativePath = PublishManifestUtility.NormalizeRelativePath(relativePath);
        var current = Clone(rules.Defaults);
        foreach (var rule in rules.Rules)
        {
            if (Matches(rule.Pattern, relativePath))
                current = Merge(current, rule.Headers);
        }
        return Normalize(current);
    }

    public static ObjectWriteHeaders ToObjectWriteHeaders(PublishObjectHeaders? value) =>
        new ObjectWriteHeaders(
            value?.ContentType,
            value?.CacheControl,
            value?.ContentEncoding,
            value?.ContentDisposition,
            value?.ExpiresUtc,
            value?.Metadata,
            value?.Tags?.Select(pair => new ObjectTag(pair.Key, pair.Value)).ToArray())
        .ValidateAndNormalize();

    private static PublishObjectHeaders? Normalize(PublishObjectHeaders? value)
    {
        if (value is null) return null;
        var normalized = ToObjectWriteHeaders(value);
        var result = new PublishObjectHeaders
        {
            ContentType = normalized.ContentType,
            CacheControl = normalized.CacheControl,
            ContentEncoding = normalized.ContentEncoding,
            ContentDisposition = normalized.ContentDisposition,
            ExpiresUtc = normalized.ExpiresUtc,
            Metadata = normalized.Metadata is { Count: > 0 }
                ? normalized.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                : null,
            Tags = normalized.Tags is { Count: > 0 }
                ? normalized.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal)
                    .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal)
                : null
        };
        return IsEmpty(result) ? null : result;
    }

    private static PublishObjectHeaders Merge(PublishObjectHeaders? current, PublishObjectHeaders overlay)
    {
        current ??= new PublishObjectHeaders();
        var metadata = MergeDictionary(current.Metadata, overlay.Metadata, StringComparer.OrdinalIgnoreCase);
        var tags = MergeDictionary(current.Tags, overlay.Tags, StringComparer.Ordinal);
        return new PublishObjectHeaders
        {
            ContentType = overlay.ContentType ?? current.ContentType,
            CacheControl = overlay.CacheControl ?? current.CacheControl,
            ContentEncoding = overlay.ContentEncoding ?? current.ContentEncoding,
            ContentDisposition = overlay.ContentDisposition ?? current.ContentDisposition,
            ExpiresUtc = overlay.ExpiresUtc ?? current.ExpiresUtc,
            Metadata = metadata,
            Tags = tags
        };
    }

    private static Dictionary<string, string>? MergeDictionary(
        IReadOnlyDictionary<string, string>? current,
        IReadOnlyDictionary<string, string>? overlay,
        StringComparer comparer)
    {
        if (current is null && overlay is null) return null;
        var result = new Dictionary<string, string>(comparer);
        foreach (var pair in current ?? new Dictionary<string, string>()) result[pair.Key] = pair.Value;
        foreach (var pair in overlay ?? new Dictionary<string, string>()) result[pair.Key] = pair.Value;
        return result;
    }

    private static PublishObjectHeaders? Clone(PublishObjectHeaders? value) => value is null
        ? null
        : new PublishObjectHeaders
        {
            ContentType = value.ContentType,
            CacheControl = value.CacheControl,
            ContentEncoding = value.ContentEncoding,
            ContentDisposition = value.ContentDisposition,
            ExpiresUtc = value.ExpiresUtc,
            Metadata = value.Metadata is null
                ? null
                : new Dictionary<string, string>(value.Metadata, StringComparer.OrdinalIgnoreCase),
            Tags = value.Tags is null
                ? null
                : new Dictionary<string, string>(value.Tags, StringComparer.Ordinal)
        };

    private static bool IsEmpty(PublishObjectHeaders value) =>
        value.ContentType is null && value.CacheControl is null && value.ContentEncoding is null &&
        value.ContentDisposition is null && value.ExpiresUtc is null &&
        value.Metadata is not { Count: > 0 } && value.Tags is not { Count: > 0 };

    private static bool Matches(string pattern, string relativePath)
    {
        var normalizedPattern = pattern.Trim().Replace('\\', '/').TrimStart('/');
        var candidate = normalizedPattern.Contains('/', StringComparison.Ordinal)
            ? relativePath
            : relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return CreateMatcher(normalizedPattern).IsMatch(candidate);
    }

    private static Regex CreateMatcher(string pattern)
    {
        pattern = pattern.Trim().Replace('\\', '/').TrimStart('/');
        var expression = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    expression.Append("(?:.*/)?");
                }
                else
                {
                    expression.Append(".*");
                }
            }
            else if (character == '*') expression.Append("[^/]*");
            else if (character == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(character.ToString()));
        }
        expression.Append('$');
        try
        {
            return new Regex(
                expression.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"发布 Header 规则 pattern 无效：{pattern}", exception);
        }
    }
}
