using System.Security.Cryptography;
using S3Explorer.Contracts;
using S3Explorer.Core;

namespace S3Explorer.Cli;

public sealed record LocalPublishFile(string FullPath, PublishManifestFile Entry);
public sealed record PublishMirrorDeleteCandidate(string Path, string Key, long Size);

public static class PublishManifestUtility
{
    public static async Task<IReadOnlyList<LocalPublishFile>> ScanAsync(
        string sourceDirectory,
        string manifestPath,
        CancellationToken cancellationToken = default)
        => await ScanAsync(sourceDirectory, manifestPath, null, cancellationToken).ConfigureAwait(false);

    public static async Task<IReadOnlyList<LocalPublishFile>> ScanAsync(
        string sourceDirectory,
        string manifestPath,
        PublishHeaderRuleSet? headerRules,
        CancellationToken cancellationToken = default)
    {
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        manifestPath = Path.GetFullPath(manifestPath);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"发布源目录不存在：{sourceDirectory}");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var results = new List<LocalPublishFile>();
        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", options)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(path), manifestPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceDirectory, path));
            var info = new FileInfo(path);
            results.Add(new LocalPublishFile(
                path,
                new PublishManifestFile
                {
                    Path = relative,
                    Size = info.Length,
                    Sha256 = await ComputeSha256Async(path, cancellationToken),
                    Headers = PublishHeaderRuleUtility.Resolve(headerRules, relative)
                }));
        }
        return results;
    }

    public static PublishPlan CreatePlan(
        IReadOnlyCollection<PublishManifestFile> localFiles,
        PublishManifest? remoteManifest)
    {
        foreach (var file in localFiles) ValidateFile(file);
        if (remoteManifest is not null) ValidateManifest(remoteManifest);
        var remote = (remoteManifest?.Files ?? [])
            .ToDictionary(value => value.Path, StringComparer.Ordinal);
        var plan = new PublishPlan();
        foreach (var file in localFiles.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            var change = !remote.TryGetValue(file.Path, out var existing)
                ? PublishChangeKind.New
                : existing.Size == file.Size &&
                  string.Equals(existing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) &&
                  HeadersEquivalent(existing.Headers, file.Headers)
                    ? PublishChangeKind.Unchanged
                    : PublishChangeKind.Modified;
            plan.Items.Add(new PublishPlanItem
            {
                Path = file.Path,
                Size = file.Size,
                Sha256 = file.Sha256,
                Headers = file.Headers,
                Change = change
            });
            switch (change)
            {
                case PublishChangeKind.New:
                    plan.NewFiles++;
                    plan.UploadBytes += file.Size;
                    break;
                case PublishChangeKind.Modified:
                    plan.ModifiedFiles++;
                    plan.UploadBytes += file.Size;
                    break;
                case PublishChangeKind.Unchanged:
                    plan.UnchangedFiles++;
                    break;
            }
        }
        return plan;
    }

    public static IReadOnlyList<PublishMirrorDeleteCandidate> CreateMirrorDeletePlan(
        IReadOnlyCollection<PublishManifestFile> localFiles,
        IReadOnlyCollection<S3ObjectEntry> remoteObjects,
        string prefix,
        string manifestName)
    {
        ArgumentNullException.ThrowIfNull(localFiles);
        ArgumentNullException.ThrowIfNull(remoteObjects);
        foreach (var file in localFiles)
            ValidateFile(file);

        var normalizedPrefix = prefix.Replace('\\', '/').Trim('/');
        var canonicalPrefix = string.Join(
            '/', normalizedPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (normalizedPrefix.Length == 0 ||
            !string.Equals(normalizedPrefix, canonicalPrefix, StringComparison.Ordinal) ||
            normalizedPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
            throw new InvalidDataException("镜像发布需要安全的非空远程前缀。");

        var rootPrefix = normalizedPrefix + "/";
        var manifestKey = rootPrefix + NormalizeRelativePath(manifestName);
        var localPaths = localFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<PublishMirrorDeleteCandidate>();

        foreach (var remote in remoteObjects.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (remote.IsDirectory || string.IsNullOrEmpty(remote.Key) || remote.Key.EndsWith('/'))
                continue;
            if (!remote.Key.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"远端对象超出镜像发布前缀：{remote.Key}");
            if (string.Equals(remote.Key, manifestKey, StringComparison.Ordinal))
                continue;

            var relative = remote.Key[rootPrefix.Length..];
            var normalizedRelative = NormalizeRelativePath(relative);
            if (!string.Equals(relative, normalizedRelative, StringComparison.Ordinal))
                throw new InvalidDataException($"远端对象路径不规范：{remote.Key}");
            if (!localPaths.Contains(normalizedRelative))
                candidates.Add(new PublishMirrorDeleteCandidate(
                    normalizedRelative,
                    remote.Key,
                    Math.Max(0, remote.Size)));
        }

        return candidates;
    }

    public static void ValidateManifest(PublishManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!ContractCompatibility.SupportsManifestSchemaVersion(manifest.SchemaVersion))
            throw new InvalidDataException($"不支持的发布 Manifest 版本：{manifest.SchemaVersion}。");
        if (manifest.Files is null)
            throw new InvalidDataException("发布 Manifest 的 files 不能为空。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            ValidateFile(file);
            if (!paths.Add(file.Path))
                throw new InvalidDataException($"发布 Manifest 包含重复路径：{file.Path}");
        }
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("发布文件相对路径不能为空。");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var canonical = string.Join(
            '/', normalized.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (Path.IsPathFullyQualified(path) ||
            !string.Equals(normalized, canonical, StringComparison.Ordinal) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..") ||
            normalized.EndsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException($"发布文件路径不安全：{path}");
        return normalized;
    }

    public static Uri BuildCdnUri(CdnProfile profile, string path)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new InvalidDataException($"CDN 基础 URL 无效：{profile.BaseUrl}");
        var trailingSlash = path.Replace('\\', '/').EndsWith('/');
        var normalized = path.Replace('\\', '/').Trim('/');
        var escaped = string.Join(
            "/",
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        if (trailingSlash && escaped.Length > 0) escaped += "/";
        var baseText = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri.AbsoluteUri
            : baseUri.AbsoluteUri + "/";
        return new Uri(new Uri(baseText, UriKind.Absolute), escaped);
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateFile(PublishManifestFile file)
    {
        if (file is null) throw new InvalidDataException("发布 Manifest 包含空文件记录。");
        var normalized = NormalizeRelativePath(file.Path);
        if (!string.Equals(normalized, file.Path, StringComparison.Ordinal))
            throw new InvalidDataException($"发布文件路径必须使用规范正斜杠：{file.Path}");
        if (file.Size < 0) throw new InvalidDataException($"发布文件大小无效：{file.Path}");
        if (file.Sha256.Length != 64 || file.Sha256.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidDataException($"发布文件 SHA-256 无效：{file.Path}");
        _ = PublishHeaderRuleUtility.ToObjectWriteHeaders(file.Headers);
    }

    private static bool HeadersEquivalent(PublishObjectHeaders? left, PublishObjectHeaders? right)
    {
        var normalizedLeft = PublishHeaderRuleUtility.ToObjectWriteHeaders(left);
        var normalizedRight = PublishHeaderRuleUtility.ToObjectWriteHeaders(right);
        return string.Equals(normalizedLeft.ContentType, normalizedRight.ContentType, StringComparison.Ordinal) &&
               string.Equals(normalizedLeft.CacheControl, normalizedRight.CacheControl, StringComparison.Ordinal) &&
               string.Equals(normalizedLeft.ContentEncoding, normalizedRight.ContentEncoding, StringComparison.Ordinal) &&
               string.Equals(normalizedLeft.ContentDisposition, normalizedRight.ContentDisposition, StringComparison.Ordinal) &&
               normalizedLeft.ExpiresUtc == normalizedRight.ExpiresUtc &&
               DictionariesEqual(normalizedLeft.Metadata, normalizedRight.Metadata, StringComparer.OrdinalIgnoreCase) &&
               TagsEqual(normalizedLeft.Tags, normalizedRight.Tags);
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right,
        StringComparer keyComparer)
    {
        left ??= new Dictionary<string, string>();
        right ??= new Dictionary<string, string>();
        if (left.Count != right.Count) return false;
        var lookup = new Dictionary<string, string>(right, keyComparer);
        return left.All(pair => lookup.TryGetValue(pair.Key, out var value) &&
                                string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool TagsEqual(IReadOnlyList<ObjectTag>? left, IReadOnlyList<ObjectTag>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count) return false;
        var lookup = right.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
        return left.All(tag => lookup.TryGetValue(tag.Key, out var value) &&
                               string.Equals(tag.Value, value, StringComparison.Ordinal));
    }
}
