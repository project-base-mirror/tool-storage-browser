namespace S3Explorer.Core;

public readonly record struct S3Location(string Profile, string? Bucket, string Prefix)
{
    public static S3Location Parse(string value)
    {
        if (!TryParse(value, out var result))
            throw new FormatException("S3 路径格式应为 s3://<profile>/<bucket>/<prefix>。");
        return result;
    }

    public static bool TryParse(string? value, out S3Location result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            return false;

        // Only URI separators are structural. Decode each field once and preserve the
        // object key verbatim: %, repeated slashes, backslashes and spaces are data.
        var parts = value[5..].Split('/', 3, StringSplitOptions.None);
        if (parts[0].Length == 0) return false;
        var profile = Uri.UnescapeDataString(parts[0]);
        var bucket = parts.Length > 1 && parts[1].Length > 0 ? Uri.UnescapeDataString(parts[1]) : null;
        var prefix = parts.Length > 2
            ? string.Join('/', parts[2].Split('/').Select(Uri.UnescapeDataString))
            : string.Empty;
        if (bucket is null && prefix.Length > 0) return false;

        result = new(profile, bucket, prefix);
        return true;
    }

    public S3Location Parent()
    {
        if (Bucket is null)
            return this;
        if (string.IsNullOrEmpty(Prefix))
            return new(Profile, null, string.Empty);

        return new(Profile, Bucket, S3Path.ParentPrefix(Prefix));
    }

    public override string ToString()
    {
        var profile = Uri.EscapeDataString(Profile);
        if (Bucket is null)
            return $"s3://{profile}/";
        var prefix = string.Join('/', Prefix.Split('/').Select(Uri.EscapeDataString));
        return $"s3://{profile}/{Uri.EscapeDataString(Bucket)}/{prefix}";
    }
}

public static class S3Path
{
    // S3 keys are opaque strings, not filesystem paths. Normalizing separators,
    // whitespace or dot segments would select a different remote object.
    public static string NormalizePrefix(string? prefix) => prefix ?? string.Empty;

    public static string Combine(string? prefix, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return NormalizePrefix(prefix) + name;
    }

    public static string ParentPrefix(string? prefix)
    {
        var normalized = NormalizePrefix(prefix);
        if (normalized.Length == 0)
            return string.Empty;

        // Remove exactly one structural delimiter. Each additional slash is an
        // empty S3 path segment which must remain reachable via Up/Back/Forward.
        var trimmed = normalized.EndsWith('/') ? normalized[..^1] : normalized;
        var separator = trimmed.LastIndexOf('/');
        return separator < 0 ? string.Empty : trimmed[..(separator + 1)];
    }

    public static string FolderMarker(string? prefix, string folderName)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderName);
        if (folderName.Any(char.IsControl))
            throw new ArgumentException("文件夹名称不能包含控制字符。", nameof(folderName));
        var key = Combine(prefix, folderName);
        return key.EndsWith('/') ? key : key + "/";
    }

    /// <summary>The literal final segment, never a user-facing label.</summary>
    public static string LeafName(string key, bool isPrefix)
    {
        ArgumentNullException.ThrowIfNull(key);
        var trimmed = isPrefix && key.EndsWith('/') ? key[..^1] : key;
        var index = trimmed.LastIndexOf('/');
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    public static string DisplayName(string key, bool isPrefix) => LeafName(key, isPrefix) switch
    {
        "" => "（空路径段）",
        "." => "（字面路径段：.）",
        ".." => "（字面路径段：..）",
        var name when string.IsNullOrWhiteSpace(name) => $"（空白名称：{Uri.EscapeDataString(name)}）",
        var name => name
    };

    public static string RenameLeaf(string key, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return key[..(key.LastIndexOf('/') + 1)] + name;
    }
}
