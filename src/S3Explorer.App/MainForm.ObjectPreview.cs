using System.Diagnostics;
using System.Security.Cryptography;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private async Task PreviewSelectedObjectAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory) return;
        var entry = selected[0];
        var maximumBytes = ObjectPreviewPolicy.MaximumBytes(entry.Key);
        if (maximumBytes == 0)
        {
            MessageBox.Show(this, "仅支持有限大小的文本、JSON、CSV、PNG/JPEG/BMP 预览；不执行 HTML/脚本，也不预览 SVG、GIF、Parquet 或可执行文件。", "不支持此预览类型");
            return;
        }
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var root = Path.Combine(Path.GetTempPath(), "S3Explorer-preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var downloaded = Path.Combine(root, "object.bin");
        var editPath = Path.Combine(root, "edit.txt");
        ObjectProperties? original = null;
        byte[]? originalBytes = null;
        byte[]? uploadedBytes = null;
        ObjectPreviewDialog? dialog = null;
        try
        {
            dialog = new ObjectPreviewDialog(entry.Key, async token =>
            {
                original = await _storage.GetObjectPropertiesAsync(profile, bucket, entry.Key, token);
                if (original.Size > maximumBytes)
                    throw new InvalidOperationException($"对象为 {original.Size:N0} 字节，超过本类型 {maximumBytes:N0} 字节预览上限。");
                DownloadCheckpoint? saved = null;
                var transfer = PreviewTransfer(maximumBytes) with
                {
                    UpdateCheckpointAsync = (_, checkpoint, _, _) => { saved = checkpoint; return Task.CompletedTask; }
                };
                if (string.IsNullOrWhiteSpace(original.VersionId))
                    await _storage.DownloadFileAsync(profile, bucket, entry.Key, downloaded, transfer, token);
                else
                    await _storage.DownloadObjectVersionAsync(profile, bucket, entry.Key, original.VersionId, downloaded, transfer, token);
                if (saved is not null && !string.Equals(saved.ETag, original.ETag, StringComparison.Ordinal))
                    throw new InvalidOperationException("读取期间远端对象发生变化，请关闭后重新预览。");
                originalBytes = await File.ReadAllBytesAsync(downloaded, token);
                if (originalBytes.LongLength > maximumBytes)
                    throw new InvalidOperationException("预览实际下载大小超过限制。");
                var protectionReason = ObjectPreviewPolicy.EditingReadOnlyReason(profile.ServiceType, original);
                var canEdit = protectionReason is null && ObjectPreviewPolicy.IsText(entry.Key) &&
                    profile.ServiceType is S3ServiceType.AmazonS3 or S3ServiceType.MinIO or S3ServiceType.CloudflareR2 &&
                    saved is not null && !string.IsNullOrWhiteSpace(original.ETag) && string.IsNullOrWhiteSpace(original.ContentEncoding);
                var explanation = ObjectPreviewPolicy.IsImage(entry.Key)
                    ? "图片仅预览，最多 1600 万像素。"
                    : $"{(Path.GetExtension(entry.Key).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV 仅显示前 200 行 / 50 列；编辑使用完整原文件。 " : "")}" +
                      (canEdit ? "外部编辑后须确认上传；上传使用远端 ETag 条件，ETag 相同的更改无法原子区分。" : protectionReason ?? "此服务商/编码/对象身份不满足安全回传条件，仅可预览。");
                return new ObjectPreviewContent(entry.Key, originalBytes, canEdit, explanation);
            }, async token =>
            {
                if (original is null || originalBytes is null) return;
                if (ObjectPreviewPolicy.EditingReadOnlyReason(profile.ServiceType, original) is { } reason)
                    throw new InvalidOperationException(reason);
                // Preserve modeled content headers, user metadata and tags; this is not an ACL/Object Lock editor.
                var tags = S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging.Supported
                    ? await _storage.GetObjectTagsAsync(profile, bucket, entry.Key, original.VersionId, token)
                    : Array.Empty<ObjectTag>();
                var headers = new ObjectWriteHeaders(original.ContentType, original.CacheControl, original.ContentEncoding,
                    original.ContentDisposition, original.ExpiresUtc, original.Metadata, tags);
                ObjectPreviewPolicy.DecodeText(originalBytes);
                await File.WriteAllBytesAsync(editPath, originalBytes, token);
                if (dialog is not null) dialog.EditedFilePath = editPath;
                using var editor = Process.Start(ObjectPreviewPolicy.EditorStartInfo(editPath))
                    ?? throw new InvalidOperationException("无法启动 Windows 记事本。");
                // Modern Notepad may hand the document to an existing process and exit its launcher.
                // Require an explicit user hand-off as well as waiting for this launched process.
                MessageBox.Show(dialog, "请在记事本中保存并关闭此文件，然后点击确定检查更改。此步骤不会上传。", "完成编辑后继续", MessageBoxButtons.OK, MessageBoxIcon.Information);
                // Never delete the temporary file while the explicitly opened editor is still running.
                await editor.WaitForExitAsync(CancellationToken.None);
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(editPath);
                if (info.Length > ObjectPreviewPolicy.TextLimit)
                    throw new InvalidOperationException("编辑后的文件超过 2 MiB 上限，不会上传。");
                var edited = await File.ReadAllBytesAsync(editPath, token);
                ObjectPreviewPolicy.DecodeText(edited);
                if (SHA256.HashData(originalBytes).AsSpan().SequenceEqual(SHA256.HashData(edited))) return;
                if (dialog is null || dialog.IsDisposed || MessageBox.Show(dialog,
                        $"将编辑后的文件上传并替换 {bucket}/{entry.Key}？\n\n仅当远端 ETag 仍匹配时写入，不会自动重试无条件覆盖。\n\n这是替换写入：保留 Content-Type、Cache-Control、Content-Disposition、Expires、用户元数据和标签；已报告的 AES256 加密会显式保留；KMS、客户密钥和对象锁保护不在此流程中编辑。ACL 等其他未列出的独立配置按服务端写入规则处理，不承诺原样保留。",
                        "确认上传编辑内容", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                token.ThrowIfCancellationRequested();
                var current = await _storage.GetObjectPropertiesAsync(profile, bucket, entry.Key, token);
                EnsurePreviewIdentityUnchanged(original, current);
                if (S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging.Supported)
                {
                    var currentTags = await _storage.GetObjectTagsAsync(profile, bucket, entry.Key, current.VersionId, token);
                    if (!tags.OrderBy(tag => tag.Key, StringComparer.Ordinal).SequenceEqual(currentTags.OrderBy(tag => tag.Key, StringComparer.Ordinal)))
                        throw new InvalidOperationException("远端对象标签已变化，不会覆盖；请重新预览。");
                }
                // Stable upload snapshot: do not race subsequent saves to the editor's path.
                var uploadPath = Path.Combine(root, "confirmed-upload.txt");
                await File.WriteAllBytesAsync(uploadPath, edited, token);
                var transfer = PreviewTransfer(ObjectPreviewPolicy.TextLimit) with
                {
                    Options = new TransferExecutionOptions
                    {
                        MaximumDownloadBytes = ObjectPreviewPolicy.TextLimit,
                        ExpectedDestinationETag = original.ETag,
                        RequireSseS3Encryption = ObjectPreviewPolicy.RequiresSseS3Encryption(original),
                        AllowDestinationOverwrite = true
                    }
                };
                await _storage.UploadFileAsync(profile, bucket, entry.Key, uploadPath,
                    ObjectPreviewPolicy.ExistingStorageClass(original.StorageClass), headers, transfer, token);
                uploadedBytes = edited;
                MessageBox.Show(dialog, "编辑内容已通过条件写入上传。", "上传完成");
            });
            using (dialog)
            {
                dialog.ShowDialog(this);
                await dialog.PendingOperation;
            }
        }
        finally
        {
            var retainEdits = ObjectPreviewPolicy.ShouldRetainEditedFile(editPath, uploadedBytes ?? originalBytes);
            try
            {
                if (!retainEdits) Directory.Delete(root, recursive: true);
                else
                {
                    // Keep the user's edited document, not redundant downloaded/upload snapshots.
                    File.Delete(downloaded);
                    File.Delete(ResumableDownloadFile.TemporaryPath(downloaded));
                    File.Delete(Path.Combine(root, "confirmed-upload.txt"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Error("Preview temporary file cleanup failed", exception);
            }
            if (retainEdits && !IsDisposed)
                MessageBox.Show(this, $"本地编辑内容尚未确认完整回传（可能已取消、发生冲突或上传结果不明确），已保留，请另存或核对：\n\n{editPath}",
                    "已保留本地编辑文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static void EnsurePreviewIdentityUnchanged(ObjectProperties original, ObjectProperties current)
    {
        if (ObjectPreviewPolicy.ProtectedObjectReadOnlyReason(original) is { } originalProtection)
            throw new InvalidOperationException(originalProtection);
        if (ObjectPreviewPolicy.ProtectedObjectReadOnlyReason(current) is { } currentProtection)
            throw new InvalidOperationException(currentProtection);
        if (string.IsNullOrWhiteSpace(original.ETag) ||
            !string.Equals(original.ETag, current.ETag, StringComparison.Ordinal) ||
            !string.Equals(original.VersionId, current.VersionId, StringComparison.Ordinal) ||
            original.Size != current.Size || original.LastModified != current.LastModified ||
            original.ContentType != current.ContentType || original.CacheControl != current.CacheControl ||
            original.ContentEncoding != current.ContentEncoding || original.ContentDisposition != current.ContentDisposition ||
            original.ExpiresUtc != current.ExpiresUtc ||
            original.ServerSideEncryptionMethod != current.ServerSideEncryptionMethod ||
            original.EncryptionKeyId != current.EncryptionKeyId || original.CustomerEncryptionMethod != current.CustomerEncryptionMethod ||
            original.ObjectLockMode != current.ObjectLockMode || original.ObjectLockLegalHoldStatus != current.ObjectLockLegalHoldStatus ||
            ObjectPreviewPolicy.ExistingStorageClass(original.StorageClass) != ObjectPreviewPolicy.ExistingStorageClass(current.StorageClass) ||
            original.Metadata.Count != current.Metadata.Count || original.Metadata.Any(pair =>
                !current.Metadata.Any(other => string.Equals(pair.Key, other.Key, StringComparison.OrdinalIgnoreCase) && pair.Value == other.Value)))
            throw new InvalidOperationException("远端对象已变化，不会上传覆盖；请重新预览后决定。");
    }

    private static TransferOperationContext PreviewTransfer(long maximumBytes) => new(
        new TransferExecutionOptions { MaximumDownloadBytes = maximumBytes },
        new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
}
