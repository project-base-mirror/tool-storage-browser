using System.Runtime.CompilerServices;

namespace S3Explorer.Core;

public static class RecursiveObjectListing
{
    private sealed class PrefixPage(string prefix)
    {
        public string Prefix { get; } = prefix;
        public string? Token { get; set; }
        public HashSet<string> SeenTokens { get; } = new(StringComparer.Ordinal);
    }

    public static async IAsyncEnumerable<S3ObjectEntry> EnumerateFilesAsync(
        string rootPrefix,
        int pageSize,
        int itemLimit,
        Func<string, string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int maxConcurrency = 4,
        Func<string, bool>? excludePrefix = null,
        Func<string, bool>? excludeKey = null)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (itemLimit < 1) throw new ArgumentOutOfRangeException(nameof(itemLimit));
        if (maxConcurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        rootPrefix = S3Path.NormalizePrefix(rootPrefix);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Queue<PrefixPage>();
        var running = new Dictionary<Task<PagedObjectResult>, PrefixPage>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal) { rootPrefix };
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(new PrefixPage(rootPrefix));
        var discoveredItems = 0;

        try
        {
            while (pending.Count > 0 || running.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (pending.Count > 0 && running.Count < maxConcurrency)
                {
                    var next = pending.Dequeue();
                    // An async wrapper gives even synchronously cached pages a unique Task key.
                    running.Add(LoadAsync(next), next);
                }
                var finished = await Task.WhenAny(running.Keys).ConfigureAwait(false);
                var state = running[finished];
                running.Remove(finished);
                var page = await finished.ConfigureAwait(false);
                foreach (var item in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rootPrefix.Length > 0 && !item.Key.StartsWith(rootPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException($"服务端返回了根前缀之外的对象：{item.Key}");
                    discoveredItems++;
                    if (discoveredItems > itemLimit)
                        throw new InvalidOperationException($"递归对象数量达到内存保护上限 {itemLimit:N0}，已停止操作。");

                    if (item.IsDirectory)
                    {
                        var child = S3Path.NormalizePrefix(item.Key);
                        if (child.Length == 0 || string.Equals(child, state.Prefix, StringComparison.Ordinal)) continue;
                        if (!child.StartsWith(state.Prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException($"服务端返回了当前前缀之外的子目录：{item.Key}");
                        if (excludePrefix?.Invoke(child) != true && seenPrefixes.Add(child))
                            pending.Enqueue(new PrefixPage(child));
                    }
                    else if (excludeKey?.Invoke(item.Key) != true && seenKeys.Add(item.Key))
                    {
                        yield return item;
                    }
                }
                if (!page.HasMore) continue;
                var nextToken = page.ContinuationToken;
                if (string.IsNullOrWhiteSpace(nextToken) || !state.SeenTokens.Add(nextToken))
                    throw new InvalidOperationException("对象列表分页令牌无效或重复，已停止操作。");
                state.Token = nextToken;
                pending.Enqueue(state);
            }
        }
        finally
        {
            // Breaking enumeration, a failed page, and cancellation all stop sibling requests.
            await stop.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(running.Keys).ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }

        async Task<PagedObjectResult> LoadAsync(PrefixPage page) =>
            await loadPage(page.Prefix, page.Token, stop.Token).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<S3ObjectEntry>> ListFilesAsync(
        string rootPrefix,
        int pageSize,
        int itemLimit,
        Func<string, string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        CancellationToken cancellationToken = default,
        int maxConcurrency = 4,
        Func<string, bool>? excludePrefix = null,
        Func<string, bool>? excludeKey = null)
    {
        var result = new List<S3ObjectEntry>();
        await foreach (var item in EnumerateFilesAsync(
                           rootPrefix, pageSize, itemLimit, loadPage, cancellationToken,
                           maxConcurrency, excludePrefix, excludeKey))
            result.Add(item);
        return result;
    }
}
