using System.Reflection;
using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ConnectionDialogRoundTripTests
{
    [Fact]
    public void ProviderRoundTripRestoresSelectedCredentialAndNeverChoosesFirstIdentity()
    {
        RunSta(() =>
        {
            var first = AccessKey("A AWS key", CredentialProviderKind.AmazonWebServices);
            var selected = AccessKey("B AWS key", CredentialProviderKind.AmazonWebServices);
            var r2 = AccessKey("R2 key", CredentialProviderKind.Cloudflare);
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                CredentialId = selected.Id,
                AccessKey = selected.AccessKeyId,
                SecretKey = selected.Secret
            };
            using var dialog = new ConnectionDialog(null!, profile, [first, selected, r2]);
            var account = Find<ComboBox>(dialog, "AccountTypeComboBox");
            SelectByText(account, "Cloudflare R2");
            Assert.Null(ReadDraft(dialog).CredentialId);
            Assert.Contains("未选择", Find<ComboBox>(dialog, "StorageCredentialComboBox").Text, StringComparison.Ordinal);
            Find<ComboBox>(dialog, "StorageCredentialComboBox").SelectedIndex = 1;
            SelectByText(account, "Amazon S3");
            Assert.Equal(selected.Id, ReadDraft(dialog).CredentialId);
            Assert.Equal(selected.AccessKeyId, ReadDraft(dialog).AccessKey);
            SelectByText(account, "Cloudflare R2");
            Assert.Equal(r2.Id, ReadDraft(dialog).CredentialId);
            SelectByText(account, "Amazon S3");
            Assert.Equal(selected.Id, ReadDraft(dialog).CredentialId);
            // Cancelling after switches never mutates the original profile.
            Assert.Equal(profile, dialog.Profile);
        });
    }

    [Theory]
    [InlineData(CredentialSourceKind.AwsSharedProfile)]
    [InlineData(CredentialSourceKind.AwsSso)]
    [InlineData(CredentialSourceKind.AwsAssumeRole)]
    [InlineData(CredentialSourceKind.AwsWebIdentity)]
    [InlineData(CredentialSourceKind.AwsEnvironmentVariables)]
    [InlineData(CredentialSourceKind.AwsContainerRole)]
    [InlineData(CredentialSourceKind.AwsInstanceRole)]
    [InlineData(CredentialSourceKind.AwsDefaultChain)]
    public void AwsAuthenticationDraftSurvivesProviderRoundTrip(CredentialSourceKind source)
    {
        RunSta(() =>
        {
            var externalId = new CredentialProfile
            {
                Name = "Role External ID",
                Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.SecretValue,
                Secret = "synthetic-external-id"
            };
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "AWS role",
                CredentialSource = source,
                AwsProfileName = "readonly",
                AwsSourceProfileName = "bootstrap",
                AwsRoleArn = "arn:aws:iam::123456789012:role/Audit",
                AwsRoleSessionName = "s3explorer-audit",
                AwsRoleSourceIdentity = "operator-42",
                AwsExternalIdCredentialId = externalId.Id,
                AwsSessionDurationSeconds = 1800,
                AwsWebIdentityTokenFile = Path.Combine(Path.GetTempPath(), "synthetic-token.txt")
            };
            using var dialog = new ConnectionDialog(null!, profile, [externalId]);
            var before = ReadDraft(dialog);
            var account = Find<ComboBox>(dialog, "AccountTypeComboBox");
            SelectByText(account, "S3 兼容存储");
            Assert.Equal(CredentialSourceKind.StoredKeys, ReadDraft(dialog).CredentialSource);
            SelectByText(account, "Amazon S3");
            var after = ReadDraft(dialog);
            Assert.Equal(before.CredentialSource, after.CredentialSource);
            Assert.Equal(before.AwsProfileName, after.AwsProfileName);
            Assert.Equal(before.AwsSourceProfileName, after.AwsSourceProfileName);
            Assert.Equal(before.AwsRoleArn, after.AwsRoleArn);
            Assert.Equal(before.AwsRoleSessionName, after.AwsRoleSessionName);
            Assert.Equal(before.AwsRoleSourceIdentity, after.AwsRoleSourceIdentity);
            Assert.Equal(before.AwsExternalIdCredentialId, after.AwsExternalIdCredentialId);
            Assert.Equal(before.AwsExternalId, after.AwsExternalId);
            Assert.Equal(before.AwsSessionDurationSeconds, after.AwsSessionDurationSeconds);
            Assert.Equal(before.AwsWebIdentityTokenFile, after.AwsWebIdentityTokenFile);
        });
    }

    [Fact]
    public void MissingCredentialReferenceDoesNotSelectAnotherSavedCredential()
    {
        RunSta(() =>
        {
            var key = AccessKey("Available AWS key", CredentialProviderKind.AmazonWebServices);
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                CredentialId = Guid.NewGuid()
            };
            using var dialog = new ConnectionDialog(null!, profile, [key]);
            Assert.Null(ReadDraft(dialog).CredentialId);
            Assert.Empty(ReadDraft(dialog).AccessKey);
        });
    }

    [Theory]
    [InlineData("http://minio.example:9000")]
    [InlineData("http://minio.example:443")]
    [InlineData("http://minio.example:80")]
    [InlineData("http://[::1]:9000")]
    [InlineData("http://minio.example/base/")]
    public void HttpsRoundTripPreservesExplicitPortsAndPath(string endpoint)
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.Custom) with
            {
                Endpoint = endpoint,
                UseHttps = false
            };
            using var dialog = new ConnectionDialog(null!, profile);
            var https = Find<CheckBox>(dialog, "UseHttpsCheckBox");
            https.Checked = true;
            Assert.Equal("https" + endpoint[4..], ReadDraft(dialog).Endpoint);
            https.Checked = false;
            Assert.Equal(endpoint, ReadDraft(dialog).Endpoint);
        });
    }

    [Theory]
    [InlineData(S3ServiceType.AmazonS3, "r1", "r2")]
    [InlineData(S3ServiceType.MinIO, "minio-region", "")]
    [InlineData(S3ServiceType.CloudflareR2, "custom-region", "custom-signature")]
    public void NameOnlyEditPreservesAdvancedRegionAndUnknownStorageClass(
        S3ServiceType service, string region, string signatureRegion)
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(service) with
            {
                Name = "Original",
                Endpoint = "https://storage.example.test",
                Region = region,
                SignatureRegion = signatureRegion,
                DefaultStorageClass = "PROVIDER_SPECIFIC_CLASS"
            };
            using var dialog = new ConnectionDialog(null!, profile);
            Find<TextBox>(dialog, "ConnectionNameTextBox").Text = "Renamed";
            var after = ReadDraft(dialog);
            Assert.Equal("Renamed", after.Name);
            Assert.Equal(region, after.Region);
            Assert.Equal(signatureRegion, after.SignatureRegion);
            Assert.Equal(profile.EffectiveSignatureRegion, after.EffectiveSignatureRegion);
            Assert.Equal(profile.DefaultStorageClass, after.DefaultStorageClass);
            after.ValidateConfiguration();
            Assert.Throws<ArgumentException>(() => after.Validate());
        });
    }

    [Fact]
    public void SignatureRegionOverrideCanBeEditedIndependently()
    {
        RunSta(() =>
        {
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.Custom) with
            {
                Region = "region-one",
                SignatureRegion = "signature-one"
            };
            using var dialog = new ConnectionDialog(null!, profile);
            Find<TextBox>(dialog, "SignatureRegionTextBox").Text = "signature-two";
            Assert.Equal("region-one", ReadDraft(dialog).Region);
            Assert.Equal("signature-two", ReadDraft(dialog).EffectiveSignatureRegion);
            Find<TextBox>(dialog, "SignatureRegionTextBox").Clear();
            Assert.Equal("region-one", ReadDraft(dialog).EffectiveSignatureRegion);
        });
    }

    private static CredentialProfile AccessKey(string name, CredentialProviderKind provider) => new()
    {
        Name = name, Provider = provider, Kind = CredentialKind.AccessKeyPair,
        AccessKeyId = "synthetic-" + name, Secret = "synthetic-secret"
    };

    private static ConnectionProfile ReadDraft(ConnectionDialog dialog) =>
        (ConnectionProfile)typeof(ConnectionDialog).GetMethod("ReadProfile",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;

    private static T Find<T>(Control root, string name) where T : Control =>
        Assert.IsType<T>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void SelectByText(ComboBox comboBox, string text)
    {
        var item = Assert.Single(comboBox.Items.Cast<object>(), value => value.ToString() == text);
        comboBox.SelectedItem = item;
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
