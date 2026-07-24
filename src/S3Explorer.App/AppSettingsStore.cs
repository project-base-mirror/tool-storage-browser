using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record AppSettings
{
    public int WindowX { get; init; } = -1;
    public int WindowY { get; init; } = -1;
    public int WindowWidth { get; init; } = 1280;
    public int WindowHeight { get; init; } = 780;
    public int LeftPanelWidth { get; init; } = 270;
    public int TransferPanelHeight { get; init; } = 190;
    public bool ShowTransfers { get; init; } = true;
    public bool RememberLayout { get; init; } = true;
    public bool ConfirmDelete { get; init; } = true;
    public bool ConfirmOverwrite { get; init; } = true;
    public bool AutoConnectLastProfile { get; init; }
    public string DefaultDownloadDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int ObjectPageSize { get; init; } = ObjectListingLimits.DefaultPageSize;
    public int ObjectCacheLimit { get; init; } = ObjectListingLimits.DefaultCacheLimit;
    public int ConcurrentTransfers { get; init; } = 4;
    public int MultipartConcurrency { get; init; } = 4;
    public int MultipartThresholdMb { get; init; } = 64;
    public int PartSizeMb { get; init; } = 16;
    public int RetryCount { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 2;
    public int[] ObjectColumnWidths { get; init; } = [320, 110, 120, 165, 120];
    public int SortColumn { get; init; }
    public bool SortAscending { get; init; } = true;
}

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "S3Explorer",
        "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
                .ConfigureAwait(false) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }
}
