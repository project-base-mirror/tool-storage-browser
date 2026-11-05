namespace S3Explorer.Core;

/// <summary>A snapshot descriptor. Key is the original S3 key, never a display label.</summary>
public sealed record WindowsFileClipboardEntry(
    string RelativePath,
    string Key,
    bool IsDirectory,
    long Size,
    DateTimeOffset? LastModified,
    string? ETag = null,
    string? VersionId = null);

public static class WindowsFileClipboardManifest
{
    public const int DefaultItemLimit = 10_000;
    public const int MaximumRelativePathLength = 259;

    /// <summary>Build Windows Shell descriptors without accessing the local filesystem or renaming keys.</summary>
    public static IReadOnlyList<WindowsFileClipboardEntry> Build(
        string currentPrefix, IEnumerable<S3ObjectEntry> entries, int itemLimit = DefaultItemLimit)
    {
        ValidatePrefix(currentPrefix);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemLimit, 1);
        var result = new Dictionary<string, WindowsFileClipboardEntry>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var entry in entries)
        {
            if (++count > itemLimit) throw LimitExceeded(itemLimit);
            if (!entry.Key.StartsWith(currentPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"对象不属于当前目录：{entry.Key}");
            var directory = entry.IsDirectory || entry.Key.EndsWith('/');
            if (directory && !entry.Key.EndsWith('/'))
                throw new InvalidOperationException($"目录 Key 缺少末尾斜杠：{entry.Key}");
            if (entry.Size < 0 || (directory && entry.Size != 0))
                throw new InvalidOperationException($"对象大小或目录标记无法表示为 Windows 文件：{entry.Key}");
            var relative = entry.Key[currentPrefix.Length..];
            if (directory) relative = relative[..^1];
            ValidateRelativeName(relative);
            var segments = relative.Split('/');
            for (var i = 1; i < segments.Length; i++)
            {
                var parent = string.Join('/', segments.Take(i));
                Add(new WindowsFileClipboardEntry(parent.Replace('/', '\\'), currentPrefix + parent + "/", true, 0, null));
            }
            Add(new WindowsFileClipboardEntry(relative.Replace('/', '\\'), entry.Key, directory,
                directory ? 0 : entry.Size, entry.LastModified, entry.ETag, entry.VersionId));
        }
        // Parents precede children. Ordinal ordering keeps descriptor indexes stable across page order.
        return Array.AsReadOnly(result.Values.OrderBy(item => item.RelativePath.Count(c => c == '\\'))
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());

        void Add(WindowsFileClipboardEntry item)
        {
            if (result.TryGetValue(item.RelativePath, out var previous))
            {
                if (!string.Equals(previous.Key, item.Key, StringComparison.Ordinal) || previous.IsDirectory != item.IsDirectory ||
                    (!item.IsDirectory && previous != item))
                    throw new InvalidOperationException($"Windows 文件名冲突，不能无损复制：{item.RelativePath}");
                if (item.IsDirectory && item.LastModified is not null) result[item.RelativePath] = item;
                return;
            }
            if (result.Count >= itemLimit) throw LimitExceeded(itemLimit);
            result.Add(item.RelativePath, item);
        }
    }

    /// <summary>Discover selected folders, including empty folders, before exposing any clipboard data.</summary>
    public static async Task<IReadOnlyList<WindowsFileClipboardEntry>> BuildAsync(
        string currentPrefix,
        IReadOnlyCollection<S3ObjectEntry> selection,
        Func<string, string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        CancellationToken cancellationToken = default,
        int itemLimit = DefaultItemLimit,
        bool flatListing = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(loadPage);
        cancellationToken.ThrowIfCancellationRequested();
        _ = Build(currentPrefix, selection, itemLimit); // Reject unsafe selections before issuing requests.
        var entries = new List<S3ObjectEntry>(selection);
        var pending = new Queue<string>();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in selection)
            if ((item.IsDirectory || item.Key.EndsWith('/')) && prefixes.Add(item.Key)) pending.Enqueue(item.Key);
        var discovered = selection.Count;
        var pageCount = 0;
        while (pending.TryDequeue(out var prefix))
        {
            string? token = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++pageCount > itemLimit) throw LimitExceeded(itemLimit);
                var page = await loadPage(prefix, token, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++discovered > itemLimit) throw LimitExceeded(itemLimit);
                    if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                        throw new InvalidOperationException($"服务端返回了所选目录之外的对象：{item.Key}");
                    // Some services return the listed directory's own marker.
                    if (item.Key == prefix)
                    {
                        if ((item.Size != 0) || !item.Key.EndsWith('/'))
                            throw new InvalidOperationException($"目录标记无法表示为 Windows 文件：{item.Key}");
                        continue;
                    }
                    entries.Add(item);
                    if (!flatListing && (item.IsDirectory || item.Key.EndsWith('/')) && prefixes.Add(item.Key)) pending.Enqueue(item.Key);
                }
                // Validate each page, so malformed keys cannot trigger further recursive requests.
                _ = Build(currentPrefix, entries, itemLimit);
                if (!page.HasMore) break;
                token = page.ContinuationToken;
                if (string.IsNullOrWhiteSpace(token) || !seenTokens.Add(token))
                    throw new InvalidOperationException("对象列表分页令牌无效或重复，已停止复制。");
            } while (true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Build(currentPrefix, entries, itemLimit);
    }

    private static void ValidatePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            throw new ArgumentException("当前目录前缀必须保留末尾斜杠。", nameof(prefix));
    }

    private static void ValidateRelativeName(string relative)
    {
        if (relative.Length is 0 or > MaximumRelativePathLength)
            throw new InvalidOperationException("Windows 剪贴板相对路径必须为 1–259 个 UTF-16 字符。");
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(c => c < 32 || c is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*'))
                throw new InvalidOperationException($"对象名不能无损表示为 Windows 文件名：{relative}");
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 "123456789¹²³".Contains(stem[3])))
                throw new InvalidOperationException($"对象名是 Windows 保留设备名称：{relative}");
            for (var i = 0; i < segment.Length; i++)
                if (char.IsSurrogate(segment[i]) && (!char.IsHighSurrogate(segment[i]) || ++i == segment.Length || !char.IsLowSurrogate(segment[i])))
                    throw new InvalidOperationException("对象名包含无效的 UTF-16 字符。");
        }
    }

    private static InvalidOperationException LimitExceeded(int limit) =>
        new($"剪贴板对象或目录数量达到保护上限 {limit:N0}，请减少选择范围。");
}
