using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ConnectionDialogCredentialLayoutTests
{
    [Fact]
    public void SharedProfileShowsOnlyItsNonSensitiveReferenceFields()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "AWS shared",
                CredentialSource = CredentialSourceKind.AwsSharedProfile,
                AwsProfileName = "readonly"
            };
            using var dialog = new ConnectionDialog(null!, profile);
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            dialog.Show();
            Application.DoEvents();

            var source = Assert.IsType<ComboBox>(Find(dialog, "CredentialSourceComboBox"));
            var awsProfile = Assert.IsType<TextBox>(Find(dialog, "AwsProfileNameTextBox"));
            var access = Find(dialog, "AccessKeyTextBox");
            var secret = Find(dialog, "SecretKeyTextBox");
            var session = Find(dialog, "SessionTokenTextBox");

            Assert.True(source.Enabled);
            Assert.Contains("shared", source.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(awsProfile.Visible);
            Assert.Equal("readonly", awsProfile.Text);
            Assert.False(access.Visible);
            Assert.False(secret.Visible);
            Assert.False(session.Visible);
            Assert.True(source.Width >= 240, $"Credential source width was {source.Width}px.");

            source.SelectedIndex = 0;
            Application.DoEvents();
            Assert.True(access.Visible);
            Assert.True(secret.Visible);
            source.SelectedIndex = 1;
            Application.DoEvents();
            Assert.Equal("readonly", awsProfile.Text);
        });
    }

    [Fact]
    public void CompatibleProviderLocksCredentialSourceToStoredKeys()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
            {
                Name = "MinIO",
                AccessKey = "access",
                SecretKey = "secret"
            };
            using var dialog = new ConnectionDialog(null!, profile);
            dialog.Show();
            Application.DoEvents();

            var source = Assert.IsType<ComboBox>(Find(dialog, "CredentialSourceComboBox"));

            Assert.False(source.Enabled);
            Assert.Contains("Access Key", source.Text, StringComparison.Ordinal);
            Assert.True(Find(dialog, "AccessKeyTextBox").Visible);
            Assert.True(Find(dialog, "SecretKeyTextBox").Visible);
            Assert.False(Find(dialog, "AwsProfileNameTextBox").Visible);
        });
    }

    private static Control Find(Control root, string name) =>
        Assert.Single(root.Controls.Find(name, searchAllChildren: true));

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
