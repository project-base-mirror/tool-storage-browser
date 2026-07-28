using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ConnectionTransferDialogLayoutTests
{
    [Fact]
    public void ImportPreviewKeepsSelectionAndConfirmationButtonsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var package = new ConnectionArchivePackage(
                [new ConnectionProfile { Name = "example", Endpoint = "https://s3.amazonaws.com" }],
                ContainsCredentials: false,
                ExportedAtUtc: DateTimeOffset.UtcNow);

            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new ConnectionImportPreviewDialog(package, []);
            dialog.Font = largerFont;
            PerformLayout(dialog);

            var selectAll = FindButton(dialog, "SelectAllConnectionsButton");
            var selectNone = FindButton(dialog, "SelectNoConnectionsButton");
            var import = FindButton(dialog, "ImportConnectionsButton");

            AssertButtonIsReadable(dialog, selectAll);
            AssertButtonIsReadable(dialog, selectNone);
            AssertButtonIsReadable(dialog, import);
            Assert.True(import.Enabled);
            Assert.Same(import, dialog.AcceptButton);
        });
    }

    [Fact]
    public void PasswordDialogKeepsUnlockButtonReadableAtLargeText()
    {
        RunSta(() =>
        {
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new ConnectionArchivePasswordDialog();
            dialog.Font = largerFont;
            PerformLayout(dialog);

            var unlock = FindButton(dialog, "UnlockConnectionArchiveButton");

            AssertButtonIsReadable(dialog, unlock);
            Assert.Same(unlock, dialog.AcceptButton);
        });
    }

    [Fact]
    public void ExportWithoutStoredKeysCannotRequestAnUnnecessaryPassword()
    {
        RunSta(() =>
        {
            using var dialog = new ConnectionExportOptionsDialog(profileCount: 2, profilesWithCredentials: 0);
            var include = Assert.IsType<CheckBox>(Assert.Single(
                dialog.Controls.Find("IncludeStoredCredentialsCheckBox", searchAllChildren: true)));

            Assert.False(include.Enabled);
            Assert.False(include.Checked);
            Assert.Contains("已保存", include.Text, StringComparison.Ordinal);
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
            $"{button.Name} bounds {bounds} were outside the dialog client area {dialog.ClientRectangle}.");
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
