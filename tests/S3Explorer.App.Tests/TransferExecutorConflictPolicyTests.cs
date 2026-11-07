using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class TransferExecutorConflictPolicyTests
{
    [Theory]
    [InlineData(TransferDirection.Copy, ObjectConflictPolicy.Skip, false)]
    [InlineData(TransferDirection.Move, ObjectConflictPolicy.Skip, false)]
    [InlineData(TransferDirection.Copy, ObjectConflictPolicy.AutoRename, false)]
    [InlineData(TransferDirection.Move, ObjectConflictPolicy.AutoRename, false)]
    [InlineData(TransferDirection.Copy, ObjectConflictPolicy.Ask, false)]
    [InlineData(TransferDirection.Move, ObjectConflictPolicy.Ask, false)]
    [InlineData(TransferDirection.Copy, ObjectConflictPolicy.Overwrite, true)]
    [InlineData(TransferDirection.Move, ObjectConflictPolicy.Overwrite, true)]
    public async Task CopyAndMovePassPersistedConflictPolicyToAtomicWrite(
        TransferDirection direction, ObjectConflictPolicy policy, bool overwrite)
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);
        var storage = DispatchProxy.Create<IS3StorageService, RecordingStorage>();
        var recorder = (RecordingStorage)storage;
        var context = new ExecutionContext(new TransferTaskRecord
        {
            ProfileId = profile.Id, Direction = direction, Bucket = "source", ObjectKey = "key",
            DestinationBucket = "target", DestinationObjectKey = "copy", ConflictPolicy = policy
        });
        var executor = new S3TransferTaskExecutor(new Profiles(profile), storage,
            new TransferRuntimeConfiguration(), null!, new SimpleFileLogger());
        await executor.ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(direction == TransferDirection.Copy ? "CopyObjectAsync" : "MoveObjectAsync", recorder.Operation);
        Assert.NotNull(recorder.Context);
        Assert.Equal(overwrite, recorder.Context.Options.AllowDestinationOverwrite);
    }

    [Theory]
    [InlineData(ObjectConflictPolicy.Skip, false)]
    [InlineData(ObjectConflictPolicy.AutoRename, false)]
    [InlineData(ObjectConflictPolicy.Overwrite, true)]
    public async Task CrossConnectionCopyUsesIndependentProfilesAndPersistedConflictPolicy(ObjectConflictPolicy policy, bool overwrite)
    {
        var source = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss);
        var destination = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2);
        var storage = DispatchProxy.Create<IS3StorageService, CrossConnectionRecordingStorage>();
        var recorder = (CrossConnectionRecordingStorage)storage;
        var context = new ExecutionContext(new TransferTaskRecord
        {
            ProfileId = source.Id, DestinationProfileId = destination.Id, DestinationProfileName = destination.Name,
            Direction = TransferDirection.Copy, Bucket = "source", ObjectKey = "key",
            DestinationBucket = "target", DestinationObjectKey = "copy", ConflictPolicy = policy
        });
        var executor = new S3TransferTaskExecutor(new Profiles(source, destination), storage,
            new TransferRuntimeConfiguration(), null!, new SimpleFileLogger());
        await executor.ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(nameof(IS3StorageService.CopyObjectAcrossConnectionsAsync), recorder.Operation);
        Assert.Same(source, recorder.Arguments![0]);
        Assert.Same(destination, recorder.Arguments[3]);
        Assert.Equal(overwrite, Assert.IsType<TransferOperationContext>(recorder.Arguments[6]).Options.AllowDestinationOverwrite);
    }

    [Fact]
    public async Task CrossConnectionMultipartCleanupNeedsOnlyDestinationCredentials()
    {
        var source = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss);
        var destination = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2);
        var storage = DispatchProxy.Create<IS3StorageService, CrossConnectionRecordingStorage>();
        var recorder = (CrossConnectionRecordingStorage)storage;
        var context = new ExecutionContext(new TransferTaskRecord
        {
            ProfileId = source.Id, DestinationProfileId = destination.Id, Direction = TransferDirection.Copy,
            Bucket = "source", ObjectKey = "key", DestinationBucket = "target", DestinationObjectKey = "copy",
            MultipartCheckpoint = new("upload", 5 * 1024 * 1024, [], Bucket: "target", ObjectKey: "copy")
        });
        // The source connection may have been removed; abort still targets the destination.
        var executor = new S3TransferTaskExecutor(new Profiles(destination), storage,
            new TransferRuntimeConfiguration(), null!, new SimpleFileLogger());
        await executor.AbortMultipartAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(nameof(IS3StorageService.AbortMultipartUploadAsync), recorder.Operation);
        Assert.Same(destination, recorder.Arguments![0]);
        Assert.Equal("target", recorder.Arguments[1]);
        Assert.Equal("copy", recorder.Arguments[2]);
        Assert.Equal("upload", recorder.Arguments[3]);
    }

    public class CrossConnectionRecordingStorage : DispatchProxy
    {
        public string? Operation;
        public object?[]? Arguments;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Operation = targetMethod!.Name;
            Arguments = args;
            return Task.CompletedTask;
        }
    }

    public class RecordingStorage : DispatchProxy
    {
        public string? Operation;
        public TransferOperationContext? Context;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Operation = targetMethod!.Name;
            Context = Assert.IsType<TransferOperationContext>(args![5]);
            return Task.CompletedTask;
        }
    }

    private sealed class Profiles(params ConnectionProfile[] profiles) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(profiles);
        public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ExecutionContext(TransferTaskRecord task) : ITransferTaskExecutionContext
    {
        public TransferTaskRecord Task { get; } = task;
        public void ReportProgress(TransferProgress progress) { }
        public Task UpdateCheckpointAsync(long transferredBytes, DownloadCheckpoint? downloadCheckpoint,
            MultipartUploadCheckpoint? multipartCheckpoint, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.CompletedTask;
        public Task UpdateDestinationSnapshotAsync(bool destinationExistedBeforeTransfer, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
