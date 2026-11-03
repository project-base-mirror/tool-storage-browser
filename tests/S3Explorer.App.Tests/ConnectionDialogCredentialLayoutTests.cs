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
            var credential = Assert.IsType<ComboBox>(Find(dialog, "StorageCredentialComboBox"));

            Assert.True(source.Enabled);
            Assert.Contains("shared", source.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(awsProfile.Visible);
            Assert.Equal("readonly", awsProfile.Text);
            Assert.False(credential.Visible);
            Assert.True(source.Width >= 240, $"Credential source width was {source.Width}px.");

            source.SelectedIndex = 0;
            Application.DoEvents();
            Assert.True(credential.Visible);
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
            var credential = new CredentialProfile
            {
                Name = "MinIO deployment key",
                Provider = CredentialProviderKind.S3Compatible,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "access",
                Secret = "secret"
            };
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
            {
                Name = "MinIO",
                CredentialId = credential.Id,
                AccessKey = credential.AccessKeyId,
                SecretKey = credential.Secret
            };
            using var dialog = new ConnectionDialog(null!, profile, [credential]);
            dialog.Show();
            Application.DoEvents();

            var source = Assert.IsType<ComboBox>(Find(dialog, "CredentialSourceComboBox"));

            Assert.False(source.Enabled);
            Assert.Contains("Access Key", source.Text, StringComparison.Ordinal);
            var credentialChoice = Assert.IsType<ComboBox>(Find(dialog, "StorageCredentialComboBox"));
            Assert.True(credentialChoice.Visible);
            Assert.Contains(credential.Name, credentialChoice.Text, StringComparison.Ordinal);
            Assert.False(Find(dialog, "AwsProfileNameTextBox").Visible);
        });
    }

    [Fact]
    public void AmazonS3ExplainsWhenVaultHasOnlyIncompatibleCredentialsAndOffersQuickCreate()
    {
        RunSta(() =>
        {
            var aliyunCredential = new CredentialProfile
            {
                Name = "Aliyun deployment key",
                Provider = CredentialProviderKind.AlibabaCloud,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "access",
                Secret = "secret"
            };
            using var dialog = new ConnectionDialog(
                null!,
                credentials: [aliyunCredential],
                saveNewCredentialAsync: credential =>
                    Task.FromResult<IReadOnlyList<CredentialProfile>>([aliyunCredential, credential]));
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            dialog.Show();
            Application.DoEvents();

            var credential = Assert.IsType<ComboBox>(Find(dialog, "StorageCredentialComboBox"));
            var create = Assert.IsType<Button>(Find(dialog, "NewStorageCredentialButton"));

            Assert.Single(credential.Items);
            Assert.Contains("没有与 Amazon S3 兼容", credential.Text, StringComparison.Ordinal);
            Assert.True(create.Visible);
            Assert.True(create.Enabled);
            Assert.True(credential.Width >= 220,
                $"Credential selector width was {credential.Width}px; picker={credential.Parent?.Width}px; button={create.Width}px.");
            var createBounds = dialog.RectangleToClient(create.RectangleToScreen(create.ClientRectangle));
            Assert.True(createBounds.Right <= dialog.ClientSize.Width,
                $"Quick-create button right edge {createBounds.Right}px exceeded {dialog.ClientSize.Width}px.");
            Assert.True(createBounds.Bottom <= dialog.ClientSize.Height,
                $"Quick-create button bottom edge {createBounds.Bottom}px exceeded {dialog.ClientSize.Height}px.");
            Assert.Contains("凭据中心已有 1 个凭据", AllControlText(dialog), StringComparison.Ordinal);
            Assert.Contains("其他提供方的凭据不会显示", AllControlText(dialog), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CreatedCompatibleCredentialRefreshesAndSelectsWithoutClosingConnectionDialog()
    {
        RunSta(() =>
        {
            IReadOnlyList<CredentialProfile> saved = [];
            using var dialog = new ConnectionDialog(
                null!,
                credentials: [],
                saveNewCredentialAsync: credential =>
                {
                    saved = [credential];
                    return Task.FromResult(saved);
                });
            dialog.Show();
            Application.DoEvents();
            var created = new CredentialProfile
            {
                Name = "AWS release key",
                Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "AKIATEST",
                Secret = "secret"
            };

            dialog.AddCreatedCredentialAsync(created).GetAwaiter().GetResult();
            Application.DoEvents();

            var credential = Assert.IsType<ComboBox>(Find(dialog, "StorageCredentialComboBox"));
            Assert.Single(saved);
            Assert.Single(credential.Items);
            Assert.Contains(created.Name, credential.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(created.Secret, AllControlText(dialog), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void QuickCreateEditorUsesRequestedStorageProviderAndCredentialKind()
    {
        RunSta(() =>
        {
            using var editor = new CredentialEditorDialog(
                null,
                CredentialProviderKind.AmazonWebServices,
                CredentialKind.AccessKeyPair);
            editor.Show();
            Application.DoEvents();

            var provider = Assert.IsType<ComboBox>(Find(editor, "CredentialProvider"));
            var type = Assert.IsType<ComboBox>(Find(editor, "CdnCredentialType"));

            Assert.Equal("Amazon Web Services", provider.Text);
            Assert.Equal("Access Key / Secret Key", type.Text);
        });
    }

    [Fact]
    public void AssumeRoleShowsRoleChainFieldsAndReferencesExternalIdCredential()
    {
        RunSta(() =>
        {
            var externalIdCredential = new CredentialProfile
            {
                Name = "Audit role External ID",
                Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.SecretValue,
                Secret = "external-secret"
            };
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "Audit role",
                CredentialSource = CredentialSourceKind.AwsAssumeRole,
                AwsSourceProfileName = "bootstrap",
                AwsRoleArn = "arn:aws:iam::123456789012:role/Audit",
                AwsRoleSessionName = "s3explorer-audit",
                AwsRoleSourceIdentity = "operator-42",
                AwsExternalIdCredentialId = externalIdCredential.Id,
                AwsExternalId = externalIdCredential.Secret,
                AwsSessionDurationSeconds = 1800
            };
            using var dialog = new ConnectionDialog(null!, profile, [externalIdCredential]);
            dialog.Show();
            Application.DoEvents();

            Assert.True(Find(dialog, "AwsSourceProfileNameTextBox").Visible);
            Assert.True(Find(dialog, "AwsRoleArnTextBox").Visible);
            Assert.True(Find(dialog, "AwsRoleSessionNameTextBox").Visible);
            Assert.True(Find(dialog, "AwsRoleSourceIdentityTextBox").Visible);
            var externalId = Assert.IsType<ComboBox>(Find(dialog, "AwsExternalIdCredentialComboBox"));
            Assert.True(externalId.Visible);
            Assert.Contains(externalIdCredential.Name, externalId.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(externalIdCredential.Secret, AllControlText(dialog), StringComparison.Ordinal);
            Assert.False(Find(dialog, "AwsWebIdentityTokenFileTextBox").Visible);
            Assert.False(Find(dialog, "StorageCredentialComboBox").Visible);
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
    public void R2_has_its_own_account_type_and_category_templates_never_leak()
    {
        RunSta(() =>
        {
            using var dialog = new ConnectionDialog(null!, ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2));
            dialog.Show();
            Application.DoEvents();
            var account = Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox"));
            var provider = Assert.IsType<ComboBox>(Find(dialog, "ProviderComboBox"));
            var endpoint = Assert.IsType<TextBox>(Find(dialog, "EndpointTextBox"));
            Assert.Equal("Cloudflare R2", account.Text);
            Assert.Equal("Cloudflare R2", provider.Text);
            Assert.Single(provider.Items);
            Assert.False(provider.Enabled);
            Assert.True(endpoint.Visible);
            Assert.False(Find(dialog, "RegionComboBox").Visible);
            Assert.Equal(S3ServiceType.CloudflareR2, ReadDraft(dialog).ServiceType);
            foreach (var category in Enum.GetValues<S3AccountCategory>())
            {
                SelectByText(account, S3ProviderCatalog.CategoryDisplayName(category));
                Application.DoEvents();
                Assert.Equal(S3ProviderCatalog.ForCategory(category).Select(item => item.DisplayName),
                    provider.Items.Cast<object>().Select(item => item.ToString()));
                Assert.Equal(category, S3ProviderCatalog.Get(ReadDraft(dialog).ServiceType).Category);
            }
        });
    }

    [Fact]
    public void Custom_endpoint_survives_provider_and_account_switches_and_is_saved()
    {
        RunSta(() =>
        {
            using var dialog = new ConnectionDialog(null!, ConnectionProfile.CreatePreset(S3ServiceType.Custom));
            dialog.Show();
            var account = Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox"));
            var provider = Assert.IsType<ComboBox>(Find(dialog, "ProviderComboBox"));
            var endpoint = Assert.IsType<TextBox>(Find(dialog, "EndpointTextBox"));
            const string custom = "https://my-account.r2.cloudflarestorage.com";
            endpoint.Text = custom;
            SelectByText(provider, "MinIO");
            Application.DoEvents();
            Assert.Equal(custom, endpoint.Text);
            SelectByText(account, "Cloudflare R2");
            Application.DoEvents();
            Assert.Equal(custom, endpoint.Text);
            Assert.Equal(S3ServiceType.CloudflareR2, ReadDraft(dialog).ServiceType);
            SelectByText(account, "Amazon S3");
            Application.DoEvents();
            Assert.True(endpoint.Visible);
            Assert.Equal(custom, ReadDraft(dialog).Endpoint);
            SelectByText(account, "Google Cloud Storage");
            Application.DoEvents();
            Assert.Equal(custom, ReadDraft(dialog).Endpoint);
            SelectByText(account, "S3 兼容存储");
            Application.DoEvents();
            Assert.Equal("MinIO", provider.Text);
            Assert.Equal(custom, endpoint.Text);
            Assert.Equal(custom, ReadDraft(dialog).Endpoint);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://s3.example.com")]
    [InlineData("http://s3.example.com/")]
    public void Blank_or_template_default_endpoint_follows_new_template(string original)
    {
        RunSta(() =>
        {
            using var dialog = new ConnectionDialog(null!, ConnectionProfile.CreatePreset(S3ServiceType.Custom));
            dialog.Show();
            var endpoint = Assert.IsType<TextBox>(Find(dialog, "EndpointTextBox"));
            endpoint.Text = original;
            SelectByText(Assert.IsType<ComboBox>(Find(dialog, "ProviderComboBox")), "MinIO");
            Application.DoEvents();
            Assert.Equal(ConnectionProfile.CreatePreset(S3ServiceType.MinIO).Endpoint, endpoint.Text);
            SelectByText(Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox")), "Cloudflare R2");
            Application.DoEvents();
            Assert.Equal(ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2).Endpoint, endpoint.Text);
        });
    }

    [Fact]
    public void Custom_endpoint_preservation_does_not_mutate_profile_on_cancel()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.Custom) with { Endpoint = "https://storage.example.test/custom" };
            using var dialog = new ConnectionDialog(null!, profile);
            dialog.Show();
            SelectByText(Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox")), "Cloudflare R2");
            Application.DoEvents();
            dialog.Close();
            Assert.Equal(profile, dialog.Profile);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AwsCustomEndpointReopensWithoutChangingProviderOrCredential(bool withCredential)
    {
        RunSta(() =>
        {
            var awsKey = new CredentialProfile
            {
                Name = "AWS proxy key", Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.AccessKeyPair, AccessKeyId = "synthetic-access", Secret = "synthetic-secret"
            };
            var otherKey = awsKey with { Id = Guid.NewGuid(), Name = "Other S3 key", Provider = CredentialProviderKind.S3Compatible };
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Endpoint = "https://s3-proxy.example.test",
                CredentialId = withCredential ? awsKey.Id : null,
                AccessKey = withCredential ? awsKey.AccessKeyId : string.Empty,
                SecretKey = withCredential ? awsKey.Secret : string.Empty
            };
            using var dialog = new ConnectionDialog(null!, profile, withCredential ? [awsKey, otherKey] : []);
            dialog.Show();
            Application.DoEvents();
            Assert.Equal("Amazon S3", Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox")).Text);
            var saved = ReadDraft(dialog);
            Assert.Equal(profile.ServiceType, saved.ServiceType);
            Assert.Equal(profile.Endpoint, saved.Endpoint);
            Assert.Equal(profile.CredentialId, saved.CredentialId);
            using var reopened = new ConnectionDialog(null!, saved, withCredential ? [awsKey, otherKey] : []);
            Assert.Equal(profile.ServiceType, reopened.Profile.ServiceType);
            Assert.Equal(profile.CredentialId, reopened.Profile.CredentialId);
        });
    }

    [Fact]
    public void Explicit_upload_signing_loads_and_survives_provider_round_trip()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.Custom) with
            {
                Endpoint = "https://test.r2.cloudflarestorage.com",
                UploadSigningMode = UploadSigningMode.UnsignedPayload
            };
            using var dialog = new ConnectionDialog(null!, profile);
            dialog.Show();
            var signing = Assert.IsType<ComboBox>(Find(dialog, "UploadSigningComboBox"));
            Assert.Contains("兼容", signing.Text, StringComparison.Ordinal);
            Assert.Equal(UploadSigningMode.UnsignedPayload, ReadDraft(dialog).UploadSigningMode);
            var account = Assert.IsType<ComboBox>(Find(dialog, "AccountTypeComboBox"));
            SelectByText(account, "Cloudflare R2");
            SelectByText(account, "S3 兼容存储");
            Application.DoEvents();
            Assert.Equal(UploadSigningMode.UnsignedPayload, ReadDraft(dialog).UploadSigningMode);
            SelectByText(signing, "标准（AWS SDK 流式签名）");
            Assert.Equal(UploadSigningMode.Streaming, ReadDraft(dialog).UploadSigningMode);
        });
    }

    private static ConnectionProfile ReadDraft(ConnectionDialog dialog) =>
        (ConnectionProfile)typeof(ConnectionDialog).GetMethod("ReadProfile",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog, null)!;

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

    [Fact]
    public void ConnectionTestFormatterReportsRoleDiagnosticsWithoutExternalIdValue()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Audit role",
            CredentialSource = CredentialSourceKind.AwsAssumeRole,
            AwsSourceProfileName = "bootstrap",
            AwsRoleArn = "arn:aws:iam::123456789012:role/Audit",
            AwsRoleSessionName = "s3explorer-audit",
            AwsExternalId = "external-secret"
        };
        var result = new ConnectionTestResult(
            true, TimeSpan.FromMilliseconds(80), 3, "连接成功。",
            CredentialSource: profile.CredentialSourceDisplayName,
            AwsIdentity: new AwsIdentitySummary(
                CredentialSourceKind.AwsAssumeRole,
                "shared profile bootstrap / SourceIdentity operator-42",
                profile.AwsRoleArn,
                ExternalIdConfigured: true,
                new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)));

        var text = ConnectionTestResultFormatter.Format(result, profile);

        Assert.Contains("源身份", text, StringComparison.Ordinal);
        Assert.Contains(profile.AwsRoleArn, text, StringComparison.Ordinal);
        Assert.Contains("External ID：已配置", text, StringComparison.Ordinal);
        Assert.Contains("会话到期", text, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.AwsExternalId, text, StringComparison.Ordinal);
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

    private static string AllControlText(Control root) => string.Join("\n",
        root.Controls.Cast<Control>().SelectMany(control =>
            new[] { control.Text }.Concat(control.Controls.Cast<Control>().Select(AllControlText))));

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
