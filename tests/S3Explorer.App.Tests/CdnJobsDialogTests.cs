using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CdnJobsDialogTests
{
    [Fact]
    public void JobCenterKeepsActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com"
            };
            var store = new MemoryJobStore();
            var queue = new PersistentCdnJobQueue(store, new CompletedExecutor());
            try
            {
                queue.InitializeAsync().GetAwaiter().GetResult();
                queue.EnqueueAsync(new CdnJobRecord
                {
                    IdempotencyKey = "layout-test",
                    CdnProfileId = profile.Id,
                    Action = CdnJobAction.Warmup,
                    Urls = ["https://cdn.example.com/assets/app.js"]
                }).GetAwaiter().GetResult();

                using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
                using var dialog = new CdnJobsDialog(queue, [profile]);
                dialog.Font = largerFont;
                dialog.Size = dialog.MinimumSize;
                PerformLayout(dialog);

                var list = Assert.IsType<ListView>(Assert.Single(
                    dialog.Controls.Find("CdnJobsList", searchAllChildren: true)));
                Assert.NotEmpty(list.Items.Cast<ListViewItem>());
                foreach (var name in new[]
                         {
                             "RetryCdnJobButton",
                             "RetryAllCdnJobsButton",
                             "CancelCdnJobButton",
                             "ClearCompletedCdnJobsButton",
                             "CloseCdnJobsButton"
                         })
                {
                    AssertButtonIsReadable(dialog, FindButton(dialog, name));
                }

                var status = Assert.IsType<Label>(Assert.Single(
                    dialog.Controls.Find("CdnJobsStatus", searchAllChildren: true)));
                Assert.Contains("共 1 个任务", status.Text, StringComparison.Ordinal);
                Assert.Same(FindButton(dialog, "CloseCdnJobsButton"), dialog.CancelButton);
            }
            finally
            {
                queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    private static Button FindButton(Control root, string name) =>
        Assert.IsType<Button>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void AssertButtonIsReadable(Form dialog, Button button)
    {
        Assert.True(button.Height >= 34, $"{button.Name} height was {button.Height}.");
        Assert.True(button.Width >= button.PreferredSize.Width,
            $"{button.Name} width {button.Width} was smaller than preferred width {button.PreferredSize.Width}.");
        Assert.True(button.Height >= button.PreferredSize.Height,
            $"{button.Name} height {button.Height} was smaller than preferred height {button.PreferredSize.Height}.");

        var bounds = button.Bounds;
        for (var parent = button.Parent; parent is not null && parent != dialog; parent = parent.Parent)
            bounds.Offset(parent.Left, parent.Top);
        Assert.True(dialog.ClientRectangle.Contains(bounds),
            $"{button.Name} bounds {bounds} were outside {dialog.ClientRectangle}.");
    }

    private static void PerformLayout(Control control)
    {
        control.CreateControl();
        control.PerformLayout();
        foreach (Control child in control.Controls)
            PerformLayout(child);
        control.PerformLayout();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class MemoryJobStore : ICdnJobStore
    {
        private CdnJobStoreSnapshot _snapshot = new();

        public Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(CdnJobStoreSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedExecutor : ICdnJobExecutor
    {
        public Task<CdnProviderResult> ExecuteAsync(
            CdnJobRecord job,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnProviderResult(
                CdnProviderOperationState.Completed,
                "完成",
                StatusCode: 200));
    }
}
