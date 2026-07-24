using System.Text;
using System.Text.RegularExpressions;

namespace S3Explorer.Core;

public enum TransferBatchState
{
    Discovering,
    Queued,
    Running,
    Completed,
    CompletedWithFailures,
    Cancelled
}

public sealed record TransferBatchSummary(
    Guid BatchId,
    string Name,
    TransferDirection Direction,
    TransferBatchState State,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    int ActiveFiles,
    long TotalBytes,
    long TransferredBytes)
{
    public double ProgressPercentage =>
        TotalBytes <= 0
            ? (TotalFiles == 0 ? 0 : (CompletedFiles + FailedFiles + SkippedFiles) * 100d / TotalFiles)
            : Math.Clamp(TransferredBytes * 100d / TotalBytes, 0, 100);
}

public sealed record TransferFailureDetail(
    Guid TaskId,
    Guid BatchId,
    string RelativePath,
    TransferFailureCategory Category,
    int? HttpStatusCode,
    string? ServiceCode,
    bool Retryable,
    string Message);

public static partial class TransferBatchProjector
{
    [GeneratedRegex(@"(?i)(https?://[^\s?]+)\?[^\s,;]+")]
    private static partial Regex PresignedUrlPattern();

    public static TransferBatchSummary Project(
        TransferBatchRecord batch,
        IEnumerable<TransferTaskRecord> allTasks,
        IReadOnlyDictionary<Guid, long>? liveProgress = null)
    {
        var tasks = allTasks.Where(task => task.BatchId == batch.Id).ToArray();
        var completed = tasks.Count(task => task.State == TransferTaskState.Completed);
        var failed = tasks.Count(task => task.State is TransferTaskState.Failed or TransferTaskState.CleanupPending);
        var cancelledBeforeStart = tasks.Count(task =>
            task.State == TransferTaskState.Cancelled && task.StartedAt is null);
        var skipped = checked(batch.SkippedCount + cancelledBeforeStart);
        var active = tasks.Count(task => task.State is
            TransferTaskState.Queued or
            TransferTaskState.Running or
            TransferTaskState.RetryPending or
            TransferTaskState.Paused or
            TransferTaskState.Interrupted);

        var totalBytes = tasks.Sum(task => task.TotalBytes);
        var transferredBytes = tasks.Sum(task =>
        {
            if (task.State == TransferTaskState.Completed)
                return task.TotalBytes;
            if (liveProgress is not null && liveProgress.TryGetValue(task.Id, out var live))
                return Math.Clamp(live, 0, task.TotalBytes);
            return Math.Clamp(task.TransferredBytes, 0, task.TotalBytes);
        });

        var state = DetermineState(batch, tasks, active, failed);
        return new TransferBatchSummary(
            batch.Id, batch.Name, batch.Direction, state,
            checked(tasks.Length + batch.SkippedCount), completed, failed, skipped, active,
            totalBytes, transferredBytes);
    }

    public static IReadOnlyList<TransferFailureDetail> Failures(
        TransferBatchRecord batch,
        IEnumerable<TransferTaskRecord> allTasks)
    {
        return allTasks
            .Where(task =>
                task.BatchId == batch.Id &&
                task.State is TransferTaskState.Failed or TransferTaskState.CleanupPending)
            .OrderBy(task => task.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id)
            .Select(task =>
            {
                var failure = task.Failure ?? new TransferFailureInfo("未知错误。");
                return new TransferFailureDetail(
                    task.Id, batch.Id,
                    SafeText(string.IsNullOrWhiteSpace(task.RelativePath)
                        ? Path.GetFileName(task.LocalPath)
                        : task.RelativePath),
                    failure.Category, failure.HttpStatusCode, SafeText(failure.ServiceCode),
                    failure.Retryable, SafeText(failure.SafeMessage));
            })
            .ToArray();
    }

    public static string ExportFailuresCsv(
        TransferBatchRecord batch,
        IEnumerable<TransferTaskRecord> allTasks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("RelativePath,Category,HttpStatus,ServiceCode,Retryable,Message");
        foreach (var item in Failures(batch, allTasks))
        {
            builder.Append(Csv(item.RelativePath)).Append(',')
                .Append(Csv(item.Category.ToString())).Append(',')
                .Append(item.HttpStatusCode?.ToString() ?? string.Empty).Append(',')
                .Append(Csv(item.ServiceCode ?? string.Empty)).Append(',')
                .Append(item.Retryable ? "true" : "false").Append(',')
                .Append(Csv(item.Message))
                .AppendLine();
        }
        return builder.ToString();
    }

    private static TransferBatchState DetermineState(
        TransferBatchRecord batch,
        IReadOnlyCollection<TransferTaskRecord> tasks,
        int active,
        int failed)
    {
        if (!batch.DiscoveryCompleted) return TransferBatchState.Discovering;
        if (active > 0)
            return tasks.Any(task => task.State == TransferTaskState.Running)
                ? TransferBatchState.Running
                : TransferBatchState.Queued;
        if (batch.CancellationRequested) return TransferBatchState.Cancelled;
        if (failed > 0) return TransferBatchState.CompletedWithFailures;
        return TransferBatchState.Completed;
    }

    private static string SafeText(string? value)
    {
        var redacted = SensitiveDataRedactor.Redact(value);
        return PresignedUrlPattern().Replace(redacted, "$1?[redacted]");
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
