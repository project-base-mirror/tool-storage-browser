using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class TransferQueueControlTests
{
    [Fact]
    public void QueueSeparatesAllActiveSuccessfulAndFailedTransfers()
    {
        RunSta(() =>
        {
            var profileId = Guid.NewGuid();
            var tasks = new[]
            {
                CreateTask(profileId, "paused.bin", TransferTaskState.Paused),
                CreateTask(profileId, "success.bin", TransferTaskState.Completed),
                CreateTask(profileId, "failed.bin", TransferTaskState.Failed),
                CreateTask(profileId, "cancelled.bin", TransferTaskState.Cancelled)
            };
            var store = new SnapshotStore(new TransferStoreSnapshot { Tasks = tasks });
            var queue = new PersistentTransferQueue(store, new UnexpectedExecutor());
            try
            {
                using var control = new TransferQueueControl(queue);
                control.CreateControl();
                control.InitializeAsync().GetAwaiter().GetResult();
                control.PerformLayout();

                var tabs = Assert.Single(control.Controls.OfType<TabControl>());
                Assert.Equal(
                    ["批次 (0)", "全部 (4)", "进行中 (1)", "成功 (1)", "失败 (1)"],
                    tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
                Assert.Equal(4, FindList(control, "AllTransfersList").Items.Count);
                Assert.Single(FindList(control, "ActiveTransfersList").Items.Cast<ListViewItem>());
                Assert.Single(FindList(control, "SuccessfulTransfersList").Items.Cast<ListViewItem>());
                Assert.Single(FindList(control, "FailedTransfersList").Items.Cast<ListViewItem>());
                Assert.Equal("成功", FindList(control, "SuccessfulTransfersList").Items[0].SubItems[8].Text);
            }
            finally
            {
                queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighFrequencyProgressKeepsMessagePumpResponsiveAndDisplaysFinalProgress(bool manualRetry)
    {
        RunSta(() =>
        {
            var executor = new HighFrequencyProgressExecutor();
            var task = CreateTask(Guid.NewGuid(), "large.bin",
                manualRetry ? TransferTaskState.Failed : TransferTaskState.RetryPending) with
            {
                Direction = TransferDirection.Upload,
                TotalBytes = 100_000,
                AttemptCount = manualRetry ? 3 : 1,
                NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            var queue = new PersistentTransferQueue(
                new SnapshotStore(new TransferStoreSnapshot { Tasks = [task] }), executor);
            using var form = new Form { Width = 900, Height = 600 };
            using var control = new TransferQueueControl(queue) { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            control.CreateControl();
            control.InitializeAsync().GetAwaiter().GetResult();

            var timer = new System.Windows.Forms.Timer { Interval = 20 };
            var pumpTicksWhileRunning = 0;
            var sawIntermediateProgress = false;
            var timedOut = false;
            var sixtyPercent = $"{60d:N1}%";
            var oneHundredPercent = $"{100d:N1}%";
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            var cancellationToken = TestContext.Current.CancellationToken;
            timer.Tick += (_, _) =>
            {
                if (executor.Reported.Task.IsCompleted &&
                    queue.Snapshot.Tasks.SingleOrDefault(item => item.Id == task.Id)?.State == TransferTaskState.Running)
                {
                    pumpTicksWhileRunning++;
                    var rows = FindList(control, "AllTransfersList").Items;
                    if (rows.Count > 0 && rows[0].SubItems[5].Text == sixtyPercent)
                        sawIntermediateProgress = true;
                    if (sawIntermediateProgress && pumpTicksWhileRunning >= 5)
                        executor.Continue();
                }

                var row = FindList(control, "AllTransfersList").Items
                    .Cast<ListViewItem>()
                    .FirstOrDefault(item => item.Tag is TransferTaskRecord record && record.Id == task.Id);
                if (row is not null && row.SubItems[5].Text == oneHundredPercent && row.SubItems[8].Text == "成功")
                {
                    timer.Stop();
                    form.Close();
                    return;
                }
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    timedOut = true;
                    timer.Stop();
                    executor.Continue();
                    form.Close();
                }
            };

            form.Shown += (_, _) =>
            {
                timer.Start();
                var resume = Task.Run(() => manualRetry
                    ? queue.RetryAsync(task.Id, cancellationToken)
                    : queue.ResumeAsync(task.Id, cancellationToken), cancellationToken);
                executor.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                resume.GetAwaiter().GetResult();
            };
            try
            {
                Application.Run(form);
            }
            finally
            {
                timer.Dispose();
                executor.Continue();
                queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Assert.False(timedOut, "The WinForms message pump did not observe upload completion.");
            Assert.True(pumpTicksWhileRunning >= 5, "The WinForms message pump stopped while progress was reported.");
            Assert.True(sawIntermediateProgress, "The throttled UI did not display progress before completion.");
        });
    }

    [Fact]
    public void TaskDetailsShowFullFailureAndRedactCredentials()
    {
        var task = CreateTask(Guid.NewGuid(), "failed.bin", TransferTaskState.Failed) with
        {
            LocalPath = @"C:\downloads\failed.bin",
            AttemptCount = 3,
            Failure = new TransferFailureInfo(
                "Authorization: top-secret raw failure details",
                TransferFailureCategory.Authentication,
                403,
                "AccessDenied",
                "request-123",
                false)
        };

        var details = TransferTaskDetailsFormatter.Format(task);

        Assert.Contains(@"C:\downloads\failed.bin", details, StringComparison.Ordinal);
        Assert.Contains("AccessDenied", details, StringComparison.Ordinal);
        Assert.Contains("request-123", details, StringComparison.Ordinal);
        Assert.Contains("Authorization=***", details, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", details, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsDialogProvidesReadableCopyAction()
    {
        RunSta(() =>
        {
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new TransferTaskDetailsDialog(
                CreateTask(Guid.NewGuid(), "failed.bin", TransferTaskState.Failed));
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var content = Assert.IsType<RichTextBox>(Find(dialog, "TransferTaskDetailsContent"));
            var copy = Assert.IsType<Button>(Find(dialog, "CopyTransferTaskDetailsButton"));
            Assert.True(content.ReadOnly);
            Assert.False(content.WordWrap);
            Assert.Contains("任务 ID", content.Text, StringComparison.Ordinal);
            AssertButtonIsReadable(dialog, copy);
            AssertButtonIsReadable(
                dialog,
                Assert.IsType<Button>(Find(dialog, "CloseTransferTaskDetailsButton")));
        });
    }

    private static TransferTaskRecord CreateTask(
        Guid profileId,
        string key,
        TransferTaskState state) =>
        new()
        {
            ProfileId = profileId,
            ProfileName = "test-profile",
            Direction = TransferDirection.Download,
            State = state,
            Bucket = "test-bucket",
            ObjectKey = key,
            LocalPath = Path.Combine(Path.GetTempPath(), key),
            TotalBytes = 100,
            TransferredBytes = state == TransferTaskState.Completed ? 100 : 0,
            Failure = state == TransferTaskState.Failed
                ? new TransferFailureInfo("full failure details")
                : null
        };

    private static ListView FindList(Control root, string name) =>
        Assert.IsType<ListView>(Find(root, name));

    private static Control Find(Control root, string name) =>
        Assert.Single(root.Controls.Find(name, searchAllChildren: true));

    private static void PerformLayout(Control control)
    {
        control.CreateControl();
        control.PerformLayout();
        foreach (Control child in control.Controls)
            PerformLayout(child);
        control.PerformLayout();
    }

    private static void AssertButtonIsReadable(Form dialog, Button button)
    {
        Assert.True(button.Width >= button.PreferredSize.Width);
        Assert.True(button.Height >= button.PreferredSize.Height);

        var bounds = button.Bounds;
        for (var parent = button.Parent; parent is not null && parent != dialog; parent = parent.Parent)
            bounds.Offset(parent.Left, parent.Top);
        Assert.True(dialog.ClientRectangle.Contains(bounds),
            $"{button.Name} bounds {bounds} were outside {dialog.ClientRectangle}.");
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class SnapshotStore(TransferStoreSnapshot snapshot) : ITransferTaskStore
    {
        private TransferStoreSnapshot _snapshot = snapshot;
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);
        public Task SaveAsync(TransferStoreSnapshot value, CancellationToken cancellationToken = default)
        {
            _snapshot = value;
            return Task.CompletedTask;
        }
    }

    private sealed class UnexpectedExecutor : ITransferTaskExecutor
    {
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Terminal and paused test tasks must not execute.");
    }

    private sealed class HighFrequencyProgressExecutor : ITransferTaskExecutor
    {
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            for (var bytes = 1; bytes <= 60_000; bytes++)
                context.ReportProgress(new TransferProgress(bytes, 100_000));
            Reported.TrySetResult();
            await _continue.Task.WaitAsync(cancellationToken);
            for (var bytes = 60_001; bytes <= 100_000; bytes++)
                context.ReportProgress(new TransferProgress(bytes, 100_000));
        }

        public void Continue() => _continue.TrySetResult();
    }
}
