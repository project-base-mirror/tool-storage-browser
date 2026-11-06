using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    // These tests use the real out-of-process Explorer Shell, not an IDataObject mock.
    // Run only in a disposable interactive Windows desktop; they replace the system clipboard.
    [Fact]
    [Trait("Category", "ExplorerClipboard")]
    public async Task ExplorerPastesMainFormVirtualTreeTwiceWithExactBytes()
    {
        RequireExplorerClipboardOptIn();
        var output = TestContext.Current.TestOutputHelper!;
        await RunMessagePumpAsync(async unusedHost =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-explorer-out-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var profile = new ConnectionProfile { Name = "shell-test", Endpoint = "https://unused.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, ExplorerStorageProxy>();
            var fake = (ExplorerStorageProxy)(object)storage;
            fake.Files["remote/first.bin"] = [0, 1, 2, 127, 128, 255];
            fake.Files["remote/zero.txt"] = [];
            fake.Files["remote/目录/nested/文件 + 100%.txt"] = Encoding.UTF8.GetBytes("真实资源管理器 clipboard\r\n");
            var runtime = new TransferRuntimeConfiguration();
            await using var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
            await queue.InitializeAsync(TestContext.Current.CancellationToken);
            using var form = CreateForm(queue, runtime, new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(profile), storage: storage);
            try
            {
                SetField(form, "_currentProfile", profile);
                SetField(form, "_currentBucket", "test-bucket");
                SetField(form, "_currentPrefix", "remote/");
                var objects = GetField<ListView>(form, "_objects");
                _ = form.Handle;
                _ = objects.Handle;
                foreach (var entry in new[]
                {
                    new S3ObjectEntry("remote/first.bin", "first.bin", 6, false, null, "STANDARD"),
                    new S3ObjectEntry("remote/zero.txt", "zero.txt", 0, false, null, "STANDARD"),
                    new S3ObjectEntry("remote/目录/", "目录", 0, true, null, "STANDARD")
                })
                {
                    var item = new ListViewItem(entry.Name) { Tag = entry };
                    objects.Items.Add(item);
                    item.Selected = true;
                }
                await InvokeFileClipboardAsync(form, "CopySelectionToObjectClipboardAsync", false);
                Assert.True(await AccessClipboardAsync(() => Clipboard.ContainsData("FileGroupDescriptorW")));
                Assert.Empty(fake.Downloads);
                var expected = fake.Files.ToDictionary(pair => pair.Key["remote/".Length..], pair => pair.Value);
                foreach (var name in new[] { "paste-one", "paste-two" })
                {
                    var destination = Path.Combine(root, name);
                    Directory.CreateDirectory(destination);
                    using var explorer = await ExplorerTestWindow.OpenAsync(destination, output.WriteLine);
                    explorer.Paste();
                    await WaitForExplorerTreeAsync(destination, expected, output.WriteLine);
                    Assert.True(Directory.Exists(Path.Combine(destination, "目录", "empty")), "Explorer lost the empty directory.");
                    Assert.Equal(new[] { "目录", "目录/empty", "目录/nested" },
                        Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
                            .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray());
                    output.WriteLine($"PASS {name}: 3 exact files, nested/unicode paths, empty directory, zero-byte file.");
                }
                Assert.Equal(3, fake.Downloads.Count);
                Assert.All(fake.Downloads.Values, count => Assert.Equal(1, count));
                // Replacing the clipboard must survive disposal of the old MainForm/session.
                await AccessClipboardAsync(() => { Clipboard.SetText("explorer-test-new-owner"); return true; });
                await InvokeFileClipboardAsync(form, "ShutdownFileClipboardAsync");
                Assert.Equal("explorer-test-new-owner", await AccessClipboardAsync(Clipboard.GetText));
                output.WriteLine("PASS repeated paste reused verified download cache; shutdown preserved replacement clipboard owner.");
            }
            finally
            {
                await InvokeFileClipboardAsync(form, "ShutdownFileClipboardAsync");
                await AccessClipboardAsync(() => { Clipboard.Clear(); return true; });
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    [Trait("Category", "ExplorerClipboard")]
    public async Task ExplorerCopyProducesFileDropAndMainFormUploadsExactBytesWithoutDeletingSource()
    {
        RequireExplorerClipboardOptIn();
        var output = TestContext.Current.TestOutputHelper!;
        await RunMessagePumpAsync(async unusedHost =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-explorer-in-" + Guid.NewGuid().ToString("N"));
            var sourceFolder = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceFolder);
            var source = Path.Combine(sourceFolder, "本地 + 100%.bin");
            byte[] expected = [0, 1, 10, 128, 255];
            await File.WriteAllBytesAsync(source, expected, TestContext.Current.CancellationToken);
            var profile = new ConnectionProfile { Name = "shell-test", Endpoint = "https://unused.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, ExplorerStorageProxy>();
            var fake = (ExplorerStorageProxy)(object)storage;
            fake.Exists = (_, _, _) => Task.FromResult(false);
            var runtime = new TransferRuntimeConfiguration();
            var logger = new SimpleFileLogger(Path.Combine(root, "logs"));
            await using var queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(root, "tasks.json")),
                new S3TransferTaskExecutor(new SingleProfileStore(profile), storage, runtime, new EmptyCdnConfigurationStore(), logger));
            await queue.InitializeAsync(TestContext.Current.CancellationToken);
            using var form = CreateForm(queue, runtime, logger, new SingleProfileStore(profile), storage: storage);
            try
            {
                using var explorer = await ExplorerTestWindow.OpenAsync(sourceFolder, output.WriteLine);
                await AccessClipboardAsync(() => { Clipboard.Clear(); return true; });
                explorer.CopyFile(Path.GetFileName(source));
                await WaitUntilAsync(() => Clipboard.ContainsFileDropList(), TimeSpan.FromSeconds(8));
                var paths = await AccessClipboardAsync(() => Clipboard.GetFileDropList().Cast<string>().ToArray());
                Assert.Equal(source, Assert.Single(paths));
                output.WriteLine($"Explorer Copy generated CF_HDROP: {source}");
                SetField(form, "_currentProfile", profile);
                SetField(form, "_currentBucket", "current-bucket");
                SetField(form, "_currentPrefix", "当前/destination/");
                _ = form.Handle;
                form.TransferConflictPolicySelector = _ => throw new InvalidOperationException("Clear destinations must not prompt.");
                await InvokeFileClipboardAsync(form, "PasteObjectClipboardAsync");
                await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                var task = Assert.Single(queue.Snapshot.Tasks);
                Assert.Equal(TransferTaskState.Completed, task.State);
                Assert.Equal(profile.Id, task.ProfileId);
                Assert.Equal("current-bucket", task.Bucket);
                Assert.Equal("当前/destination/本地 + 100%.bin", task.ObjectKey);
                Assert.Equal(expected, fake.Uploaded[task.ObjectKey]);
                Assert.Equal(expected, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
                output.WriteLine($"PASS real Explorer Copy -> MainForm Paste -> fake storage: bucket={task.Bucket}, key={task.ObjectKey}, exact bytes; source retained.");
            }
            finally
            {
                await queue.DisposeAsync();
                await AccessClipboardAsync(() => { Clipboard.Clear(); return true; });
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static void RequireExplorerClipboardOptIn()
    {
        if (Environment.GetEnvironmentVariable("S3EXPLORER_EXPLORER_CLIPBOARD_TEST") != "1")
            Assert.Skip("Real Explorer integration is opt-in: set S3EXPLORER_EXPLORER_CLIPBOARD_TEST=1 on a disposable interactive Windows desktop.");
        Assert.True(Environment.UserInteractive, "Explorer integration opted in but this process has no interactive Windows desktop. Use an interactive Windows runner, not a service/session 0.");
    }

    private static async Task WaitForExplorerTreeAsync(string root, Dictionary<string, byte[]> expected, Action<string> log)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                if (Directory.Exists(Path.Combine(root, "目录", "empty")) && expected.All(pair => File.Exists(Path.Combine(root, pair.Key)) &&
                    File.ReadAllBytes(Path.Combine(root, pair.Key)).SequenceEqual(pair.Value)))
                {
                    Assert.Equal(expected.Keys.Order(StringComparer.Ordinal).ToArray(),
                        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray());
                    return;
                }
            }
            catch (IOException) { /* Explorer may still be writing a destination. */ }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        var observed = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        log("Observed after timeout: " + string.Join(", ", observed));
        Assert.Fail($"Real Explorer Paste did not produce the exact expected tree within 10 seconds: {root}. Check shell copy/error dialogs and interactive desktop availability.");
    }

    public class ExplorerStorageProxy : FakeStorageProxy
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte[]> Uploaded { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IS3StorageService.GetObjectPropertiesAsync))
            {
                var key = (string)args![2]!;
                return Task.FromResult(new ObjectProperties((string)args[1]!, key, Files[key].Length, null,
                    "test-etag", "application/octet-stream", "STANDARD", null, new Dictionary<string, string>()));
            }
            if (targetMethod?.Name == nameof(IS3StorageService.ListObjectsForDeletionAsync))
                return Task.FromResult(new PagedObjectResult([
                    new("remote/目录/", "目录", 0, true, null, "STANDARD"),
                    new("remote/目录/empty/", "empty", 0, true, null, "STANDARD"),
                    new("remote/目录/nested/文件 + 100%.txt", "文件 + 100%.txt", Files["remote/目录/nested/文件 + 100%.txt"].Length, false, null, "STANDARD")
                ], null, false));
            if (targetMethod?.Name == nameof(IS3StorageService.DownloadFileAsync))
                return DownloadAsync((string)args![2]!, (string)args[3]!, args.OfType<TransferOperationContext>().Single(), (CancellationToken)args[^1]!);
            if (targetMethod?.Name == nameof(IS3StorageService.UploadFileAsync))
                return UploadAsync((string)args![2]!, (string)args[3]!, args.OfType<TransferOperationContext>().Single(), (CancellationToken)args[^1]!);
            return base.Invoke(targetMethod, args);
        }

        private async Task DownloadAsync(string key, string path, TransferOperationContext transfer, CancellationToken token)
        {
            Downloads.AddOrUpdate(key, 1, (_, previous) => previous + 1);
            var bytes = Files[key];
            await transfer.UpdateCheckpointAsync(0, new DownloadCheckpoint(path, 0, bytes.Length, "test-etag"), null, token);
            await File.WriteAllBytesAsync(path, bytes, token);
            transfer.ReportProgress(new TransferProgress(bytes.Length, bytes.Length));
        }

        private async Task UploadAsync(string key, string path, TransferOperationContext transfer, CancellationToken token)
        {
            var bytes = await File.ReadAllBytesAsync(path, token);
            Uploaded[key] = bytes;
            transfer.ReportProgress(new TransferProgress(bytes.Length, bytes.Length));
        }
    }

    private sealed class ExplorerTestWindow : IDisposable
    {
        private readonly dynamic shell;
        private readonly dynamic window;
        private readonly string path;
        private readonly Action<string> log;

        private ExplorerTestWindow(object shell, object window, string path, Action<string> log)
        { this.shell = shell; this.window = window; this.path = path; this.log = log; }

        public static async Task<ExplorerTestWindow> OpenAsync(string path, Action<string> log)
        {
            using var current = Process.GetCurrentProcess();
            log($"Test process pid={current.Id}, session={current.SessionId}, interactive={Environment.UserInteractive}, OS={Environment.OSVersion}");
            Assert.NotEqual(0, current.SessionId);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
            using var launched = Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{path}\"") { UseShellExecute = true });
            var timer = Stopwatch.StartNew();
            string? lastNavigationError = null;
            var waitingForVisibility = new HashSet<IntPtr>();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                dynamic windows = shell.Windows();
                try
                {
                    for (var index = 0; index < (int)windows.Count; index++)
                    {
                        dynamic candidate = windows.Item(index);
                        if (candidate is null) continue;
                        bool retained = false;
                        try
                        {
                            string executable = candidate.FullName;
                            if (!string.Equals(Path.GetFileName(executable), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string actual = candidate.Document.Folder.Self.Path;
                            if (!Path.IsPathFullyQualified(actual)) continue;
                            if (!string.Equals(Path.GetFullPath(actual).TrimEnd('\\'), Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                            IntPtr hwnd = new((long)candidate.HWND);
                            ExplorerGetWindowThreadProcessId(hwnd, out var pid);
                            using var process = Process.GetProcessById((int)pid);
                            Assert.Equal("explorer", process.ProcessName.ToLowerInvariant());
                            Assert.Equal(current.SessionId, process.SessionId);
                            if (!ExplorerIsWindowVisible(hwnd))
                            {
                                // Shell registers the navigated window before it finishes showing it.
                                // Keep the visibility requirement, but allow the bounded discovery poll
                                // to observe the same verified window becoming ready.
                                if (waitingForVisibility.Add(hwnd))
                                    log($"Waiting for matched Explorer window to become visible: hwnd=0x{hwnd.ToInt64():X}, pid={pid}, session={process.SessionId}, exact path={actual}, elapsed={timer.ElapsedMilliseconds}ms");
                                continue;
                            }
                            log($"Verified visible Explorer window hwnd=0x{hwnd.ToInt64():X}, pid={pid}, session={process.SessionId}, exact path={actual}, elapsed={timer.ElapsedMilliseconds}ms, visibilityWaited={waitingForVisibility.Contains(hwnd)}");
                            retained = true;
                            return new ExplorerTestWindow(shell, candidate, path, log);
                        }
                        catch (COMException exception)
                        {
                            // Shell windows can disappear/navigate during enumeration; only discovery is retried.
                            lastNavigationError = $"0x{exception.HResult:X8}: {exception.Message}";
                        }
                        finally { if (!retained && candidate is not null && Marshal.IsComObject(candidate)) Marshal.ReleaseComObject(candidate); }
                    }
                }
                finally { if (Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows); }
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            if (waitingForVisibility.Count > 0)
                log($"Explorer readiness timeout: matched {waitingForVisibility.Count} window(s) with the exact path/process/session, but no matched window became visible within 10 seconds.");
            if (lastNavigationError is not null) log("Last Explorer discovery COM error: " + lastNavigationError);
            if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            throw new InvalidOperationException($"Explorer integration opted in, but no visible explorer.exe window in session {current.SessionId} navigated to {path} within 10 seconds. This runner requires an interactive Windows shell.");
        }

        public void Paste()
        {
            Assert.Equal(path, (string)window.Document.Folder.Self.Path, ignoreCase: true);
            log($"Invoking canonical Shell paste verb on verified Explorer folder: {path} (not a keyboard simulation).");
            try { window.Document.Folder.Self.InvokeVerb("paste"); }
            catch (COMException exception)
            {
                log($"Canonical Explorer paste failed: path={path}, HRESULT=0x{exception.HResult:X8}, {exception.Message}");
                throw new InvalidOperationException($"Real Explorer Shell rejected the canonical paste verb for {path}.", exception);
            }
        }

        public void CopyFile(string name)
        {
            dynamic item = window.Document.Folder.ParseName(name);
            Assert.NotNull((object?)item);
            try
            {
                Assert.Equal(Path.Combine(path, name), (string)item.Path, ignoreCase: true);
                window.Document.SelectItem(item, 1 | 4 | 8 | 16);
                item.InvokeVerb("copy");
                log($"Invoked canonical Shell copy verb for exact Explorer item: {item.Path}");
            }
            finally { if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
        }

        public void Dispose()
        {
            try { window.Quit(); }
            catch (COMException exception) { log("Explorer window cleanup: " + exception.Message); }
            finally
            {
                if (Marshal.IsComObject(window)) Marshal.ReleaseComObject(window);
                if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint ExplorerGetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExplorerIsWindowVisible(IntPtr hwnd);
}
