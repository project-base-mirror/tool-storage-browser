using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ConfigurationRecoveryDialogTests
{
    [Fact]
    public void RecoveryIsOfferedOnlyAfterCanonicalFilesWereQuarantined()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-recovery-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var corruption = new InvalidDataException("test corruption");
            Assert.False(ConfigurationRecoveryDialog.CanOfferRecovery(root, corruption));
            File.WriteAllText(Path.Combine(root, "configuration.json.corrupt-test"), "preserved");
            Assert.True(ConfigurationRecoveryDialog.CanOfferRecovery(root, corruption));
            Assert.False(ConfigurationRecoveryDialog.CanOfferRecovery(root, new TimeoutException("locked")));
            Assert.False(ConfigurationRecoveryDialog.CanOfferRecovery(root, new UnauthorizedAccessException("denied")));
            Assert.False(ConfigurationRecoveryDialog.CanOfferRecovery(root, new OperationCanceledException()));
            File.WriteAllText(Path.Combine(root, "configuration.json.bak"), "backup");
            Assert.False(ConfigurationRecoveryDialog.CanOfferRecovery(root, corruption));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OpeningRecoveryDialogDoesNotResetOrChooseADestructiveDefault()
    {
        RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3explorer-recovery-dialog-" + Guid.NewGuid().ToString("N"));
            using var dialog = new ConfigurationRecoveryDialog(root, new InvalidDataException("test corruption"));
            dialog.CreateControl();
            dialog.PerformLayout();
            Assert.Null(dialog.RecoveredStore);
            Assert.Null(dialog.AcceptButton);
            Assert.False(new DurableJsonFile(Path.Combine(root, "configuration.json")).HasPersistedState);
            var exit = Assert.IsType<Button>(Assert.Single(dialog.Controls.Find("ExitRecovery", true)));
            Assert.Same(exit, dialog.CancelButton);
            Assert.Equal(DialogResult.Cancel, exit.DialogResult);
            Assert.NotNull(Assert.Single(dialog.Controls.Find("RestoreConfiguration", true)));
            Assert.NotNull(Assert.Single(dialog.Controls.Find("ResetConfiguration", true)));
            var details = Assert.IsType<TextBox>(Assert.Single(dialog.Controls.Find("RecoveryDetails", true)));
            Assert.True(details.ReadOnly);
            Assert.Contains(root, details.Text, StringComparison.Ordinal);
            Assert.Contains("test corruption", details.Text, StringComparison.Ordinal);
        });
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
