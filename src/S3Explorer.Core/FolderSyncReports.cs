using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace S3Explorer.Core;

public sealed record FolderSyncExecutionReportItem(
    Guid TaskId,
    Guid BatchId,
    string RelativePath,
    TransferDirection Direction,
    TransferTaskState State,
    long TotalBytes,
    long TransferredBytes,
    bool Retryable,
    string? Error);

public sealed record FolderSyncExecutionReport(
    Guid ExecutionId,
    Guid JobId,
    string JobName,
    DateTimeOffset QueuedAt,
    DateTimeOffset? CompletedAt,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int CancelledFiles,
    int ActiveFiles,
    int SkippedFiles,
    long TotalBytes,
    long TransferredBytes,
    IReadOnlyList<FolderSyncExecutionReportItem> Items)
{
    public bool IsFinished => ActiveFiles == 0;
}

public static partial class FolderSyncReportProjector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [GeneratedRegex(@"(?i)(https?://[^\s?]+)\?[^\s,;]+")]
    private static partial Regex PresignedUrlPattern();

    public static Guid? FindLatestExecutionId(Guid jobId, IEnumerable<TransferBatchRecord> batches) =>
        batches
            .Where(batch => batch.FolderSyncJobId == jobId && batch.FolderSyncExecutionId is not null)
            .GroupBy(batch => batch.FolderSyncExecutionId!.Value)
            .OrderByDescending(group => group.Max(batch => batch.CreatedAt))
            .Select(group => (Guid?)group.Key)
            .FirstOrDefault();

    public static FolderSyncExecutionReport Project(
        FolderSyncJob job,
        Guid executionId,
        TransferStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(snapshot);
        var batches = snapshot.Batches
            .Where(batch => batch.FolderSyncJobId == job.Id && batch.FolderSyncExecutionId == executionId)
            .OrderBy(batch => batch.CreatedAt)
            .ToArray();
        if (batches.Length == 0)
            throw new InvalidOperationException("找不到该同步任务的执行记录。");

        var batchIds = batches.Select(batch => batch.Id).ToHashSet();
        var tasks = snapshot.Tasks
            .Where(task => task.BatchId is Guid batchId && batchIds.Contains(batchId))
            .OrderBy(task => task.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Direction)
            .ThenBy(task => task.Id)
            .ToArray();
        var active = tasks.Count(task => task.State is
            TransferTaskState.Queued or TransferTaskState.Running or TransferTaskState.RetryPending or
            TransferTaskState.Paused or TransferTaskState.Interrupted);
        var finishedDiscovery = batches.All(batch => batch.DiscoveryCompleted);
        if (!finishedDiscovery) active++;
        DateTimeOffset? completedAt = active == 0
            ? tasks.Select(task => task.CompletedAt ?? task.UpdatedAt)
                .Append(batches.Max(batch => batch.UpdatedAt))
                .Max()
            : null;
        var items = tasks.Select(task => new FolderSyncExecutionReportItem(
            task.Id,
            task.BatchId!.Value,
            SafeText(string.IsNullOrWhiteSpace(task.RelativePath) ? Path.GetFileName(task.LocalPath) : task.RelativePath),
            task.Direction,
            task.State,
            task.TotalBytes,
            task.State == TransferTaskState.Completed
                ? task.TotalBytes
                : Math.Clamp(task.TransferredBytes, 0, task.TotalBytes),
            task.Failure?.Retryable == true,
            task.Failure is null ? null : SafeText(task.Failure.SafeMessage)))
            .ToArray();

        return new FolderSyncExecutionReport(
            executionId,
            job.Id,
            job.Name,
            batches.Min(batch => batch.CreatedAt),
            completedAt,
            tasks.Length + batches.Sum(batch => batch.SkippedCount),
            tasks.Count(task => task.State == TransferTaskState.Completed),
            tasks.Count(task => task.State is TransferTaskState.Failed or TransferTaskState.CleanupPending),
            tasks.Count(task => task.State == TransferTaskState.Cancelled),
            active,
            batches.Sum(batch => batch.SkippedCount),
            tasks.Sum(task => task.TotalBytes),
            items.Sum(item => item.TransferredBytes),
            items);
    }

    public static string ExportJson(FolderSyncExecutionReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string ExportCsv(FolderSyncExecutionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("RelativePath,Direction,State,TotalBytes,TransferredBytes,Retryable,Error");
        foreach (var item in report.Items)
        {
            builder.Append(Csv(item.RelativePath)).Append(',')
                .Append(Csv(item.Direction.ToString())).Append(',')
                .Append(Csv(item.State.ToString())).Append(',')
                .Append(item.TotalBytes).Append(',')
                .Append(item.TransferredBytes).Append(',')
                .Append(item.Retryable ? "true" : "false").Append(',')
                .Append(Csv(item.Error ?? string.Empty))
                .AppendLine();
        }
        return builder.ToString();
    }

    private static string SafeText(string? value)
    {
        var redacted = SensitiveDataRedactor.Redact(value);
        return PresignedUrlPattern().Replace(redacted, "$1?[redacted]");
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
