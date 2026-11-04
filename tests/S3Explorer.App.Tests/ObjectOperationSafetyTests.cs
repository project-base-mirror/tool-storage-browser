using System.Reflection;
using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ObjectOperationSafetyTests
{
    [Fact]
    public async Task RenameRefusesExistingTargetWithoutMovingSource()
    {
        var service = DispatchProxy.Create<IS3StorageService, RenameStorage>();
        var recorder = (RenameStorage)(object)service;
        recorder.Exists = true;
        await Assert.ThrowsAsync<IOException>(() => MainForm.RenameObjectSafelyAsync(
            service, new ConnectionProfile(), "bucket", "source.txt", "target.txt", TestContext.Current.CancellationToken));
        Assert.Null(recorder.MoveContext);
    }

    [Fact]
    public async Task RenameRequiresAtomicNonoverwriteAfterSuccessfulPreflight()
    {
        var service = DispatchProxy.Create<IS3StorageService, RenameStorage>();
        var recorder = (RenameStorage)(object)service;
        await MainForm.RenameObjectSafelyAsync(
            service, new ConnectionProfile(), "bucket", "source.txt", "target.txt", TestContext.Current.CancellationToken);
        Assert.NotNull(recorder.MoveContext);
        Assert.False(recorder.MoveContext.Options.AllowDestinationOverwrite);
        Assert.False(recorder.MoveContext.Options.RetainFailedTransferState);
    }

    [Theory]
    [InlineData(TransferDirection.Upload)]
    [InlineData(TransferDirection.DeleteLocal)]
    [InlineData(TransferDirection.Download)]
    public void FolderSyncQueuesActualUnderscoreFilename(TransferDirection direction)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new FolderSyncDialog(null!, null!, null!, new PersistentTransferQueue(null!, null!), new AppSettings());
                var root = Path.Combine(Path.GetTempPath(), "sync-queue-" + Guid.NewGuid().ToString("N"));
                var local = new FolderSyncFile("foo_bar.txt", 8, DateTimeOffset.UtcNow);
                var item = new FolderSyncPlanItem("foo_bar.txt", FolderSyncChange.Changed, FolderSyncAction.Upload, local, null, "test");
                var method = typeof(FolderSyncDialog).GetMethod("CreateTransferTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var task = Assert.IsType<TransferTaskRecord>(method.Invoke(dialog,
                    [new FolderSyncJob { LocalDirectory = root }, new ConnectionProfile(), item, direction]));
                Assert.Equal(LocalObjectPath.ToExtendedLengthPath(Path.Combine(root, "foo_bar.txt")), task.LocalPath);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public class RenameStorage : DispatchProxy
    {
        public bool Exists { get; set; }
        public TransferOperationContext? MoveContext { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(args);
            if (targetMethod!.Name == nameof(IS3StorageService.ObjectExistsAsync)) return Task.FromResult(Exists);
            if (targetMethod.Name == nameof(IS3StorageService.MoveObjectAsync))
            {
                Assert.Equal("source.txt", args[2]);
                Assert.Equal("target.txt", args[4]);
                MoveContext = Assert.IsType<TransferOperationContext>(args[5]);
                return Task.CompletedTask;
            }
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
