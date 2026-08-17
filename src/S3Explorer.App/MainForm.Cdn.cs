using System.Diagnostics;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private readonly ICdnConfigurationStore _cdnConfigurationStore;
    private readonly ICredentialStore _credentialStore;
    private readonly ICdnDeliveryService _cdnDeliveryService;
    private readonly PersistentCdnJobQueue _cdnJobQueue;
    private readonly CdnUploadAutomationCoordinator _cdnUploadAutomation;
    private readonly ICdnCertificateInspector _cdnCertificateInspector;
    private CdnConfiguration _cdnConfiguration = CdnConfiguration.Empty;
    private IReadOnlyList<CredentialProfile> _credentials = [];
    private ToolStripMenuItem? _cdnObjectContextMenu;
    private ToolStripMenuItem? _cdnObjectContextCopy;
    private ToolStripMenuItem? _cdnObjectContextOpen;
    private ToolStripMenuItem? _cdnObjectContextProbe;
    private ToolStripMenuItem? _cdnObjectContextWarmup;
    private ToolStripMenuItem? _cdnObjectContextPurge;
    private readonly List<ToolStripMenuItem> _cdnSpecifiedMenus = [];

    private ToolStripMenuItem BuildCdnMenu()
    {
        var menu = new ToolStripMenuItem("CDN / 分发(&C)") { Name = "CdnMenu" };
        menu.DropDownItems.Add(Command(
            "cdn-configure",
            "CDN 配置中心...",
            async (_, _) => await ShowCdnConfigurationAsync()));
        menu.DropDownItems.Add(Command(
            "cdn-jobs",
            "CDN 任务中心...",
            (_, _) => ShowCdnJobs()));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Command("cdn-copy-url", "复制 CDN URL", (_, _) => CopySelectedCdnUrl()));
        menu.DropDownItems.Add(CreateSpecifiedCdnMenu(
            "复制指定 CDN URL", "CdnCopySpecifiedMenu", target =>
            {
                CopyCdnUrl(target);
                return Task.CompletedTask;
            }));
        menu.DropDownItems.Add(Command("cdn-open-url", "使用 CDN 打开", (_, _) => OpenSelectedCdnUrl()));
        menu.DropDownItems.Add(CreateSpecifiedCdnMenu(
            "使用指定 CDN 打开", "CdnOpenSpecifiedMenu", target =>
            {
                OpenCdnUrl(target);
                return Task.CompletedTask;
            }));
        menu.DropDownItems.Add(Command(
            "cdn-download-test",
            "CDN 下载测试...",
            (_, _) => ShowSelectedCdnDownloadTest()));
        menu.DropDownItems.Add(CreateSpecifiedCdnMenu(
            "使用指定 CDN 下载测试", "CdnProbeSpecifiedMenu", target =>
            {
                ShowCdnDownloadTest(target);
                return Task.CompletedTask;
            }));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Command(
            "cdn-warmup",
            "HTTP 预热",
            async (_, _) => await EnqueueSelectedCdnOperationAsync(CdnJobAction.Warmup)));
        menu.DropDownItems.Add(CreateSpecifiedCdnMenu(
            "使用指定 CDN 预热", "CdnWarmupSpecifiedMenu",
            target => EnqueueCdnOperationAsync(CdnJobAction.Warmup, target)));
        menu.DropDownItems.Add(Command(
            "cdn-purge",
            "刷新 CDN 缓存",
            async (_, _) => await EnqueueSelectedCdnOperationAsync(CdnJobAction.PurgeUrl)));
        menu.DropDownItems.Add(CreateSpecifiedCdnMenu(
            "使用指定 CDN 刷新缓存", "CdnPurgeSpecifiedMenu",
            target => EnqueueCdnOperationAsync(CdnJobAction.PurgeUrl, target)));
        menu.DropDownOpening += (_, _) => PopulateSpecifiedCdnMenus();
        return menu;
    }

    private ToolStripMenuItem BuildObjectCdnContextMenu()
    {
        _cdnObjectContextMenu = new ToolStripMenuItem("CDN / 分发")
        {
            Name = "CdnObjectContextMenu"
        };
        _cdnObjectContextCopy = new ToolStripMenuItem("复制 CDN URL", null, (_, _) => CopySelectedCdnUrl())
        {
            Name = "CdnObjectContextCopy"
        };
        var copySpecified = CreateSpecifiedCdnMenu(
            "复制指定 CDN URL", "CdnObjectContextCopySpecified", target =>
            {
                CopyCdnUrl(target);
                return Task.CompletedTask;
            });
        _cdnObjectContextOpen = new ToolStripMenuItem("使用 CDN 打开", null, (_, _) => OpenSelectedCdnUrl())
        {
            Name = "CdnObjectContextOpen"
        };
        var openSpecified = CreateSpecifiedCdnMenu(
            "使用指定 CDN 打开", "CdnObjectContextOpenSpecified", target =>
            {
                OpenCdnUrl(target);
                return Task.CompletedTask;
            });
        _cdnObjectContextProbe = new ToolStripMenuItem("下载测试...", null, (_, _) => ShowSelectedCdnDownloadTest())
        {
            Name = "CdnObjectContextProbe"
        };
        var probeSpecified = CreateSpecifiedCdnMenu(
            "使用指定 CDN 下载测试", "CdnObjectContextProbeSpecified", target =>
            {
                ShowCdnDownloadTest(target);
                return Task.CompletedTask;
            });
        _cdnObjectContextWarmup = new ToolStripMenuItem("HTTP 预热", null, async (_, _) =>
            await EnqueueSelectedCdnOperationAsync(CdnJobAction.Warmup))
        {
            Name = "CdnObjectContextWarmup"
        };
        var warmupSpecified = CreateSpecifiedCdnMenu(
            "使用指定 CDN 预热", "CdnObjectContextWarmupSpecified",
            target => EnqueueCdnOperationAsync(CdnJobAction.Warmup, target));
        _cdnObjectContextPurge = new ToolStripMenuItem("刷新缓存", null, async (_, _) =>
            await EnqueueSelectedCdnOperationAsync(CdnJobAction.PurgeUrl))
        {
            Name = "CdnObjectContextPurge"
        };
        var purgeSpecified = CreateSpecifiedCdnMenu(
            "使用指定 CDN 刷新缓存", "CdnObjectContextPurgeSpecified",
            target => EnqueueCdnOperationAsync(CdnJobAction.PurgeUrl, target));
        var jobs = new ToolStripMenuItem("查看 CDN 任务...", null, (_, _) => ShowCdnJobs())
        {
            Name = "CdnObjectContextJobs"
        };
        var configure = new ToolStripMenuItem("管理 CDN 配置...", null, async (_, _) =>
            await ShowCdnConfigurationAsync())
        {
            Name = "CdnObjectContextConfigure"
        };

        _cdnObjectContextMenu.DropDownItems.AddRange([
            _cdnObjectContextCopy,
            copySpecified,
            _cdnObjectContextOpen,
            openSpecified,
            _cdnObjectContextProbe,
            probeSpecified,
            new ToolStripSeparator(),
            _cdnObjectContextWarmup,
            warmupSpecified,
            _cdnObjectContextPurge,
            purgeSpecified,
            new ToolStripSeparator(),
            jobs,
            configure
        ]);
        _cdnObjectContextMenu.DropDownOpening += (_, _) => PopulateSpecifiedCdnMenus();
        return _cdnObjectContextMenu;
    }

    private ToolStripMenuItem CreateSpecifiedCdnMenu(
        string text,
        string name,
        Func<CdnResolvedTarget, Task> action)
    {
        var menu = new ToolStripMenuItem(text) { Name = name, Tag = action };
        _cdnSpecifiedMenus.Add(menu);
        return menu;
    }

    private void PopulateSpecifiedCdnMenus()
    {
        var targets = ResolveSelectedCdnTargets();
        foreach (var menu in _cdnSpecifiedMenus)
        {
            menu.DropDownItems.Clear();
            menu.Enabled = targets.Count > 0;
            if (menu.Tag is not Func<CdnResolvedTarget, Task> action)
                continue;
            foreach (var choice in CdnSpecifiedTargetMenu.Build(targets))
            {
                var target = choice.Target;
                var item = new ToolStripMenuItem(choice.Label)
                {
                    ToolTipText = choice.ToolTip,
                    Name = $"{menu.Name}_{target.Profile.Id:N}"
                };
                if (menu.Name?.Contains("Purge", StringComparison.Ordinal) == true &&
                    !target.Profile.Capabilities.HasFlag(CdnCapabilities.Purge))
                    item.Enabled = false;
                item.Click += async (_, _) => await action(target);
                menu.DropDownItems.Add(item);
            }
        }
    }

    private ToolStripMenuItem BuildBucketCdnContextMenu()
    {
        var item = new ToolStripMenuItem(
            "CDN 关联...",
            null,
            async (_, _) =>
            {
                if (_tree.SelectedNode?.Tag is BucketNodeTag tag)
                    await ShowCdnConfigurationAsync(tag.Profile, tag.Bucket);
                else
                    await ShowCdnConfigurationAsync(_currentProfile, _currentBucket);
            });
        item.Name = "CdnBucketContextConfigure";
        return item;
    }

    private async Task<IReadOnlyList<string>> LoadCdnStateAsync()
    {
        var warnings = new List<string>();
        var configuration = CdnConfiguration.Empty;
        IReadOnlyList<CredentialProfile> credentials = [];
        try
        {
            configuration = await _cdnConfigurationStore.LoadAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to load CDN configuration", exception);
            warnings.Add($"CDN 配置：{exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            credentials = await _credentialStore.LoadAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to load CDN credentials", exception);
            warnings.Add($"CDN 独立凭据：{exception.GetType().Name}: {exception.Message}");
        }

        _cdnConfiguration = configuration;
        _credentials = credentials;
        try
        {
            CdnConfigurationValidator.EnsureValid(configuration, credentials);
        }
        catch (Exception exception)
        {
            _logger.Error("CDN configuration and credentials are not mutually consistent", exception);
            warnings.Add($"CDN 配置关联：{exception.Message}");
        }
        AddRecoveryWarning(warnings, "CDN 配置", _cdnConfigurationStore as IRecoveryAwareStore);
        return warnings;
    }

    private async Task ShowCdnConfigurationAsync(
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null,
        bool openCredentialCenter = false)
    {
        using var dialog = new CdnConfigurationDialog(
            _profiles,
            _cdnConfiguration,
            _credentials,
            initialProfile ?? _currentProfile,
            initialBucket ?? _currentBucket,
            _cdnCertificateInspector,
            _storage,
            _cdnDeliveryService,
            PersistCdnCertificateResultAsync,
            openCredentialCenter);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            CdnConfigurationValidator.EnsureValid(dialog.Configuration, dialog.Credentials);
            await _configurationStore.SaveAsync(new ExplorerConfiguration(
                new ConnectionProfileConfiguration(_profiles, _profileGroups),
                dialog.Configuration,
                dialog.Credentials));
            _credentials = dialog.Credentials;
            _cdnConfiguration = dialog.Configuration;
            UpdateCommandStates();
            MessageBox.Show(
                this,
                "CDN 配置、统一凭据和 Bucket/前缀关联已保存。",
                "CDN 配置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to save CDN configuration", exception);
            ErrorDialog.ShowException(this, "无法保存 CDN 配置", "CDN 配置和统一凭据", exception);
        }
    }

    private async Task PersistCdnCertificateResultAsync(
        Guid profileId,
        CdnCertificateCheckResult result,
        CancellationToken cancellationToken)
    {
        var profile = _cdnConfiguration.Profiles.FirstOrDefault(value => value.Id == profileId);
        var updated = CdnCertificatePersistence.Apply(_cdnConfiguration, profileId, result);
        await _cdnConfigurationStore.SaveAsync(updated, cancellationToken);
        _cdnConfiguration = updated;
        _logger.Info($"CDN certificate result saved immediately. Profile={profile!.Name}; CheckedAt={result.CheckedAt:O}");
    }

    private void ShowCdnJobs()
    {
        using var dialog = new CdnJobsDialog(_cdnJobQueue, _cdnConfiguration.Profiles);
        dialog.ShowDialog(this);
    }

    private async Task ProcessCompletedCdnUploadsAsync(IEnumerable<TransferTaskRecord> tasks)
    {
        try
        {
            var jobs = await _cdnUploadAutomation.ProcessCompletedUploadsAsync(tasks, _cdnConfiguration);
            if (jobs.Count > 0)
            {
                _logger.Info($"CDN upload automation enqueued jobs={jobs.Count}");
                _requestStatus.Text = $"已生成 {jobs.Count} 个 CDN 自动化任务";
            }
        }
        catch (Exception exception)
        {
            _logger.Error("CDN upload automation failed", exception);
        }
    }

    private bool TryResolveSelectedCdnTarget(
        out CdnResolvedTarget? target,
        out CredentialProfile? credential,
        bool showMessage)
    {
        target = null;
        credential = null;
        var targets = ResolveSelectedCdnTargets();
        if (targets.Count == 0)
        {
            if (showMessage)
            {
                MessageBox.Show(
                    this,
                    "请选择当前 Bucket 中已配置 CDN 关联的一个文件对象。",
                    "没有可用的 CDN 关联",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return false;
        }

        target = targets[0];
        return TryResolveCdnCredential(target, out credential, showMessage);
    }

    private IReadOnlyList<CdnResolvedTarget> ResolveSelectedCdnTargets()
    {
        var selected = SelectedEntries();
        if (_currentProfile is null || _currentBucket is null || selected.Count != 1 || selected[0].IsDirectory)
            return [];
        return CdnUrlMapper.ResolveAll(
            _cdnConfiguration,
            _currentProfile.Id,
            _currentBucket,
            selected[0].Key);
    }

    private bool TryResolveCdnCredential(
        CdnResolvedTarget target,
        out CredentialProfile? credential,
        bool showMessage)
    {
        credential = null;
        if (target.Profile.CredentialId is not Guid credentialId)
            return true;
        credential = _credentials.FirstOrDefault(value => value.Id == credentialId);
        if (credential is not null)
            return true;
        if (showMessage)
        {
            MessageBox.Show(
                this,
                $"CDN 配置“{target.Profile.Name}”引用的独立凭据不存在。请修复配置后重试。",
                "CDN 凭据缺失",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        return false;
    }

    private void CopySelectedCdnUrl()
    {
        if (!TryResolveSelectedCdnTarget(out var target, out _, showMessage: true) || target is null)
            return;

        CopyCdnUrl(target);
    }

    private void CopyCdnUrl(CdnResolvedTarget target)
    {
        Clipboard.SetText(target.Url.AbsoluteUri);
        _requestStatus.Text = $"已复制 CDN URL：{target.Profile.Name}";
    }

    private void OpenSelectedCdnUrl()
    {
        if (!TryResolveSelectedCdnTarget(out var target, out _, showMessage: true) || target is null)
            return;

        OpenCdnUrl(target);
    }

    private void OpenCdnUrl(CdnResolvedTarget target) => OpenExternalUrl(target.Url.AbsoluteUri);

    private void ShowSelectedCdnDownloadTest()
    {
        if (!TryResolveSelectedCdnTarget(out var target, out var credential, showMessage: true) || target is null)
            return;

        ShowCdnDownloadTest(target, credential);
    }

    private void ShowCdnDownloadTest(CdnResolvedTarget target)
    {
        if (!TryResolveCdnCredential(target, out var credential, showMessage: true))
            return;
        ShowCdnDownloadTest(target, credential);
    }

    private void ShowCdnDownloadTest(CdnResolvedTarget target, CredentialProfile? credential)
    {
        using var dialog = new CdnDownloadTestDialog(
            _cdnDeliveryService,
            target.Profile,
            credential,
            target.Url);
        dialog.ShowDialog(this);
    }

    private async Task DownloadFromCdnAsync(
        S3ObjectEntry entry,
        CdnResolvedTarget target)
    {
        if (!TryResolveCdnCredential(target, out var credential, showMessage: true))
            return;

        using var save = new SaveFileDialog
        {
            FileName = entry.Name,
            InitialDirectory = _settings.DefaultDownloadDirectory,
            OverwritePrompt = _settings.ConfirmOverwrite
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        var destination = LocalObjectPath.ToExtendedLengthPath(save.FileName);
        var temporary = destination + $".s3explorer-cdn-{Guid.NewGuid():N}.part";
        string? completedStatus = null;
        try
        {
            SetBusy($"正在通过 CDN 下载：{target.Profile.Name}...");
            CdnDownloadResult result;
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                result = await _cdnDeliveryService.DownloadAsync(
                    target.Profile,
                    credential,
                    target.Url,
                    output,
                    CancellationToken.None);
            }

            File.Move(temporary, destination, overwrite: true);
            _logger.Info(
                $"CDN download completed profile={target.Profile.Name} bucket={_currentBucket} " +
                $"key={entry.Key} bytes={result.BytesWritten}");
            completedStatus =
                $"CDN 下载完成：{target.Profile.Name}，{FileSizeFormatter.Format(result.BytesWritten)}";
            MessageBox.Show(
                this,
                $"已通过 CDN“{target.Profile.Name}”保存到：{Environment.NewLine}{save.FileName}",
                "CDN 下载完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error(
                $"CDN download failed profile={target.Profile.Name} bucket={_currentBucket} key={entry.Key}",
                exception);
            ErrorDialog.ShowException(
                this,
                "CDN 下载失败",
                target.Profile.Name,
                exception,
                $"对象：{_currentBucket}/{entry.Key}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception cleanupException)
            {
                _logger.Error("Failed to clean temporary CDN download file", cleanupException);
            }
            SetIdle();
            if (completedStatus is not null)
                _requestStatus.Text = completedStatus;
        }
    }

    private async Task EnqueueSelectedCdnOperationAsync(CdnJobAction action)
    {
        if (!TryResolveSelectedCdnTarget(out var target, out _, showMessage: true) || target is null)
            return;

        await EnqueueCdnOperationAsync(action, target);
    }

    private async Task EnqueueCdnOperationAsync(CdnJobAction action, CdnResolvedTarget target)
    {
        if (!TryResolveCdnCredential(target, out _, showMessage: true))
            return;

        var purge = action is CdnJobAction.PurgeUrl or CdnJobAction.PurgeThenWarmup;
        if (purge && !target.Profile.Capabilities.HasFlag(CdnCapabilities.Purge))
        {
            MessageBox.Show(
                this,
                $"CDN 配置“{target.Profile.Name}”没有设置通用刷新端点。",
                "刷新不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (purge && MessageBox.Show(
                this,
                $"将把以下 URL 的刷新请求加入 CDN 任务中心：{Environment.NewLine}{Environment.NewLine}" +
                $"{target.Url.AbsoluteUri}{Environment.NewLine}{Environment.NewLine}是否继续？",
                "确认刷新 CDN 缓存",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try
        {
            var job = await _cdnJobQueue.EnqueueAsync(new CdnJobRecord
            {
                IdempotencyKey = $"manual:{Guid.NewGuid():N}",
                CdnProfileId = target.Profile.Id,
                BindingId = target.Binding.Id,
                Action = action,
                Urls = [target.Url.AbsoluteUri],
                LastMessage = "由用户手动提交。"
            });
            var actionName = action == CdnJobAction.Warmup ? "HTTP 预热" : "刷新 CDN 缓存";
            _logger.Info(
                $"CDN job enqueued. Job={job.Id}; Action={action}; Profile={target.Profile.Name}; Url={target.Url}");
            _requestStatus.Text = $"{actionName}已加入 CDN 任务中心";
            MessageBox.Show(
                this,
                $"{actionName}已加入 CDN 任务中心。{Environment.NewLine}" +
                $"任务 ID：{job.Id}{Environment.NewLine}" +
                "可在“CDN / 分发 → CDN 任务中心”中查看进度。",
                "CDN 任务已创建",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error($"Failed to enqueue CDN job. Action={action}; Profile={target.Profile.Name}", exception);
            ErrorDialog.ShowException(this, "无法创建 CDN 任务", target.Profile.Name, exception);
        }
    }

    private void UpdateCdnCommandStates(bool oneFile)
    {
        CdnResolvedTarget? target = null;
        var resolved = oneFile &&
            TryResolveSelectedCdnTarget(out target, out _, showMessage: false) &&
            target is not null;
        var purge = resolved && target!.Profile.Capabilities.HasFlag(CdnCapabilities.Purge);

        SetEnabled("cdn-configure", true);
        SetEnabled("cdn-jobs", true);
        SetEnabled("cdn-copy-url", resolved);
        SetEnabled("cdn-open-url", resolved);
        SetEnabled("cdn-download-test", resolved);
        SetEnabled("cdn-warmup", resolved);
        SetEnabled("cdn-purge", purge);
    }

    private void UpdateCdnContextCommandStates()
    {
        var resolved = TryResolveSelectedCdnTarget(out var target, out _, showMessage: false) &&
            target is not null;
        var purge = resolved && target!.Profile.Capabilities.HasFlag(CdnCapabilities.Purge);

        if (_cdnObjectContextCopy is not null) _cdnObjectContextCopy.Enabled = resolved;
        if (_cdnObjectContextOpen is not null) _cdnObjectContextOpen.Enabled = resolved;
        if (_cdnObjectContextProbe is not null) _cdnObjectContextProbe.Enabled = resolved;
        if (_cdnObjectContextWarmup is not null) _cdnObjectContextWarmup.Enabled = resolved;
        if (_cdnObjectContextPurge is not null) _cdnObjectContextPurge.Enabled = purge;
    }
}
