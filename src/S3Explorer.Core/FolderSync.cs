using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace S3Explorer.Core;

public enum FolderSyncDirection
{
    Upload,
    Download
}

public enum FolderSyncChange
{
    New,
    Changed,
    Deleted,
    Unchanged,
    Excluded
}

public enum FolderSyncAction
{
    None,
    Upload,
    Download,
    DeleteRemote,
    DeleteLocal
}

public sealed record FolderSyncJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新同步任务";
    public string LocalDirectory { get; init; } = string.Empty;
    public Guid ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Prefix { get; init; } = string.Empty;
    public FolderSyncDirection Direction { get; init; } = FolderSyncDirection.Upload;
    public bool IncludeNewFiles { get; init; } = true;
    public bool IncludeChangedFiles { get; init; } = true;
    public bool PropagateDeletions { get; init; }
    public bool CompareHashesWhenAvailable { get; init; }
    public IReadOnlyList<string> ExclusionPatterns { get; init; } = Array.Empty<string>();
    public DateTimeOffset? LastRunAt { get; init; }

    public string S3Location => new S3Location(ProfileName, Bucket, S3Path.NormalizePrefix(Prefix)).ToString();

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("同步任务 ID 不能为空。", nameof(Id));
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("同步任务名称不能为空。", nameof(Name));
        if (string.IsNullOrWhiteSpace(LocalDirectory) || !Path.IsPathFullyQualified(LocalDirectory))
            throw new ArgumentException("本地文件夹必须使用绝对路径。", nameof(LocalDirectory));
        if (ProfileId == Guid.Empty) throw new ArgumentException("同步任务必须选择连接。", nameof(ProfileId));
        if (string.IsNullOrWhiteSpace(ProfileName)) throw new ArgumentException("同步任务连接名称不能为空。", nameof(ProfileName));
        if (string.IsNullOrWhiteSpace(Bucket)) throw new ArgumentException("同步任务 Bucket 不能为空。", nameof(Bucket));
        if (Bucket.Any(char.IsControl) || Bucket.Contains('/') || Bucket.Contains('\\'))
            throw new ArgumentException("同步任务 Bucket 名称无效。", nameof(Bucket));
        foreach (var pattern in ExclusionPatterns ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(char.IsControl))
                throw new ArgumentException("排除规则不能为空或包含控制字符。", nameof(ExclusionPatterns));
        }
    }
}

public sealed record FolderSyncFile(
    string RelativePath,
    long Size,
    DateTimeOffset LastModifiedUtc,
    string? Checksum = null)
{
    public string NormalizedPath => FolderSyncPath.NormalizeRelative(RelativePath);
}

public sealed record FolderSyncPlanItem(
    string RelativePath,
    FolderSyncChange Change,
    FolderSyncAction Action,
    FolderSyncFile? Local,
    FolderSyncFile? Remote,
    string Reason)
{
    public long SourceSize(FolderSyncDirection direction) =>
        direction == FolderSyncDirection.Upload ? Local?.Size ?? 0 : Remote?.Size ?? 0;
}

