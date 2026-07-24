using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class TransferRuntimeConfiguration
{
    private readonly object _sync = new();
    private readonly SharedTransferBandwidthLimiter _bandwidthLimiter = new();
    private TransferExecutionOptions _options = new();

    public void Apply(AppSettings settings)
    {
        var options = new TransferExecutionOptions
        {
            MultipartThresholdBytes = settings.MultipartThresholdMb * 1024L * 1024,
            PartSizeBytes = settings.PartSizeMb * 1024L * 1024,
            MultipartConcurrency = settings.MultipartConcurrency,
            UploadBytesPerSecond = settings.UploadLimitKibPerSecond * 1024L,
            DownloadBytesPerSecond = settings.DownloadLimitKibPerSecond * 1024L
        };
        options.Validate();
        _bandwidthLimiter.Configure(options.UploadBytesPerSecond, options.DownloadBytesPerSecond);
        lock (_sync)
            _options = options;
    }

    public TransferOperationContext CreateContext(ITransferTaskExecutionContext context)
    {
        TransferExecutionOptions options;
        lock (_sync)
            options = _options;
        var task = context.Task;
        return new TransferOperationContext(
            options,
            _bandwidthLimiter,
            task.DownloadCheckpoint,
            task.MultipartCheckpoint,
            context.ReportProgress,
            context.UpdateCheckpointAsync);
    }
}
