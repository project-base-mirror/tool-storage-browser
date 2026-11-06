using System.Collections.Specialized;
using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Fact]
    public async Task FileDropPasteUsesCapturedDestinationAndNeverDeletesCutSource()
    {
        await RunMessagePumpAsync(async _ =>
        {
            var previous = await AccessClipboardAsync(CaptureClipboardSnapshot);
            var root = Path.Combine(Path.GetTempPath(), "s3-file-paste-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "文件 + 100%.txt");
            await File.WriteAllTextAsync(source, "source remains intact", TestContext.Current.CancellationToken);
            var profile = new ConnectionProfile { Name = "original", Endpoint = "https://original.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
            var fake = (FileClipboardStorageProxy)(object)storage;
            var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLookup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.Exists = (actualProfile, bucket, key) =>
            {
                Assert.Equal(profile.Id, actualProfile.Id);
                Assert.Equal("original-bucket", bucket);
                Assert.Equal("original-prefix/文件 + 100%.txt", key);
                lookupStarted.TrySetResult();
                return releaseLookup.Task;
            };
            var upload = new PausedFakeUpload();
            fake.Upload = upload;
            var runtime = new TransferRuntimeConfiguration();
            var logger = new SimpleFileLogger(Path.Combine(root, "logs"));
            var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")),
                new S3TransferTaskExecutor(new SingleProfileStore(profile), storage, runtime, new EmptyCdnConfigurationStore(), logger));
            try
            {
                await queue.InitializeAsync(TestContext.Current.CancellationToken);
                using var form = CreateForm(queue, runtime, logger, new SingleProfileStore(profile), storage: storage);
                PrepareFileClipboardSelection(form, profile);
                // Establish a real owned clipboard payload before another application replaces it.
                await InvokeFileClipboardAsync(form, "CopySelectionToObjectClipboardAsync", true);
                var data = new DataObject();
                data.SetFileDropList(new StringCollection { source });
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2))); // Explorer cut
                await RestoreClipboardAsync(data);
                form.TransferConflictPolicySelector = _ => throw new InvalidOperationException("Clear destinations must not prompt.");
                var paste = InvokeFileClipboardAsync(form, "PasteObjectClipboardAsync");
                await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                SetField(form, "_currentProfile", new ConnectionProfile { Name = "other", Endpoint = "https://other.example.test" });
                SetField(form, "_currentBucket", "other-bucket");
                SetField(form, "_currentPrefix", "other-prefix/");
                releaseLookup.TrySetResult(false);
                await paste;
                await upload.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                upload.Release.TrySetResult();
                await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
                var queued = Assert.Single(queue.Snapshot.Tasks);
                Assert.Equal(TransferTaskState.Completed, queued.State);
                Assert.Equal(profile.Id, queued.ProfileId);
                Assert.Equal("original-bucket", queued.Bucket);
                Assert.Equal("original-prefix/文件 + 100%.txt", queued.ObjectKey);
                Assert.Equal(source, queued.LocalPath);
                Assert.Equal("source remains intact", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
                Assert.Null(GetField<ObjectClipboardPayload?>(form, "_objectClipboard"));
                Assert.Equal(0, fake.DownloadCalls);
            }
            finally
            {
                releaseLookup.TrySetResult(false);
                upload.Release.TrySetResult();
                await queue.DisposeAsync();
                await RestoreClipboardAsync(previous ?? new DataObject());
                Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public async Task TextReplacementCannotPasteStaleRemoteSelection()
    {
        await RunMessagePumpAsync(async _ =>
        {
            var previous = await AccessClipboardAsync(CaptureClipboardSnapshot);
            var root = Path.Combine(Path.GetTempPath(), "s3-text-paste-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var profile = new ConnectionProfile { Name = "test", Endpoint = "https://storage.example.test" };
            var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
            try
            {
                await queue.InitializeAsync(TestContext.Current.CancellationToken);
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(), new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(profile));
                PrepareFileClipboardSelection(form, profile);
                await InvokeFileClipboardAsync(form, "CopySelectionToObjectClipboardAsync", true);
                var replacement = new DataObject();
                replacement.SetText("s3://original-bucket/remote.txt");
                await RestoreClipboardAsync(replacement);
                await InvokeFileClipboardAsync(form, "PasteObjectClipboardAsync");
                Assert.Empty(queue.Snapshot.Tasks);
                Assert.Null(GetField<ObjectClipboardPayload?>(form, "_objectClipboard"));
                Assert.Equal("s3://original-bucket/remote.txt", await AccessClipboardAsync(Clipboard.GetText));
            }
            finally
            {
                await queue.DisposeAsync();
                await RestoreClipboardAsync(previous ?? new DataObject());
                Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public async Task FreezeManifestUsesHeadIdentityWithoutDownloadingObject()
    {
        var profile = new ConnectionProfile { Name = "test", Endpoint = "https://storage.example.test" };
        var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
        var fake = (FileClipboardStorageProxy)(object)storage;
        var modified = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        fake.Head = (actualProfile, bucket, key) =>
        {
            Assert.Equal(profile.Id, actualProfile.Id);
            Assert.Equal("bucket", bucket);
            Assert.Equal("prefix/remote.txt", key);
            return Task.FromResult(new ObjectProperties(bucket, key, 42, modified, "head-etag", "text/plain", "STANDARD", "version-7", new Dictionary<string, string>()));
        };
        var selected = new S3ObjectEntry("prefix/remote.txt", "misleading-label.txt", 1, false, null, "STANDARD", "stale-list-etag");
        var manifest = await MainForm.FreezeClipboardManifestAsync(storage, profile, "bucket", "prefix/", [selected], TestContext.Current.CancellationToken);
        var file = Assert.Single(manifest);
        Assert.Equal("remote.txt", file.RelativePath);
        Assert.Equal(selected.Key, file.Key);
        Assert.Equal(42, file.Size);
        Assert.Equal(modified, file.LastModified);
        Assert.Equal("head-etag", file.ETag);
        Assert.Equal("version-7", file.VersionId);
        Assert.Equal(1, fake.HeadCalls);
        Assert.Equal(0, fake.DownloadCalls);
    }

    [Fact]
    public async Task FreezeManifestRejectsNonemptyRootMarkerHiddenByOrdinaryListing()
    {
        var profile = new ConnectionProfile { Name = "test", Endpoint = "https://storage.example.test" };
        var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
        var fake = (FileClipboardStorageProxy)(object)storage;
        fake.RawList = (actualProfile, bucket, prefix, continuation) =>
        {
            Assert.Equal(profile.Id, actualProfile.Id);
            Assert.Equal("bucket", bucket);
            Assert.Equal("prefix/folder/", prefix);
            Assert.Null(continuation);
            return Task.FromResult(new PagedObjectResult([
                new S3ObjectEntry(prefix, "folder", 7, true, null, "STANDARD")], null, false));
        };
        var folder = new S3ObjectEntry("prefix/folder/", "folder", 0, true, null, "STANDARD");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MainForm.FreezeClipboardManifestAsync(
            storage, profile, "bucket", "prefix/", [folder], TestContext.Current.CancellationToken));
        Assert.Equal(1, fake.RawListCalls);
        Assert.Equal(0, fake.ListRequests); // Ordinary listing would return empty and silently lose bytes.
        Assert.Equal(0, fake.HeadCalls);
        Assert.Equal(0, fake.DownloadCalls);
    }

    [Fact]
    public async Task FreezeManifestUsesFlatRawListingAndPreservesEmptyChildMarker()
    {
        var profile = new ConnectionProfile { Name = "test", Endpoint = "https://storage.example.test" };
        var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
        var fake = (FileClipboardStorageProxy)(object)storage;
        fake.RawList = (_, bucket, prefix, continuation) =>
        {
            Assert.Equal("bucket", bucket);
            Assert.Equal("prefix/folder/", prefix); // Child markers must not cause another recursive listing.
            Assert.Null(continuation);
            return Task.FromResult(new PagedObjectResult([
                new S3ObjectEntry(prefix, "folder", 0, true, null, "STANDARD"),
                new S3ObjectEntry(prefix + "empty/", "empty", 0, true, null, "STANDARD"),
                new S3ObjectEntry(prefix + "file.txt", "file.txt", 3, false, null, "STANDARD")], null, false));
        };
        fake.Head = (_, bucket, key) =>
        {
            Assert.Equal("prefix/folder/file.txt", key);
            return Task.FromResult(new ObjectProperties(bucket, key, 3, null, "etag", "text/plain", "STANDARD", "v1", new Dictionary<string, string>()));
        };
        var folder = new S3ObjectEntry("prefix/folder/", "folder", 0, true, null, "STANDARD");
        var manifest = await MainForm.FreezeClipboardManifestAsync(
            storage, profile, "bucket", "prefix/", [folder], TestContext.Current.CancellationToken);
        Assert.Equal(3, manifest.Count);
        Assert.Contains(manifest, entry => entry.Key == "prefix/folder/" && entry.IsDirectory);
        Assert.Contains(manifest, entry => entry.Key == "prefix/folder/empty/" && entry.IsDirectory && entry.Size == 0);
        Assert.Contains(manifest, entry => entry.Key == "prefix/folder/file.txt" && !entry.IsDirectory && entry.ETag == "etag");
        Assert.Equal(1, fake.RawListCalls);
        Assert.Equal(0, fake.ListRequests);
        Assert.Equal(1, fake.HeadCalls);
        Assert.Equal(0, fake.DownloadCalls);
    }

    [Fact]
    public async Task ClipboardReplacementWhileHeadIsPendingCannotRestoreOldCopy()
    {
        await RunMessagePumpAsync(async _ =>
        {
            var previous = await AccessClipboardAsync(CaptureClipboardSnapshot);
            var root = Path.Combine(Path.GetTempPath(), "s3-head-clipboard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var profile = new ConnectionProfile { Name = "test", Endpoint = "https://storage.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
            var fake = (FileClipboardStorageProxy)(object)storage;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<ObjectProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.Head = (_, _, _) => { started.TrySetResult(); return release.Task; };
            var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
            Task? copy = null;
            var properties = new ObjectProperties("original-bucket", "original-prefix/remote.txt", 3, null, "etag", "text/plain", "STANDARD", "v1", new Dictionary<string, string>());
            try
            {
                await queue.InitializeAsync(TestContext.Current.CancellationToken);
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(), new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(profile), storage: storage);
                PrepareFileClipboardSelection(form, profile);
                copy = InvokeFileClipboardAsync(form, "CopySelectionToObjectClipboardAsync", false);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                var replacement = new DataObject();
                replacement.SetText("new clipboard owner");
                await RestoreClipboardAsync(replacement);
                release.TrySetResult(properties);
                await copy.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal("new clipboard owner", await AccessClipboardAsync(Clipboard.GetText));
                Assert.False(await AccessClipboardAsync(() => Clipboard.ContainsData("FileGroupDescriptorW")));
                await InvokeFileClipboardAsync(form, "PasteObjectClipboardAsync");
                Assert.Empty(queue.Snapshot.Tasks);
                Assert.Equal(0, fake.DownloadCalls);
            }
            finally
            {
                release.TrySetResult(properties);
                if (copy is not null) await copy.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await queue.DisposeAsync();
                await RestoreClipboardAsync(previous ?? new DataObject());
                Directory.Delete(root, true);
            }
        });
    }

    private static Task InvokeFileClipboardAsync(MainForm form, string method, params object[] arguments) =>
        Assert.IsAssignableFrom<Task>(typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments));

    private static void PrepareFileClipboardSelection(MainForm form, ConnectionProfile profile)
    {
        SetField(form, "_currentProfile", profile);
        SetField(form, "_currentBucket", "original-bucket");
        SetField(form, "_currentPrefix", "original-prefix/");
        var objects = GetField<ListView>(form, "_objects");
        _ = form.Handle;
        _ = objects.Handle;
        objects.Items.Clear();
        var entry = new S3ObjectEntry("original-prefix/remote.txt", "remote.txt", 3, false, null, "STANDARD");
        var item = new ListViewItem(entry.Name) { Tag = entry };
        objects.Items.Add(item);
        item.Selected = true;
        item.Focused = true;
    }

    public class FileClipboardStorageProxy : FakeStorageProxy
    {
        public Func<ConnectionProfile, string, string, Task<ObjectProperties>>? Head { get; set; }
        public Func<ConnectionProfile, string, string, string?, Task<PagedObjectResult>>? RawList { get; set; }
        public int RawListCalls;
        public int HeadCalls;
        public int DownloadCalls;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IS3StorageService.ListObjectsForDeletionAsync))
            {
                Interlocked.Increment(ref RawListCalls);
                return (RawList ?? throw new InvalidOperationException("Unexpected raw listing"))(
                    (ConnectionProfile)args![0]!, (string)args[1]!, (string)args[2]!, (string?)args[3]);
            }
            if (targetMethod?.Name == nameof(IS3StorageService.GetObjectPropertiesAsync))
            {
                Interlocked.Increment(ref HeadCalls);
                return (Head ?? throw new InvalidOperationException("Unexpected HEAD"))((ConnectionProfile)args![0]!, (string)args[1]!, (string)args[2]!);
            }
            if (targetMethod?.Name is nameof(IS3StorageService.DownloadFileAsync) or nameof(IS3StorageService.DownloadObjectVersionAsync))
            {
                Interlocked.Increment(ref DownloadCalls);
                throw new InvalidOperationException("Copying descriptors must not download data.");
            }
            return base.Invoke(targetMethod, args);
        }
    }
}