public sealed record FolderSyncPlan(
    Guid JobId,
    DateTimeOffset AnalyzedAt,
    IReadOnlyList<FolderSyncPlanItem> Items)
{
    public string JobFingerprint { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public int ActionCount => Items.Count(item => item.Action != FolderSyncAction.None);
    public int NewCount => Items.Count(item => item.Change == FolderSyncChange.New);
    public int ChangedCount => Items.Count(item => item.Change == FolderSyncChange.Changed);
    public int DeletedCount => Items.Count(item => item.Change == FolderSyncChange.Deleted);
    public int ExcludedCount => Items.Count(item => item.Change == FolderSyncChange.Excluded);
    public long TransferBytes => Items
        .Where(item => item.Action is FolderSyncAction.Upload or FolderSyncAction.Download)
        .Sum(item => item.SourceSize(item.Action == FolderSyncAction.Upload
            ? FolderSyncDirection.Upload
            : FolderSyncDirection.Download));

    public bool IsValidFor(FolderSyncJob job, DateTimeOffset now, out string reason)
    {
        if (job.Id != JobId)
        {
            reason = "分析结果属于其他同步任务。";
            return false;
        }
        if (!string.Equals(JobFingerprint, FolderSyncPlanIdentity.CreateFingerprint(job), StringComparison.Ordinal))
        {
            reason = "连接、Bucket、路径、方向或同步规则已变化。";
            return false;
        }
        if (ExpiresAt == default || now >= ExpiresAt)
        {
            reason = "分析结果已过期。";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}

public static class FolderSyncPlanIdentity
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    public static string CreateFingerprint(FolderSyncJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var builder = new StringBuilder();
        Append(builder, job.Id.ToString("N"));
        Append(builder, Path.GetFullPath(job.LocalDirectory).Replace('\\', '/').TrimEnd('/').ToUpperInvariant());
        Append(builder, job.ProfileId.ToString("N"));
        Append(builder, job.Bucket);
        Append(builder, S3Path.NormalizePrefix(job.Prefix));
        Append(builder, job.Direction.ToString());
        Append(builder, job.IncludeNewFiles ? "1" : "0");
        Append(builder, job.IncludeChangedFiles ? "1" : "0");
        Append(builder, job.PropagateDeletions ? "1" : "0");
        Append(builder, job.CompareHashesWhenAvailable ? "1" : "0");
        foreach (var pattern in (job.ExclusionPatterns ?? Array.Empty<string>())
                     .Select(value => value.Trim())
                     .Where(value => value.Length > 0)
                     .Order(StringComparer.OrdinalIgnoreCase))
            Append(builder, pattern.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');
}

public sealed class FolderSyncPlanSelection
{
    private readonly HashSet<string> _selectedPaths = new(StringComparer.Ordinal);

    public static FolderSyncPlanSelection SelectAllActions(FolderSyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var selection = new FolderSyncPlanSelection();
        selection.Set(plan.Items, selected: true);
        return selection;
    }

    public bool IsSelected(FolderSyncPlanItem item) =>
        item.Action != FolderSyncAction.None && _selectedPaths.Contains(item.RelativePath);

    public void Set(FolderSyncPlanItem item, bool selected)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Action == FolderSyncAction.None || !selected)
            _selectedPaths.Remove(item.RelativePath);
        else
            _selectedPaths.Add(item.RelativePath);
    }

    public void Set(IEnumerable<FolderSyncPlanItem> items, bool selected)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items) Set(item, selected);
    }

    public void Invert(IEnumerable<FolderSyncPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items.Where(value => value.Action != FolderSyncAction.None))
            Set(item, !IsSelected(item));
    }

    public IReadOnlyList<FolderSyncPlanItem> SelectedItems(FolderSyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Items.Where(IsSelected).ToArray();
    }
}

