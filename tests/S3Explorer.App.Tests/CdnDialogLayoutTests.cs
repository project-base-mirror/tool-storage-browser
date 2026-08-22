using System.Runtime.ExceptionServices;
using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CdnDialogLayoutTests
{
    [Fact]
    public void DiscardConfirmationIsCenteredOnItsOwnerAndDefaultsToKeepChanges()
    {
        RunSta(() =>
        {
            using var dialog = new DiscardCdnChangesDialog();

            Assert.Equal(FormStartPosition.CenterParent, dialog.StartPosition);
            Assert.Equal(DialogResult.No, Assert.IsType<Button>(dialog.AcceptButton).DialogResult);
            Assert.Equal(DialogResult.No, Assert.IsType<Button>(dialog.CancelButton).DialogResult);
            AssertButtonIsReadable(dialog, FindButton(dialog, "DiscardCdnChangesButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "KeepCdnChangesButton"));
        });
    }

    [Fact]
    public void CertificatePersistenceUpdatesOnlyTheSavedMatchingEndpoint()
    {
        var checkedAt = DateTimeOffset.Parse("2026-07-30T08:00:00Z");
        var first = new CdnProfile { Name = "first", BaseUrl = "https://cdn.example.com/" };
        var second = new CdnProfile { Name = "second", BaseUrl = "https://other.example.com" };
        var result = new CdnCertificateCheckResult(
            new Uri("https://cdn.example.com"), checkedAt, checkedAt.AddDays(-1), checkedAt.AddDays(60),
            "CN=cdn.example.com", "CN=CA", new string('A', 64), "Tls13",
            CdnCertificateProblems.None, []);

        var updated = CdnCertificatePersistence.Apply(new CdnConfiguration([first, second], []), first.Id, result);

        Assert.Equal(result, updated.Profiles.Single(profile => profile.Id == first.Id).LastCertificateCheck);
        Assert.Null(updated.Profiles.Single(profile => profile.Id == second.Id).LastCertificateCheck);
        Assert.Throws<InvalidOperationException>(() => CdnCertificatePersistence.Apply(
            new CdnConfiguration([first with { BaseUrl = "https://changed.example.com" }], []),
            first.Id,
            result));
    }

    [Fact]
    public void SpecifiedCdnChoicesKeepEveryMatchingTargetAndMarkTheDefault()
    {
        var first = new CdnProfile { Name = "cdn-a", BaseUrl = "https://a.example.com" };
        var second = new CdnProfile { Name = "cdn-b", BaseUrl = "https://b.example.com" };
        var storageId = Guid.NewGuid();
        var configuration = new CdnConfiguration(
            [first, second],
            [
                new CdnBinding
                {
                    StorageProfileId = storageId,
                    Bucket = "assets",
                    SourcePrefix = "deploy/",
                    CdnProfileId = first.Id,
                    IsDefault = true
                },
                new CdnBinding
                {
                    StorageProfileId = storageId,
                    Bucket = "assets",
                    SourcePrefix = "deploy/",
                    CdnProfileId = second.Id,
                    IsDefault = false
                }
            ]);

        var choices = CdnSpecifiedTargetMenu.Build(
            CdnUrlMapper.ResolveAll(configuration, storageId, "assets", "deploy/game.bin"));

        Assert.Equal(2, choices.Count);
        Assert.Equal("cdn-a（默认）", choices[0].Label);
        Assert.Equal("cdn-b", choices[1].Label);
        Assert.Equal("https://a.example.com/game.bin", choices[0].ToolTip);
        Assert.Equal("https://b.example.com/game.bin", choices[1].ToolTip);
    }

    [Fact]
    public void ConfigurationCenterKeepsPrimaryActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var storage = new ConnectionProfile
            {
                Name = "site-storage",
                Endpoint = "https://s3.example.com"
            };
            var credential = new CredentialProfile
            {
                Name = "purge-token",
                Provider = CredentialProviderKind.GenericHttp,
                Kind = CredentialKind.BearerToken,
                Secret = "test-only"
            };
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                CredentialId = credential.Id,
                PurgeEndpointTemplate = "https://api.example.com/purge?url={url}"
            };
            var binding = new CdnBinding
            {
                StorageProfileId = storage.Id,
                Bucket = "site",
                SourcePrefix = "assets/",
                CdnProfileId = profile.Id,
                CdnPathPrefix = "static/"
            };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnConfigurationDialog(
                [storage],
                new CdnConfiguration([profile], [binding]),
                [credential],
                storage,
                "site",
                new StubCertificateInspector());
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var tabs = Assert.IsType<TabControl>(
                Assert.Single(dialog.Controls.Find("CdnConfigurationTabs", searchAllChildren: true)));
            Assert.Equal(2, tabs.TabPages.Count);
            Assert.DoesNotContain(tabs.TabPages.Cast<TabPage>(), page => page.Name == "CredentialCenterTab");
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnProfileButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CopyCdnProfileButton"));
            var certificate = FindButton(dialog, "CheckCdnCertificateButton");
            AssertButtonIsReadable(dialog, certificate);
            Assert.True(certificate.Enabled);
            Assert.Empty(dialog.Controls.Find("AddCredentialButton", searchAllChildren: true));
            Assert.Empty(dialog.Controls.Find("CheckCredentialPermissionsButton", searchAllChildren: true));
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnBindingButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CopyCdnBindingButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CheckCdnBindingsButton"));
            var profileGrid = Assert.IsType<DataGridView>(Assert.Single(
                dialog.Controls.Find("CdnProfilesTabGrid", searchAllChildren: true)));
            var bindingGrid = Assert.IsType<DataGridView>(Assert.Single(
                dialog.Controls.Find("CdnBindingsTabGrid", searchAllChildren: true)));
            Assert.True(profileGrid.MultiSelect);
            Assert.True(bindingGrid.MultiSelect);
            Assert.Contains(bindingGrid.Columns.Cast<DataGridViewColumn>(), column => column.Name == "check");
            var save = FindButton(dialog, "SaveCdnConfigurationButton");
            AssertButtonIsReadable(dialog, save);
            Assert.Equal("保存全部更改", save.Text);
            Assert.False(save.Enabled);
            Assert.Same(save, dialog.AcceptButton);
            var dirty = Assert.IsType<Label>(Assert.Single(
                dialog.Controls.Find("CdnConfigurationDirtyStatus", searchAllChildren: true)));
            Assert.Equal("没有未保存的更改", dirty.Text);
            typeof(CdnConfigurationDialog)
                .GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null);
            Assert.True(save.Enabled);
            Assert.Contains("未保存", dirty.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CertificateResultKeepsDetailsAndActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var result = new CdnCertificateCheckResult(
                new Uri("https://cdn.example.com"),
                now,
                now.AddDays(-30),
                now.AddDays(12),
                "CN=cdn.example.com",
                "CN=Example CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
                []);
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnCertificateResultDialog(result);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            AssertButtonIsReadable(dialog, FindButton(dialog, "CopyCdnCertificateResultButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CloseCdnCertificateResultButton"));
            var details = Assert.IsType<TextBox>(Assert.Single(
                dialog.Controls.Find("CdnCertificateResultDetails", searchAllChildren: true)));
            Assert.Contains("到期时间", details.Text, StringComparison.Ordinal);
            Assert.Contains("剩余天数：12", details.Text, StringComparison.Ordinal);
            Assert.Contains("吊销状态：未检查", details.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CredentialPermissionResultKeepsSafetyExplanationAndActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var credential = new CredentialProfile
            {
                Name = "aliyun-release",
                Provider = CredentialProviderKind.AlibabaCloud,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "test-access",
                Secret = "test-secret"
            };
            var report = new PermissionCheckReport(
            [
                new PermissionCheckResult(credential.Id,
                [
                    new PermissionCheck("storage", "ListBucket", PermissionCheckState.Passed, "可列举。"),
                    new PermissionCheck("storage", "PutObject", PermissionCheckState.Indeterminate, "未执行写入探针。")
                ])
                {
                    TargetScope = "s3://release/assets/",
                    CheckedAtUtc = DateTimeOffset.Parse("2026-08-17T08:00:00Z")
                }
            ]);
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CredentialPermissionResultDialog(credential, report);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            AssertButtonIsReadable(dialog, FindButton(dialog, "CopyCredentialPermissionResultButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CloseCredentialPermissionResultButton"));
            var details = Assert.IsType<TextBox>(Assert.Single(
                dialog.Controls.Find("CredentialPermissionDetailsTextBox", searchAllChildren: true)));
            Assert.Contains("ListBucket", details.Text, StringComparison.Ordinal);
            Assert.Contains("PutObject", details.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("test-secret", details.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("permission check --probe-write", AllControlText(dialog), StringComparison.Ordinal);
            Assert.Contains("凭据 → 权限检查", AllControlText(dialog), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CertificateCheckCanBeCancelledWithoutClosingConfiguration()
    {
        RunSta(() =>
        {
            var inspector = new BlockingCertificateInspector();
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com"
            };
            using var dialog = new CdnConfigurationDialog(
                [],
                new CdnConfiguration([profile], []),
                [],
                certificateInspector: inspector);
            dialog.Show();

            var check = FindButton(dialog, "CheckCdnCertificateButton");
            check.PerformClick();
            WaitUntil(() => inspector.Started && check.Text == "取消证书检测");
            check.PerformClick();
            var grid = Assert.IsType<DataGridView>(Assert.Single(
                dialog.Controls.Find("CdnProfilesTabGrid", searchAllChildren: true)));
            WaitUntil(() => grid.Rows[0].Cells["certificate"].Value?.ToString() == "检测已取消");

            Assert.False(dialog.IsDisposed);
            Assert.Equal("检测 HTTPS 证书", check.Text);
            Assert.True(check.Enabled);
            dialog.Close();
        });
    }

    [Fact]
    public void ProfileEditorLoadsAndSavesNotes()
    {
        RunSta(() =>
        {
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                Notes = "原备注"
            };
            using var dialog = new CdnProfileEditorDialog(profile, []);
            var notes = Assert.IsType<TextBox>(Assert.Single(
                dialog.Controls.Find("CdnProfileNotes", searchAllChildren: true)));

            Assert.Equal("原备注", notes.Text);
            Assert.True(notes.Multiline);
            Assert.Equal(CdnProfile.MaximumNotesLength, notes.MaxLength);
            notes.Text = "发布域名，证书由平台团队维护。";
            dialog.Show();
            var confirm = FindButton(dialog, "SaveCdnProfileButton");
            Assert.Equal("确定", confirm.Text);
            confirm.PerformClick();

            Assert.Equal(DialogResult.OK, dialog.DialogResult);
            Assert.Equal("发布域名，证书由平台团队维护。", dialog.Profile.Notes);
        });
    }

    [Fact]
    public void CredentialCenterCanOpenDirectlyAndEditorKeepsActionsReadable()
    {
        RunSta(() =>
        {
            var credential = new CredentialProfile
            {
                Name = "aliyun-release",
                Provider = CredentialProviderKind.AlibabaCloud,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "test-access",
                Secret = "test-secret"
            };
            using (var center = new CredentialCenterDialog(
                       [], [credential], CdnConfiguration.Empty))
            {
                Assert.Equal("凭据中心", center.Text);
                Assert.NotNull(Assert.Single(center.Controls.Find("CredentialCenterGrid", searchAllChildren: true)));
                AssertButtonIsReadable(center, FindButton(center, "AddCredentialButton"));
                AssertButtonIsReadable(center, FindButton(center, "CheckCredentialPermissionsButton"));
                var save = FindButton(center, "SaveCredentialCenterButton");
                var cancel = FindButton(center, "CancelCredentialCenterButton");
                AssertButtonIsReadable(center, save);
                AssertButtonIsReadable(center, cancel);
                Assert.False(save.Enabled);
                Assert.Same(save, center.AcceptButton);
                Assert.Same(cancel, center.CancelButton);
            }

            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var editor = new CredentialEditorDialog(credential);
            editor.Font = largerFont;
            editor.Size = editor.MinimumSize;
            PerformLayout(editor);
            AssertButtonIsReadable(editor, FindButton(editor, "SaveCredentialButton"));
            AssertButtonIsReadable(editor, FindButton(editor, "CancelCredentialButton"));
            var secret = Assert.IsType<TextBox>(Assert.Single(
                editor.Controls.Find("CredentialSecret", searchAllChildren: true)));
            Assert.True(secret.UseSystemPasswordChar);
        });
    }

    [Fact]
    public void CredentialAndCdnCentersValidateGroupedConnectionsWithoutDroppingGroups()
    {
        RunSta(() =>
        {
            var group = new ConnectionGroup { Name = "发布环境" };
            var storage = new ConnectionProfile
            {
                Name = "release",
                GroupId = group.Id,
                Endpoint = "https://s3.example.test"
            };

            using (var credentials = new CredentialCenterDialog(
                       [storage],
                       [],
                       CdnConfiguration.Empty,
                       connectionGroups: [group]))
            {
                typeof(CredentialCenterDialog)
                    .GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(credentials, null);
                credentials.Show();
                FindButton(credentials, "SaveCredentialCenterButton").PerformClick();
                Assert.Equal(DialogResult.OK, credentials.DialogResult);
            }

            using (var cdn = new CdnConfigurationDialog(
                       [storage],
                       CdnConfiguration.Empty,
                       [],
                       connectionGroups: [group]))
            {
                typeof(CdnConfigurationDialog)
                    .GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(cdn, null);
                cdn.Show();
                FindButton(cdn, "SaveCdnConfigurationButton").PerformClick();
                Assert.Equal(DialogResult.OK, cdn.DialogResult);
            }
        });
    }

    [Fact]
    public void AliyunProfileUsesTypedCredentialAndDropsGenericPurgeFields()
    {
        RunSta(() =>
        {
            var credential = new CredentialProfile
            {
                Name = "aliyun-release",
                Provider = CredentialProviderKind.AlibabaCloud,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "test-access",
                Secret = "test-secret"
            };
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com/",
                PurgeEndpointTemplate = "https://legacy.example/purge?url={url}",
                PurgeBodyTemplate = "{url}"
            };
            using var editor = new CdnProfileEditorDialog(profile, [credential]);
            editor.Show();
            SelectByText(Assert.IsType<ComboBox>(Assert.Single(
                editor.Controls.Find("CdnProfileProvider", searchAllChildren: true))), "阿里云 CDN");
            SelectByTextContains(Assert.IsType<ComboBox>(Assert.Single(
                editor.Controls.Find("CdnProfileCredential", searchAllChildren: true))), credential.Name);
            FindButton(editor, "SaveCdnProfileButton").PerformClick();

            Assert.Equal(DialogResult.OK, editor.DialogResult);
            Assert.Equal(CdnProfile.AlibabaCloudProviderId, editor.Profile.ProviderId);
            Assert.Equal(credential.Id, editor.Profile.CredentialId);
            Assert.Empty(editor.Profile.PurgeEndpointTemplate);
            Assert.Empty(editor.Profile.PurgeBodyTemplate);
        });
    }

    [Fact]
    public void SavedCertificateStatusIsShownAndOnlyKeptForTheSameEndpoint()
    {
        RunSta(() =>
        {
            var now = DateTimeOffset.Parse("2026-07-29T12:00:00Z");
            var check = new CdnCertificateCheckResult(
                new Uri("https://cdn.example.com"),
                now,
                now.AddDays(-30),
                now.AddDays(60),
                "CN=cdn.example.com",
                "CN=Example CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
                []);
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                LastCertificateCheck = check
            };
            using (var center = new CdnConfigurationDialog([], new CdnConfiguration([profile], []), []))
            {
                var grid = Assert.IsType<DataGridView>(Assert.Single(
                    center.Controls.Find("CdnProfilesTabGrid", searchAllChildren: true)));
                Assert.Contains("正常", grid.Rows[0].Cells["certificate"].Value?.ToString(), StringComparison.Ordinal);
                Assert.Contains("2026-07-29", grid.Rows[0].Cells["certificate"].Value?.ToString(), StringComparison.Ordinal);
            }

            using (var unchanged = new CdnProfileEditorDialog(profile, []))
            {
                unchanged.Show();
                FindButton(unchanged, "SaveCdnProfileButton").PerformClick();
                Assert.NotNull(unchanged.Profile.LastCertificateCheck);
            }

            using (var changed = new CdnProfileEditorDialog(profile, []))
            {
                var url = Assert.IsType<TextBox>(Assert.Single(
                    changed.Controls.Find("CdnProfileBaseUrl", searchAllChildren: true)));
                url.Text = "https://other.example.com";
                changed.Show();
                FindButton(changed, "SaveCdnProfileButton").PerformClick();
                Assert.Null(changed.Profile.LastCertificateCheck);
            }
        });
    }

    [Fact]
    public void RefreshKeepsThePreviouslySelectedProfileAndBinding()
    {
        RunSta(() =>
        {
            var storage = new ConnectionProfile { Name = "storage" };
            var firstProfile = new CdnProfile { Name = "first", BaseUrl = "https://first.example.com" };
            var secondProfile = new CdnProfile { Name = "second", BaseUrl = "https://second.example.com" };
            var firstBinding = new CdnBinding
            {
                StorageProfileId = storage.Id,
                Bucket = "one",
                CdnProfileId = firstProfile.Id
            };
            var secondBinding = new CdnBinding
            {
                StorageProfileId = storage.Id,
                Bucket = "two",
                CdnProfileId = secondProfile.Id
            };
            using var dialog = new CdnConfigurationDialog(
                [storage],
                new CdnConfiguration([firstProfile, secondProfile], [firstBinding, secondBinding]),
                []);
            var profiles = Assert.IsType<DataGridView>(Assert.Single(
                dialog.Controls.Find("CdnProfilesTabGrid", searchAllChildren: true)));
            var bindings = Assert.IsType<DataGridView>(Assert.Single(
                dialog.Controls.Find("CdnBindingsTabGrid", searchAllChildren: true)));
            SelectOnly(profiles, secondProfile.Id);
            SelectOnly(bindings, secondBinding.Id);

            typeof(CdnConfigurationDialog)
                .GetMethod("RefreshAll", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, [null, null]);

            Assert.Equal(secondProfile.Id, Assert.Single(profiles.SelectedRows.Cast<DataGridViewRow>()).Tag);
            Assert.Equal(secondBinding.Id, Assert.Single(bindings.SelectedRows.Cast<DataGridViewRow>()).Tag);
        });
    }

    [Fact]
    public void BindingEditorLoadsAndSavesUploadAutomationAtLargeText()
    {
        RunSta(() =>
        {
            var storage = new ConnectionProfile
            {
                Name = "site-storage",
                Endpoint = "https://s3.example.com"
            };
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                PurgeEndpointTemplate = "https://api.example.com/purge?url={url}"
            };
            var binding = new CdnBinding
            {
                StorageProfileId = storage.Id,
                Bucket = "site",
                CdnProfileId = profile.Id,
                NewObjectAction = CdnUploadAction.Warmup,
                OverwriteAction = CdnUploadAction.PurgeThenWarmup
            };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnBindingEditorDialog(
                binding,
                [storage],
                [profile],
                storage,
                "site");
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var newObject = Assert.IsType<ComboBox>(Assert.Single(
                dialog.Controls.Find("CdnBindingNewObjectAction", searchAllChildren: true)));
            var overwrite = Assert.IsType<ComboBox>(Assert.Single(
                dialog.Controls.Find("CdnBindingOverwriteAction", searchAllChildren: true)));
            Assert.Equal(1, newObject.SelectedIndex);
            Assert.Equal(2, overwrite.SelectedIndex);
            AssertButtonIsReadable(dialog, FindButton(dialog, "SaveCdnBindingButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CancelCdnBindingButton"));

            newObject.SelectedIndex = 0;
            overwrite.SelectedIndex = 1;
            dialog.Show();
            FindButton(dialog, "SaveCdnBindingButton").PerformClick();

            Assert.Equal(CdnUploadAction.None, dialog.Binding.NewObjectAction);
            Assert.Equal(CdnUploadAction.Purge, dialog.Binding.OverwriteAction);
        });
    }

    [Fact]
    public void DownloadTestKeepsTestAndCloseActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var profile = new CdnProfile { Name = "site-cdn", BaseUrl = "https://cdn.example.com" };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnDownloadTestDialog(
                new StubDeliveryService(),
                profile,
                null,
                new Uri("https://cdn.example.com/assets/app.js"));
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var run = FindButton(dialog, "RunCdnDownloadTestButton");
            var cancel = FindButton(dialog, "CancelCdnDownloadTestButton");
            var close = FindButton(dialog, "CloseCdnDownloadTestButton");
            AssertButtonIsReadable(dialog, run);
            AssertButtonIsReadable(dialog, cancel);
            AssertButtonIsReadable(dialog, close);
            var timeout = Assert.IsType<NumericUpDown>(Assert.Single(
                dialog.Controls.Find("CdnDownloadTimeoutSeconds", searchAllChildren: true)));
            var status = Assert.IsType<Label>(Assert.Single(
                dialog.Controls.Find("CdnDownloadTestStatus", searchAllChildren: true)));
            Assert.Equal(profile.TimeoutSeconds, timeout.Value);
            var footer = Assert.IsType<TableLayoutPanel>(status.Parent);
            var actions = Assert.IsType<FlowLayoutPanel>(run.Parent);
            Assert.Equal(0, footer.GetColumn(status));
            Assert.Equal(1, footer.GetColumn(actions));
            Assert.Same(run, dialog.AcceptButton);
            Assert.Same(close, dialog.CancelButton);
        });
    }

    [Fact]
    public void DownloadTestCanBeCancelledWithoutClosingDialog()
    {
        RunSta(() =>
        {
            var service = new BlockingDeliveryService();
            var profile = new CdnProfile { Name = "site-cdn", BaseUrl = "https://cdn.example.com" };
            using var dialog = new CdnDownloadTestDialog(
                service,
                profile,
                null,
                new Uri("https://cdn.example.com/assets/app.js"));
            dialog.Show();

            WaitUntil(() => service.Started && FindButton(dialog, "CancelCdnDownloadTestButton").Enabled);
            FindButton(dialog, "CancelCdnDownloadTestButton").PerformClick();
            WaitUntil(() => FindLabel(dialog, "CdnDownloadTestStatus").Text == "测试已取消");

            Assert.True(FindButton(dialog, "RunCdnDownloadTestButton").Enabled);
            Assert.False(FindButton(dialog, "CancelCdnDownloadTestButton").Enabled);
            Assert.False(dialog.IsDisposed);
            dialog.Close();
        });
    }

    [Fact]
    public void DownloadTestReportsConfiguredTimeout()
    {
        RunSta(() =>
        {
            var service = new BlockingDeliveryService();
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                TimeoutSeconds = 1
            };
            using var dialog = new CdnDownloadTestDialog(
                service,
                profile,
                null,
                new Uri("https://cdn.example.com/assets/app.js"));
            dialog.Show();

            WaitUntil(
                () => FindLabel(dialog, "CdnDownloadTestStatus").Text == "测试超时（1 秒）",
                TimeSpan.FromSeconds(5));

            Assert.True(FindButton(dialog, "RunCdnDownloadTestButton").Enabled);
            Assert.False(FindButton(dialog, "CancelCdnDownloadTestButton").Enabled);
            dialog.Close();
        });
    }

    private static Button FindButton(Control root, string name) =>
        Assert.IsType<Button>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static Label FindLabel(Control root, string name) =>
        Assert.IsType<Label>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void SelectOnly(DataGridView grid, Guid id)
    {
        grid.ClearSelection();
        var row = grid.Rows.Cast<DataGridViewRow>().Single(item => item.Tag is Guid value && value == id);
        row.Selected = true;
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

    private static void SelectByTextContains(ComboBox comboBox, string text)
    {
        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index]?.ToString()?.Contains(text, StringComparison.Ordinal) == true)
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }
        throw new Xunit.Sdk.XunitException($"ComboBox option containing text was not found: {text}");
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected dialog state was not reached.");
            Application.DoEvents();
            Thread.Sleep(10);
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
        var hierarchy = new List<string>
        {
            $"{button.Name}={button.Bounds}"
        };
        for (var parent = button.Parent; parent is not null && parent != dialog; parent = parent.Parent)
        {
            bounds.Offset(parent.Left, parent.Top);
            hierarchy.Add($"{parent.Name}/{parent.GetType().Name}={parent.Bounds}; Client={parent.ClientRectangle}");
        }
        Assert.True(dialog.ClientRectangle.Contains(bounds),
            $"{button.Name} bounds {bounds} were outside {dialog.ClientRectangle}. Hierarchy: {string.Join(" -> ", hierarchy)}");
    }

    private static void PerformLayout(Control control)
    {
        control.CreateControl();
        control.PerformLayout();
        foreach (Control child in control.Controls)
            PerformLayout(child);
        control.PerformLayout();
    }

    private static string AllControlText(Control control) =>
        string.Join('\n', new[] { control.Text }.Concat(
            control.Controls.Cast<Control>().Select(AllControlText)));

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

    private sealed class StubDeliveryService : ICdnDeliveryService
    {
        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnProbeResult(
                url,
                url,
                206,
                "Partial Content",
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
                1024,
                1024,
                "application/javascript",
                "X-Cache: HIT",
                new Dictionary<string, string> { ["X-Cache"] = "HIT" }));

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnOperationResult(true, 200, TimeSpan.Zero, 0, "ok"));

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnOperationResult(true, 200, TimeSpan.Zero, 0, "ok"));
    }

    private sealed class BlockingDeliveryService : ICdnDeliveryService
    {
        public bool Started { get; private set; }

        public async Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken)
        {
            Started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking probe should only finish through cancellation.");
        }

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCertificateInspector : ICdnCertificateInspector
    {
        public Task<CdnCertificateCheckResult> InspectAsync(
            CdnProfile profile,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CdnCertificateCheckResult(
                new Uri(profile.BaseUrl),
                now,
                now.AddDays(-30),
                now.AddDays(60),
                "CN=cdn.example.com",
                "CN=Example CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
            []));
        }
    }

    private sealed class BlockingCertificateInspector : ICdnCertificateInspector
    {
        public bool Started { get; private set; }

        public async Task<CdnCertificateCheckResult> InspectAsync(
            CdnProfile profile,
            CancellationToken cancellationToken)
        {
            Started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The certificate check should finish through cancellation.");
        }
    }
}
