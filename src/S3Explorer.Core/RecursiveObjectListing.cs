using System.Runtime.CompilerServices;

namespace S3Explorer.Core;

public static class RecursiveObjectListing
{
    public static async IAsyncEnumerable<S3ObjectEntry> EnumerateFilesAsync(
        string rootPrefix,
        int pageSize,
        int itemLimit,
        Func<string, string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (itemLimit < 1) throw new ArgumentOutOfRangeException(nameof(itemLimit));

        rootPrefix = S3Path.NormalizePrefix(rootPrefix);
        var pendingPrefixes = new Stack<string>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        pendingPrefixes.Push(rootPrefix);
        var discoveredItems = 0;

        while (pendingPrefixes.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = pendingPrefixes.Pop();
            if (!seenPrefixes.Add(prefix)) continue;

            string? continuationToken = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await loadPage(prefix, continuationToken, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rootPrefix.Length > 0 && !item.Key.StartsWith(rootPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException($"服务端返回了根前缀之外的对象：{item.Key}");

                    if (item.IsDirectory)
                    {
                        var childPrefix = S3Path.NormalizePrefix(item.Key);
                        if (childPrefix.Length == 0 || string.Equals(childPrefix, prefix, StringComparison.Ordinal))
                            continue;
                        if (!childPrefix.StartsWith(prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException($"服务端返回了当前前缀之外的子目录：{item.Key}");
                        if (!seenPrefixes.Contains(childPrefix)) pendingPrefixes.Push(childPrefix);
                    }
                    else if (seenKeys.Add(item.Key))
                    {
                        yield return item;
                    }

                    discoveredItems++;
                    if (discoveredItems > itemLimit)
                        throw new InvalidOperationException($"递归对象数量达到内存保护上限 {itemLimit:N0}，已停止操作。");
                }

                if (!page.HasMore) break;
                var nextToken = page.ContinuationToken;
                if (string.IsNullOrWhiteSpace(nextToken) || !seenTokens.Add(nextToken))
                    throw new InvalidOperationException("对象列表分页令牌无效或重复，已停止操作。");
                continuationToken = nextToken;
            } while (true);
        }
    }

    public static async Task<IReadOnlyList<S3ObjectEntry>> ListFilesAsync(
        string rootPrefix,
        int pageSize,
        int itemLimit,
        Func<string, string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        CancellationToken cancellationToken = default)
    {
        var result = new List<S3ObjectEntry>();
        await foreach (var item in EnumerateFilesAsync(
                           rootPrefix, pageSize, itemLimit, loadPage, cancellationToken))
        {
            result.Add(item);
        }
        return result;
    }
}
