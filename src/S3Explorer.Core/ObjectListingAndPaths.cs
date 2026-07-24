namespace S3Explorer.Core;

public static class ObjectListingLimits
{
    public const int MinimumPageSize = 1;
    public const int MaximumPageSize = 1000;
    public const int DefaultPageSize = 1000;
    public const int MinimumCacheLimit = 1000;
    public const int MaximumCacheLimit = 1_000_000;
    public const int DefaultCacheLimit = 100_000;
}

public readonly record struct ObjectEntryIdentity(string Key, bool IsDirectory);

public readonly record struct ObjectCacheAddResult(int AddedCount, bool Truncated);

public sealed class BoundedObjectCache
{
    private readonly List<S3ObjectEntry> _items = [];
    private readonly HashSet<ObjectEntryIdentity> _identities = [];

    public BoundedObjectCache(int limit)
    {
        Reset(limit);
    }

    public IReadOnlyList<S3ObjectEntry> Items => _items;
    public int Count => _items.Count;
    public int Limit { get; private set; }
    public bool LimitReached => Count >= Limit;

    public void Reset(int limit)
    {
        if (limit is < 1 or > ObjectListingLimits.MaximumCacheLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));

        Limit = limit;
        Clear();
    }

    public void Clear()
    {
        _items.Clear();
        _identities.Clear();
    }

    public ObjectCacheAddResult AddRange(IEnumerable<S3ObjectEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var added = 0;
        foreach (var entry in entries)
        {
            var identity = new ObjectEntryIdentity(entry.Key, entry.IsDirectory);
            if (_identities.Contains(identity))
                continue;

            if (LimitReached)
                return new ObjectCacheAddResult(added, true);

            _identities.Add(identity);
            _items.Add(entry);
            added++;
        }

        return new ObjectCacheAddResult(added, false);
    }
}

public static class LocalObjectPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string MapRelativeKey(string rootDirectory, string relativeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrEmpty(relativeKey);

        var root = Path.GetFullPath(rootDirectory);
        var combined = root;
        foreach (var segment in relativeKey.Split('/', StringSplitOptions.None))
            combined = Path.Combine(combined, SanitizeSegment(segment));

        var fullPath = Path.GetFullPath(combined);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison))
            throw new InvalidOperationException("对象 Key 解析后的本地路径超出下载目录。");

        return ToExtendedLengthPath(fullPath);
    }

    public static string ToExtendedLengthPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows() && path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;

        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return fullPath;

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    public static string SanitizeSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment is "." or "..")
            throw new InvalidOperationException("对象 Key 包含不安全的相对路径段。");
        if (segment.Length == 0)
            return "_empty_";

        var trailingStart = segment.Length;
        while (trailingStart > 0 && segment[trailingStart - 1] is ' ' or '.')
            trailingStart--;

        var builder = new System.Text.StringBuilder(segment.Length);
        for (var index = 0; index < segment.Length; index++)
        {
            var value = segment[index];
            if (IsInvalidWindowsCharacter(value) || index >= trailingStart)
                builder.Append($"_x{(int)value:X4}_");
            else
                builder.Append(value);
        }

        var safe = builder.ToString();
        var stem = safe.Split('.', 2)[0];
        return ReservedNames.Contains(stem) ? "_" + safe : safe;
    }

    private static bool IsInvalidWindowsCharacter(char value) =>
        value <= '\u001F' || value is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*' or '/';
}
