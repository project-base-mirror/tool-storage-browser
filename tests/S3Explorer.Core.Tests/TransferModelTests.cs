using System.Text.Json;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferModelTests
{
    private static TransferTaskRecord CreateTask(TransferTaskState state = TransferTaskState.Queued) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        ProfileName = "test-profile",
        Direction = TransferDirection.Download,
        State = state,
        Bucket = "assets",
        ObjectKey = "folder/file.bin",
        LocalPath = @"C:\downloads\file.bin",
        TotalBytes = 100,
        TransferredBytes = state == TransferTaskState.Completed ? 100 : 0,
        MaxAttempts = 3
    };

    [Fact]
    public void RunningTasksRecoverAsInterrupted()
    {
        var running = CreateTask(TransferTaskState.Running) with { AttemptCount = 1 };
        var queued = CreateTask();
        var now = DateTimeOffset.Parse("2026-07-24T10:00:00Z");

        var recovered = TransferTaskStateMachine.RecoverInterrupted(new TransferStoreSnapshot
        {
            Tasks = [running, queued]
        }, now);

        Assert.Equal(TransferTaskState.Interrupted, recovered.Tasks[0].State);
        Assert.Equal(now, recovered.Tasks[0].UpdatedAt);
        Assert.Equal(TransferTaskState.Queued, recovered.Tasks[1].State);
    }

    [Fact]
    public void StateTransitionsAreIdempotentAndRejectIllegalJumps()
    {
        var task = CreateTask();
        var now = DateTimeOffset.UtcNow;

        Assert.Same(task, TransferTaskStateMachine.Transition(task, TransferTaskState.Queued, now));
        var running = TransferTaskStateMachine.Transition(task, TransferTaskState.Running, now);
        Assert.Equal(1, running.AttemptCount);
        var completed = TransferTaskStateMachine.Transition(running, TransferTaskState.Completed, now.AddSeconds(1));
        Assert.Equal(100, completed.TransferredBytes);
        Assert.Throws<InvalidOperationException>(() =>
            TransferTaskStateMachine.Transition(completed, TransferTaskState.Running, now.AddSeconds(2)));
    }

    [Fact]
    public void RetryAndPauseTransitionsRemainRecoverable()
    {
        var now = DateTimeOffset.UtcNow;
        var running = TransferTaskStateMachine.Transition(CreateTask(), TransferTaskState.Running, now);
        var paused = TransferTaskStateMachine.Transition(running, TransferTaskState.Paused, now.AddSeconds(1));
        var queued = TransferTaskStateMachine.Transition(paused, TransferTaskState.Queued, now.AddSeconds(2));
        var failed = TransferTaskStateMachine.Transition(
            TransferTaskStateMachine.Transition(queued, TransferTaskState.Running, now.AddSeconds(3)),
            TransferTaskState.Failed,
            now.AddSeconds(4),
            new TransferFailureInfo("temporary", Retryable: true));

        Assert.True(paused.CanResume);
        Assert.True(failed.CanResume);
        Assert.True(failed.Failure!.Retryable);
    }

    [Fact]
    public void SnapshotSerializationContainsNoCredentialFields()
    {
        var snapshot = new TransferStoreSnapshot { Tasks = [CreateTask()] };
        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("SecretKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SessionToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Amz-Signature", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DownloadAndMultipartCheckpointsValidateBoundsAndUniqueness()
    {
        new DownloadCheckpoint(@"C:\temp\file.part", 50, 100, "etag").Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownloadCheckpoint(@"C:\temp\file.part", 101, 100).Validate());

        var multipart = new MultipartUploadCheckpoint("upload-id", 5L * 1024 * 1024,
        [
            new MultipartPartCheckpoint(1, "etag-1", 5L * 1024 * 1024),
            new MultipartPartCheckpoint(2, "etag-2", 5L * 1024 * 1024)
        ]);
        multipart.Validate();
        Assert.Throws<ArgumentException>(() => (multipart with
        {
            CompletedParts = [
                new MultipartPartCheckpoint(1, "a", 5L * 1024 * 1024),
                new MultipartPartCheckpoint(1, "b", 5L * 1024 * 1024)]
        }).Validate());
    }

    [Fact]
    public void BatchRejectsDuplicateTaskIds()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new TransferBatchRecord
        {
            Name = "folder",
            Direction = TransferDirection.Upload,
            TaskIds = [id, id]
        }.Validate());
    }
}
