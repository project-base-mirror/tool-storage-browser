using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private readonly PermissionCheckHistoryStore _permissionCheckHistoryStore;

    private ToolStripMenuItem BuildCredentialMenu()
    {
        var menu = new ToolStripMenuItem("凭据(&R)") { Name = "CredentialMenu" };
        menu.DropDownItems.Add(Command(
            "credential-center",
            "凭据中心...",
            async (_, _) => await ShowCredentialCenterAsync()));
        menu.DropDownItems.Add(Command(
            "permission-check-history",
            "权限检查...",
            async (_, _) => await ShowPermissionChecksAsync()));
        return menu;
    }

    private async Task ShowCredentialCenterAsync()
    {
        using var dialog = new CredentialCenterDialog(
            _profiles,
            _credentials,
            _cdnConfiguration,
            _storage,
            _cdnDeliveryService,
            PersistNonDestructivePermissionReportAsync,
            _profileGroups);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var updated = await _configurationStore.UpdateAsync(current => current with
            {
                CredentialVault = dialog.Credentials.ToArray()
            });
            _profiles = updated.Storage.Profiles;
            _profileGroups = updated.Storage.Groups;
            _cdnConfiguration = updated.Cdn;
            _credentials = updated.CredentialVault;
            if (_currentProfile is not null)
                _currentProfile = _profiles.FirstOrDefault(value => value.Id == _currentProfile.Id);
            PopulateProfiles();
            UpdateCommandStates();
            MessageBox.Show(this, "凭据更改已保存。", "凭据中心", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to save credentials", exception);
            ErrorDialog.ShowException(this, "无法保存凭据", "凭据中心", exception);
        }
    }

    private Task PersistNonDestructivePermissionReportAsync(
        CredentialProfile credential,
        PermissionCheckReport report,
        CancellationToken cancellationToken) =>
        _permissionCheckHistoryStore.UpsertAsync(
            credential,
            report,
            mutationProbe: false,
            cancellationToken);

    private async Task ShowPermissionChecksAsync()
    {
        using var dialog = new PermissionCheckHistoryDialog(
            _permissionCheckHistoryStore,
            RunStoragePermissionProbeAsync);
        dialog.ShowDialog(this);
        await Task.CompletedTask;
    }

    private async Task RunStoragePermissionProbeAsync(IWin32Window owner, CancellationToken cancellationToken)
    {
        var profiles = new ExplorerConfiguration(
                new ConnectionProfileConfiguration(_profiles, _profileGroups),
                _cdnConfiguration,
                _credentials)
            .ResolveCredentialReferences()
            .Storage.Profiles
            .Where(profile => profile.CredentialId is Guid)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (profiles.Length == 0)
        {
            MessageBox.Show(
                owner,
                "没有关联到凭据中心的对象存储连接。请先在连接设置中选择统一凭据。",
                "无法执行写入探针",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var probeDialog = new StoragePermissionProbeDialog(profiles);
        if (probeDialog.ShowDialog(owner) != DialogResult.OK || probeDialog.Request is not { } request)
            return;

        var credential = request.Profile.CredentialId is Guid credentialId
            ? _credentials.FirstOrDefault(value => value.Id == credentialId)
            : null;
        if (credential is null)
            throw new InvalidOperationException("所选对象存储连接没有可用的统一凭据。");

        try
        {
            UseWaitCursor = true;
            var result = await new S3PermissionChecker(_storage)
                .CheckAsync(request, cancellationToken);
            var report = new PermissionCheckReport([result]);
            await _permissionCheckHistoryStore.UpsertAsync(
                credential,
                report,
                mutationProbe: true,
                cancellationToken);
            using var resultDialog = new CredentialPermissionResultDialog(credential, report, mutationProbe: true);
            resultDialog.ShowDialog(owner);
        }
        catch (OperationCanceledException)
        {
            // Closing the parent dialog is the normal cancellation path.
        }
        catch (Exception exception)
        {
            _logger.Error("Storage permission mutation probe failed", exception);
            ErrorDialog.ShowException(
                owner,
                "写入权限探针失败",
                "探针会尝试上传临时对象并立即清理；请检查结果中是否提示远端残留对象。",
                exception);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
