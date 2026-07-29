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
}
