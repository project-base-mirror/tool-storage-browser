using System.Collections.Concurrent;
using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class TransferPathIntegrityTests
{
    [Theory]
    [InlineData(FolderSyncAction.Upload)]
    [InlineData(FolderSyncAction.DeleteLocal)]
    public async Task SyncUsesExactScannedFileWhenEscapeLikeNeighborExists(FolderSyncAction action)
    {
        var root = CreateRoot();
        try
        {
            var source = Path.Combine(root, "foo_bar.txt");
            var neighbor = Path.Combine(root, "foo_x005F_bar.txt");
            await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(neighbor, "untouched", TestContext.Current.CancellationToken);
            var local = new FolderSyncFile("foo_bar.txt", 8, DateTimeOffset.UtcNow);
            var plan = new FolderSyncPlan(Guid.NewGuid(), DateTimeOffset.UtcNow,
                [new("foo_bar.txt", FolderSyncChange.Changed, action, local, null, "test")]);
            var job = new FolderSyncJob { LocalDirectory = root, Bucket = "bucket", Prefix = "root/" };
            var storage = DispatchProxy.Create<IS3StorageService, StorageRecorder>();
            var recorder = (StorageRecorder)(object)storage;
            var method = typeof(Program).GetMethod("ExecuteSyncPlanAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var result = await Assert.IsAssignableFrom<Task<int>>(method.Invoke(null,
                [job, new ConnectionProfile(), plan, storage, Runtime(), TestContext.Current.CancellationToken]));
            Assert.Equal(1, result);
            Assert.Equal("untouched", await File.ReadAllTextAsync(neighbor, TestContext.Current.CancellationToken));
            if (action == FolderSyncAction.Upload)
                Assert.Equal("original", Assert.Single(recorder.Uploaded).Value);
            else Assert.False(File.Exists(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecursiveDownloadKeepsCaseAliasesAndExistingFileWithoutOverwriting()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "existing", TestContext.Current.CancellationToken);
            var storage = DispatchProxy.Create<IS3StorageService, StorageRecorder>();
            var recorder = (StorageRecorder)(object)storage;
            recorder.Entries = [Entry("root/a.txt"), Entry("root/A.txt")];
            var method = typeof(Program).GetMethod("DownloadPathAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var result = await Assert.IsAssignableFrom<Task<int>>(method.Invoke(null,
                [storage, new ConnectionProfile(), new S3Location("profile", "bucket", "root/"), root, true, Runtime(), TestContext.Current.CancellationToken]));
            Assert.Equal(2, result);
            Assert.Equal(2, recorder.Downloaded.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(root, "A.txt"), TestContext.Current.CancellationToken));
            foreach (var entry in recorder.Downloaded)
                Assert.Equal(entry.Key, await File.ReadAllTextAsync(entry.Value, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DownloadPlanIsIndependentOfListingOrderAndDetectsFileDirectoryAlias()
    {
        var root = CreateRoot();
        try
        {
            var entries = new[] { Entry("root/A.txt"), Entry("root/a.txt"), Entry("root/file"), Entry("root/file.s3explorer.download") };
            var first = CliDownloadPlanner.Plan(root, "root/", entries).Select(item => (item.ObjectKey, item.LocalPath));
            var second = CliDownloadPlanner.Plan(root, "root/", entries.Reverse()).Select(item => (item.ObjectKey, item.LocalPath));
            Assert.Equal(first, second);
            Assert.Throws<InvalidOperationException>(() => CliDownloadPlanner.Plan(root, "root/", [Entry("root/file"), Entry("root/file/child")]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NonpersistentCliContextsDoNotRetainAbandonedTransfers() => Assert.False(Runtime().CreateContext().Options.RetainFailedTransferState);

    private static CliTransferRuntime Runtime() => CliTransferRuntime.Create(CliArguments.Parse(["download", "--transfers", "2"]));
    private static S3ObjectEntry Entry(string key) => new(key, key, 8, false, null, "STANDARD");
    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cli-path-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public class StorageRecorder : DispatchProxy
    {
        public S3ObjectEntry[] Entries { get; set; } = [];
        public ConcurrentDictionary<string, string> Uploaded { get; } = new();
        public ConcurrentDictionary<string, string> Downloaded { get; } = new();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(args);
            return targetMethod!.Name switch
            {
                nameof(IS3StorageService.ListObjectsAsync) => Task.FromResult(new PagedObjectResult(Entries, null, false)),
                nameof(IS3StorageService.UploadFileAsync) => Upload((string)args[2]!, (string)args[3]!, (CancellationToken)args[^1]!),
                nameof(IS3StorageService.DownloadFileAsync) => Download((string)args[2]!, (string)args[3]!, (TransferOperationContext)args[4]!, (CancellationToken)args[^1]!),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }
        private async Task Upload(string key, string path, CancellationToken token) => Uploaded[key] = await File.ReadAllTextAsync(path, token);
        private async Task Download(string key, string path, TransferOperationContext context, CancellationToken token)
        {
            Assert.False(context.Options.AllowDestinationOverwrite);
            Assert.False(context.Options.RetainFailedTransferState);
            await File.WriteAllTextAsync(path, key, token);
            Downloaded[key] = path;
        }
    }
}
