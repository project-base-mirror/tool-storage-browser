using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class FolderSyncReportTests
{
    [Fact]
    public void Latest_execution_and_report_are_rebuilt_from_persisted_batches()
    {
        var job = Job();
        var older = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var oldBatch = Batch(job, older, DateTimeOffset.Parse("2026-07-29T09:00:00Z"));
        var batch = Batch(job, latest, DateTimeOffset.Parse("2026-07-29T10:00:00Z"));
        var completed = Task(batch, "done.bin", 10) with
        {
            State = TransferTaskState.Completed,
            TransferredBytes = 10,
            CompletedAt = DateTimeOffset.Parse("2026-07-29T10:01:00Z")
        };
        var failed = Task(batch, "failed.bin", 20) with
        {
            State = TransferTaskState.Failed,
            Failure = new TransferFailureInfo("SecretKey=hidden https://test/object?X-Amz-Signature=secret", Retryable: true),
            UpdatedAt = DateTimeOffset.Parse("2026-07-29T10:02:00Z")
        };
        var snapshot = new TransferStoreSnapshot
        {
            Batches = [oldBatch, batch],
            Tasks = [completed, failed]
        };

        Assert.Equal(latest, FolderSyncReportProjector.FindLatestExecutionId(job.Id, snapshot.Batches));
        var report = FolderSyncReportProjector.Project(job, latest, snapshot);

        Assert.True(report.IsFinished);
        Assert.Equal(2, report.TotalFiles);
        Assert.Equal(1, report.CompletedFiles);
        Assert.Equal(1, report.FailedFiles);
        Assert.Equal(30, report.TotalBytes);
        Assert.Equal(10, report.TransferredBytes);
        Assert.NotNull(report.CompletedAt);
        Assert.True(report.Items.Single(item => item.RelativePath == "failed.bin").Retryable);
    }

    [Fact]
    public void Json_and_csv_reports_redact_credentials_and_presigned_queries()
    {
        var job = Job();
        var execution = Guid.NewGuid();
        var batch = Batch(job, execution, DateTimeOffset.UtcNow);
        var task = Task(batch, "folder/a,\"b.bin", 5) with
        {
            State = TransferTaskState.Failed,
            Failure = new TransferFailureInfo(
                "AccessKey=visible SecretKey=hidden https://test/object?X-Amz-Credential=value&X-Amz-Signature=secret",
                Retryable: false)
        };
        var report = FolderSyncReportProjector.Project(job, execution, new TransferStoreSnapshot
        {
            Batches = [batch],
            Tasks = [task]
        });

        var json = FolderSyncReportProjector.ExportJson(report);
        var csv = FolderSyncReportProjector.ExportCsv(report);

        Assert.DoesNotContain("hidden", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Amz-Credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Amz-Credential", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("?[redacted]", json, StringComparison.Ordinal);
        Assert.Contains("folder/a,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_metadata_requires_both_sync_identifiers()
    {
        var batch = Batch(Job(), Guid.NewGuid(), DateTimeOffset.UtcNow) with { FolderSyncExecutionId = null };

        Assert.Throws<ArgumentException>(batch.Validate);
    }

    private static FolderSyncJob Job() => new()
    {
        Id = Guid.NewGuid(),
        Name = "deploy",
        LocalDirectory = Path.GetFullPath(Path.GetTempPath()),
        ProfileId = Guid.NewGuid(),
        ProfileName = "profile",
        Bucket = "bucket"
    };

    private static TransferBatchRecord Batch(FolderSyncJob job, Guid execution, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = job.ProfileId,
        ProfileName = job.ProfileName,
        Name = $"同步 {job.Name}",
        Bucket = job.Bucket,
        RootPath = job.LocalDirectory,
        Direction = TransferDirection.Upload,
        FolderSyncJobId = job.Id,
        FolderSyncExecutionId = execution,
        DiscoveryCompleted = true,
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private static TransferTaskRecord Task(TransferBatchRecord batch, string path, long size) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = batch.Id,
        ProfileId = batch.ProfileId,
        ProfileName = batch.ProfileName,
        Direction = batch.Direction,
        Kind = TransferTaskKind.FolderBatchItem,
        Bucket = batch.Bucket,
        ObjectKey = path,
        LocalPath = Path.Combine(Path.GetTempPath(), path.Replace('/', Path.DirectorySeparatorChar)),
        RelativePath = path,
        TotalBytes = size,
        MaxAttempts = 1
    };
}
