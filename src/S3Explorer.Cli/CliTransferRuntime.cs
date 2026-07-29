using S3Explorer.Core;

namespace S3Explorer.Cli;

internal sealed record CliTransferSettings
{
    public int Transfers { get; init; } = 4;
    public int MultipartConcurrency { get; init; } = 4;
    public long MultipartThresholdBytes { get; init; } = 64L * 1024 * 1024;
    public long PartSizeBytes { get; init; } = 16L * 1024 * 1024;
    public long UploadBytesPerSecond { get; init; }
    public long DownloadBytesPerSecond { get; init; }

    public static CliTransferSettings Parse(CliArguments args)
    {
        var settings = new CliTransferSettings
        {
            Transfers = ParseInt(args.Optional("transfers"), "--transfers", 1, 32, 4),
            MultipartConcurrency = ParseInt(
                args.Optional("multipart-concurrency"), "--multipart-concurrency", 1, 32, 4),
            MultipartThresholdBytes = ParseMib(
                args.Optional("multipart-threshold"), "--multipart-threshold", 5, 5_120, 64),
            PartSizeBytes = ParseMib(args.Optional("part-size"), "--part-size", 5, 5_120, 16),
            UploadBytesPerSecond = ParseKibPerSecond(args.Optional("upload-limit"), "--upload-limit"),
            DownloadBytesPerSecond = ParseKibPerSecond(args.Optional("download-limit"), "--download-limit")
        };
        settings.ToExecutionOptions().Validate();
        return settings;
    }

    public TransferExecutionOptions ToExecutionOptions() => new()
    {
        MultipartThresholdBytes = MultipartThresholdBytes,
        PartSizeBytes = PartSizeBytes,
        MultipartConcurrency = MultipartConcurrency,
        UploadBytesPerSecond = UploadBytesPerSecond,
        DownloadBytesPerSecond = DownloadBytesPerSecond
    };

    private static int ParseInt(string? value, string option, int minimum, int maximum, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
            throw new CliUsageException($"{option} 必须是 {minimum}–{maximum} 之间的整数。");
        return parsed;
    }

    private static long ParseMib(string? value, string option, int minimum, int maximum, int defaultValue) =>
        checked(ParseInt(value, option, minimum, maximum, defaultValue) * 1024L * 1024L);

    private static long ParseKibPerSecond(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (!long.TryParse(value, out var parsed) || parsed < 0 || parsed > 2_147_483_647)
            throw new CliUsageException($"{option} 必须是 0–2147483647 之间的整数（KiB/s，0 表示不限速）。");
        return checked(parsed * 1024L);
    }
}

internal sealed class CliTransferRuntime
{
    private readonly SharedTransferBandwidthLimiter _bandwidthLimiter = new();

    private CliTransferRuntime(CliTransferSettings settings)
    {
        Settings = settings;
        _bandwidthLimiter.Configure(settings.UploadBytesPerSecond, settings.DownloadBytesPerSecond);
    }

    public CliTransferSettings Settings { get; }

    public static CliTransferRuntime Create(CliArguments args) => new(CliTransferSettings.Parse(args));

    public TransferOperationContext CreateContext() => new(
        Settings.ToExecutionOptions(), _bandwidthLimiter, null, null, _ => { },
        (_, _, _, _) => Task.CompletedTask);

    public Task ForEachAsync<T>(
        IEnumerable<T> values,
        Func<T, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(
            values,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Settings.Transfers,
                CancellationToken = cancellationToken
            },
            operation);
}
