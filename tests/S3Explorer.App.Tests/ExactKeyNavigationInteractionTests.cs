using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Fact]
    public void DownloadPathsUseExactKeysAndSafeLocalNamesRatherThanLabels()
    {
        var folder = new S3ObjectEntry("/root//", "friendly empty label", 0, true, null, "");
        var relative = MainForm.DownloadRelativeKey(folder, "/root///back\\%2F.txt");
        Assert.Equal("//back\\%2F.txt", relative);
        var root = Path.Combine(Path.GetTempPath(), "s3-exact-download");
        Assert.EndsWith(Path.Combine("_empty_", "_empty_", "back_x005C_%2F.txt"),
            LocalObjectPath.MapRelativeKey(root, relative));
        var file = new S3ObjectEntry("root/ ", "friendly whitespace label", 0, false, null, "STANDARD");
        Assert.Equal(" ", MainForm.DownloadRelativeKey(file, file.Key));
        Assert.Equal("_x0020_", MainForm.SuggestedDownloadFileName(file.Key));
        Assert.Equal("_x002E__x002E_", MainForm.SuggestedDownloadFileName("root/.."));
        Assert.Throws<InvalidOperationException>(() => MainForm.DownloadRelativeKey(folder, "/root/other"));
    }

    [Fact]
    public async Task AddressUpBackAndForwardPreserveEmptyAndLiteralSegments()
    {
        await RunMessagePumpAsync(async _ =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-exact-navigation-" + Guid.NewGuid().ToString("N"));
            var profile = new ConnectionProfile { Name = "profile", Endpoint = "https://s3.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, ExactKeyStorageProxy>();
            var recorder = (ExactKeyStorageProxy)(object)storage;
            await using var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
            await queue.InitializeAsync();
            using var form = CreateForm(queue, new TransferRuntimeConfiguration(), new SimpleFileLogger(Path.Combine(root, "logs")),
                new SingleProfileStore(profile), storage: storage);
            SetField(form, "_profiles", new[] { profile });
            var prefix = "/root//back\\slash/%2F/../";
            var address = GetField<ToolStripTextBox>(form, "_address");
            address.Text = new S3Location(profile.Name, "bucket", prefix).ToString();
            await InvokeAsync(form, "NavigateAddressAsync");
            Assert.Equal(prefix, GetField<string>(form, "_currentPrefix"));
            Assert.Equal(prefix, Assert.Single(recorder.Prefixes));
            await InvokeAsync(form, "NavigateUpAsync");
            Assert.Equal("/root//back\\slash/%2F/", GetField<string>(form, "_currentPrefix"));
            await InvokeAsync(form, "NavigateHistoryAsync", -1);
            Assert.Equal(prefix, GetField<string>(form, "_currentPrefix"));
            await InvokeAsync(form, "NavigateHistoryAsync", 1);
            Assert.Equal("/root//back\\slash/%2F/", GetField<string>(form, "_currentPrefix"));
            await InvokeAsync(form, "NavigateAsync", profile, "bucket", "//", true);
            await InvokeAsync(form, "NavigateUpAsync");
            Assert.Equal("/", GetField<string>(form, "_currentPrefix"));
            await InvokeAsync(form, "NavigateUpAsync");
            Assert.Equal("", GetField<string>(form, "_currentPrefix"));
            Assert.Equal(new[] { prefix, "/root//back\\slash/%2F/", prefix,
                "/root//back\\slash/%2F/", "//", "/", "" }, recorder.Prefixes);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        });
    }

    [Fact]
    public async Task CopyUsesLiteralKeyNotLabelAndIncludesEmptyFolderMarker()
    {
        await RunMessagePumpAsync(async _ =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-exact-copy-" + Guid.NewGuid().ToString("N"));
            var profile = new ConnectionProfile { Name = "profile", Endpoint = "https://s3.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, ExactKeyStorageProxy>();
            var recorder = (ExactKeyStorageProxy)(object)storage;
            recorder.RawItems = [
                new("/source//", "never use marker label", 0, false, null, "STANDARD"),
                new("/source///back\\%2F", "never use file label", 1, false, null, "STANDARD")
            ];
            await using var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
            await queue.InitializeAsync();
            using var form = CreateForm(queue, new TransferRuntimeConfiguration(), new SimpleFileLogger(Path.Combine(root, "logs")),
                new SingleProfileStore(profile), storage: storage);
            SetField(form, "_currentProfile", profile);
            SetField(form, "_currentBucket", "source");
            var payload = new ObjectClipboardPayload(profile.Id, profile.Name, "source",
                [new("/source//", "friendly empty segment label", true, 0), new(" ", "friendly whitespace label", false, 0)], false);
            await InvokeAsync(form, "QueueObjectTransferAsync", payload, "destination", "/destination//", ObjectConflictPolicy.Overwrite);
            await queue.WaitForIdleAsync();
            Assert.Equal("/source//", Assert.Single(recorder.RawPrefixes));
            var tasks = queue.Snapshot.Tasks.ToDictionary(task => task.ObjectKey, StringComparer.Ordinal);
            Assert.Equal("/destination///", tasks["/source//"].DestinationObjectKey);
            Assert.Equal("/destination////back\\%2F", tasks["/source///back\\%2F"].DestinationObjectKey);
            Assert.Equal("/destination// ", tasks[" "].DestinationObjectKey);
            Assert.DoesNotContain(tasks.Values, task => task.DestinationObjectKey.Contains("label", StringComparison.Ordinal));
            Directory.Delete(root, true);
        });
    }

    [Fact]
    public async Task LiteralDotDirectoryCannotBeMistakenForUpRow()
    {
        await RunMessagePumpAsync(host =>
        {
            using var list = new ListView { View = View.Details };
            for (var index = 0; index < 5; index++) list.Columns.Add(index.ToString());
            host.Controls.Add(list);
            var directory = new S3ObjectEntry("root/../", S3Path.DisplayName("root/../", true), 0, true, null, "");
            MainForm.SynchronizeObjectRows(list, [directory], true, false);
            var parent = Assert.Single(list.Items.Cast<ListViewItem>(), item => item.Tag is ParentDirectoryTag);
            var literal = Assert.Single(list.Items.Cast<ListViewItem>(), item => item.Tag is S3ObjectEntry);
            Assert.NotEqual(parent.Text, literal.Text);
            Assert.Equal("root/../", Assert.IsType<S3ObjectEntry>(literal.Tag).Key);
            Assert.Contains(Uri.EscapeDataString(directory.Key), literal.ToolTipText, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    private static Task InvokeAsync(MainForm form, string method, params object?[] arguments) =>
        Assert.IsAssignableFrom<Task>(typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, arguments));

    public class ExactKeyStorageProxy : DispatchProxy
    {
        public List<string> Prefixes { get; } = [];
        public List<string> RawPrefixes { get; } = [];
        public IReadOnlyList<S3ObjectEntry> RawItems { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IS3StorageService.ListObjectsAsync))
            {
                Prefixes.Add((string)arguments![2]!);
                return Task.FromResult(new PagedObjectResult([], null, false));
            }
            if (method.Name == nameof(IS3StorageService.ListObjectsForDeletionAsync))
            {
                RawPrefixes.Add((string)arguments![2]!);
                return Task.FromResult(new PagedObjectResult(RawItems, null, false));
            }
            throw new NotSupportedException(method.Name);
        }
    }
}
