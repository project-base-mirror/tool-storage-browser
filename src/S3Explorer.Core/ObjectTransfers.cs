namespace S3Explorer.Core;

public enum ObjectConflictPolicy
{
    Overwrite,
    Skip,
    AutoRename,
    Ask
}

public sealed record ObjectTransferPlanItem(
    string SourceBucket,
    string SourceKey,
    string DestinationBucket,
    string DestinationKey,
    string RelativePath,
    long Size);

public static class ObjectTransferPlanner
{
    public static string BuildDestinationKey(
        string destinationPrefix,
        string topLevelName,
        string? relativePath = null)
    {
        if (string.IsNullOrWhiteSpace(topLevelName))
            throw new ArgumentException("顶层对象名称不能为空。", nameof(topLevelName));

        var segments = new[]
        {
            NormalizePrefix(destinationPrefix),
            topLevelName.Replace('\\', '/').Trim('/'),
            relativePath?.Replace('\\', '/').Trim('/') ?? string.Empty
        };
        var key = string.Join("/", segments.Where(segment => segment.Length > 0));
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("目标对象 Key 不能为空。");
        return key;
    }

    public static string GetRelativePath(string sourcePrefix, string objectKey)
    {
        var prefix = NormalizeDirectoryKey(sourcePrefix);
        if (!objectKey.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("对象 Key 不属于所选文件夹。");
        return objectKey[prefix.Length..];
    }

    public static void ValidateDestination(
        string sourceBucket,
        string sourceKey,
        bool isDirectory,
        string destinationBucket,
        string destinationKey)
    {
        if (string.IsNullOrWhiteSpace(sourceBucket) || string.IsNullOrWhiteSpace(destinationBucket))
            throw new ArgumentException("源和目标 Bucket 不能为空。");
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(destinationKey))
            throw new ArgumentException("源和目标对象 Key 不能为空。");
        if (!string.Equals(sourceBucket, destinationBucket, StringComparison.Ordinal))
            return;

        if (!isDirectory)
        {
            if (string.Equals(sourceKey, destinationKey, StringComparison.Ordinal))
                throw new InvalidOperationException("源对象和目标对象不能相同。");
            return;
        }

        var sourcePrefix = NormalizeDirectoryKey(sourceKey);
        var destinationPrefix = NormalizeDirectoryKey(destinationKey);
        if (destinationPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("文件夹不能复制或移动到自身或其后代目录。");
    }

    public static string GetAutoRenameCandidate(string objectKey, int sequence)
    {
        if (sequence < 2) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("对象 Key 不能为空。", nameof(objectKey));

        var isDirectory = objectKey.EndsWith("/", StringComparison.Ordinal);
        var trimmed = isDirectory ? objectKey.TrimEnd('/') : objectKey;
        var slash = trimmed.LastIndexOf('/');
        var parent = slash >= 0 ? trimmed[..(slash + 1)] : string.Empty;
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (name.Length == 0) throw new ArgumentException("对象名称不能为空。", nameof(objectKey));

        string renamed;
        if (isDirectory)
        {
            renamed = $"{name} ({sequence})/";
        }
        else
        {
            var dot = name.LastIndexOf('.');
            var hasExtension = dot > 0 && dot < name.Length - 1;
            var stem = hasExtension ? name[..dot] : name;
            var extension = hasExtension ? name[dot..] : string.Empty;
            renamed = $"{stem} ({sequence}){extension}";
        }
        return parent + renamed;
    }

    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        return prefix.Trim().Replace('\\', '/').Trim('/');
    }

    private static string NormalizeDirectoryKey(string key) =>
        key.TrimStart('/').TrimEnd('/') + "/";
}
