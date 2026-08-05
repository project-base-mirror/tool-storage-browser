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
    public bool CheckForUpdatesOnStartup { get; init; } = true;
    public bool KeepRunningInTray { get; init; } = true;
    public bool ShowTrayTransferNotifications { get; init; } = true;
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
    public int UploadLimitKibPerSecond { get; init; }
    public int DownloadLimitKibPerSecond { get; init; }
    public int[] ObjectColumnWidths { get; init; } = [320, 110, 120, 165, 120];
    public int SortColumn { get; init; }
    public bool SortAscending { get; init; } = true;

    public AppSettings Normalize()
    {
        var defaults = new AppSettings();
        var widths = ObjectColumnWidths is { Length: 5 }
            ? ObjectColumnWidths.Select(value => Math.Clamp(value, 40, 2000)).ToArray()
            : defaults.ObjectColumnWidths;
        return this with
        {
            WindowWidth = Math.Clamp(WindowWidth, 960, 10000),
            WindowHeight = Math.Clamp(WindowHeight, 600, 10000),
            LeftPanelWidth = Math.Clamp(LeftPanelWidth, 180, 4000),
            TransferPanelHeight = Math.Clamp(TransferPanelHeight, 120, 3000),
            DefaultDownloadDirectory = string.IsNullOrWhiteSpace(DefaultDownloadDirectory)
                ? defaults.DefaultDownloadDirectory
                : DefaultDownloadDirectory.Trim(),
            ObjectPageSize = Math.Clamp(
                ObjectPageSize,
                ObjectListingLimits.MinimumPageSize,
                ObjectListingLimits.MaximumPageSize),
            ObjectCacheLimit = Math.Clamp(
                ObjectCacheLimit,
                ObjectListingLimits.MinimumCacheLimit,
                ObjectListingLimits.MaximumCacheLimit),
            ConcurrentTransfers = Math.Clamp(ConcurrentTransfers, 1, 32),
            MultipartConcurrency = Math.Clamp(MultipartConcurrency, 1, 32),
            MultipartThresholdMb = Math.Clamp(MultipartThresholdMb, 5, 10240),
            PartSizeMb = Math.Clamp(PartSizeMb, 5, 512),
            RetryCount = Math.Clamp(RetryCount, 0, 20),
            RetryDelaySeconds = Math.Clamp(RetryDelaySeconds, 0, 300),
            UploadLimitKibPerSecond = Math.Clamp(UploadLimitKibPerSecond, 0, 1_048_576),
            DownloadLimitKibPerSecond = Math.Clamp(DownloadLimitKibPerSecond, 0, 1_048_576),
            ObjectColumnWidths = widths,
            SortColumn = Math.Clamp(SortColumn, 0, widths.Length - 1)
        };
    }
}

internal sealed class AppSettingsStore : IRecoveryAwareStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DurableJsonFile _file;

    public AppSettingsStore(string? path = null)
    {
        _file = new DurableJsonFile(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "settings.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _file.LoadAsync(
            static () => new AppSettings(),
            Options,
            useDefaultWhenUnrecoverable: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return settings.Normalize();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _file.SaveAsync(settings.Normalize(), Options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
