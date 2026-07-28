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

    [Fact]
    public void ExportWithOnlyCdnCredentialCanRequestPasswordProtection()
    {
        RunSta(() =>
        {
            using var dialog = new ConnectionExportOptionsDialog(
                profileCount: 1,
                profilesWithCredentials: 0,
                cdnProfileCount: 1,
                cdnCredentials: 1);
            var include = Assert.IsType<CheckBox>(Assert.Single(
                dialog.Controls.Find("IncludeStoredCredentialsCheckBox", searchAllChildren: true)));

            Assert.True(include.Enabled);
            Assert.Contains("CDN", include.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ExportOptionsKeepsConfirmationButtonsReadableAtLargeText()
    {
        RunSta(() =>
        {
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new ConnectionExportOptionsDialog(
                profileCount: 5,
                profilesWithCredentials: 5,
                cdnProfileCount: 4,
                cdnCredentials: 2);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var export = FindButton(dialog, "ContinueConnectionExportButton");
            var cancel = FindButton(dialog, "CancelConnectionExportButton");
            AssertButtonIsReadable(dialog, export);
            AssertButtonIsReadable(dialog, cancel);
            Assert.Same(export, dialog.AcceptButton);
            Assert.Same(cancel, dialog.CancelButton);
        });
    }

    [Fact]
    public void ImportPreviewSummarizesCdnProfilesBindingsAndCredentials()
    {
        RunSta(() =>
        {
            var storage = new ConnectionProfile
            {
                Name = "example",
                Endpoint = "https://s3.amazonaws.com"
            };
            var credential = new CdnCredential
            {
                Name = "cdn-token",
                AuthenticationType = CdnAuthenticationType.BearerToken,
                Secret = "test-only"
            };
            var cdn = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                CredentialId = credential.Id
            };
            var package = new ConnectionArchivePackage(
                [storage],
                ContainsCredentials: true,
                ExportedAtUtc: DateTimeOffset.UtcNow,
                new CdnConfiguration(
                    [cdn],
                    [new CdnBinding
                    {
                        StorageProfileId = storage.Id,
                        Bucket = "assets",
                        CdnProfileId = cdn.Id
                    }]),
                [credential]);

            using var dialog = new ConnectionImportPreviewDialog(package, []);
            var summary = Assert.IsType<Label>(Assert.Single(
                dialog.Controls.Find("ConnectionImportSummary", searchAllChildren: true)));
            var importCredentials = dialog.Controls
                .OfType<Control>()
                .SelectMany(Flatten)
                .OfType<CheckBox>()
                .Single(checkBox => checkBox.Text.Contains("Token/Header", StringComparison.Ordinal));

            Assert.Contains("1 个 CDN 配置", summary.Text, StringComparison.Ordinal);
            Assert.Contains("1 个关联", summary.Text, StringComparison.Ordinal);
            Assert.True(importCredentials.Enabled);
            Assert.False(importCredentials.Checked);
        });
    }

    private static Button FindButton(Control root, string name) =>
        Assert.IsType<Button>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static IEnumerable<Control> Flatten(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Flatten(child))
                yield return descendant;
        }
    }

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
