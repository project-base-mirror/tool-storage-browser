using System.Reflection;
using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CredentialCenterChangeTests
{
    [Fact]
    public void DecliningReferenceClearKeepsOriginalOutputAndAllowsLaterConfirmation()
    {
        RunSta(() =>
        {
            var credential = new CredentialProfile
            {
                Name = "R2", Provider = CredentialProviderKind.Cloudflare,
                Kind = CredentialKind.AccessKeyPair, AccessKeyId = "synthetic-access", Secret = "synthetic-secret"
            };
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2) with
            {
                Name = "R2 connection", Endpoint = "https://example.r2.cloudflarestorage.com", CredentialId = credential.Id
            };
            using var dialog = new CredentialCenterDialog([profile], [credential], CdnConfiguration.Empty);
            var drafts = (List<CredentialProfile>)typeof(CredentialCenterDialog)
                .GetField("_credentials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            drafts[0] = credential with { Kind = CredentialKind.BearerToken, AccessKeyId = string.Empty };

            Assert.False(dialog.TryPrepareChanges(impacts =>
            {
                Assert.Equal(profile.Id, Assert.Single(impacts).ProfileId);
                return false;
            }));
            Assert.Null(dialog.ChangePlan);
            Assert.Equal(credential, Assert.Single(dialog.Credentials));
            Assert.NotEqual(DialogResult.OK, dialog.DialogResult);
            Assert.Equal(credential.Id, profile.CredentialId);

            Assert.True(dialog.TryPrepareChanges(_ => true));
            Assert.NotNull(dialog.ChangePlan);
            var updated = dialog.ChangePlan.Apply(new(new([profile], []), CdnConfiguration.Empty, [credential]), true);
            Assert.Null(Assert.Single(updated.Storage.Profiles).CredentialId);
            Assert.Equal(CredentialKind.BearerToken, Assert.Single(updated.CredentialVault).Kind);
        });
    }

    [Fact]
    public void CompatibleEditSkipsClearConfirmation()
    {
        RunSta(() =>
        {
            var credential = new CredentialProfile
            {
                Name = "key", Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.AccessKeyPair, AccessKeyId = "synthetic-access", Secret = "synthetic-secret"
            };
            using var dialog = new CredentialCenterDialog([], [credential], CdnConfiguration.Empty);
            var drafts = (List<CredentialProfile>)typeof(CredentialCenterDialog)
                .GetField("_credentials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            drafts[0] = credential with { Name = "renamed" };
            Assert.True(dialog.TryPrepareChanges(_ => throw new InvalidOperationException("Unexpected confirmation")));
            Assert.Equal("renamed", Assert.Single(dialog.Credentials).Name);
            Assert.Empty(dialog.ChangePlan!.IncompatibleReferences);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { exception = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test did not finish.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
