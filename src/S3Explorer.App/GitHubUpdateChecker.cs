using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S3Explorer.App;

internal sealed record GitHubReleaseInfo(
    string TagName,
    Version Version,
    Uri ReleasePage,
    Uri? PreferredDownload,
    string Notes,
    DateTimeOffset? PublishedAt)
{
    public bool IsNewerThan(Version currentVersion) =>
        GitHubUpdateChecker.NormalizeVersion(Version).CompareTo(
            GitHubUpdateChecker.NormalizeVersion(currentVersion)) > 0;
}

internal sealed partial class GitHubUpdateChecker : IDisposable
{
    private const int MaximumNotesLength = 20_000;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubUpdateChecker(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1024 * 1024
        };
    }

    public async Task<GitHubReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProjectLinks.LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("S3Explorer-UpdateChecker/0.5");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseRelease(payload);
    }

    internal static GitHubReleaseInfo ParseRelease(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        var tagName = RequiredString(root, "tag_name");
        if (!TryParseReleaseVersion(tagName, out var version))
            throw new InvalidDataException($"GitHub Release tag 不是受支持的 vX.Y.Z 版本：{tagName}");

        var releasePage = RequiredHttpsUri(root, "html_url");
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? string.Empty
            : string.Empty;
        notes = string.IsNullOrWhiteSpace(notes) ? "此版本未提供详细说明。" : notes.Trim();
        if (notes.Length > MaximumNotesLength)
            notes = notes[..MaximumNotesLength] + Environment.NewLine + Environment.NewLine + "（版本说明过长，已截断；请打开 Release 查看完整内容。）";

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var published) &&
            published.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(published.GetString(), out var parsedDate))
            publishedAt = parsedDate;

        Uri? preferredDownload = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String ||
                    !string.Equals(name.GetString(), "S3Explorer-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (asset.TryGetProperty("browser_download_url", out var download) &&
                    download.ValueKind == JsonValueKind.String &&
                    TryHttpsUri(download.GetString(), out var uri))
                    preferredDownload = uri;
                break;
            }
        }

        return new GitHubReleaseInfo(tagName, version!, releasePage, preferredDownload, notes, publishedAt);
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
            throw new InvalidDataException($"GitHub Release 响应缺少 {property}。");
        return value.GetString()!;
    }

    private static Uri RequiredHttpsUri(JsonElement root, string property)
    {
        var value = RequiredString(root, property);
        if (!TryHttpsUri(value, out var uri))
            throw new InvalidDataException($"GitHub Release 响应中的 {property} 不是有效 HTTPS 地址。");
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

    [GeneratedRegex("^v?(?<version>\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();
}
