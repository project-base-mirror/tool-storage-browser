using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private async Task UploadFilesAsync()
    {
        if (!EnsureLocation()) return;
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await UploadPathsAsync(dialog.FileNames);
    }

    private async Task UploadFolderAsync()
    {
        if (!EnsureLocation()) return;
        using var dialog = new FolderBrowserDialog { Description = "选择要递归上传的文件夹" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await UploadPathsAsync([dialog.SelectedPath]);
    }

    private async Task UploadPathsAsync(IEnumerable<string> paths)
    {
        if (!EnsureLocation()) return;
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var prefix = _currentPrefix;

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                await EnqueueUploadAsync(path, prefix + Path.GetFileName(path));
                continue;
            }
            if (!Directory.Exists(path))
                continue;

            var rootName = new DirectoryInfo(path).Name;
            var batch = await _transfers.CreateBatchAsync(
                profile,
                bucket,
                $"上传 {rootName}",
                path,
                TransferDirection.Upload);
            var chunk = new List<UploadBatchItem>(256);
            var skipped = 0;
            try
            {
                foreach (var file in EnumerateFilesSafely(path, (directory, exception) =>
                         {
                             skipped++;
                             _logger.Error($"Folder upload discovery skipped directory={directory}", exception);
                         }))
                {
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    chunk.Add(new UploadBatchItem(
                        file,
                        S3Path.Combine(prefix, $"{rootName}/{relative}"),
                        relative,
                        info.Length,
                        profile.DefaultStorageClass));
                    if (chunk.Count < 256)
                        continue;
                    await _transfers.AddUploadBatchItemsAsync(batch, chunk);
                    chunk.Clear();
                }
                if (chunk.Count > 0)
                    await _transfers.AddUploadBatchItemsAsync(batch, chunk);
            }
            catch (Exception exception)
            {
                skipped++;
                _logger.Error($"Folder upload discovery failed root={path}", exception);
                MessageBox.Show(
                    this,
                    $"文件夹发现提前停止：{exception.Message}\n\n已发现的文件仍会继续传输。",
                    "文件夹上传",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                await _transfers.CompleteBatchDiscoveryAsync(batch.Id, skipped);
            }
            _logger.Info($"Upload batch queued profile={profile.Name} bucket={bucket} batch={batch.Id} root={path} skipped={skipped}");
        }
        SetTransferVisibility(true);
    }

    private async Task EnqueueUploadAsync(string localPath, string key)
    {
        var file = new FileInfo(localPath);
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        await _transfers.EnqueueUploadAsync(
            profile, bucket, key, localPath, file.Length, profile.DefaultStorageClass);
        _logger.Info($"Upload queued profile={profile.Name} bucket={bucket} key={key} bytes={file.Length}");
    }

    private async Task DownloadSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count == 0) return;

        string targetRoot;
        if (selected.Count == 1 && !selected[0].IsDirectory)
        {
            await DownloadSingleObjectAsync(selected[0]);
            return;
        }
        using (var folder = new FolderBrowserDialog
               {
                   InitialDirectory = _settings.DefaultDownloadDirectory,
                   Description = "选择下载目标文件夹"
               })
        {
            if (folder.ShowDialog(this) != DialogResult.OK) return;
            targetRoot = folder.SelectedPath;
        }

        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var batchName = selected.Count == 1
            ? $"下载 {selected[0].Name}"
            : $"下载 {selected.Count:N0} 项";
        var batch = await _transfers.CreateBatchAsync(
            profile,
            bucket,
            batchName,
            targetRoot,
            TransferDirection.Download);
        var chunk = new List<DownloadBatchItem>(256);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        try
        {
            foreach (var entry in selected)
            {
                if (!entry.IsDirectory)
                {
                    if (seenKeys.Add(entry.Key))
                    {
                        var relative = entry.Name;
                        chunk.Add(new DownloadBatchItem(
                            entry.Key,
                            LocalObjectPath.MapRelativeKey(targetRoot, relative),
                            relative,
                            entry.Size));
                    }
                }
                else
                {
                    await foreach (var child in EnumerateAllObjectsAsync(entry.Key))
                    {
                        if (child.IsDirectory || !seenKeys.Add(child.Key))
                            continue;
                        if (!child.Key.StartsWith(entry.Key, StringComparison.Ordinal))
                            throw new InvalidOperationException("对象 Key 不属于所选文件夹。");

                        var childRelative = child.Key[entry.Key.Length..].TrimStart('/');
                        var relative = $"{entry.Name}/{childRelative}";
                        chunk.Add(new DownloadBatchItem(
                            child.Key,
                            LocalObjectPath.MapRelativeKey(targetRoot, relative),
                            relative,
                            child.Size));
                        if (chunk.Count < 256)
                            continue;
                        await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
                        chunk.Clear();
                    }
                }

                if (chunk.Count >= 256)
                {
                    await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
                await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
        }
        catch (Exception exception)
        {
            skipped++;
            _logger.Error($"Folder download discovery failed target={targetRoot}", exception);
            MessageBox.Show(
                this,
                $"递归发现提前停止：{exception.Message}\n\n已发现的对象仍会继续下载。",
                "文件夹下载",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            await _transfers.CompleteBatchDiscoveryAsync(batch.Id, skipped);
        }

        _logger.Info($"Download batch queued profile={profile.Name} bucket={bucket} batch={batch.Id} target={targetRoot} skipped={skipped}");
        SetTransferVisibility(true);
    }

    private async Task DownloadSelectedBucketAsync()
    {
        if (!await OpenSelectedBucketAsync()) return;
        using var folder = new FolderBrowserDialog
        {
            InitialDirectory = _settings.DefaultDownloadDirectory,
            Description = $"选择 Bucket “{_currentBucket}” 的下载目标文件夹"
        };
        if (folder.ShowDialog(this) != DialogResult.OK) return;

        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var targetRoot = folder.SelectedPath;
        var batch = await _transfers.CreateBatchAsync(
            profile,
            bucket,
            $"下载整个 Bucket {bucket}",
            targetRoot,
            TransferDirection.Download);
        var chunk = new List<DownloadBatchItem>(256);
        var skipped = 0;

        try
        {
            await foreach (var entry in EnumerateAllObjectsAsync(string.Empty))
            {
                if (entry.IsDirectory) continue;
                chunk.Add(new DownloadBatchItem(
                    entry.Key,
                    LocalObjectPath.MapRelativeKey(targetRoot, entry.Key),
                    entry.Key,
                    entry.Size));
                if (chunk.Count < 256) continue;
                await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
                chunk.Clear();
            }
            if (chunk.Count > 0)
                await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
        }
        catch (Exception exception)
        {
            skipped++;
            _logger.Error($"Bucket download discovery failed bucket={bucket} target={targetRoot}", exception);
            MessageBox.Show(
                this,
                $"Bucket 递归发现提前停止：{exception.Message}\n\n已发现的对象仍会继续下载。",
                "下载整个 Bucket",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            await _transfers.CompleteBatchDiscoveryAsync(batch.Id, skipped);
        }

        _logger.Info($"Bucket download queued profile={profile.Name} bucket={bucket} batch={batch.Id} target={targetRoot} skipped={skipped}");
        SetTransferVisibility(true);
    }

    private async Task EnqueueDownloadAsync(S3ObjectEntry entry, string localPath)
    {
        localPath = LocalObjectPath.ToExtendedLengthPath(localPath);
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        await _transfers.EnqueueDownloadAsync(profile, bucket, entry.Key, localPath, entry.Size);
        _logger.Info($"Download queued profile={profile.Name} bucket={bucket} key={entry.Key} bytes={entry.Size}");
    }

    private async Task DownloadSingleObjectAsync(S3ObjectEntry entry)
    {
        using var save = new SaveFileDialog
        {
            FileName = entry.Name,
            InitialDirectory = _settings.DefaultDownloadDirectory,
            OverwritePrompt = _settings.ConfirmOverwrite
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        await EnqueueDownloadAsync(entry, save.FileName);
        SetTransferVisibility(true);
    }

    private async Task CreateFolderAsync()
    {
        if (!EnsureLocation()) return;
        var name = PromptDialog.Show(this, "新建文件夹", "文件夹名称：");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Trim('/');
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            MessageBox.Show(this, "文件夹名称无效。", "新建文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            await _storage.CreateFolderAsync(_currentProfile!, _currentBucket!, S3Path.Combine(_currentPrefix, name) + "/", CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "创建失败", "新建虚拟目录", exception, CurrentLocationText());
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count == 0) return;
        var keys = new List<string>();
        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
                keys.AddRange((await ListAllObjectsAsync(entry.Key)).Where(item => !item.IsDirectory).Select(item => item.Key));
            else
                keys.Add(entry.Key);
        }
        keys = keys.Distinct(StringComparer.Ordinal).ToList();
        if (_settings.ConfirmDelete)
        {
            var versioningWarning = "版本控制状态未确认；普通删除在版本化 Bucket 中可能创建 Delete Marker。";
            try
            {
                if (S3ProviderCapabilityRegistry.For(_currentProfile!.ServiceType).Object.VersionOperations.Supported)
                {
                    var state = await _storage.GetBucketVersioningAsync(
                        _currentProfile, _currentBucket!, CancellationToken.None);
                    versioningWarning = state == BucketVersioningState.Enabled
                        ? "当前 Bucket 已启用版本控制：本次普通删除将创建 Delete Marker，不会永久删除历史版本。"
                        : state == BucketVersioningState.Suspended
                            ? "当前 Bucket 已暂停版本控制：普通删除仍可能创建或替换 null Version 的 Delete Marker。"
                            : "当前 Bucket 未启用版本控制：删除通常不可撤销。";
                }
            }
            catch (Exception exception)
            {
                _logger.Error($"Versioning state check failed bucket={_currentBucket}", exception);
            }
            if (MessageBox.Show(this,
                    $"将删除 {keys.Count:N0} 个对象。\n\n{versioningWarning}",
                    "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }
        try
        {
            SetBusy($"正在删除 {keys.Count:N0} 个对象...");
            await _storage.DeleteObjectsAsync(_currentProfile!, _currentBucket!, keys, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "删除失败", "批量删除对象", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private void CopySelectionToObjectClipboard(bool move)
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count == 0) return;
        _objectClipboard = new ObjectClipboardPayload(
            _currentProfile!.Id, _currentProfile.Name, _currentBucket!,
            selected.Select(entry => new ObjectClipboardEntry(
                entry.Key, entry.Name, entry.IsDirectory, entry.Size)).ToArray(), move);
        _requestStatus.Text = $"已{(move ? "剪切" : "复制")} {selected.Count:N0} 个对象";
        UpdateCommandStates();
    }

    private async Task PasteObjectClipboardAsync()
    {
        if (!EnsureLocation() || _objectClipboard is null || !EnsureClipboardProfile()) return;
        await QueueObjectTransferAsync(
            _objectClipboard, _currentBucket!, _currentPrefix, ObjectConflictPolicy.Ask);
    }

    private async Task CopyOrMoveSelectedAsync(bool move)
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count == 0) return;
        var payload = new ObjectClipboardPayload(
            _currentProfile!.Id, _currentProfile.Name, _currentBucket!,
            selected.Select(entry => new ObjectClipboardEntry(
                entry.Key, entry.Name, entry.IsDirectory, entry.Size)).ToArray(), move);
        using var dialog = new ObjectTransferDialog(
            move, _currentBucket!, _currentPrefix, selected.Count);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Options is null) return;
        await QueueObjectTransferAsync(
            payload, dialog.Options.DestinationBucket,
            dialog.Options.DestinationPrefix, dialog.Options.ConflictPolicy);
    }

    private async Task QueueObjectTransferAsync(
        ObjectClipboardPayload payload,
        string destinationBucket,
        string destinationPrefix,
        ObjectConflictPolicy conflictPolicy)
    {
        if (_currentProfile is null || payload.ProfileId != _currentProfile.Id)
        {
            MessageBox.Show(this, "对象剪贴板属于另一个连接。请在源连接中重新复制或剪切。", "对象剪贴板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var direction = payload.Move ? TransferDirection.Move : TransferDirection.Copy;
        var batch = await _transfers.CreateBatchAsync(
            _currentProfile, payload.SourceBucket,
            $"{(payload.Move ? "移动" : "复制")} {payload.Entries.Count:N0} 项",
            $"s3://{payload.SourceBucket}", direction);
        var chunk = new List<ObjectTransferBatchItem>(256);
        var skipped = 0;
        var cancelled = false;
        try
        {
            foreach (var entry in payload.Entries)
            {
                var topName = entry.Name.Trim('/');
                if (entry.IsDirectory)
                {
                    var destinationRoot = ObjectTransferPlanner.BuildDestinationKey(destinationPrefix, topName) + "/";
                    ObjectTransferPlanner.ValidateDestination(
                        payload.SourceBucket, entry.Key, true, destinationBucket, destinationRoot);
                    await foreach (var child in EnumerateAllObjectsAsync(entry.Key))
                    {
                        if (child.IsDirectory) continue;
                        var relative = ObjectTransferPlanner.GetRelativePath(entry.Key, child.Key);
                        var target = ObjectTransferPlanner.BuildDestinationKey(destinationPrefix, topName, relative);
                        var resolved = await ResolveObjectConflictAsync(destinationBucket, target, conflictPolicy);
                        if (resolved is null) { skipped++; continue; }
                        chunk.Add(new ObjectTransferBatchItem(
                            child.Key, destinationBucket, resolved, $"{topName}/{relative}", child.Size, conflictPolicy));
                        if (chunk.Count >= 256)
                        {
                            await _transfers.AddObjectTransferBatchItemsAsync(batch, chunk);
                            chunk.Clear();
                        }
                    }
                }
                else
                {
                    var target = ObjectTransferPlanner.BuildDestinationKey(destinationPrefix, topName);
                    ObjectTransferPlanner.ValidateDestination(
                        payload.SourceBucket, entry.Key, false, destinationBucket, target);
                    var resolved = await ResolveObjectConflictAsync(destinationBucket, target, conflictPolicy);
                    if (resolved is null) { skipped++; continue; }
                    chunk.Add(new ObjectTransferBatchItem(
                        entry.Key, destinationBucket, resolved, topName, entry.Size, conflictPolicy));
                }

                if (chunk.Count >= 256)
                {
                    await _transfers.AddObjectTransferBatchItemsAsync(batch, chunk);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
                await _transfers.AddObjectTransferBatchItemsAsync(batch, chunk);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            await _transferQueue.CancelBatchAsync(batch.Id);
        }
        catch (Exception exception)
        {
            skipped++;
            _logger.Error($"Object transfer discovery failed batch={batch.Id}", exception);
            ErrorDialog.ShowException(this, "无法建立对象传输批次", payload.Move ? "移动对象" : "复制对象", exception, CurrentLocationText());
        }
        finally
        {
            if (!cancelled)
                await _transfers.CompleteBatchDiscoveryAsync(batch.Id, skipped);
        }

        if (cancelled) return;
        if (payload.Move && ReferenceEquals(payload, _objectClipboard))
            _objectClipboard = null;
        SetTransferVisibility(true);
        _requestStatus.Text = $"已建立{(payload.Move ? "移动" : "复制")}批次，跳过 {skipped:N0} 项";
        UpdateCommandStates();
    }

    private async Task<string?> ResolveObjectConflictAsync(
        string bucket, string key, ObjectConflictPolicy policy)
    {
        if (policy == ObjectConflictPolicy.Overwrite) return key;
        if (!await _storage.ObjectExistsAsync(_currentProfile!, bucket, key, CancellationToken.None))
            return key;
        if (policy == ObjectConflictPolicy.Skip) return null;
        if (policy == ObjectConflictPolicy.Ask)
        {
            var answer = MessageBox.Show(this,
                $"目标对象已存在：\n\ns3://{bucket}/{key}\n\n是：覆盖　否：跳过　取消：停止整个批次",
                "对象冲突", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            return answer switch
            {
                DialogResult.Yes => key,
                DialogResult.No => null,
                _ => throw new OperationCanceledException()
            };
        }
        for (var sequence = 2; sequence <= 10_000; sequence++)
        {
            var candidate = ObjectTransferPlanner.GetAutoRenameCandidate(key, sequence);
            if (!await _storage.ObjectExistsAsync(_currentProfile!, bucket, candidate, CancellationToken.None))
                return candidate;
        }
        throw new InvalidOperationException("无法为目标对象生成不冲突的名称。");
    }

    private bool EnsureClipboardProfile(bool showMessage = true)
    {
        var valid = _objectClipboard is not null &&
            _currentProfile?.Id == _objectClipboard.ProfileId &&
            _currentBucket is not null;
        if (!valid && showMessage)
            MessageBox.Show(this, "对象剪贴板为空，或属于另一个连接。", "对象剪贴板", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return valid;
    }

    private async Task RenameSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory)
        {
            MessageBox.Show(this, "当前版本一次只支持重命名一个文件对象。目录重命名将在后续版本提供。", "重命名");
            return;
        }
        var entry = selected[0];
        var name = PromptDialog.Show(this, "重命名对象", "新名称：", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        var parent = entry.Key[..Math.Max(0, entry.Key.LastIndexOf('/') + 1)];
        var targetKey = parent + name.Trim('/');
        try
        {
            SetBusy("正在重命名（Copy + Delete）...");
            await _storage.MoveObjectAsync(_currentProfile!, _currentBucket!, entry.Key, _currentBucket!, targetKey, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "重命名失败", "Copy + Delete", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private async Task ShowPropertiesAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory) return;
        var entry = selected[0];
        ObjectProperties properties;
        try
        {
            SetBusy("正在读取对象属性...");
            properties = await _storage.GetObjectPropertiesAsync(
                _currentProfile!,
                _currentBucket!,
                entry.Key,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "读取失败", "对象属性", exception, CurrentLocationText());
            return;
        }
        finally { SetIdle(); }

        var cdnTargets = CdnUrlMapper.ResolveAll(
            _cdnConfiguration,
            _currentProfile!.Id,
            _currentBucket!,
            entry.Key);
        using var dialog = new ObjectPropertiesDialog(
            properties,
            _currentProfile.Endpoint,
            cdnProfileName: cdnTargets.FirstOrDefault()?.Profile.Name,
            storage: _storage,
            profile: _currentProfile);
        dialog.ShowDialog(this);
        switch (dialog.SelectedAction)
        {
            case ObjectPropertiesAction.DownloadFromObjectStorage:
                await DownloadSingleObjectAsync(entry);
                break;
            case ObjectPropertiesAction.DownloadFromCdn when cdnTargets.Count > 0:
                await DownloadFromCdnAsync(entry, cdnTargets[0]);
                break;
        }
    }

    private async Task EditBatchMetadataAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries().Where(entry => !entry.IsDirectory).ToArray();
        if (selected.Length == 0) return;
        var capability = S3ProviderCapabilityRegistry.For(_currentProfile!.ServiceType).Object.MetadataRewrite;
        if (!capability.Supported)
        {
            MessageBox.Show(this, capability.Reason, "批量 Header / Metadata", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ObjectMetadataBatchDialog(selected.Length);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(
                this,
                $"确定对 {selected.Length:N0} 个对象逐个执行原地 Copy 吗？\r\n\r\n" +
                "这可能产生请求费和新对象版本；失败对象会单独报告，不会回滚已成功对象。",
                "确认批量 Header / Metadata",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        var failures = new List<string>();
        var succeeded = 0;
        SetBusy($"正在更新 {selected.Length:N0} 个对象的 Header / Metadata...");
        try
        {
            foreach (var entry in selected)
            {
                try
                {
                    var current = await _storage.GetObjectPropertiesAsync(
                        _currentProfile, _currentBucket!, entry.Key, CancellationToken.None);
                    var headers = dialog.ApplyTo(current);
                    await _storage.ReplaceObjectMetadataAsync(
                        _currentProfile, _currentBucket!, entry.Key, current.VersionId,
                        headers, CancellationToken.None);
                    succeeded++;
                }
                catch (Exception exception)
                {
                    failures.Add($"{entry.Key}: {SensitiveDataRedactor.Redact(exception.Message)}");
                }
            }
        }
        finally { SetIdle(); }

        if (failures.Count == 0)
            MessageBox.Show(this, $"已更新 {succeeded:N0} 个对象。", "批量 Header / Metadata");
        else
            MessageBox.Show(
                this,
                $"成功 {succeeded:N0}，失败 {failures.Count:N0}。\r\n\r\n{string.Join("\r\n", failures.Take(10))}",
                "批量 Header / Metadata",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        await RefreshAsync();
    }

    private void ShowPresignedUrl()
    {
        if (!EnsureLocation()) return;
        var support = S3ProviderCapabilityRegistry.For(_currentProfile!.ServiceType).Object.PresignedUrl;
        if (!support.Supported)
        {
            MessageBox.Show(this, support.Reason, "预签名 URL", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory) return;
        var entry = selected[0];
        using var dialog = new PresignedUrlDialog(
            $"s3://{_currentProfile!.Name}/{_currentBucket}/{entry.Key}",
            lifetime => _storage.CreatePresignedUrl(_currentProfile!, _currentBucket!, entry.Key, lifetime));
        dialog.ShowDialog(this);
        _logger.Info($"Presigned URL generated bucket={_currentBucket} key={entry.Key}");
    }

}