public static class FolderSyncPlanner
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public static FolderSyncPlan Analyze(
        FolderSyncJob job,
        IReadOnlyCollection<FolderSyncFile> localFiles,
        IReadOnlyCollection<FolderSyncFile> remoteFiles,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null)
    {
        job.Validate();
        ArgumentNullException.ThrowIfNull(localFiles);
        ArgumentNullException.ThrowIfNull(remoteFiles);
        var local = ToMap(localFiles, "本地");
        var remote = ToMap(remoteFiles, "远端");
        var allPaths = local.Keys.Concat(remote.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var result = new List<FolderSyncPlanItem>(allPaths.Length);

        foreach (var path in allPaths)
        {
            local.TryGetValue(path, out var localFile);
            remote.TryGetValue(path, out var remoteFile);
            if (FolderSyncGlobMatcher.IsMatch(path, job.ExclusionPatterns))
            {
                result.Add(new(path, FolderSyncChange.Excluded, FolderSyncAction.None, localFile, remoteFile, "匹配排除规则"));
                continue;
            }

            var source = job.Direction == FolderSyncDirection.Upload ? localFile : remoteFile;
            var destination = job.Direction == FolderSyncDirection.Upload ? remoteFile : localFile;
            if (source is not null && destination is null)
            {
                result.Add(new(
                    path,
                    FolderSyncChange.New,
                    job.IncludeNewFiles ? TransferAction(job.Direction) : FolderSyncAction.None,
                    localFile,
                    remoteFile,
                    job.IncludeNewFiles ? "目标中不存在" : "任务未包含新文件"));
                continue;
            }

            if (source is null && destination is not null)
            {
                result.Add(new(
                    path,
                    FolderSyncChange.Deleted,
                    job.PropagateDeletions ? DeleteAction(job.Direction) : FolderSyncAction.None,
                    localFile,
                    remoteFile,
                    job.PropagateDeletions ? "源中已不存在，将从目标删除" : "未启用删除传播"));
                continue;
            }

            if (source is null || destination is null) continue;
            var changed = IsChanged(source, destination, job.CompareHashesWhenAvailable, out var reason);
            result.Add(new(
                path,
                changed ? FolderSyncChange.Changed : FolderSyncChange.Unchanged,
                changed && job.IncludeChangedFiles ? TransferAction(job.Direction) : FolderSyncAction.None,
                localFile,
                remoteFile,
                changed ? (job.IncludeChangedFiles ? reason : "任务未包含已更改文件") : reason));
        }

        var analyzedAt = now ?? DateTimeOffset.UtcNow;
        var effectiveLifetime = lifetime ?? FolderSyncPlanIdentity.DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "分析结果有效期必须大于零。");
        return new FolderSyncPlan(job.Id, analyzedAt, result)
        {
            JobFingerprint = FolderSyncPlanIdentity.CreateFingerprint(job),
            ExpiresAt = analyzedAt + effectiveLifetime
        };
    }

    private static Dictionary<string, FolderSyncFile> ToMap(IEnumerable<FolderSyncFile> files, string side)
    {
        var result = new Dictionary<string, FolderSyncFile>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = file.NormalizedPath;
            if (!result.TryAdd(path, file with { RelativePath = path }))
                throw new InvalidOperationException($"{side}快照包含重复路径：{path}");
        }
        return result;
    }

    private static bool IsChanged(
        FolderSyncFile source,
        FolderSyncFile destination,
        bool compareHashes,
        out string reason)
    {
        if (source.Size != destination.Size)
        {
            reason = "文件大小不同";
            return true;
        }

        if (compareHashes && !string.IsNullOrWhiteSpace(source.Checksum) && !string.IsNullOrWhiteSpace(destination.Checksum))
        {
            var changed = !string.Equals(NormalizeChecksum(source.Checksum), NormalizeChecksum(destination.Checksum), StringComparison.OrdinalIgnoreCase);
            reason = changed ? "文件哈希不同" : "文件大小和哈希相同";
            return changed;
        }

        var destinationIsOlder = destination.LastModifiedUtc + TimestampTolerance < source.LastModifiedUtc;
        reason = destinationIsOlder ? "源文件更新时间较新" : "文件大小相同且目标不旧于源";
        return destinationIsOlder;
    }

    private static string NormalizeChecksum(string value) => value.Trim().Trim('"');

    private static FolderSyncAction TransferAction(FolderSyncDirection direction) =>
        direction == FolderSyncDirection.Upload ? FolderSyncAction.Upload : FolderSyncAction.Download;

    private static FolderSyncAction DeleteAction(FolderSyncDirection direction) =>
        direction == FolderSyncDirection.Upload ? FolderSyncAction.DeleteRemote : FolderSyncAction.DeleteLocal;
}

