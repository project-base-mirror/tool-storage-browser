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

    [Fact]
    public void NewCompatibleConnectionUsesAutoRegion()
    {
        RunSta(() =>
        {
            using var dialog = new ConnectionDialog(null!);
            dialog.Show();
            Application.DoEvents();

            var accountType = Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox"));
            SelectByText(accountType, "S3 兼容存储");
            Application.DoEvents();

            var provider = Assert.IsType<ComboBox>(Find(dialog, "ProviderComboBox"));
            var endpoint = Assert.IsType<TextBox>(Find(dialog, "EndpointTextBox"));
            var region = Assert.IsType<ComboBox>(Find(dialog, "RegionComboBox"));

            Assert.Equal("其他 S3 兼容存储", provider.Text);
            Assert.Equal("https://s3.example.com", endpoint.Text);
            Assert.Equal("auto", region.Text);
            Assert.True(endpoint.Visible);
            Assert.True(region.Visible);
        });
    }

    [Fact]
    public void LegacyCompatibleEndpointIsVisibleAndSurvivesAccountTypeRoundTrip()
    {
        RunSta(() =>
        {
            const string endpointValue = "https://oss-cn-shenzhen.aliyuncs.com";
            var legacy = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "legacy-compatible",
                Endpoint = endpointValue,
                Region = "auto",
                AccessKey = "access",
                SecretKey = "secret"
            };
            using var dialog = new ConnectionDialog(null!, legacy);
            dialog.Show();
            Application.DoEvents();

            var accountType = Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox"));
            var provider = Assert.IsType<ComboBox>(Find(dialog, "ProviderComboBox"));
            var endpoint = Assert.IsType<TextBox>(Find(dialog, "EndpointTextBox"));

            Assert.Equal("S3 兼容存储", accountType.Text);
            Assert.Equal("其他 S3 兼容存储", provider.Text);
            Assert.True(endpoint.Visible);
            Assert.Equal(endpointValue, endpoint.Text);

            SelectByText(accountType, "Amazon S3");
            Application.DoEvents();
            SelectByText(accountType, "S3 兼容存储");
            Application.DoEvents();

            Assert.True(endpoint.Visible);
            Assert.Equal(endpointValue, endpoint.Text);
        });
    }

    [Fact]
    public void ConnectionTestResultIsDetailedCopyableAndInsideDialog()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.Custom) with
            {
                Name = "test",
                AccessKey = "access",
                SecretKey = "secret"
            };
            var result = new ConnectionTestResult(
                false,
                TimeSpan.FromMilliseconds(1234),
                0,
                "服务端拒绝了 ListBuckets 请求，但 Endpoint 已响应。",
                403,
                "AccessDenied",
                "request-123");
            using var dialog = new ConnectionDialog(null!, profile);
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            dialog.Show();

            var details = Assert.IsType<TextBox>(Find(dialog, "ConnectionTestResultTextBox"));
            details.Text = ConnectionTestResultFormatter.Format(result, profile);
            details.Visible = true;
            dialog.PerformLayout();
            Application.DoEvents();

            Assert.True(details.Multiline);
            Assert.True(details.ReadOnly);
            Assert.Contains(result.Message, details.Text, StringComparison.Ordinal);
            Assert.Contains("AccessDenied", details.Text, StringComparison.Ordinal);
            Assert.Contains("request-123", details.Text, StringComparison.Ordinal);
            Assert.True(details.Height >= 62, $"Result height was {details.Height}px.");
            var bounds = dialog.RectangleToClient(details.RectangleToScreen(details.ClientRectangle));
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0);
            Assert.True(bounds.Right <= dialog.ClientSize.Width, $"Result right edge {bounds.Right}px exceeded {dialog.ClientSize.Width}px.");
            Assert.True(bounds.Bottom <= dialog.ClientSize.Height, $"Result bottom edge {bounds.Bottom}px exceeded {dialog.ClientSize.Height}px.");
        });
    }

    private static void SelectByText(ComboBox comboBox, string text)
    {
        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (string.Equals(comboBox.Items[index]?.ToString(), text, StringComparison.Ordinal))
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"ComboBox option was not found: {text}");
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
