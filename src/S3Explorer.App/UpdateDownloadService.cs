using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace S3Explorer.App;

internal sealed record UpdateDownloadProgress(string Stage, long BytesReceived, long? TotalBytes);

internal sealed record VerifiedUpdatePackage(
    string PackagePath,
    string Sha256,
    long Bytes,
    Version Version);

internal sealed partial class UpdateDownloadService : IDisposable
{
    private const long MaximumChecksumBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _downloadRoot;

    public UpdateDownloadService(HttpClient? client = null, string? downloadRoot = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _downloadRoot = Path.GetFullPath(downloadRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "S3Explorer",
            "updates"));
    }

    public async Task<VerifiedUpdatePackage> DownloadAsync(
        GitHubReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        progress?.Report(new UpdateDownloadProgress("正在读取 SHA-256 校验清单...", 0, null));
        var checksumText = await DownloadTextAsync(
            release.ChecksumsDownload!,
            MaximumChecksumBytes,
            timeout.Token).ConfigureAwait(false);
        var expectedSha256 = ParseExpectedSha256(checksumText, release.PreferredAssetName!);

        var versionDirectory = Path.Combine(_downloadRoot, release.TagName);
        Directory.CreateDirectory(versionDirectory);
        var targetPath = Path.Combine(versionDirectory, release.PreferredAssetName!);
        var partialPath = targetPath + ".partial";

        if (File.Exists(targetPath))
        {
            var existing = await ComputeSha256Async(targetPath, timeout.Token).ConfigureAwait(false);
            if (FixedTimeEquals(existing, expectedSha256))
            {
                var existingFile = new FileInfo(targetPath);
                progress?.Report(new UpdateDownloadProgress("安装包已存在并通过校验。", existingFile.Length, existingFile.Length));
                return new VerifiedUpdatePackage(targetPath, expectedSha256, existingFile.Length, release.Version);
            }
            File.Delete(targetPath);
        }

        if (File.Exists(partialPath))
            File.Delete(partialPath);

        try
        {
            using var request = CreateRequest(release.PreferredDownload!);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total is > MaximumPackageBytes)
                throw new InvalidDataException("安装包超过允许的最大大小 512 MiB。");

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                received += read;
                if (received > MaximumPackageBytes)
                    throw new InvalidDataException("安装包超过允许的最大大小 512 MiB。");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress("正在下载安装包...", received, total));
            }
            await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!FixedTimeEquals(actualSha256, expectedSha256))
                throw new InvalidDataException("安装包 SHA-256 与发布清单不一致，已删除下载内容。");
            await destination.DisposeAsync().ConfigureAwait(false);
            File.Move(partialPath, targetPath, true);
            progress?.Report(new UpdateDownloadProgress("安装包下载完成并通过 SHA-256 校验。", received, received));
            return new VerifiedUpdatePackage(targetPath, expectedSha256, received, release.Version);
        }
        catch
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
            throw;
        }
    }

    internal static void ValidateRelease(GitHubReleaseInfo release)
    {
        if (!release.HasVerifiedInstallerDownload)
            throw new InvalidOperationException("当前更新没有可校验的 MSI 安装包。");
        if (!GitHubUpdateChecker.TryParseReleaseVersion(release.TagName, out var tagVersion) ||
            GitHubUpdateChecker.NormalizeVersion(tagVersion!).CompareTo(
                GitHubUpdateChecker.NormalizeVersion(release.Version)) != 0)
            throw new InvalidDataException("更新版本与 tag 不一致。");

        var assetName = release.PreferredAssetName!;
        if (!string.Equals(Path.GetFileName(assetName), assetName, StringComparison.Ordinal) ||
            !assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新资产名称无效。");

        ValidateGitHubReleaseUri(release.PreferredDownload!, release.TagName, assetName);
        ValidateGitHubReleaseUri(release.ChecksumsDownload!, release.TagName, "SHA256SUMS.txt");
    }

    internal static string ParseExpectedSha256(string payload, string assetName)
    {
        string? result = null;
        using var reader = new StringReader(payload);
        while (reader.ReadLine() is { } line)
        {
            var match = ChecksumLinePattern().Match(line);
            if (!match.Success || !string.Equals(match.Groups["name"].Value, assetName, StringComparison.Ordinal))
                continue;
            var candidate = match.Groups["hash"].Value.ToLowerInvariant();
            if (result is not null && !string.Equals(result, candidate, StringComparison.Ordinal))
                throw new InvalidDataException($"SHA256SUMS.txt 对 {assetName} 包含冲突条目。");
            result = candidate;
        }
        return result ?? throw new InvalidDataException($"SHA256SUMS.txt 缺少 {assetName}。");
    }

    private async Task<string> DownloadTextAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumBytes)
            throw new InvalidDataException("校验清单过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("校验清单过大。");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("S3Explorer-Updater", "1.0"));
        return request;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == 64 &&
        right.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static void ValidateGitHubReleaseUri(Uri uri, string tagName, string assetName)
    {
        var expectedPath = $"/project-base-mirror/tool-storage-browser/releases/download/{tagName}/{assetName}";
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Uri.UnescapeDataString(uri.AbsolutePath), expectedPath, StringComparison.Ordinal) ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
            throw new InvalidDataException("更新下载地址不属于受信任的项目 Release。");
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    [GeneratedRegex("^(?<hash>[0-9A-Fa-f]{64})[ \\t]+\\*?(?<name>[^\\r\\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();
}
