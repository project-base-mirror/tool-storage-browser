using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S3Explorer.App;

internal enum UpdateReleaseSource
{
    PagesManifest,
    GitHubApi,
    Cache
}

internal sealed record GitHubReleaseInfo(
    string TagName,
    Version Version,
    Uri ReleasePage,
    Uri? PreferredDownload,
    string Notes,
    DateTimeOffset? PublishedAt,
    UpdatePackageKind RecommendedPackage = UpdatePackageKind.PortableFrameworkDependent,
    UpdateReleaseSource Source = UpdateReleaseSource.GitHubApi,
    DateTimeOffset? CachedAtUtc = null)
{
    public bool IsFromCache => Source == UpdateReleaseSource.Cache;

    public bool IsNewerThan(Version currentVersion) =>
        GitHubUpdateChecker.NormalizeVersion(Version).CompareTo(
            GitHubUpdateChecker.NormalizeVersion(currentVersion)) > 0;
}

internal sealed partial class GitHubUpdateChecker : IDisposable
{
    private const int MaximumNotesLength = 20_000;
    private static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _cachePath;
    private readonly TimeSpan _checkTimeout;
    private readonly UpdatePackageKind _packageKind;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public GitHubUpdateChecker(
        HttpClient? client = null,
        string? cachePath = null,
        TimeSpan? checkTimeout = null,
        UpdatePackageKind? packageKind = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024
        };
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "S3Explorer",
            "update-cache.json");
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
        _packageKind = packageKind ?? UpdatePackageDetector.Detect();
        if (_checkTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(checkTimeout));
    }

    public async Task<GitHubReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checkTimeout);
        var failures = new List<Exception>();

        try
        {
            var release = await GetPagesManifestAsync(timeout.Token).ConfigureAwait(false);
            await TrySaveCacheAsync(release, cancellationToken).ConfigureAwait(false);
            return release;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!timeout.IsCancellationRequested)
        {
            try
            {
                var release = await GetGitHubReleaseAsync(timeout.Token).ConfigureAwait(false);
                await TrySaveCacheAsync(release, cancellationToken).ConfigureAwait(false);
                return release;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var cached = await TryLoadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        if (timeout.IsCancellationRequested)
            throw new TimeoutException($"检查更新超过 {_checkTimeout.TotalSeconds:N0} 秒，且没有可用缓存。");

        throw new InvalidOperationException(
            "Pages 更新清单和 GitHub Release API 均不可用，且没有可用缓存。" +
            Environment.NewLine + string.Join(Environment.NewLine, failures.Select(FriendlyFailure)));
    }

    private async Task<GitHubReleaseInfo> GetPagesManifestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProjectLinks.UpdateManifest);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("S3Explorer-UpdateChecker/0.5");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        ThrowForUnsuccessfulResponse(response, "Pages 更新清单");
        return ParseManifest(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            _packageKind);
    }

    private async Task<GitHubReleaseInfo> GetGitHubReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProjectLinks.LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("S3Explorer-UpdateChecker/0.5");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        ThrowForUnsuccessfulResponse(response, "GitHub Release API");
        return ParseRelease(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            _packageKind);
    }

    internal static GitHubReleaseInfo ParseManifest(
        string payload,
        UpdatePackageKind packageKind = UpdatePackageKind.PortableFrameworkDependent)
    {
        using var document = ParseJson(payload, "Pages 更新清单");
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var parsedSchemaVersion) ||
            parsedSchemaVersion is not (1 or 2))
            throw new InvalidDataException("Pages 更新清单 schemaVersion 必须为 1 或 2。");

        var tagName = RequiredString(root, "tagName");
        if (!TryParseReleaseVersion(tagName, out var version))
            throw new InvalidDataException($"Pages 更新清单 tagName 不是受支持的 vX.Y.Z 版本：{tagName}");
        var declaredVersion = RequiredString(root, "version");
        if (!Version.TryParse(declaredVersion, out var manifestVersion) ||
            NormalizeVersion(manifestVersion).CompareTo(NormalizeVersion(version!)) != 0)
            throw new InvalidDataException("Pages 更新清单的 version 与 tagName 不一致。");

        var notes = OptionalNotes(root);
        var preferredDownload = parsedSchemaVersion == 2
            ? RequiredPackageDownload(root, packageKind)
            : OptionalHttpsUri(root, "downloadUrl");
        return new GitHubReleaseInfo(
            tagName,
            version!,
            RequiredHttpsUri(root, "releasePage"),
            preferredDownload,
            notes,
            OptionalDate(root, "publishedAt"),
            packageKind,
            UpdateReleaseSource.PagesManifest);
    }

    internal static GitHubReleaseInfo ParseRelease(
        string payload,
        UpdatePackageKind packageKind = UpdatePackageKind.PortableFrameworkDependent)
    {
        using var document = ParseJson(payload, "GitHub Release 响应");
        var root = document.RootElement;
        var tagName = RequiredString(root, "tag_name");
        if (!TryParseReleaseVersion(tagName, out var version))
            throw new InvalidDataException($"GitHub Release tag 不是受支持的 vX.Y.Z 版本：{tagName}");

        var releasePage = RequiredHttpsUri(root, "html_url");
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
            ? NormalizeNotes(body.GetString())
            : "此版本未提供详细说明。";

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var published) &&
            published.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(published.GetString(), out var parsedDate))
            publishedAt = parsedDate;

        Uri? preferredDownload = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var preferredNames = PreferredAssetNames(tagName, packageKind);
            foreach (var preferredName in preferredNames)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String ||
                        !string.Equals(name.GetString(), preferredName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (asset.TryGetProperty("browser_download_url", out var download) &&
                        download.ValueKind == JsonValueKind.String &&
                        TryHttpsUri(download.GetString(), out var uri))
                        preferredDownload = uri;
                    break;
                }
                if (preferredDownload is not null)
                    break;
            }
        }

        return new GitHubReleaseInfo(
            tagName,
            version!,
            releasePage,
            preferredDownload,
            notes,
            publishedAt,
            packageKind,
            UpdateReleaseSource.GitHubApi);
    }

    private static string[] PreferredAssetNames(string tagName, UpdatePackageKind packageKind) => packageKind switch
    {
        UpdatePackageKind.InstallerSelfContained =>
            [$"S3Explorer-{tagName}-win-x64-setup.msi"],
        UpdatePackageKind.InstallerFrameworkDependent =>
            [$"S3Explorer-{tagName}-win-x64-framework-dependent-setup.msi"],
        UpdatePackageKind.PortableSelfContained =>
            [$"S3Explorer-{tagName}-win-x64-self-contained.zip"],
        _ => [$"S3Explorer-{tagName}-win-x64.zip", "S3Explorer-win-x64.zip"]
    };

    private static Uri RequiredPackageDownload(JsonElement root, UpdatePackageKind packageKind)
    {
        if (!root.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Pages 更新清单缺少 downloads。");
        var property = packageKind switch
        {
            UpdatePackageKind.InstallerSelfContained => "installerSelfContained",
            UpdatePackageKind.InstallerFrameworkDependent => "installerFrameworkDependent",
            UpdatePackageKind.PortableSelfContained => "portableSelfContained",
            _ => "portableFrameworkDependent"
        };
        return RequiredHttpsUri(downloads, property);
    }

    private async Task TrySaveCacheAsync(GitHubReleaseInfo release, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var document = CachedReleaseDocument.FromRelease(release, DateTimeOffset.UtcNow);
            var temporaryPath = _cachePath + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 更新缓存失败不能让已经取得的在线结果失效。
        }
    }

    private async Task<GitHubReleaseInfo?> TryLoadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            await using var stream = File.OpenRead(_cachePath);
            var document = await JsonSerializer.DeserializeAsync<CachedReleaseDocument>(
                stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            return document?.ToRelease();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static void ThrowForUnsuccessfulResponse(HttpResponseMessage response, string channel)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta is { } delay
                ? $"，建议 {Math.Ceiling(delay.TotalMinutes):N0} 分钟后重试"
                : TryRateLimitReset(response, out var reset)
                    ? $"，预计 {reset.ToLocalTime():yyyy-MM-dd HH:mm} 后恢复"
                    : string.Empty;
            throw new HttpRequestException(
                $"{channel} 请求受限（HTTP {(int)response.StatusCode}）{retry}。",
                null,
                response.StatusCode);
        }

        throw new HttpRequestException(
            $"{channel} 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
            null,
            response.StatusCode);
    }

    private static bool TryRateLimitReset(HttpResponseMessage response, out DateTimeOffset reset)
    {
        reset = default;
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values) ||
            !long.TryParse(values.FirstOrDefault(), out var seconds))
            return false;
        try
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string FriendlyFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "更新请求超时。",
        HttpRequestException or InvalidDataException => exception.Message,
        _ => $"{exception.GetType().Name}: {exception.Message}"
    };

    private static JsonDocument ParseJson(string payload, string source)
    {
        try
        {
            return JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{source}不是有效 JSON：{exception.Message}", exception);
        }
    }

    private static string OptionalNotes(JsonElement root)
    {
        if (!root.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.String)
            return "此版本未提供详细说明。";
        return NormalizeNotes(notes.GetString());
    }

    private static string NormalizeNotes(string? notes)
    {
        var value = string.IsNullOrWhiteSpace(notes) ? "此版本未提供详细说明。" : notes.Trim();
        return value.Length <= MaximumNotesLength
            ? value
            : value[..MaximumNotesLength] + Environment.NewLine + Environment.NewLine +
              "（版本说明过长，已截断；请打开 Release 查看完整内容。）";
    }

    private static DateTimeOffset? OptionalDate(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            return parsed;
        throw new InvalidDataException($"更新清单中的 {property} 不是有效时间。");
    }

    private static Uri? OptionalHttpsUri(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String && TryHttpsUri(value.GetString(), out var uri))
            return uri;
        throw new InvalidDataException($"更新清单中的 {property} 不是有效 HTTPS 地址。");
    }

    internal static Version NormalizeVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    internal static bool TryParseReleaseVersion(string value, out Version? version)
    {
        var match = ReleaseVersionPattern().Match(value.Trim());
        if (match.Success)
            return Version.TryParse(match.Groups["version"].Value, out version);
        version = null;
        return false;
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"更新响应缺少 {property}。");
        return value.GetString()!;
    }

    private static Uri RequiredHttpsUri(JsonElement root, string property)
    {
        var value = RequiredString(root, property);
        if (!TryHttpsUri(value, out var uri))
            throw new InvalidDataException($"更新响应中的 {property} 不是有效 HTTPS 地址。");
        return uri;
    }

    private static bool TryHttpsUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private sealed class CachedReleaseDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string TagName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ReleasePage { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public string Notes { get; set; } = string.Empty;
        public DateTimeOffset? PublishedAt { get; set; }
        public UpdatePackageKind RecommendedPackage { get; set; }
        public DateTimeOffset CachedAtUtc { get; set; }

        public static CachedReleaseDocument FromRelease(GitHubReleaseInfo release, DateTimeOffset cachedAtUtc) => new()
        {
            TagName = release.TagName,
            Version = release.Version.ToString(3),
            ReleasePage = release.ReleasePage.AbsoluteUri,
            DownloadUrl = release.PreferredDownload?.AbsoluteUri,
            Notes = release.Notes,
            PublishedAt = release.PublishedAt,
            RecommendedPackage = release.RecommendedPackage,
            CachedAtUtc = cachedAtUtc
        };

        public GitHubReleaseInfo ToRelease()
        {
            if (SchemaVersion != 1)
                throw new InvalidDataException("更新缓存版本不受支持。");
            if (!TryParseReleaseVersion(TagName, out var tagVersion) ||
                !System.Version.TryParse(Version, out var version) ||
                NormalizeVersion(version).CompareTo(NormalizeVersion(tagVersion!)) != 0)
                throw new InvalidDataException("更新缓存内容无效。");
            if (!TryHttpsUri(ReleasePage, out var releasePage))
                throw new InvalidDataException("更新缓存中的 Release 地址无效。");
            Uri? downloadUrl = null;
            if (DownloadUrl is not null)
            {
                if (!TryHttpsUri(DownloadUrl, out var parsedDownloadUrl))
                    throw new InvalidDataException("更新缓存中的下载地址无效。");
                downloadUrl = parsedDownloadUrl;
            }
            return new GitHubReleaseInfo(
                TagName,
                version,
                releasePage,
                downloadUrl,
                NormalizeNotes(Notes),
                PublishedAt,
                RecommendedPackage,
                UpdateReleaseSource.Cache,
                CachedAtUtc);
        }
    }

    [GeneratedRegex("^v?(?<version>\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();
}
