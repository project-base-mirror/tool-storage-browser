namespace S3Explorer.Core;

public static class ObjectDeletionListing
{
    public static async Task<IReadOnlyList<string>> ListKeysAsync(
        string prefix,
        int itemLimit,
        Func<string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        if (itemLimit < 1) throw new ArgumentOutOfRangeException(nameof(itemLimit));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await loadPage(token, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Items)
            {
                if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("服务端返回了删除前缀之外的对象，已停止删除。");
                if (keys.Add(entry.Key) && keys.Count > itemLimit)
                    throw new InvalidOperationException($"删除对象数量达到保护上限 {itemLimit:N0}，已停止操作。");
            }
            if (!page.HasMore) break;
            token = page.ContinuationToken;
            if (string.IsNullOrWhiteSpace(token) || !tokens.Add(token))
                throw new InvalidOperationException("删除对象列表分页令牌无效或重复，已停止操作。");
        } while (true);
        return keys.Order(StringComparer.Ordinal).ToArray();
    }
}