public static class FolderSyncAnalyzer
{
    public static async Task<FolderSyncPlan> AnalyzeAsync(
        FolderSyncJob job,
        ConnectionProfile profile,
        IS3StorageService storage,
        int pageSize = ObjectListingLimits.DefaultPageSize,
        int itemLimit = ObjectListingLimits.DefaultCacheLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        job.Validate();
        if (profile.Id != job.ProfileId)
            throw new InvalidOperationException("同步任务引用的连接与当前连接不一致。");
        pageSize = Math.Clamp(pageSize, ObjectListingLimits.MinimumPageSize, ObjectListingLimits.MaximumPageSize);
        itemLimit = Math.Clamp(itemLimit, ObjectListingLimits.MinimumCacheLimit, ObjectListingLimits.MaximumCacheLimit);

        var localTask = FolderSyncSnapshot.ReadLocalAsync(
            job.LocalDirectory, job.CompareHashesWhenAvailable, itemLimit, cancellationToken);
        var remoteTask = RecursiveObjectListing.ListFilesAsync(
            job.Prefix,
            pageSize,
            itemLimit,
            (prefix, token, operationToken) => storage.ListObjectsAsync(
                profile, job.Bucket, prefix, token, pageSize, operationToken),
            cancellationToken);

        await Task.WhenAll(localTask, remoteTask).ConfigureAwait(false);
        var remote = FolderSyncSnapshot.FromRemote(job.Prefix, await remoteTask.ConfigureAwait(false));
        return FolderSyncPlanner.Analyze(job, await localTask.ConfigureAwait(false), remote);
    }
}

public static class FolderSyncSnapshot
{
    public static async Task<IReadOnlyList<FolderSyncFile>> ReadLocalAsync(
        string rootDirectory,
        bool calculateHashes,
        int itemLimit,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(rootDirectory))
            throw new ArgumentException("本地文件夹必须使用绝对路径。", nameof(rootDirectory));
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"本地文件夹不存在：{rootDirectory}");
        if (itemLimit < 1) throw new ArgumentOutOfRangeException(nameof(itemLimit));

        var result = new List<FolderSyncFile>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= itemLimit)
                throw new InvalidOperationException($"本地文件数量达到保护上限 {itemLimit:N0}，已停止分析。");
            var info = new FileInfo(path);
            string? checksum = null;
            if (calculateHashes)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                checksum = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }
            result.Add(new FolderSyncFile(
                FolderSyncPath.NormalizeRelative(Path.GetRelativePath(rootDirectory, path)),
                info.Length,
                info.LastWriteTimeUtc,
                checksum));
        }
        return result;
    }

    public static IReadOnlyList<FolderSyncFile> FromRemote(
        string rootPrefix,
        IEnumerable<S3ObjectEntry> objects)
    {
        rootPrefix = S3Path.NormalizePrefix(rootPrefix);
        var result = new List<FolderSyncFile>();
        foreach (var item in objects.Where(item => !item.IsDirectory))
        {
            if (!item.Key.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"对象不属于同步根路径：{item.Key}");
            var relative = item.Key[rootPrefix.Length..].TrimStart('/');
            if (relative.Length == 0) continue;
            result.Add(new FolderSyncFile(
                FolderSyncPath.NormalizeRelative(relative),
                item.Size,
                item.LastModified ?? DateTimeOffset.MinValue,
                IsSimpleEtag(item.ETag) ? item.ETag?.Trim('"') : null));
        }
        return result;
    }

    private static bool IsSimpleEtag(string? etag)
    {
        var value = etag?.Trim().Trim('"');
        return value is { Length: 32 } && value.All(Uri.IsHexDigit);
    }
}

public static class FolderSyncPath
{
    public static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("同步相对路径不能为空。", nameof(path));
        var normalized = path.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Any(char.IsControl)))
            throw new ArgumentException($"同步相对路径无效：{path}", nameof(path));
        return string.Join('/', segments);
    }
}

public static class FolderSyncGlobMatcher
{
    public static bool IsMatch(string relativePath, IEnumerable<string>? patterns)
    {
        relativePath = FolderSyncPath.NormalizeRelative(relativePath);
        foreach (var rawPattern in patterns ?? Array.Empty<string>())
        {
            foreach (var pattern in rawPattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Regex.IsMatch(relativePath, ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }
        }
        return false;
    }

    private static string ToRegex(string pattern)
    {
        pattern = pattern.Replace('\\', '/').Trim('/');
        var result = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                result.Append(".*");
                index++;
            }
            else if (current == '*') result.Append("[^/]*");
            else if (current == '?') result.Append("[^/]");
            else result.Append(Regex.Escape(current.ToString()));
        }
        result.Append('$');
        return result.ToString();
    }
}
