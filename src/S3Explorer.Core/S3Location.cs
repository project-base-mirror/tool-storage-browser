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

        var remainder = value[5..].Replace('\\', '/').Trim('/');
        var parts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var profile = Uri.UnescapeDataString(parts[0]);
        var bucket = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        var prefix = parts.Length > 2 ? string.Join('/', parts.Skip(2).Select(Uri.UnescapeDataString)) : string.Empty;
        if (prefix.Length > 0 && value.EndsWith('/'))
            prefix += '/';

        result = new(profile, bucket, S3Path.NormalizePrefix(prefix));
        return true;
    }

    public S3Location Parent()
    {
        if (Bucket is null)
            return this;
        if (string.IsNullOrEmpty(Prefix))
            return new(Profile, null, string.Empty);

        var trimmed = Prefix.TrimEnd('/');
        var separator = trimmed.LastIndexOf('/');
        return separator < 0
            ? new(Profile, Bucket, string.Empty)
            : new(Profile, Bucket, trimmed[..(separator + 1)]);
    }

    public override string ToString()
    {
        var profile = Uri.EscapeDataString(Profile);
        if (Bucket is null)
            return $"s3://{profile}/";
        return $"s3://{profile}/{Uri.EscapeDataString(Bucket)}/{Prefix}";
    }
}

public static class S3Path
{
    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;
        return string.Join('/', prefix.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
               + (prefix.EndsWith('/') ? "/" : string.Empty);
    }

    public static string Combine(string? prefix, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedPrefix = NormalizePrefix(prefix);
        var normalizedName = name.Replace('\\', '/').TrimStart('/');
        return normalizedPrefix + normalizedName;
    }

    public static string ParentPrefix(string? prefix)
    {
        var normalized = NormalizePrefix(prefix);
        if (normalized.Length == 0)
            return string.Empty;

        var trimmed = normalized.TrimEnd('/');
        var separator = trimmed.LastIndexOf('/');
        return separator < 0 ? string.Empty : trimmed[..(separator + 1)];
    }

    public static string FolderMarker(string? prefix, string folderName)
    {
        if (folderName.Any(char.IsControl) || folderName.Trim() is "" or "/")
            throw new ArgumentException("文件夹名称不能为空、不能仅为 /，也不能包含控制字符。", nameof(folderName));
        return Combine(prefix, folderName.Trim().Trim('/')) + "/";
    }

    public static string DisplayName(string key, bool isPrefix)
    {
        var trimmed = isPrefix ? key.TrimEnd('/') : key;
        var index = trimmed.LastIndexOf('/');
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }
}
