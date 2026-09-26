using System.Reflection;
using System.Runtime.InteropServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MainFormInteractionCollection
{
    public const string Name = "MainForm interactions";
}

[Collection(MainFormInteractionCollection.Name)]
public sealed class MainFormInteractionTests
{
    [Fact]
    public async Task DragDropUploadKeepsMessagePumpResponsiveAndCompletesWithExactSourceAndKey()
    {
        await RunMessagePumpAsync(async host =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3explorer-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var localPath = Path.Combine(root, "大文件 + 100%.bin");
            using (var file = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                file.SetLength(96L * 1024 * 1024);

            var profile = new ConnectionProfile { Name = "ui-test", Endpoint = "https://storage.example.test" };
            var upload = new PausedFakeUpload();
            var storage = DispatchProxy.Create<IS3StorageService, FakeStorageProxy>();
            ((FakeStorageProxy)(object)storage).Upload = upload;
            var runtime = new TransferRuntimeConfiguration();
            var logger = new SimpleFileLogger(Path.Combine(root, "logs"));
            var queue = new PersistentTransferQueue(
                new JsonTransferTaskStore(Path.Combine(root, "transfers.json")),
                new S3TransferTaskExecutor(
                    new SingleProfileStore(profile), storage, runtime, new EmptyCdnConfigurationStore(), logger));
            System.Windows.Forms.Timer? timer = null;
            try
            {
                await queue.InitializeAsync();
                using var form = CreateForm(queue, runtime, logger, new SingleProfileStore(profile), storage: storage);
                SetField(form, "_currentProfile", profile);
                SetField(form, "_currentBucket", "ui-bucket");
                SetField(form, "_currentPrefix", "中文/目录/");
                var objects = GetField<ListView>(form, "_objects");
                _ = form.Handle;
                _ = objects.Handle;
                var transfers = GetField<TransferQueueControl>(form, "_transfers");
                _ = transfers.Handle;
                await transfers.InitializeAsync();
                var rows = Assert.IsType<ListView>(Assert.Single(transfers.Controls.Find("AllTransfersList", true)));
                _ = rows.Handle;

                var responsiveButton = new Button { Text = "message-pump-probe", Dock = DockStyle.Top };
                var buttonClicked = false;
                responsiveButton.Click += (_, _) => buttonClicked = true;
                host.Controls.Add(responsiveButton);
                timer = new System.Windows.Forms.Timer { Interval = 10 };
                var timerTicks = 0;
                timer.Tick += (_, _) => timerTicks++;
                timer.Start();

                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { localPath });
                var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                InvokeProtected(objects, "OnDragEnter", args);
                Assert.Equal(DragDropEffects.Copy, args.Effect);
                InvokeProtected(objects, "OnDragDrop", args);

                await upload.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(upload.ProgressReports >= 1_000);
                Assert.Equal(TransferTaskState.Running, Assert.Single(queue.Snapshot.Tasks).State);
                Assert.Equal("中文/目录/大文件 + 100%.bin", Assert.Single(queue.Snapshot.Tasks).ObjectKey);
                Assert.Equal(localPath, Assert.Single(queue.Snapshot.Tasks).LocalPath);

                responsiveButton.PerformClick();
                await WaitUntilAsync(() => timerTicks >= 2, TimeSpan.FromSeconds(2));
                Assert.True(buttonClicked);
                await WaitUntilAsync(() => rows.Items.Count == 1 && rows.Items[0].SubItems[5].Text == $"{60d:N1}%",
                    TimeSpan.FromSeconds(3));

                upload.Release.TrySetResult();
                await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.Completed,
                    TimeSpan.FromSeconds(10));
                var completed = Assert.Single(queue.Snapshot.Tasks);
                Assert.Equal(TransferTaskState.Completed, completed.State);
                Assert.Equal(localPath, completed.LocalPath);
                Assert.Equal("中文/目录/大文件 + 100%.bin", completed.ObjectKey);
                await WaitUntilAsync(() => rows.Items.Count == 1 && rows.Items[0].SubItems[8].Text == "成功",
                    TimeSpan.FromSeconds(3));
                Assert.Equal($"{100d:N1}%", rows.Items[0].SubItems[5].Text);
                Assert.True(((FakeStorageProxy)(object)storage).ListRequests > 0);
            }
            finally
            {
                timer?.Stop();
                timer?.Dispose();
                upload.Release.TrySetResult();
                await queue.DisposeAsync();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public async Task CdnDefaultAndSpecifiedMenuClicksWriteMappedUrlThroughInjectedWriter()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var writes = new List<string>();
            using var form = CreateForm(
                new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                    new NeverRunExecutor()),
                new TransferRuntimeConfiguration(),
                new SimpleFileLogger(Path.Combine(Path.GetTempPath(), "s3explorer-test-logs")),
                new SingleProfileStore(new ConnectionProfile { Name = "cdn-ui", Endpoint = "https://storage.example.test" }),
                (text, _) => { writes.Add(text); return Task.CompletedTask; });
            PrepareCdnSelection(form, out var expected);

            var command = Assert.IsType<ToolStripMenuItem>(
                GetField<Dictionary<string, ToolStripItem>>(form, "_commands")["cdn-copy-url"]);
            command.Enabled = true;
            command.PerformClick();
            await WaitUntilAsync(() => writes.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(expected, writes[0]);
            Assert.Contains("已复制 CDN URL", GetStatus(form));

            InvokeInstance(form, "PopulateSpecifiedCdnMenus");
            var menus = GetField<List<ToolStripMenuItem>>(form, "_cdnSpecifiedMenus");
            var mainSpecified = menus.Single(menu => menu.Name == "CdnCopySpecifiedMenu");
            Assert.Equal(2, mainSpecified.DropDownItems.Count);
            mainSpecified.DropDownItems[0].PerformClick();
            await WaitUntilAsync(() => writes.Count == 2, TimeSpan.FromSeconds(2));
            Assert.Equal(expected, writes[1]);

            var objectMenu = GetField<ContextMenuStrip>(form, "_objectMenu");
            var contextCopy = FindMenuItem(objectMenu.Items, "CdnObjectContextCopy");
            contextCopy.Enabled = true;
            contextCopy.PerformClick();
            await WaitUntilAsync(() => writes.Count == 3, TimeSpan.FromSeconds(2));
            Assert.Equal(expected, writes[2]);

            var contextSpecified = menus.Single(menu => menu.Name == "CdnObjectContextCopySpecified");
            Assert.Equal(2, contextSpecified.DropDownItems.Count);
            contextSpecified.DropDownItems[1].PerformClick();
            await WaitUntilAsync(() => writes.Count == 4, TimeSpan.FromSeconds(2));
            Assert.Equal("https://backup.example.test/assets/v2/%E5%9B%BE%E6%A0%87%20%2B%20100%25%23%3F.png", writes[3]);
        });
    }

    [Fact]
    public async Task CdnContextMenuClickUsesProductionClipboardWriterAndCopiesEscapedUrl()
    {
        await RunMessagePumpAsync(async _ =>
        {
            var previous = await AccessClipboardAsync(CaptureClipboardSnapshot);
            try
            {
                using var form = CreateForm(
                    new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                        new NeverRunExecutor()),
                    new TransferRuntimeConfiguration(),
                    new SimpleFileLogger(Path.Combine(Path.GetTempPath(), "s3explorer-test-logs")),
                    new SingleProfileStore(new ConnectionProfile { Name = "cdn-ui", Endpoint = "https://storage.example.test" }));
                PrepareCdnSelection(form, out var expected);
                var contextCopy = FindMenuItem(GetField<ContextMenuStrip>(form, "_objectMenu").Items, "CdnObjectContextCopy");
                contextCopy.Enabled = true;
                contextCopy.PerformClick();
                await WaitUntilAsync(() => GetStatus(form).StartsWith("已复制 CDN URL", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(3));
                Assert.Equal(expected, await AccessClipboardAsync(() => Clipboard.GetText()));
            }
            finally
            {
                if (previous is not null)
                    await RestoreClipboardAsync(previous);
                else
                    Clipboard.Clear();
            }
        });
    }

    [Fact]
    public async Task CdnCopyRetriesWhileAnotherThreadOwnsTheRealClipboard()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var previous = await AccessClipboardAsync(CaptureClipboardSnapshot);
            (TaskCompletionSource Opened, TaskCompletionSource Release, TaskCompletionSource Closed)? hold = null;
            try
            {
                using var form = CreateForm(
                    new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                        new NeverRunExecutor()),
                    new TransferRuntimeConfiguration(),
                    new SimpleFileLogger(Path.Combine(Path.GetTempPath(), "s3explorer-test-logs")),
                    new SingleProfileStore(new ConnectionProfile { Name = "cdn-ui", Endpoint = "https://storage.example.test" }));
                PrepareCdnSelection(form, out var expected);
                var contextCopy = FindMenuItem(GetField<ContextMenuStrip>(form, "_objectMenu").Items, "CdnObjectContextCopy");
                contextCopy.Enabled = true;
                hold = StartClipboardHold();
                await hold.Value.Opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
                contextCopy.PerformClick();
                await WaitUntilAsync(() => GetStatus(form).StartsWith("正在复制 CDN URL", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2));
                Assert.DoesNotContain("已复制 CDN URL", GetStatus(form));
                await Task.Delay(250);
                hold.Value.Release.TrySetResult();
                await hold.Value.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await WaitUntilAsync(() => GetStatus(form).StartsWith("已复制 CDN URL", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));
                Assert.Equal(expected, await AccessClipboardAsync(() => Clipboard.GetText()));
                Assert.Contains("已复制 CDN URL", GetStatus(form));
            }
            finally
            {
                if (hold is not null)
                {
                    hold.Value.Release.TrySetResult();
                    await hold.Value.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                if (previous is not null)
                    await RestoreClipboardAsync(previous);
                else
                    Clipboard.Clear();
            }
        });
    }

    [Fact]
    public async Task CdnCopyReportsPersistentClipboardFailureInStatusWithoutModalDialog()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            using var form = CreateForm(
                new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                    new NeverRunExecutor()),
                new TransferRuntimeConfiguration(),
                new SimpleFileLogger(Path.Combine(Path.GetTempPath(), "s3explorer-test-logs")),
                new SingleProfileStore(new ConnectionProfile { Name = "cdn-ui", Endpoint = "https://storage.example.test" }),
                (_, _) => Task.FromException(new ExternalException("clipboard locked")));
            PrepareCdnSelection(form, out _);
            var contextCopy = FindMenuItem(GetField<ContextMenuStrip>(form, "_objectMenu").Items, "CdnObjectContextCopy");
            contextCopy.Enabled = true;
            contextCopy.PerformClick();
            await WaitUntilAsync(() => GetStatus(form).Contains("剪贴板", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
            Assert.Contains("复制 CDN URL 失败", GetStatus(form));
            Assert.DoesNotContain("已复制 CDN URL", GetStatus(form));
        });
    }

    private static MainForm CreateForm(
        PersistentTransferQueue queue,
        TransferRuntimeConfiguration runtime,
        SimpleFileLogger logger,
        IProfileStore profiles,
        Func<string, CancellationToken, Task>? clipboardWriter = null,
        IS3StorageService? storage = null)
    {
        var cdnQueue = new PersistentCdnJobQueue(new EmptyCdnJobStore(), new NeverRunCdnExecutor());
        var updateChecker = new GitHubUpdateChecker(cachePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var form = new MainForm(
            profiles,
            storage ?? DispatchProxy.Create<IS3StorageService, FakeStorageProxy>(),
            new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            logger,
            queue,
            runtime,
            new EmptyFolderSyncStore(),
            updateChecker,
            null!,
            new EmptyCdnConfigurationStore(),
            null!,
            null!,
            cdnQueue,
            null!,
            clipboardWriter: clipboardWriter);
        form.Disposed += (_, _) =>
        {
            GetField<NotifyIcon?>(form, "_trayIcon")?.Dispose();
            updateChecker.Dispose();
            cdnQueue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
        return form;
    }

    private static void PrepareCdnSelection(MainForm form, out string expectedUrl)
    {
        var profile = new ConnectionProfile { Name = "cdn-ui", Endpoint = "https://storage.example.test" };
        SetField(form, "_currentProfile", profile);
        SetField(form, "_currentBucket", "静态 bucket");
        SetField(form, "_currentPrefix", "游戏/");
        var primary = new CdnProfile { Name = "默认 CDN", BaseUrl = "https://cdn.example.test/root" };
        var secondary = new CdnProfile { Name = "备用 CDN", BaseUrl = "https://backup.example.test/assets" };
        SetField(form, "_cdnConfiguration", new CdnConfiguration(
            [primary, secondary],
            [
                new CdnBinding { StorageProfileId = profile.Id, Bucket = "静态 bucket", SourcePrefix = "游戏/", CdnProfileId = primary.Id, CdnPathPrefix = "发布 版/", IsDefault = true },
                new CdnBinding { StorageProfileId = profile.Id, Bucket = "静态 bucket", SourcePrefix = "游戏/", CdnProfileId = secondary.Id, CdnPathPrefix = "v2/", IsDefault = false }
            ]));
        var objects = GetField<ListView>(form, "_objects");
        _ = form.Handle;
        _ = objects.Handle;
        objects.Items.Clear();
        var entry = new S3ObjectEntry("游戏/图标 + 100%#?.png", "图标 + 100%#?.png", 1, false, null, "STANDARD");
        var item = new ListViewItem(entry.Name) { Tag = entry };
        objects.Items.Add(item);
        item.Selected = true;
        item.Focused = true;
        expectedUrl = "https://cdn.example.test/root/%E5%8F%91%E5%B8%83%20%E7%89%88/%E5%9B%BE%E6%A0%87%20%2B%20100%25%23%3F.png";
    }

    private static string GetStatus(MainForm form) =>
        GetField<ToolStripStatusLabel>(form, "_requestStatus").Text ?? string.Empty;

    private static ToolStripMenuItem FindMenuItem(ToolStripItemCollection items, string name) =>
        items.OfType<ToolStripMenuItem>().SelectMany(item => new[] { item }.Concat(Descendants(item)))
            .First(item => item.Name == name);

    private static IEnumerable<ToolStripMenuItem> Descendants(ToolStripMenuItem item) =>
        item.DropDownItems.OfType<ToolStripMenuItem>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
         throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);

    private static void InvokeInstance(object target, string name) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
         throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, null);

    private static void InvokeProtected(Control target, string method, params object[] arguments) =>
        (typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ??
         throw new MissingMethodException(typeof(Control).FullName, method)).Invoke(target, arguments);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("UI interaction did not reach its expected state.");
            await Task.Delay(10);
        }
    }

    private static Task RunMessagePumpAsync(Func<Control, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-2000, -2000),
                Size = new Size(360, 120),
                Text = "S3 Explorer UI test"
            };
            host.Shown += async (_, _) =>
            {
                try { await action(host); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { host.Close(); }
            };
            Application.Run(host);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45)))
            throw new TimeoutException("WinForms message pump did not exit within 45 seconds.");
        return completion.Task;
    }

    private static DataObject? CaptureClipboardSnapshot()
    {
        var current = Clipboard.GetDataObject();
        if (current is null) return null;
        var snapshot = new DataObject();
        foreach (var format in current.GetFormats(autoConvert: false))
        {
            var data = current.GetData(format, autoConvert: false);
            if (data is not null)
                snapshot.SetData(format, autoConvert: false, data);
        }
        return snapshot;
    }

    private static async Task<T> AccessClipboardAsync<T>(Func<T> access)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return access(); }
            catch (ExternalException) when (attempt < 10)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }

    private static async Task RestoreClipboardAsync(IDataObject snapshot)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(snapshot, copy: true, retryTimes: 0, retryDelay: 0);
                return;
            }
            catch (ExternalException) when (attempt < 30)
            {
                await Task.Delay(100);
            }
        }
    }

    private static (TaskCompletionSource Opened, TaskCompletionSource Release, TaskCompletionSource Closed)
        StartClipboardHold()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var acquired = false;
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    throw new InvalidOperationException("Could not acquire the Windows clipboard for the contention test.");
                acquired = true;
                opened.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) { opened.TrySetException(exception); }
            finally
            {
                if (acquired) CloseClipboard();
                closed.TrySetResult();
            }
        }) { IsBackground = true };
        thread.Start();
        return (opened, release, closed);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    public sealed class PausedFakeUpload
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProgressReports;

        public async Task Run(TransferOperationContext transfer, string localPath, CancellationToken cancellationToken)
        {
            var length = new FileInfo(localPath).Length;
            for (var index = 1; index <= 2048; index++)
            {
                transfer.ReportProgress(new TransferProgress(length * index * 3 / (2048 * 5), length));
                ProgressReports++;
            }
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            transfer.ReportProgress(new TransferProgress(length, length));
        }
    }

    public class FakeStorageProxy : DispatchProxy
    {
        public PausedFakeUpload? Upload { get; set; }
        public int ListRequests;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IS3StorageService.ListObjectsAsync))
            {
                ListRequests++;
                return Task.FromResult(new PagedObjectResult([], null, false));
            }
            if (targetMethod?.Name == nameof(IS3StorageService.UploadFileAsync) && args is not null)
            {
                var transfer = args.OfType<TransferOperationContext>().Single();
                return (Upload ?? throw new InvalidOperationException("Fake upload was not configured."))
                    .Run(transfer, (string)args[3]!, (CancellationToken)args[^1]!);
            }
            throw new NotSupportedException($"Unexpected storage call: {targetMethod?.Name}");
        }
    }

    private sealed class SingleProfileStore(ConnectionProfile profile) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([profile]);
        public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyCdnConfigurationStore : ICdnConfigurationStore
    {
        public Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CdnConfiguration.Empty);
        public Task SaveAsync(CdnConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyCdnJobStore : ICdnJobStore
    {
        public Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CdnJobStoreSnapshot());
        public Task SaveAsync(CdnJobStoreSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NeverRunCdnExecutor : ICdnJobExecutor
    {
        public Task<CdnProviderResult> ExecuteAsync(CdnJobRecord job, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyFolderSyncStore : IFolderSyncJobStore
    {
        public Task<IReadOnlyList<FolderSyncJob>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FolderSyncJob>>([]);
        public Task SaveAsync(IReadOnlyCollection<FolderSyncJob> jobs, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NeverRunExecutor : ITransferTaskExecutor
    {
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
