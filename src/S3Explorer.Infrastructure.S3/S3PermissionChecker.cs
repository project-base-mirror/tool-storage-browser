using Amazon.S3;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class S3PermissionChecker(IS3StorageService storage) : IStoragePermissionChecker
{
    private const long MaximumReadProbeBytes = 64 * 1024;
    private static readonly TimeSpan ProbeCleanupTimeout = TimeSpan.FromSeconds(15);

    public async Task<PermissionCheckResult> CheckAsync(
        StoragePermissionCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Bucket);
        request.Profile.Validate();

        var prefix = NormalizePrefix(request.Prefix);
        var checks = new List<PermissionCheck>();
        PagedObjectResult? listing = null;
        try
        {
            listing = await storage.ListObjectsAsync(
                request.Profile,
                request.Bucket.Trim(),
                prefix,
                null,
                100,
                cancellationToken).ConfigureAwait(false);
            checks.Add(Passed("storage", "ListBucket", "目标 Bucket/Prefix 可列举。"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(FromException("storage", "ListBucket", exception));
        }

        if (request.Operation.HasFlag(StoragePermissionOperation.Read))
        {
            var candidate = listing?.Items.FirstOrDefault(value => !value.IsDirectory);
            if (candidate is null)
            {
                checks.Add(new PermissionCheck(
                    "storage",
                    "HeadObject",
                    PermissionCheckState.Indeterminate,
                    "目标 Prefix 中没有可用于只读验证的对象。"));
            }
            else
            {
                try
                {
                    await storage.GetObjectPropertiesAsync(
                        request.Profile,
                        request.Bucket.Trim(),
                        candidate.Key,
                        cancellationToken).ConfigureAwait(false);
                    checks.Add(Passed("storage", "HeadObject", "已读取现有对象属性；这不会证明对象内容 GET 权限。"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    checks.Add(FromException("storage", "HeadObject", exception));
                }
            }


            var contentCandidate = listing?.Items.FirstOrDefault(value =>
                !value.IsDirectory && value.Size >= 0 && value.Size <= MaximumReadProbeBytes);
            if (contentCandidate is null)
            {
                checks.Add(new PermissionCheck(
                    "storage",
                    "GetObject",
                    PermissionCheckState.Indeterminate,
                    $"前 100 个对象中没有不超过 {MaximumReadProbeBytes / 1024} KiB 的文件；为避免下载大文件，未执行内容读取。"));
            }
            else
            {
                await CheckBoundedObjectReadAsync(
                    request,
                    contentCandidate,
                    checks,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var requiresWrite = request.Operation.HasFlag(StoragePermissionOperation.Publish) ||
            request.Operation.HasFlag(StoragePermissionOperation.Mirror) ||
            request.Operation.HasFlag(StoragePermissionOperation.PutObjectAcl);
        if (requiresWrite)
        {
            if (!request.AllowMutation)
            {
                checks.Add(new PermissionCheck(
                    "storage",
                    "PutObject",
                    PermissionCheckState.Indeterminate,
                    "写入权限需要显式允许一次可清理的远端探针。"));
                if (request.Operation.HasFlag(StoragePermissionOperation.Mirror))
                    checks.Add(new PermissionCheck(
                        "storage",
                        "DeleteObject",
                        PermissionCheckState.Indeterminate,
                        "删除权限需要显式允许一次可清理的远端探针。"));
                if (request.Operation.HasFlag(StoragePermissionOperation.PutObjectAcl))
                    checks.Add(new PermissionCheck(
                        "storage",
                        "PutObjectAcl",
                        PermissionCheckState.Indeterminate,
                        "ACL 权限需要显式允许一次可清理的远端探针。"));
            }
            else
            {
                await RunMutationProbeAsync(request, prefix, checks, cancellationToken).ConfigureAwait(false);
            }
        }

        return new PermissionCheckResult(request.Profile.CredentialId ?? Guid.Empty, checks)
        {
            TargetScope = $"s3://{request.Profile.Name}/{request.Bucket.Trim()}/{prefix}",
            CheckedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task CheckBoundedObjectReadAsync(
        StoragePermissionCheckRequest request,
        S3ObjectEntry candidate,
        ICollection<PermissionCheck> checks,
        CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(
            Path.GetTempPath(),
            "s3explorer-read-permission-" + Guid.NewGuid().ToString("N") + ".probe");
        var temporaryPath = ResumableDownloadFile.TemporaryPath(localPath);
        try
        {
            await storage.DownloadFileAsync(
                request.Profile,
                request.Bucket.Trim(),
                candidate.Key,
                localPath,
                CreateTransferContext(),
                cancellationToken).ConfigureAwait(false);
            checks.Add(Passed(
                "storage",
                "GetObject",
                $"已读取小文件内容（{candidate.Size} bytes），确认对象内容 GET 权限。"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(FromException("storage", "GetObject", exception));
        }
        finally
        {
            TryDelete(localPath);
            TryDelete(temporaryPath);
        }
    }

    private async Task RunMutationProbeAsync(
        StoragePermissionCheckRequest request,
        string prefix,
        ICollection<PermissionCheck> checks,
        CancellationToken cancellationToken)
    {
        var key = prefix + ".s3explorer-permission-probe/" + Guid.NewGuid().ToString("N") + ".txt";
        var localPath = Path.Combine(Path.GetTempPath(), "s3explorer-permission-" + Guid.NewGuid().ToString("N") + ".txt");
        var uploadStarted = false;
        var cleanupRequired = false;
        try
        {
            await File.WriteAllTextAsync(localPath, "S3 Explorer permission probe", cancellationToken).ConfigureAwait(false);
            try
            {
                uploadStarted = true;
                await storage.UploadFileAsync(
                    request.Profile,
                    request.Bucket.Trim(),
                    key,
                    localPath,
                    string.Empty,
                    CreateTransferContext(),
                    cancellationToken).ConfigureAwait(false);
                cleanupRequired = true;
                checks.Add(Passed("storage", "PutObject", "远端探针写入成功。"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(FromException("storage", "PutObject", exception));
                return;
            }

            if (request.Operation.HasFlag(StoragePermissionOperation.PutObjectAcl))
            {
                try
                {
                    await storage.PutObjectAclAsync(
                        request.Profile,
                        request.Bucket.Trim(),
                        key,
                        ObjectAclMode.Private,
                        cancellationToken).ConfigureAwait(false);
                    checks.Add(Passed("storage", "PutObjectAcl", "探针对象 Private ACL 设置成功。"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    checks.Add(FromException("storage", "PutObjectAcl", exception));
                }
            }

        }
        catch (OperationCanceledException)
        {
            cleanupRequired = uploadStarted;
            checks.Add(new PermissionCheck(
                "storage",
                "PutObject",
                PermissionCheckState.Indeterminate,
                "权限探针被取消；远端写入结果可能未知。")
            {
                Required = false
            });
        }
        finally
        {
            try { if (File.Exists(localPath)) File.Delete(localPath); }
            catch { }
            if (cleanupRequired)
            {
                using var cleanupTimeout = new CancellationTokenSource(ProbeCleanupTimeout);
                try
                {
                    await storage.DeleteObjectsAsync(
                        request.Profile,
                        request.Bucket.Trim(),
                        [key],
                        cleanupTimeout.Token).ConfigureAwait(false);
                    checks.Add(Passed("storage", "DeleteObject", $"远端探针已清理：{key}"));
                }
                catch (Exception exception)
                {
                    checks.Add(FromException("storage", "DeleteObject", exception) with
                    {
                        Required = true,
                        Message = $"远端探针清理失败；对象可能仍保留在 s3://{request.Bucket.Trim()}/{key}。"
                    });
                }
            }
        }
    }

    private static PermissionCheck Passed(string subject, string name, string message) =>
        new(subject, name, PermissionCheckState.Passed, message);

    private static TransferOperationContext CreateTransferContext()
    {
        var limiter = new SharedTransferBandwidthLimiter();
        limiter.Configure(0, 0);
        return new TransferOperationContext(
            new TransferExecutionOptions { MaximumDownloadBytes = MaximumReadProbeBytes },
            limiter,
            null,
            null,
            _ => { },
            (_, _, _, _) => Task.CompletedTask);
    }

    private static PermissionCheck FromException(string subject, string name, Exception exception)
    {
        if (exception is AmazonS3Exception s3)
        {
            var denied = s3.StatusCode is System.Net.HttpStatusCode.Forbidden or
                System.Net.HttpStatusCode.Unauthorized ||
                string.Equals(s3.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase);
            return new PermissionCheck(
                subject,
                name,
                denied ? PermissionCheckState.Denied : PermissionCheckState.Indeterminate,
                denied ? "Provider 拒绝了该操作。" : "Provider 请求失败，无法确定权限。")
            {
                StatusCode = (int)s3.StatusCode,
                ProviderCode = s3.ErrorCode ?? string.Empty,
                RequestId = s3.RequestId ?? string.Empty
            };
        }

        return new PermissionCheck(
            subject,
            name,
            PermissionCheckState.Indeterminate,
            SensitiveDataRedactor.Redact(exception.Message));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static string NormalizePrefix(string? prefix)
    {
        var normalized = (prefix ?? string.Empty).Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? string.Empty : normalized + "/";
    }
}
