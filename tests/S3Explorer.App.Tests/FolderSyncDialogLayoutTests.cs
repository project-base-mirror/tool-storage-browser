using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class FolderSyncDialogLayoutTests
{
    [Fact]
    public void WorkspaceFitsTaskListPathCardsAndResultFrameAtLargeText()
    {
        RunSta(() =>
        {
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            var queue = new PersistentTransferQueue(null!, null!);
            using var dialog = new FolderSyncDialog(
                null!,
                null!,
                null!,
                queue,
                new AppSettings());
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var workspace = Assert.IsType<SplitContainer>(Find(dialog, "SyncWorkspace"));
            var jobs = Assert.IsType<ListView>(Find(dialog, "SyncJobList"));
            var results = Assert.IsType<ListView>(Find(dialog, "SyncResultsList"));
            var resultsFrame = Assert.IsType<Panel>(Find(dialog, "SyncResultsFrame"));
            var source = Find(dialog, "SyncSourcePathCard");
            var destination = Find(dialog, "SyncDestinationPathCard");

            Assert.True(workspace.Panel1.ClientSize.Width >= workspace.Panel1MinSize);
            Assert.True(workspace.Panel2.ClientSize.Width >= workspace.Panel2MinSize);
            Assert.Equal(BorderStyle.FixedSingle, resultsFrame.BorderStyle);
            Assert.True(resultsFrame.ClientSize.Width > 0 && resultsFrame.ClientSize.Height > 0);
            Assert.True(source.Width >= 200 && source.Height >= 60, $"Source card was {source.Size}.");
            Assert.True(destination.Width >= 200 && destination.Height >= 60, $"Destination card was {destination.Size}.");

            var columnsWidth = jobs.Columns.Cast<ColumnHeader>().Sum(column => column.Width);
            var safeWidth = jobs.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            Assert.True(columnsWidth <= safeWidth,
                $"Task columns used {columnsWidth}px but only {safeWidth}px was safely available.");
            Assert.True(results.CheckBoxes);
            Assert.Equal(ColumnHeaderStyle.Clickable, results.HeaderStyle);
            Assert.Contains(results.Columns.Cast<ColumnHeader>(), column => column.Text == "扩展名");
            Assert.NotNull(results.ContextMenuStrip);
            Assert.Equal(3, results.ContextMenuStrip!.Items.Count);
            var actions = dialog.Controls.OfType<ToolStrip>().Single();
            Assert.Contains(actions.Items.Cast<ToolStripItem>(), item => item.Name == "SyncExecutionReport");
        });
    }

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
}
