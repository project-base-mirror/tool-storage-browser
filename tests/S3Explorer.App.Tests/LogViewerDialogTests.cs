using System.Runtime.ExceptionServices;
using System.Text;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class LogViewerDialogTests
{
    [Fact]
    public async Task ReaderCanReadLogWhileAnotherWriterKeepsItOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s3explorer-log-{Guid.NewGuid():N}.log");
        try
        {
            await using var writer = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = Encoding.UTF8.GetBytes("first line\r\nlatest line\r\n");
            await writer.WriteAsync(bytes);
            await writer.FlushAsync();

            var snapshot = await LogFileReader.ReadAsync(path, 4096);

            Assert.True(snapshot.Exists);
            Assert.False(snapshot.IsTruncated);
            Assert.Contains("first line", snapshot.Content);
            Assert.Contains("latest line", snapshot.Content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReaderKeepsOnlyCompleteLinesFromTailWhenLogIsLarge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s3explorer-log-{Guid.NewGuid():N}.log");
        try
        {
            var lines = Enumerable.Range(0, 100).Select(index => $"line-{index:D3}-content");
            await File.WriteAllLinesAsync(path, lines);

            var snapshot = await LogFileReader.ReadAsync(path, 96);

            Assert.True(snapshot.IsTruncated);
            Assert.DoesNotContain("line-000", snapshot.Content);
            Assert.Contains("line-099-content", snapshot.Content);
            Assert.StartsWith("line-", snapshot.Content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DialogKeepsViewerAndActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"s3explorer-log-viewer-{Guid.NewGuid():N}");
            var logger = new SimpleFileLogger(directory);
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new LogViewerDialog(logger);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var content = Assert.IsType<RichTextBox>(Find(dialog, "LogViewerContent"));
            Assert.True(content.ClientSize.Width > 0 && content.ClientSize.Height > 0);
            Assert.True(content.ClientSize.Height >= 240,
                $"Log viewer content was only {content.ClientSize.Height}px high at the minimum window size.");
            Assert.True(content.ReadOnly);
            Assert.False(content.WordWrap);

            foreach (var name in new[]
                     {
                         "RefreshLogButton",
                         "CopyLogButton",
                         "ScrollLogEndButton",
                         "CloseLogViewerButton"
                     })
            {
                AssertButtonIsReadable(dialog, Assert.IsType<Button>(Find(dialog, name)));
            }

            Assert.Same(Find(dialog, "CloseLogViewerButton"), dialog.CancelButton);
        });
    }

    private static Control Find(Control root, string name) =>
        Assert.Single(root.Controls.Find(name, searchAllChildren: true));

    private static void AssertButtonIsReadable(Form dialog, Button button)
    {
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
}
