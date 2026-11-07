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
        try { await UploadPathsCoreAsync(paths); }
        catch (OperationCanceledException) { _requestStatus.Text = "上传发现已取消"; }
    }

    private async Task UploadPathsCoreAsync(IEnumerable<string> paths)
    {
        if (!EnsureLocation()) return;
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var prefix = _currentPrefix;
        var policy = DefaultTransferConflictPolicy;
        var allocated = new HashSet<string>(StringComparer.Ordinal);
        var skippedFiles = 0;

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var destination = await ResolveUploadDestinationAsync(profile, bucket, prefix + Path.GetFileName(path), policy, allocated);
                if (destination is null) { skippedFiles++; continue; }
                await EnqueueUploadAsync(profile, bucket, path, destination.Path, destination.Policy);
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
            var emptyDirectories = new List<string>();
            var skipped = 0;
            try
            {
                foreach (var file in SafeLocalFileTraversal.EnumerateFiles(path, (directory, exception) =>
                         {
                             skipped++;
                             _logger.Error($"Folder upload discovery skipped directory={directory}", exception);
                         }, directory =>
                         {
                             if (emptyDirectories.Count >= WindowsFileClipboardManifest.DefaultItemLimit)
                                 throw new InvalidOperationException("空目录数量超过保护上限，请分批上传。");
                             emptyDirectories.Add(directory);
                         }))
                {
                    _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    var destination = await ResolveUploadDestinationAsync(
                        profile, bucket, S3Path.Combine(prefix, $"{rootName}/{relative}"), policy, allocated);
                    if (destination is null) { skipped++; continue; }
                    chunk.Add(new UploadBatchItem(
                        file, destination.Path, relative, info.Length,
                        profile.DefaultStorageClass, destination.Policy));
                    if (chunk.Count < 256)
                        continue;
                    await _transfers.AddUploadBatchItemsAsync(batch, chunk);
                    chunk.Clear();
                }
                if (chunk.Count > 0)
                    await _transfers.AddUploadBatchItemsAsync(batch, chunk);
                // A folder containing no files still needs an S3 marker. Ordinary files retain the
                // resumable queue path; zero-byte markers use the same conditional upload pipeline.
                foreach (var directory in emptyDirectories)
                {
                    _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                    var relative = Path.GetRelativePath(path, directory).Replace('\\', '/');
                    var folderKey = prefix + rootName + (relative == "." ? "/" : "/" + relative + "/");
                    await UploadEmptyDirectoryMarkerAsync(profile, bucket, folderKey);
                }
            }
            catch (OperationCanceledException) { return; }
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
        _requestStatus.Text = $"上传已入队，跳过 {skippedFiles:N0} 个单文件（文件夹跳过数见批次详情）";
    }

    private async Task UploadEmptyDirectoryMarkerAsync(ConnectionProfile profile, string bucket, string key)
    {
        if (await _storage.ObjectExistsAsync(profile, bucket, key, CancellationToken.None)) return;
        var temporary = Path.GetTempFileName();
        try
        {
            var context = _transferRuntime.CreateClipboardContext();
            context = context with { Options = context.Options with
            {
                AllowDestinationOverwrite = false,
                RetainFailedTransferState = false
            } };
            // Create-only also protects an object that appears after the existence lookup.
            await _storage.UploadFileAsync(profile, bucket, key, temporary, profile.DefaultStorageClass,
                context, CancellationToken.None);
        }
        finally { File.Delete(temporary); }
    }

    private async Task EnqueueUploadAsync(ConnectionProfile profile, string bucket, string localPath, string key, ObjectConflictPolicy conflictPolicy = ObjectConflictPolicy.Overwrite)
    {
        var file = new FileInfo(localPath);
        await _transfers.EnqueueUploadAsync(
            profile, bucket, key, localPath, file.Length, profile.DefaultStorageClass, conflictPolicy: conflictPolicy);
        _logger.Info($"Upload queued profile={profile.Name} bucket={bucket} key={key} bytes={file.Length}");
    }

    private async Task DownloadSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
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

        var policy = DefaultTransferConflictPolicy;
        var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                if (!entry.IsDirectory)
                {
                    if (seenKeys.Add(entry.Key))
                    {
                        var relative = DownloadRelativeKey(entry, entry.Key);
                        var destination = ResolveDownloadDestination(
                            LocalObjectPath.MapRelativeKey(targetRoot, relative), policy, allocated);
                        if (destination is null) { skipped++; continue; }
                        chunk.Add(new DownloadBatchItem(entry.Key, destination.Path, relative, entry.Size, destination.Policy));
                    }
                }
                else
                {
                    await foreach (var child in EnumerateAllObjectsAsync(entry.Key, profile: profile, bucket: bucket))
                    {
                        _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                        if (child.IsDirectory || !seenKeys.Add(child.Key))
                            continue;
                        var relative = DownloadRelativeKey(entry, child.Key);
                        var destination = ResolveDownloadDestination(
                            LocalObjectPath.MapRelativeKey(targetRoot, relative), policy, allocated);
                        if (destination is null) { skipped++; continue; }
                        chunk.Add(new DownloadBatchItem(child.Key, destination.Path, relative, child.Size, destination.Policy));
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
        catch (OperationCanceledException) { return; }
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
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        using var folder = new FolderBrowserDialog
        {
            InitialDirectory = _settings.DefaultDownloadDirectory,
            Description = $"选择 Bucket “{_currentBucket}” 的下载目标文件夹"
        };
        if (folder.ShowDialog(this) != DialogResult.OK) return;

        var policy = DefaultTransferConflictPolicy;
        var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            await foreach (var entry in EnumerateAllObjectsAsync(string.Empty, profile: profile, bucket: bucket))
            {
                _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                if (entry.IsDirectory) continue;
                var destination = ResolveDownloadDestination(
                    LocalObjectPath.MapRelativeKey(targetRoot, entry.Key), policy, allocated);
                if (destination is null) { skipped++; continue; }
                chunk.Add(new DownloadBatchItem(entry.Key, destination.Path, entry.Key, entry.Size, destination.Policy));
                if (chunk.Count < 256) continue;
                await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
                chunk.Clear();
            }
            if (chunk.Count > 0)
                await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
        }
        catch (OperationCanceledException) { return; }
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

    private async Task EnqueueDownloadAsync(S3ObjectEntry entry, string localPath, ObjectConflictPolicy conflictPolicy = ObjectConflictPolicy.Overwrite,
        ConnectionProfile? profile = null, string? bucket = null)
    {
        localPath = LocalObjectPath.ToExtendedLengthPath(localPath);
        profile ??= _currentProfile!;
        bucket ??= _currentBucket!;
        await _transfers.EnqueueDownloadAsync(profile, bucket, entry.Key, localPath, entry.Size, conflictPolicy: conflictPolicy);
        _logger.Info($"Download queued profile={profile.Name} bucket={bucket} key={entry.Key} bytes={entry.Size}");
    }

    internal static string DownloadRelativeKey(S3ObjectEntry selected, string key) => selected.IsDirectory
        ? S3Path.LeafName(selected.Key, true) + "/" + ObjectTransferPlanner.GetRelativePath(selected.Key, key)
        : string.Equals(selected.Key, key, StringComparison.Ordinal)
            ? S3Path.LeafName(key, false)
            : throw new InvalidOperationException("对象 Key 不属于所选对象。");

    internal static string SuggestedDownloadFileName(string key)
    {
        var name = S3Path.LeafName(key, false);
        // A user-selected save destination can safely download literal dot keys.
        // Bulk path mapping still rejects traversal-like segments before writing.
        return name is "." or ".."
            ? string.Concat(Enumerable.Repeat("_x002E_", name.Length))
            : LocalObjectPath.SanitizeSegment(name);
    }

    private async Task DownloadSingleObjectAsync(S3ObjectEntry entry)
    {
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        using var save = new SaveFileDialog
        {
            FileName = SuggestedDownloadFileName(entry.Key),
            InitialDirectory = _settings.DefaultDownloadDirectory,
            OverwritePrompt = false
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        var policy = DefaultTransferConflictPolicy;
        ResolvedTransferDestination? destination;
        try { destination = ResolveDownloadDestination(save.FileName, policy, new(StringComparer.OrdinalIgnoreCase)); }
        catch (OperationCanceledException) { return; }
        if (destination is null) { _requestStatus.Text = "已跳过已有下载目标"; return; }
        await EnqueueDownloadAsync(entry, destination.Path, destination.Policy, profile, bucket);
        SetTransferVisibility(true);
    }

    private async Task CreateFolderAsync()
    {
        if (!EnsureLocation()) return;
        var name = PromptDialog.Show(this, "新建文件夹", "文件夹名称：");
        if (string.IsNullOrEmpty(name)) return;
        if (name.Any(char.IsControl))
        {
            MessageBox.Show(this, "文件夹名称无效。", "新建文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            await _storage.CreateFolderAsync(_currentProfile!, _currentBucket!, S3Path.FolderMarker(_currentPrefix, name), CancellationToken.None);
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
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var keys = new List<string>();
        try
        {
            foreach (var entry in selected)
            {
                if (entry.IsDirectory)
                    keys.AddRange(await ObjectDeletionListing.ListKeysAsync(
                        entry.Key, _settings.ObjectCacheLimit,
                        (token, ct) => _storage.ListObjectsForDeletionAsync(
                            profile, bucket, entry.Key, token, _settings.ObjectPageSize, ct)));
                else
                    keys.Add(entry.Key);
            }
        }
        catch (Exception exception)
        {
            // Discovery must finish before any delete is sent. Invalid pagination,
            // out-of-prefix results and limits are expected fail-closed errors.
            ErrorDialog.ShowException(this, "无法完整列出删除对象", "准备批量删除", exception,
                new S3Location(profile.Name, bucket, string.Empty).ToString());
            return;
        }
        keys = keys.Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return;
        if (_settings.ConfirmDelete)
        {
            var versioningWarning = "版本控制状态未确认；普通删除在版本化 Bucket 中可能创建 Delete Marker。";
            try
            {
                if (S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations.Supported)
                {
                    var state = await _storage.GetBucketVersioningAsync(
                        profile, bucket, CancellationToken.None);
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
            await _storage.DeleteObjectsAsync(profile, bucket, keys, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "删除失败", "批量删除对象", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private async Task PasteObjectClipboardAsync()
    {
        if (!EnsureLocation()) return;
        await RunUiCommandAsync("粘贴文件", async () =>
        {
            if (OwnsObjectClipboard())
            {
                if (_objectClipboard is null || !EnsureClipboardProfile()) return;
                await QueueObjectTransferAsync(_objectClipboard, _currentBucket!, _currentPrefix, ObjectConflictPolicy.Ask);
                return;
            }
            // Never interpret text/URLs or stale in-memory object selections as files.
            // A CF_HDROP cut is deliberately treated as a copy: uploading never deletes local sources.
            if (!Clipboard.ContainsFileDropList()) return;
            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (paths.Length > WindowsFileClipboardManifest.DefaultItemLimit)
                throw new InvalidOperationException("剪贴板项目过多，请分批上传。");
            if (paths.Any(path => !Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path))))
                throw new IOException("剪贴板中有文件或目录已不存在或无法访问，请重新复制。");
            await UploadPathsAsync(paths);
        });
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
            move,
            _currentBucket!,
            _currentPrefix,
            selected.Count,
            _currentProfile,
            _storage,
            _bucketDiscoveryCache,
            _profiles);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Options is null) return;
        await QueueObjectTransferAsync(
            payload, dialog.Options.DestinationBucket,
            dialog.Options.DestinationPrefix, dialog.Options.ConflictPolicy,
            dialog.Options.DestinationProfile);
    }

    private async Task QueueObjectTransferAsync(
        ObjectClipboardPayload payload,
        string destinationBucket,
        string destinationPrefix,
        ObjectConflictPolicy conflictPolicy,
        ConnectionProfile? destinationProfile = null)
    {
        // Freeze both sides before discovery yields. Navigating elsewhere must not change
        // the source enumeration, conflict checks or credentials of an already-started batch.
        destinationProfile ??= _currentProfile;
        if (destinationProfile is null) return;
        var sourceProfile = _profiles.FirstOrDefault(profile => profile.Id == payload.ProfileId)
            ?? (_currentProfile?.Id == payload.ProfileId ? _currentProfile : null)
            ?? (await _profileStore.LoadAsync()).FirstOrDefault(profile => profile.Id == payload.ProfileId);
        if (sourceProfile is null)
        {
            MessageBox.Show(this, "源连接已不存在，请重新选择要复制的对象。", "对象剪贴板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var crossConnection = sourceProfile.Id != destinationProfile.Id;
        if (payload.Move && crossConnection)
        {
            MessageBox.Show(this, "跨连接请使用复制，核对目标后再删除源对象。", "对象剪贴板", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (payload.Move && !S3ProviderCapabilityRegistry.For(sourceProfile.ServiceType).ConditionalDelete.Supported &&
            MessageBox.Show(this,
                S3ProviderCapabilityRegistry.For(sourceProfile.ServiceType).ConditionalDelete.Reason +
                "\n\n确认已停止并发写入，并继续移动？", "移动对象", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        // Validate all roots before enumeration or queueing can create objects. Regional
        // endpoint aliases must never allow a live listing to feed its own descendants.
        foreach (var entry in payload.Entries)
        {
            var name = S3Path.LeafName(entry.Key, entry.IsDirectory);
            var target = entry.IsDirectory
                ? ObjectTransferPlanner.BuildDirectoryDestinationKey(destinationPrefix, name)
                : ObjectTransferPlanner.BuildDestinationKey(destinationPrefix, name);
            ObjectTransferPlanner.ValidateDestination(sourceProfile, payload.SourceBucket, entry.Key,
                entry.IsDirectory, destinationProfile, destinationBucket, target);
        }

        var direction = payload.Move ? TransferDirection.Move : TransferDirection.Copy;
        var batch = await _transfers.CreateBatchAsync(
            sourceProfile, payload.SourceBucket,
            $"{(payload.Move ? "移动" : "复制")} {payload.Entries.Count:N0} 项" +
                (crossConnection ? $" → {destinationProfile.Name}" : string.Empty),
            $"s3://{payload.SourceBucket}", direction);
        var chunk = new List<ObjectTransferBatchItem>(256);
        var skipped = 0;
        var cancelled = false;
        try
        {
            foreach (var entry in payload.Entries)
            {
                _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                // Name is a presentation label (empty/dot segments have friendly
                // labels). Only the literal key can determine a remote target.
                var topName = S3Path.LeafName(entry.Key, entry.IsDirectory);
                if (entry.IsDirectory)
                {
                    await foreach (var child in EnumerateAllObjectsAsync(
                        entry.Key, includeFolderMarkers: true, profile: sourceProfile, bucket: payload.SourceBucket))
                    {
                        _transfers.ThrowIfBatchDiscoveryStopped(batch.Id);
                        if (child.IsDirectory) continue;
                        var relative = ObjectTransferPlanner.GetRelativePath(entry.Key, child.Key);
                        var target = ObjectTransferPlanner.BuildDestinationKey(destinationPrefix, topName, relative);
                        var resolved = await ResolveObjectConflictAsync(destinationProfile, destinationBucket, target, conflictPolicy);
                        if (resolved is null) { skipped++; continue; }
                        chunk.Add(new ObjectTransferBatchItem(
                            child.Key, destinationBucket, resolved.Value.Key, $"{topName}/{relative}", child.Size, resolved.Value.Policy,
                            crossConnection ? destinationProfile.Id : null,
                            crossConnection ? destinationProfile.Name : null));
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
                    var resolved = await ResolveObjectConflictAsync(destinationProfile, destinationBucket, target, conflictPolicy);
                    if (resolved is null) { skipped++; continue; }
                    chunk.Add(new ObjectTransferBatchItem(
                        entry.Key, destinationBucket, resolved.Value.Key, topName, entry.Size, resolved.Value.Policy,
                        crossConnection ? destinationProfile.Id : null,
                        crossConnection ? destinationProfile.Name : null));
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
            if (!_transfers.IsBatchDiscoveryStopped(batch.Id))
            {
                try { await _transferQueue.CancelBatchAsync(batch.Id); }
                catch (KeyNotFoundException) when (_transfers.IsBatchDiscoveryStopped(batch.Id)) { }
            }
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

    private async Task<(string Key, ObjectConflictPolicy Policy)?> ResolveObjectConflictAsync(
        ConnectionProfile profile, string bucket, string key, ObjectConflictPolicy policy)
    {
        if (policy == ObjectConflictPolicy.Overwrite) return (key, policy);
        if (!await _storage.ObjectExistsAsync(profile, bucket, key, CancellationToken.None))
            return (key, policy == ObjectConflictPolicy.Ask ? ObjectConflictPolicy.Skip : policy);
        if (policy == ObjectConflictPolicy.Skip) return null;
        if (policy == ObjectConflictPolicy.Ask)
        {
            var answer = MessageBox.Show(this,
                $"目标对象已存在：\n\ns3://{bucket}/{key}\n\n是：覆盖　否：跳过　取消：停止整个批次",
                "对象冲突", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            return answer switch
            {
                DialogResult.Yes => (key, ObjectConflictPolicy.Overwrite),
                DialogResult.No => null,
                _ => throw new OperationCanceledException()
            };
        }
        for (var sequence = 2; sequence <= 10_000; sequence++)
        {
            var candidate = ObjectTransferPlanner.GetAutoRenameCandidate(key, sequence);
            if (!await _storage.ObjectExistsAsync(profile, bucket, candidate, CancellationToken.None))
                return (candidate, ObjectConflictPolicy.AutoRename);
        }
        throw new InvalidOperationException("无法为目标对象生成不冲突的名称。");
    }

    private bool EnsureClipboardProfile(bool showMessage = true)
    {
        var valid = OwnsObjectClipboard() && _objectClipboard is not null &&
            _currentProfile is not null && _currentBucket is not null &&
            (!_objectClipboard.Move || _currentProfile.Id == _objectClipboard.ProfileId);
        if (!valid && showMessage)
            MessageBox.Show(this, "对象剪贴板为空，或剪切来源不属于当前连接。跨连接请使用复制。", "对象剪贴板", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        var originalName = S3Path.LeafName(entry.Key, false);
        var name = PromptDialog.Show(this, "重命名对象", "新名称：", originalName);
        if (string.IsNullOrEmpty(name) || name == originalName) return;
        var targetKey = S3Path.RenameLeaf(entry.Key, name);
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        if (!S3ProviderCapabilityRegistry.For(profile.ServiceType).ConditionalDelete.Supported &&
            MessageBox.Show(this,
                S3ProviderCapabilityRegistry.For(profile.ServiceType).ConditionalDelete.Reason +
                "\n\n确认已停止并发写入，并继续重命名？", "重命名对象", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            SetBusy("正在重命名（Copy + Delete）...");
            await RenameObjectSafelyAsync(_storage, profile, bucket, entry.Key, targetKey, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "重命名失败", "Copy + Delete", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    internal static async Task RenameObjectSafelyAsync(
        IS3StorageService storage, ConnectionProfile profile, string bucket,
        string sourceKey, string targetKey, CancellationToken cancellationToken)
    {
        if (await storage.ObjectExistsAsync(profile, bucket, targetKey, cancellationToken))
            throw new IOException("目标对象已存在；请选择其他名称，重命名不会覆盖已有对象。");
        var context = new TransferOperationContext(
            new TransferExecutionOptions { AllowDestinationOverwrite = false, RetainFailedTransferState = false },
            new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
        // Use atomic destination conditions when supported. Compatible providers
        // recheck existence before writing; their concurrency limits are disclosed above.
        await storage.MoveObjectAsync(profile, bucket, sourceKey, bucket, targetKey, context, cancellationToken);
    }

    private async Task ShowPropertiesAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory) return;
        var entry = selected[0];
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        ObjectProperties properties;
        try
        {
            SetBusy("正在读取对象属性...");
            properties = await _storage.GetObjectPropertiesAsync(
                profile,
                bucket,
                entry.Key,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "读取失败", "对象属性", exception, CurrentLocationText());
            return;
        }
        finally { SetIdle(); }

        // Do not reopen a stale selection after navigation while HEAD was in flight.
        if (_currentProfile?.Id != profile.Id || _currentBucket != bucket) return;
        IReadOnlyList<CdnResolvedTarget> cdnTargets = [];
        try
        {
            cdnTargets = CdnUrlMapper.ResolveAll(_cdnConfiguration, profile.Id, bucket, entry.Key);
        }
        catch (NotSupportedException exception)
        {
            // A nonportable CDN URL must not block ordinary object properties or S3 downloads.
            _requestStatus.Text = exception.Message;
        }
        using var dialog = new ObjectPropertiesDialog(
            properties,
            profile.Endpoint,
            cdnProfileName: cdnTargets.FirstOrDefault()?.Profile.Name,
            storage: _storage,
            profile: profile);
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
        // Keep the selected storage namespace stable across every awaited object.
        // Navigation may change the form fields while the batch is in progress.
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var capability = S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.MetadataRewrite;
        if (!capability.Supported)
        {
            MessageBox.Show(this, capability.Reason, "批量 Header / Metadata", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ObjectMetadataBatchDialog(selected.Length, profile.ServiceType);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(
                this,
                $"确定对连接“{profile.Name}”的 Bucket“{bucket}”中 {selected.Length:N0} 个对象逐个执行原地 Copy 吗？\r\n\r\n" +
                ObjectMetadataRewritePolicy.WarningFor(profile.ServiceType) + "\r\n失败对象单独报告，不会回滚已成功对象。",
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
                        profile, bucket, entry.Key, CancellationToken.None);
                    var headers = dialog.ApplyTo(current);
                    await _storage.ReplaceObjectMetadataAsync(
                        profile, current, headers, CancellationToken.None);
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
        if (_currentProfile?.Id == profile.Id && _currentBucket == bucket)
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
            new S3Location(_currentProfile!.Name, _currentBucket, entry.Key).ToString(),
            lifetime => _storage.CreatePresignedUrl(_currentProfile!, _currentBucket!, entry.Key, lifetime));
        dialog.ShowDialog(this);
        _logger.Info($"Presigned URL generated bucket={_currentBucket} key={entry.Key}");
    }

}
