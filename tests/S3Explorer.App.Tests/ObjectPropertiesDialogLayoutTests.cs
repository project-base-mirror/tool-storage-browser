using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ObjectPropertiesDialogLayoutTests
{
    [Fact]
    public void GeneralFieldsAndDownloadActionsRemainReadableAtLargeText()
    {
        RunSta(() =>
        {
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new ObjectPropertiesDialog(
                Properties(),
                "https://oss-cn-shenzhen.aliyuncs.com",
                cdnProfileName: "ali-weihu-rongyao");
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            dialog.Show();
            Application.DoEvents();
            PerformLayout(dialog);

            var table = Assert.IsType<TableLayoutPanel>(Find(dialog, "ObjectPropertiesGeneralTable"));
            foreach (var label in table.Controls.OfType<Label>())
            {
                var preferred = label.GetPreferredSize(Size.Empty);
                Assert.True(
                    label.ClientSize.Width >= preferred.Width,
                    $"{label.Text} needs {preferred.Width}px but has {label.ClientSize.Width}px.");
                Assert.True(
                    label.ClientSize.Height >= preferred.Height,
                    $"{label.Text} needs {preferred.Height}px but has {label.ClientSize.Height}px.");
            }
            for (var row = 0; row < table.RowCount; row++)
            {
                var label = Assert.IsType<Label>(table.GetControlFromPosition(0, row));
                var value = Assert.IsType<TextBox>(table.GetControlFromPosition(1, row));
                Assert.InRange(
                    Math.Abs(label.Top - value.Top),
                    0,
                    2);
            }

            AssertButtonIsReadable(dialog, "ObjectStorageDownloadButton");
            var cdn = AssertButtonIsReadable(dialog, "CdnDownloadButton");
            Assert.True(cdn.Enabled);
            AssertButtonIsReadable(dialog, "CloseObjectPropertiesButton");
            AssertButtonIsReadable(dialog, "SaveObjectRetentionButton");
            AssertButtonIsReadable(dialog, "SaveObjectLegalHoldButton");
            AssertButtonIsReadable(dialog, "ReloadObjectLockButton");
            Assert.False(Assert.IsType<CheckBox>(Find(dialog, "ObjectRetentionAuthorization")).Checked);
            Assert.False(Assert.IsType<CheckBox>(Find(dialog, "ObjectLegalHoldAuthorization")).Checked);
        });
    }

    [Fact]
    public void CdnDownloadIsDisabledWithoutBindingAndActionsAreExplicit()
    {
        RunSta(() =>
        {
            using var unavailable = new ObjectPropertiesDialog(
                Properties(),
                "https://s3.example.com");
            unavailable.Show();
            Application.DoEvents();

            Assert.False(Assert.IsType<Button>(Find(unavailable, "CdnDownloadButton")).Enabled);
            Assert.Equal(ObjectPropertiesAction.None, unavailable.SelectedAction);

            Assert.IsType<Button>(Find(unavailable, "ObjectStorageDownloadButton")).PerformClick();
            Assert.Equal(ObjectPropertiesAction.DownloadFromObjectStorage, unavailable.SelectedAction);

            using var available = new ObjectPropertiesDialog(
                Properties(),
                "https://s3.example.com",
                cdnProfileName: "site-cdn");
            available.Show();
            Application.DoEvents();
            Assert.IsType<Button>(Find(available, "CdnDownloadButton")).PerformClick();
            Assert.Equal(ObjectPropertiesAction.DownloadFromCdn, available.SelectedAction);
        });
    }

    private static ObjectProperties Properties() => new(
        "oss-muso",
        "deploy/game-survival/_availability/codex-20260731-a5f2c4e1.txt",
        69,
        DateTimeOffset.Parse("2026-07-31T19:29:40+08:00"),
        "\"35A5161706A0CA4368B37E281642414F\"",
        "text/plain",
        "STANDARD",
        null,
        new Dictionary<string, string>());

    private static Control Find(Control root, string name) =>
        Assert.Single(root.Controls.Find(name, searchAllChildren: true));

    private static Button AssertButtonIsReadable(Form dialog, string name)
    {
        var button = Assert.IsType<Button>(Find(dialog, name));
        var preferred = TextRenderer.MeasureText(button.Text, button.Font);
        Assert.True(
            button.ClientSize.Width >= preferred.Width,
            $"{button.Name} text needs {preferred.Width}px but has {button.ClientSize.Width}px in {dialog.Size}.");
        Assert.True(
            button.ClientSize.Height >= preferred.Height,
            $"{button.Name} text needs {preferred.Height}px but has {button.ClientSize.Height}px in {dialog.Size}.");
        return button;
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
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
