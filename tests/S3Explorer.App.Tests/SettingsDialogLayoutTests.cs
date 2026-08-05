using System.Runtime.ExceptionServices;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class SettingsDialogLayoutTests
{
    [Fact]
    public void ConfirmationButtonsRemainReadableAndInsideFooterAtLargeText()
    {
        RunSta(() =>
        {
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new SettingsDialog(new AppSettings());
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var footer = Find<FlowLayoutPanel>(dialog, "SettingsDialogFooter");
            var save = Find<Button>(dialog, "SaveSettingsButton");
            var cancel = Find<Button>(dialog, "CancelSettingsButton");

            AssertButtonIsReadable(save);
            AssertButtonIsReadable(cancel);
            Assert.True(footer.ClientRectangle.Contains(save.Bounds));
            Assert.True(footer.ClientRectangle.Contains(cancel.Bounds));
            Assert.False(save.Bounds.IntersectsWith(cancel.Bounds));
            Assert.Same(save, dialog.AcceptButton);
            Assert.Same(cancel, dialog.CancelButton);
        });
    }

    [Fact]
    public void NewSettingsEnableTrayResidenceAndNotifications()
    {
        RunSta(() =>
        {
            using var dialog = new SettingsDialog(new AppSettings());

            var residence = Find<CheckBox>(dialog, "KeepRunningInTray");
            var notifications = Find<CheckBox>(dialog, "ShowTrayTransferNotifications");

            Assert.True(residence.Checked);
            Assert.True(notifications.Checked);
            Assert.True(notifications.Enabled);
        });
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        Assert.IsType<T>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void AssertButtonIsReadable(Button button)
    {
        var preferred = TextRenderer.MeasureText(button.Text, button.Font);
        Assert.True(button.ClientSize.Width >= preferred.Width,
            $"{button.Name} text needs {preferred.Width}px but has {button.ClientSize.Width}px.");
        Assert.True(button.ClientSize.Height >= preferred.Height,
            $"{button.Name} text needs {preferred.Height}px but has {button.ClientSize.Height}px.");
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
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
