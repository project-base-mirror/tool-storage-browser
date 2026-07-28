using System.Diagnostics;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private readonly ICdnConfigurationStore _cdnConfigurationStore;
    private readonly ICdnCredentialStore _cdnCredentialStore;
    private readonly ICdnDeliveryService _cdnDeliveryService;
    private readonly ICdnCertificateInspector _cdnCertificateInspector;
    private CdnConfiguration _cdnConfiguration = CdnConfiguration.Empty;
    private IReadOnlyList<CdnCredential> _cdnCredentials = [];
    private ToolStripMenuItem? _cdnObjectContextMenu;
    private ToolStripMenuItem? _cdnObjectContextCopy;
    private ToolStripMenuItem? _cdnObjectContextOpen;
    private ToolStripMenuItem? _cdnObjectContextProbe;
    private ToolStripMenuItem? _cdnObjectContextWarmup;
    private ToolStripMenuItem? _cdnObjectContextPurge;
    private CancellationTokenSource? _cdnOperationCancellation;

    private ToolStripMenuItem BuildCdnMenu()
    {
        var menu = new ToolStripMenuItem("CDN / 分发(&C)") { Name = "CdnMenu" };
        menu.DropDownItems.Add(Command(
            "cdn-configure",
            "CDN 配置中心...",
            async (_, _) => await ShowCdnConfigurationAsync()));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Command("cdn-copy-url", "复制 CDN URL", (_, _) => CopySelectedCdnUrl()));
        menu.DropDownItems.Add(Command("cdn-open-url", "使用 CDN 打开", (_, _) => OpenSelectedCdnUrl()));
        menu.DropDownItems.Add(Command(
            "cdn-download-test",
            "CDN 下载测试...",
            (_, _) => ShowSelectedCdnDownloadTest()));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Command(
            "cdn-warmup",
            "HTTP 预热",
            async (_, _) => await RunSelectedCdnOperationAsync(purge: false)));
        menu.DropDownItems.Add(Command(
            "cdn-purge",
            "刷新 CDN 缓存",
            async (_, _) => await RunSelectedCdnOperationAsync(purge: true)));
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
        _cdnObjectContextOpen = new ToolStripMenuItem("使用 CDN 打开", null, (_, _) => OpenSelectedCdnUrl())
        {
            Name = "CdnObjectContextOpen"
        };
        _cdnObjectContextProbe = new ToolStripMenuItem("下载测试...", null, (_, _) => ShowSelectedCdnDownloadTest())
        {
            Name = "CdnObjectContextProbe"
        };
        _cdnObjectContextWarmup = new ToolStripMenuItem("HTTP 预热", null, async (_, _) =>
            await RunSelectedCdnOperationAsync(purge: false))
        {
            Name = "CdnObjectContextWarmup"
        };
        _cdnObjectContextPurge = new ToolStripMenuItem("刷新缓存", null, async (_, _) =>
            await RunSelectedCdnOperationAsync(purge: true))
        {
            Name = "CdnObjectContextPurge"
        };
        var configure = new ToolStripMenuItem("管理 CDN 配置...", null, async (_, _) =>
            await ShowCdnConfigurationAsync())
        {
            Name = "CdnObjectContextConfigure"
        };

        _cdnObjectContextMenu.DropDownItems.AddRange([
            _cdnObjectContextCopy,
            _cdnObjectContextOpen,
            _cdnObjectContextProbe,
            new ToolStripSeparator(),
            _cdnObjectContextWarmup,
            _cdnObjectContextPurge,
            new ToolStripSeparator(),
            configure
        ]);
        return _cdnObjectContextMenu;
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

    private async Task LoadCdnStateAsync()
    {
        try
        {
            var configuration = await _cdnConfigurationStore.LoadAsync();
            var credentials = await _cdnCredentialStore.LoadAsync();
            CdnConfigurationValidator.EnsureValid(configuration, credentials);
            _cdnConfiguration = configuration;
            _cdnCredentials = credentials;
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to load CDN configuration", exception);
            _cdnConfiguration = CdnConfiguration.Empty;
            _cdnCredentials = [];
            if (_automation is null)
            {
                ErrorDialog.ShowException(
                    this,
                    "无法加载 CDN 配置",
                    "CDN 配置和独立凭据",
                    exception);
            }
        }
    }

    private async Task ShowCdnConfigurationAsync(
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null)
    {
        using var dialog = new CdnConfigurationDialog(
            _profiles,
            _cdnConfiguration,
            _cdnCredentials,
            initialProfile ?? _currentProfile,
            initialBucket ?? _currentBucket,
            _cdnCertificateInspector);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            CdnConfigurationValidator.EnsureValid(dialog.Configuration, dialog.Credentials);
            await _cdnCredentialStore.SaveAsync(dialog.Credentials);
            await _cdnConfigurationStore.SaveAsync(dialog.Configuration);
            _cdnCredentials = dialog.Credentials;
            _cdnConfiguration = dialog.Configuration;
            UpdateCommandStates();
            MessageBox.Show(
                this,
                "CDN 配置、独立凭据和 Bucket/前缀关联已保存。",
                "CDN 配置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to save CDN configuration", exception);
            ErrorDialog.ShowException(this, "无法保存 CDN 配置", "CDN 配置和独立凭据", exception);
        }
    }

    private bool TryResolveSelectedCdnTarget(
        out CdnResolvedTarget? target,
        out CdnCredential? credential,
        bool showMessage)
    {
        target = null;
        credential = null;
        var selected = SelectedEntries();
        if (_currentProfile is null || _currentBucket is null || selected.Count != 1 || selected[0].IsDirectory)
        {
            if (showMessage)
            {
                MessageBox.Show(
                    this,
                    "请选择当前 Bucket 中的一个文件对象。",
                    "CDN / 分发",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return false;
        }

        target = CdnUrlMapper.ResolveDefault(
            _cdnConfiguration,
            _currentProfile.Id,
            _currentBucket,
            selected[0].Key);
        if (target is null)
        {
            if (showMessage)
            {
                MessageBox.Show(
                    this,
                    "当前对象没有匹配的 CDN 关联。请在“CDN 配置中心”中为对象存储连接、Bucket 和前缀建立关联。",
                    "没有 CDN 关联",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return false;
        }

        if (target.Profile.CredentialId is not Guid credentialId)
            return true;

        credential = _cdnCredentials.FirstOrDefault(value => value.Id == credentialId);
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
        target = null;
        return false;
    }

    private void CopySelectedCdnUrl()
    {
        if (!TryResolveSelectedCdnTarget(out var target, out _, showMessage: true) || target is null)
            return;

        Clipboard.SetText(target.Url.AbsoluteUri);
        _requestStatus.Text = $"已复制 CDN URL：{target.Profile.Name}";
    }

    private void OpenSelectedCdnUrl()
    {
        if (!TryResolveSelectedCdnTarget(out var target, out _, showMessage: true) || target is null)
            return;

        OpenExternalUrl(target.Url.AbsoluteUri);
    }

    private void ShowSelectedCdnDownloadTest()
    {
        if (!TryResolveSelectedCdnTarget(out var target, out var credential, showMessage: true) || target is null)
            return;

        using var dialog = new CdnDownloadTestDialog(
            _cdnDeliveryService,
            target.Profile,
            credential,
            target.Url);
        dialog.ShowDialog(this);
    }

    private async Task RunSelectedCdnOperationAsync(bool purge)
    {
        if (_cdnOperationCancellation is not null)
        {
            MessageBox.Show(
                this,
                "已有一个 CDN 操作正在执行，请等待其完成后再试。",
                "CDN / 分发",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (!TryResolveSelectedCdnTarget(out var target, out var credential, showMessage: true) || target is null)
            return;

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
                $"将请求 CDN 配置“{target.Profile.Name}”刷新以下 URL 的缓存：{Environment.NewLine}{Environment.NewLine}" +
                $"{target.Url.AbsoluteUri}{Environment.NewLine}{Environment.NewLine}是否继续？",
                "确认刷新 CDN 缓存",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var actionName = purge ? "刷新 CDN 缓存" : "HTTP 预热";
        _cdnOperationCancellation = new CancellationTokenSource();
        var cancellationToken = _cdnOperationCancellation.Token;
        SetBusy($"{actionName}...");
        UpdateCommandStates();
        try
        {
            var result = purge
                ? await _cdnDeliveryService.PurgeAsync(
                    target.Profile,
                    credential,
                    target.Url,
                    cancellationToken)
                : await _cdnDeliveryService.WarmupAsync(
                    target.Profile,
                    credential,
                    target.Url,
                    cancellationToken);

            _logger.Info(
                $"CDN operation completed. Action={actionName}; Profile={target.Profile.Name}; " +
                $"Status={result.StatusCode?.ToString() ?? "n/a"}; Success={result.Success}; " +
                $"ElapsedMs={result.Elapsed.TotalMilliseconds:F0}; Bytes={result.BytesRead}");
            var details = result.ResponseSnippet.Length == 0
                ? string.Empty
                : $"{Environment.NewLine}{Environment.NewLine}响应摘要：{Environment.NewLine}{result.ResponseSnippet}";
            MessageBox.Show(
                this,
                $"{result.Message}{Environment.NewLine}" +
                $"状态码：{result.StatusCode?.ToString() ?? "无"}{Environment.NewLine}" +
                $"耗时：{result.Elapsed.TotalMilliseconds:F0} ms{Environment.NewLine}" +
                $"读取：{FileSizeFormatter.Format(result.BytesRead)}{details}",
                actionName,
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"CDN operation cancelled. Action={actionName}; Profile={target.Profile.Name}");
            if (!_closing)
                _requestStatus.Text = $"{actionName}已取消";
        }
        catch (Exception exception)
        {
            _logger.Error($"CDN operation failed. Action={actionName}; Profile={target.Profile.Name}", exception);
            ErrorDialog.ShowException(this, $"{actionName}失败", target.Profile.Name, exception);
        }
        finally
        {
            _cdnOperationCancellation?.Dispose();
            _cdnOperationCancellation = null;
            SetIdle();
            UpdateCommandStates();
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
        SetEnabled("cdn-copy-url", resolved);
        SetEnabled("cdn-open-url", resolved);
        SetEnabled("cdn-download-test", resolved);
        var idle = _cdnOperationCancellation is null;
        SetEnabled("cdn-warmup", resolved && idle);
        SetEnabled("cdn-purge", purge && idle);
    }

    private void UpdateCdnContextCommandStates()
    {
        var resolved = TryResolveSelectedCdnTarget(out var target, out _, showMessage: false) &&
            target is not null;
        var purge = resolved && target!.Profile.Capabilities.HasFlag(CdnCapabilities.Purge);

        if (_cdnObjectContextCopy is not null) _cdnObjectContextCopy.Enabled = resolved;
        if (_cdnObjectContextOpen is not null) _cdnObjectContextOpen.Enabled = resolved;
        if (_cdnObjectContextProbe is not null) _cdnObjectContextProbe.Enabled = resolved;
        var idle = _cdnOperationCancellation is null;
        if (_cdnObjectContextWarmup is not null) _cdnObjectContextWarmup.Enabled = resolved && idle;
        if (_cdnObjectContextPurge is not null) _cdnObjectContextPurge.Enabled = purge && idle;
    }

    private void CancelCdnOperation() => _cdnOperationCancellation?.Cancel();
}
