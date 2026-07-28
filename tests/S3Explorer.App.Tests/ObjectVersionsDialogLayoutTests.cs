using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ObjectVersionsDialogLayoutTests
{
    [Fact]
    public void VersionColumnsAndDangerousActionsRemainReadableAtLargeText()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with { Name = "test" };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new ObjectVersionsDialog(
                null!, profile, "example-bucket", "assets/", null!);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var list = Assert.IsType<ListView>(Find(dialog, "ObjectVersionsList"));
            Assert.Equal(
                ["对象 Key", "类型", "Version ID", "当前", "大小", "修改时间", "存储类型"],
                list.Columns.Cast<ColumnHeader>().Select(column => column.Text));
            foreach (var name in new[]
                     {
                         "ReloadObjectVersionsButton", "NextObjectVersionsPageButton",
                         "DownloadObjectVersionButton", "RestoreObjectVersionButton",
                         "DeleteObjectVersionButton", "CleanDeleteMarkersButton",
                         "CloseObjectVersionsButton"
                     })
                AssertButtonIsReadable(dialog, Assert.IsType<Button>(Find(dialog, name)));
        });
    }

    private static Control Find(Control root, string name) =>
        Assert.Single(root.Controls.Find(name, searchAllChildren: true));

    private static void AssertButtonIsReadable(Form dialog, Button button)
    {
        var preferred = TextRenderer.MeasureText(button.Text, button.Font);
        Assert.True(button.ClientSize.Width >= preferred.Width,
            $"{button.Name} text needs {preferred.Width}px but has {button.ClientSize.Width}px in {dialog.Size}.");
        Assert.True(button.ClientSize.Height >= preferred.Height,
            $"{button.Name} text needs {preferred.Height}px but has {button.ClientSize.Height}px in {dialog.Size}.");
    }

    private static void PerformLayout(Control control)
    {
        control.CreateControl();
        control.PerformLayout();
        foreach (Control child in control.Controls) PerformLayout(child);
        control.PerformLayout();
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
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
