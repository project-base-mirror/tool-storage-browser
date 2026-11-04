using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class BucketManagementDialogLayoutTests
{
    [Fact]
    public void AdvancedBucketPagesAndActionsRemainReadableAtLargeText()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "test",
                ServiceType = S3ServiceType.AmazonS3
            };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new BucketManagementDialog(
                null!, profile, "example-bucket", BucketManagementPage.Cors);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var tabs = dialog.Controls.OfType<TabControl>().Single();
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "CORS");
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "版本控制");
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "默认加密");
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "Tags");
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "生命周期");
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "Object Lock");

            foreach (var name in new[]
                     {
                         "ReloadBucketCorsButton", "ValidateBucketCorsButton", "SaveBucketCorsButton",
                         "SaveBucketVersioningButton", "SaveBucketEncryptionButton", "SaveBucketTagsButton",
                         "ValidateBucketLifecycleButton", "SaveBucketLifecycleButton", "ReloadBucketObjectLockButton"
                     })
                AssertButtonIsReadable(dialog, FindButton(dialog, name));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MinioAclSnapshotDoesNotSelectRemovedPublicOption(bool readDenied)
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with { Name = "test" };
            using var dialog = new BucketManagementDialog(null!, profile, "bucket", BucketManagementPage.Cors);
            var snapshot = new BucketPropertiesSnapshot(
                "bucket", profile.Endpoint, profile.ServiceType, profile.EffectiveSignatureRegion,
                "Disabled", "None", false, new BucketAclSnapshot("owner", BucketAclMode.PublicRead, []),
                null, null, S3ProviderCapabilityRegistry.For(profile.ServiceType).Bucket)
            {
                ReadErrors = readDenied
                    ? new Dictionary<string, string> { ["ACL"] = "Access denied" }
                    : new Dictionary<string, string>()
            };
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(BucketManagementDialog).GetField("_properties", flags)!.SetValue(dialog, snapshot);
            typeof(BucketManagementDialog).GetMethod("ApplyProperties", flags)!.Invoke(dialog, null);
            var choices = (ComboBox)typeof(BucketManagementDialog).GetField("_aclMode", flags)!.GetValue(dialog)!;
            var save = (Button)typeof(BucketManagementDialog).GetField("_aclSave", flags)!.GetValue(dialog)!;
            Assert.Single(choices.Items);
            Assert.Equal(0, choices.SelectedIndex);
            Assert.Equal(!readDenied, save.Enabled);
        });
    }

    private static Button FindButton(Control root, string name) =>
        Assert.IsType<Button>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

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
