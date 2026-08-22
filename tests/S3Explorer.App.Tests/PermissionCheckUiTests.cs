using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class PermissionCheckUiTests
{
    [Fact]
    public async Task HistoryStoreKeepsLatestResultPerCredentialAndScopeAndRedactsMessages()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s3explorer-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PermissionCheckHistoryStore(path);
            var credential = new CredentialProfile
            {
                Name = "release",
                Provider = CredentialProviderKind.AlibabaCloud,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "AKIDEXAMPLE",
                Secret = "secret-value"
            };
            var report = new PermissionCheckReport([
                new PermissionCheckResult(credential.Id, [
                    new PermissionCheck("storage", "PutObject", PermissionCheckState.Denied,
                        "secret=secret-value")
                ])
                {
                    TargetScope = "bucket/prefix/",
                    CheckedAtUtc = DateTimeOffset.Parse("2026-08-22T00:00:00Z")
                }
            ]);

            await store.UpsertAsync(credential, report, mutationProbe: false, TestContext.Current.CancellationToken);
            var updated = report with
            {
                Results = [report.Results[0] with
                {
                    CheckedAtUtc = DateTimeOffset.Parse("2026-08-22T01:00:00Z"),
                    Checks = [new PermissionCheck("storage", "PutObject", PermissionCheckState.Passed, "new result")]
                }]
            };
            await store.UpsertAsync(credential, updated, mutationProbe: true, TestContext.Current.CancellationToken);

            var entries = await store.LoadAsync(TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.True(entry.MutationProbe);
            Assert.Equal(PermissionCheckState.Passed, Assert.Single(entry.Result.Checks).State);
            Assert.DoesNotContain("secret-value", Assert.Single(entry.Result.Checks).Message);
            var persisted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(credential.Secret, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(credential.AccessKeyId, persisted, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public async Task HistoryStoreRejectsUnknownSchemaAndPreservesTheInvalidFile()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s3explorer-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "permission-check-history.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{\"schema\":99,\"entries\":[]}",
                TestContext.Current.CancellationToken);
            var store = new PermissionCheckHistoryStore(path);

            var entries = await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Empty(entries);
            Assert.True(store.LastRecovery?.UsedDefault);
            Assert.NotNull(store.LastRecovery?.CorruptPath);
            Assert.True(File.Exists(store.LastRecovery!.CorruptPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HistoryDialogHasReadableListAndActions()
    {
        RunSta(() =>
        {
            using var dialog = new PermissionCheckHistoryDialog(
                new PermissionCheckHistoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"history-{Guid.NewGuid():N}.json")));
            Assert.NotNull(dialog.Controls.Find("PermissionCheckHistoryGrid", true).SingleOrDefault());
            Assert.NotNull(dialog.Controls.Find("ViewPermissionCheckDetailsButton", true).SingleOrDefault());
            Assert.NotNull(dialog.Controls.Find("DeletePermissionCheckHistoryButton", true).SingleOrDefault());
            Assert.NotNull(dialog.Controls.Find("ClearPermissionCheckHistoryButton", true).SingleOrDefault());
            Assert.Empty(dialog.Controls.Find("RunStoragePermissionProbeButton", true));
        });
    }

    [Fact]
    public void PermissionMatrixShowsCredentialRowsPermissionColumnsAndActions()
    {
        RunSta(() =>
        {
            var credential = new CredentialProfile
            {
                Name = "release",
                Provider = CredentialProviderKind.AlibabaCloud,
                Kind = CredentialKind.AccessKeyPair,
                AccessKeyId = "AKID",
                Secret = "secret"
            };
            using var dialog = new CredentialPermissionMatrixDialog(
                [credential],
                new PermissionCheckHistoryStore(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"matrix-{Guid.NewGuid():N}.json")),
                static (_, _, _) => Task.CompletedTask,
                static (_, _, _) => Task.CompletedTask);
            dialog.RefreshAsync().GetAwaiter().GetResult();

            var grid = Assert.IsType<DataGridView>(dialog.Controls.Find("CredentialPermissionMatrixGrid", true).Single());
            Assert.Equal(
                ["Credential", "ListBucket", "HeadObject", "GetObject", "PutObject", "DeleteObject", "PutObjectAcl", "CdnControlQuery", "RefreshOrPush", "LastChecked"],
                grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Name));
            var row = Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("release", row.Cells["Credential"].Value);
            Assert.All(
                new[] { "ListBucket", "HeadObject", "GetObject", "PutObject", "DeleteObject", "PutObjectAcl", "CdnControlQuery", "RefreshOrPush" },
                name => Assert.Equal("—", row.Cells[name].Value));
            Assert.Equal("从未检查", row.Cells["LastChecked"].Value);
            Assert.NotNull(dialog.Controls.Find("CheckSelectedCredentialPermissionsButton", true).SingleOrDefault());
            Assert.NotNull(dialog.Controls.Find("ProbeSelectedCredentialPermissionsButton", true).SingleOrDefault());
            Assert.NotNull(dialog.Controls.Find("ViewCredentialPermissionHistoryButton", true).SingleOrDefault());
        });
    }

    [Fact]
    public void ProbeDialogRequiresExplicitConfirmationAndBuildsMutationRequest()
    {
        RunSta(() =>
        {
            var profile = new ConnectionProfile
            {
                Name = "release",
                Endpoint = "https://s3.example.test"
            };
            using var dialog = new StoragePermissionProbeDialog([profile]);
            var ok = Assert.IsType<Button>(dialog.Controls.Find("ConfirmStoragePermissionProbeButton", true).Single());
            Assert.False(ok.Enabled);
            Assert.Null(dialog.Request);
            Assert.Equal(DialogResult.None, dialog.DialogResult);

            Assert.IsType<BucketPicker>(dialog.Controls.Find("StoragePermissionProbeBucket", true).Single()).BucketText = "release";
            Assert.IsType<TextBox>(dialog.Controls.Find("StoragePermissionProbePrefix", true).Single()).Text = "isolated/probe";
            Assert.IsType<TextBox>(dialog.Controls.Find("StoragePermissionProbeConfirmation", true).Single()).Text = "PROBE";
            Assert.IsType<CheckBox>(dialog.Controls.Find("StoragePermissionProbeConfirm", true).Single()).Checked = true;
            Assert.True(ok.Enabled);
            var target = Assert.IsType<Label>(dialog.Controls.Find("StoragePermissionProbeTarget", true).Single());
            Assert.Contains("s3://release/release/isolated/probe/", target.Text, StringComparison.Ordinal);
            Assert.Contains("https://s3.example.test", target.Text, StringComparison.Ordinal);
            dialog.Show();
            ok.PerformClick();

            var request = Assert.IsType<StoragePermissionCheckRequest>(dialog.Request);
            Assert.True(request.AllowMutation);
            Assert.True(request.Operation.HasFlag(StoragePermissionOperation.Publish));
            Assert.True(request.Operation.HasFlag(StoragePermissionOperation.Mirror));
            Assert.Equal("release", request.Bucket);
            Assert.Equal("isolated/probe", request.Prefix);

            using var reopened = new StoragePermissionProbeDialog([profile]);
            Assert.False(Assert.IsType<Button>(reopened.Controls.Find("ConfirmStoragePermissionProbeButton", true).Single()).Enabled);
            Assert.False(Assert.IsType<CheckBox>(reopened.Controls.Find("StoragePermissionProbeConfirm", true).Single()).Checked);
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
