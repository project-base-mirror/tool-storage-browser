using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Fact]
    public async Task CopyPasteAcrossConnectionsPreservesBothCredentialsAndIdenticalObjectNames()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var previous = await AccessClipboardAsync(CaptureClipboardSnapshot);
            var root = Path.Combine(Path.GetTempPath(), "s3-cross-paste-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with { Name = "source" };
                var destination = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2) with { Name = "destination" };
                var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
                var fake = (FileClipboardStorageProxy)(object)storage;
                fake.Head = (_, bucket, key) => Task.FromResult(new ObjectProperties(
                    bucket, key, 3, null, "etag", "text/plain", "STANDARD", null, new Dictionary<string, string>()));
                fake.Exists = (profile, bucket, key) =>
                {
                    Assert.Equal(destination.Id, profile.Id);
                    Assert.Equal("original-bucket", bucket);
                    Assert.Equal("original-prefix/remote.txt", key);
                    return Task.FromResult(false);
                };
                await using var queue = new PersistentTransferQueue(
                    new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new CompleteCrossCopyExecutor());
                await queue.InitializeAsync(TestContext.Current.CancellationToken);
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(),
                    new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(source), storage: storage);
                SetField(form, "_profiles", new[] { source, destination });
                PrepareFileClipboardSelection(form, source);
                await InvokeFileClipboardAsync(form, "CopySelectionToObjectClipboardAsync", false);
                SetField(form, "_currentProfile", destination);
                Assert.True((bool)typeof(MainForm).GetMethod("CanPasteFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!);
                await InvokeFileClipboardAsync(form, "PasteObjectClipboardAsync");
                await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
                var task = Assert.Single(queue.Snapshot.Tasks);
                Assert.Equal(source.Id, task.ProfileId);
                Assert.Equal(destination.Id, task.DestinationProfileId);
                Assert.Equal("destination", task.DestinationProfileName);
                Assert.Equal(task.Bucket, task.DestinationBucket);
                Assert.Equal(task.ObjectKey, task.DestinationObjectKey);
                Assert.Equal(ObjectConflictPolicy.Skip, task.ConflictPolicy);
                Assert.Equal(TransferTaskState.Completed, task.State);
                Assert.Equal(0, fake.DownloadCalls);
            }
            finally
            {
                await RestoreClipboardAsync(previous ?? new DataObject());
                Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public async Task CrossConnectionFolderDiscoveryAndConflictChecksStayPinnedDuringNavigation()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-cross-folder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var source = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with { Name = "source" };
                var destination = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2) with { Name = "destination" };
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
                var fake = (FileClipboardStorageProxy)(object)storage;
                fake.RawList = (profile, bucket, prefix, continuation) =>
                {
                    Assert.Equal(source.Id, profile.Id);
                    Assert.Equal("same-bucket", bucket);
                    Assert.Equal("folder/", prefix);
                    return Task.FromResult(continuation is null
                        ? new PagedObjectResult([new("folder/", "folder", 0, false, null, "STANDARD")], "next", true)
                        : new PagedObjectResult([new("folder/nested/file.txt", "file.txt", 4, false, null, "STANDARD")], null, false));
                };
                var lookups = 0;
                fake.Exists = (profile, bucket, key) =>
                {
                    Assert.Equal(destination.Id, profile.Id);
                    Assert.Equal("same-bucket", bucket);
                    Assert.StartsWith("folder/", key);
                    if (Interlocked.Increment(ref lookups) > 1) return Task.FromResult(false);
                    started.TrySetResult();
                    return release.Task;
                };
                await using var queue = new PersistentTransferQueue(
                    new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new CompleteCrossCopyExecutor());
                await queue.InitializeAsync(TestContext.Current.CancellationToken);
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(),
                    new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(source), storage: storage);
                SetField(form, "_profiles", new[] { source, destination });
                SetField(form, "_currentProfile", destination);
                SetField(form, "_currentBucket", "same-bucket");
                var payload = new ObjectClipboardPayload(source.Id, source.Name, "same-bucket",
                    [new("folder/", "folder", true, 0)], false);
                var pending = InvokeFileClipboardAsync(form, "QueueObjectTransferAsync", payload,
                    "same-bucket", "", ObjectConflictPolicy.Skip, destination);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                SetField(form, "_currentProfile", ConnectionProfile.CreatePreset(S3ServiceType.MinIO));
                SetField(form, "_currentBucket", "unrelated");
                release.TrySetResult(false);
                await pending;
                await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, fake.RawListCalls);
                Assert.Equal(2, queue.Snapshot.Tasks.Count);
                Assert.All(queue.Snapshot.Tasks, task =>
                {
                    Assert.Equal(source.Id, task.ProfileId);
                    Assert.Equal(destination.Id, task.DestinationProfileId);
                    Assert.Equal("same-bucket", task.Bucket);
                    Assert.Equal("same-bucket", task.DestinationBucket);
                    Assert.Equal(task.ObjectKey, task.DestinationObjectKey);
                });
                Assert.Contains(queue.Snapshot.Tasks, task => task.ObjectKey == "folder/" && task.TotalBytes == 0);
                Assert.Contains(queue.Snapshot.Tasks, task => task.ObjectKey == "folder/nested/file.txt");
            }
            finally
            {
                release.TrySetResult(false);
                Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public async Task CopyDialogLetsUserSelectTargetConnectionWhileMoveRetainsSource()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var source = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with { Name = "source" };
            var destination = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2) with { Name = "destination" };
            using var copy = new ObjectTransferDialog(false, "bucket", "", 1, source, profiles: [source, destination]);
            var picker = Assert.IsType<ComboBox>(Assert.Single(copy.Controls.Find("ObjectTransferDestinationProfile", true)));
            Assert.Equal(2, picker.Items.Count);
            Assert.Same(source, picker.SelectedItem);
            picker.SelectedItem = destination;
            var bucket = Assert.IsType<BucketPicker>(Assert.Single(copy.Controls.Find("ObjectTransferDestinationBucket", true)));
            Assert.Same(destination, bucket.SelectedProfile);
            bucket.BucketText = "target";
            typeof(ObjectTransferDialog).GetMethod("Confirm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(copy, null);
            Assert.Same(destination, copy.Options!.DestinationProfile);
            Assert.Equal("target", copy.Options.DestinationBucket);
            using var move = new ObjectTransferDialog(true, "bucket", "", 1, source, profiles: [source, destination]);
            var movePicker = Assert.IsType<ComboBox>(Assert.Single(move.Controls.Find("ObjectTransferDestinationProfile", true)));
            Assert.Single(movePicker.Items.Cast<object>());
            Assert.Same(source, movePicker.SelectedItem);
            Assert.False(movePicker.Enabled);
            await Task.CompletedTask;
        });
    }

    private sealed class CompleteCrossCopyExecutor : ITransferTaskExecutor
    {
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortMultipartAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
