using System.Runtime.ExceptionServices;
using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ObjectPropertiesDialogLayoutTests
{
    [Fact]
    public void MetadataCopyIsReadOnlyWithoutImmutableVersion()
    {
        RunSta(() =>
        {
            using var dialog = new ObjectPropertiesDialog(Properties(), "https://s3.amazonaws.com",
                profile: ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3));
            Assert.False(Assert.IsType<Button>(Find(dialog, "SaveObjectMetadataCopyButton")).Enabled);
            Assert.True(Assert.IsType<TextBox>(Find(dialog, "ObjectContentType")).ReadOnly);
            var grid = Assert.IsType<DataGridView>(Find(dialog, "ObjectMetadataGrid"));
            Assert.True(grid.ReadOnly);
            Assert.False(grid.AllowUserToAddRows);
            Assert.False(grid.AllowUserToDeleteRows);
            Assert.Contains("固定版本", Assert.IsType<Label>(Find(dialog, "ObjectMetadataCapabilityReason")).Text);
        });
    }

    [Fact]
    public void EligibleMetadataCopyIsEditableAndClearlyUsesANewExactKey()
    {
        RunSta(() =>
        {
            var properties = Properties() with { Key = "/folder//source ", VersionId = "v1", ServerSideEncryptionMethod = "AES256" };
            var storage = DispatchProxy.Create<IS3StorageService, NoCallsStorage>();
            using var dialog = new ObjectPropertiesDialog(properties, "https://s3.amazonaws.com",
                storage: storage, profile: ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3));
            var save = Assert.IsType<Button>(Find(dialog, "SaveObjectMetadataCopyButton"));
            Assert.True(save.Enabled);
            Assert.Contains("另存为新对象", save.Text);
            Assert.False(Assert.IsType<TextBox>(Find(dialog, "ObjectContentType")).ReadOnly);
            Assert.False(Assert.IsType<DataGridView>(Find(dialog, "ObjectMetadataGrid")).ReadOnly);
            Assert.Equal(properties.Key + ".metadata-copy",
                Assert.IsType<TextBox>(Find(dialog, "ObjectMetadataCopyDestination")).Text);
            var reason = Assert.IsType<Label>(Find(dialog, "ObjectMetadataCapabilityReason")).Text;
            Assert.Contains("BucketOwnerEnforced", reason);
            Assert.Contains("SSE-S3", reason);
            Assert.Contains("无 Object Lock", reason);
        });
    }

    [Fact]
    public void MetadataCopyCommitsActiveCellAndPreservesUnchangedValues()
    {
        RunSta(() =>
        {
            var properties = Properties() with
            {
                VersionId = "v1", ServerSideEncryptionMethod = "AES256", CacheControl = " max-age=10 ",
                Metadata = new Dictionary<string, string> { ["build"] = " before ", ["unchanged"] = " \u00a0 " }
            };
            using var dialog = new ObjectPropertiesDialog(properties, "https://s3.amazonaws.com",
                storage: DispatchProxy.Create<IS3StorageService, NoCallsStorage>(),
                profile: ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3));
            dialog.Show();
            var tabs = Assert.Single(dialog.Controls.Cast<Control>().SelectMany(Flatten).OfType<TabControl>());
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Metadata");
            Application.DoEvents();
            var grid = Assert.IsType<DataGridView>(Find(dialog, "ObjectMetadataGrid"));
            grid.CurrentCell = grid.Rows[0].Cells[1];
            Assert.True(grid.BeginEdit(false));
            Assert.IsType<DataGridViewTextBoxEditingControl>(grid.EditingControl).Text = " after ";

            var headers = dialog.ReadMetadataEdits();
            Assert.False(grid.IsCurrentCellInEditMode);
            Assert.Equal(" after ", headers.Metadata!["build"]);
            Assert.Equal(" \u00a0 ", headers.Metadata["unchanged"]);
            Assert.Equal(" max-age=10 ", headers.CacheControl);
            // Reading or dismissing the editor never starts a storage operation.
            dialog.Close();
        });
    }

    [Fact]
    public void FullLengthKeyHasAValidDifferentCopySuggestion()
    {
        RunSta(() =>
        {
            var properties = Properties() with
            {
                Key = string.Concat(Enumerable.Repeat("\U0001F680", 256)),
                VersionId = "v1", ServerSideEncryptionMethod = "AES256"
            };
            using var dialog = new ObjectPropertiesDialog(properties, "https://s3.amazonaws.com",
                profile: ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3));
            var destination = Assert.IsType<TextBox>(Find(dialog, "ObjectMetadataCopyDestination")).Text;
            ObjectMetadataCopyPolicy.ValidateDestination(properties, destination);
        });
    }

    [Fact]
    public void CopyFailureGuidanceDoesNotSuggestLocalDiskFixesOrBlindRetry()
    {
        foreach (var failure in Enum.GetValues<ObjectMetadataCopyFailure>())
        {
            var error = new ObjectMetadataCopyException(failure, new TimeoutException());
            var suggestion = ErrorDialog.Suggest(error);
            Assert.Contains("检查目标", suggestion);
            Assert.Contains("结果未确认不代表未创建副本", suggestion);
            Assert.DoesNotContain("磁盘", suggestion);
        }
    }

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
            AssertButtonIsReadable(dialog, "SaveObjectMetadataCopyButton");
            AssertButtonIsReadable(dialog, "ReloadObjectTagsButton");
            AssertButtonIsReadable(dialog, "SaveObjectTagsButton");
            AssertButtonIsReadable(dialog, "DeleteObjectTagsButton");
            Assert.IsType<DataGridView>(Find(dialog, "ObjectMetadataGrid"));
            Assert.IsType<DataGridView>(Find(dialog, "ObjectTagsGrid"));
            var tabs = Assert.Single(dialog.Controls.Cast<Control>().SelectMany(Flatten).OfType<TabControl>());
            Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "Tags");
            Assert.False(Assert.IsType<CheckBox>(Find(dialog, "ObjectRetentionAuthorization")).Checked);
            Assert.False(Assert.IsType<CheckBox>(Find(dialog, "ObjectLegalHoldAuthorization")).Checked);
        });
    }

    [Fact]
    public void BatchMetadataDialogExplainsSafeSingleObjectAlternative()
    {
        RunSta(() =>
        {
            using var dialog = new ObjectMetadataBatchDialog(12);
            dialog.Size = dialog.MinimumSize;
            dialog.Show();
            Application.DoEvents();
            PerformLayout(dialog);

            Assert.False(Assert.IsType<CheckBox>(Find(dialog, "ApplyBatchcachecontrol")).Checked);
            Assert.False(Assert.IsType<TextBox>(Find(dialog, "Batchcachecontrol")).Enabled);
            Assert.False(Assert.IsType<CheckBox>(Find(dialog, "ReplaceBatchObjectMetadata")).Checked);
            AssertButtonIsReadable(dialog, "ApplyBatchObjectMetadataButton");
            AssertButtonIsReadable(dialog, "CancelBatchObjectMetadataButton");
            Assert.Contains(
                dialog.Controls.Cast<Control>().SelectMany(Flatten).OfType<Label>(),
                label => label.Text.Contains("另存为新对象", StringComparison.Ordinal));
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

    private static IEnumerable<Control> Flatten(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

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

    public class NoCallsStorage : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Opening the metadata editor must not make storage requests.");
    }
}
