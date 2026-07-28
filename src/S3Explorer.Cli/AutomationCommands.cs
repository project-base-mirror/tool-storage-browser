using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Contracts;
using S3Explorer.Core;

namespace S3Explorer.Cli;

internal sealed record AutomationCommandResult(int ExitCode, object Data, string Text);

internal static class AutomationCommands
{
    private const int OperationFailed = 4;
    private const string DefaultManifestName = "publish-manifest.json";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<AutomationCommandResult> RunAsync(
        string command,
        string verb,
        CliArguments args,
        IProfileStore profileStore,
        IS3StorageService storage,
        ICdnConfigurationStore cdnConfigurationStore,
        ICdnCredentialStore cdnCredentialStore,
        ICdnDeliveryService cdnDeliveryService,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        return command switch
        {
            "upload" => await RunUploadAsync(args, profileStore, storage, cancellationToken),
            "publish" => await RunPublishAsync(
                args, profileStore, storage, cdnConfigurationStore, cdnCredentialStore,
                cdnDeliveryService, jsonOutput, cancellationToken),
            "verify" => await RunVerifyAsync(args, profileStore, storage, cancellationToken),
            "cdn" => await RunCdnAsync(
                verb, args, cdnConfigurationStore, cdnCredentialStore,
                cdnDeliveryService, cancellationToken),
            _ => throw new CliUsageException($"未知自动化命令：{command}")
        };
    }

    private static async Task<AutomationCommandResult> RunUploadAsync(
        CliArguments args,
        IProfileStore profileStore,
        IS3StorageService storage,
        CancellationToken cancellationToken)
    {
        var profile = ResolveProfile(
            await profileStore.LoadAsync(cancellationToken),
            args.Require("profile"));
        var source = Path.GetFullPath(args.Require("source"));
        var bucket = ResolveBucket(profile, args.Optional("bucket"));
        var prefix = NormalizePrefix(args.Optional("prefix"));
        var files = EnumerateSourceFiles(source);
        long bytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = File.Exists(source)
                ? Path.GetFileName(source)
                : PublishManifestUtility.NormalizeRelativePath(Path.GetRelativePath(source, file));
            var key = CombineKey(prefix, relative);
            await storage.UploadFileAsync(
                profile, bucket, key, file, string.Empty,
                NewTransferContext(), cancellationToken);
            bytes += new FileInfo(file).Length;
        }

        var data = new
        {
            success = true,
            profile = profile.Name,
            bucket,
            prefix,
            uploadedFiles = files.Count,
            uploadedBytes = bytes,
            remoteUri = BuildRemoteUri(profile, bucket, prefix)
        };
        return new AutomationCommandResult(
            0, data,
            $"上传完成：{files.Count:N0} 个文件，{FileSizeFormatter.Format(bytes)}，目标 {data.remoteUri}");
    }

    private static async Task<AutomationCommandResult> RunPublishAsync(
        CliArguments args,
        IProfileStore profileStore,
        IS3StorageService storage,
        ICdnConfigurationStore cdnConfigurationStore,
        ICdnCredentialStore cdnCredentialStore,
        ICdnDeliveryService cdnDeliveryService,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var profile = ResolveProfile(
            await profileStore.LoadAsync(cancellationToken),
            args.Require("profile"));
        var source = Path.GetFullPath(args.Require("source"));
        if (!Directory.Exists(source))
            throw new CliUsageException($"发布源目录不存在：{source}");
        var bucket = ResolveBucket(profile, args.Optional("bucket"));
        var project = args.Optional("project")?.Trim() ?? string.Empty;
        var product = args.Optional("product")?.Trim() ?? string.Empty;
        var version = args.Optional("version")?.Trim() ?? string.Empty;
        var prefix = NormalizePrefix(args.Optional("prefix"));
        if (prefix.Length == 0)
        {
            if (project.Length == 0 || product.Length == 0 || version.Length == 0)
                throw new CliUsageException(
                    "publish 需要 --prefix，或同时提供 --project、--product、--version。");
            prefix = NormalizePrefix($"{project}/{product}/{version}");
        }
        var deleteMode = args.Optional("delete-mode")?.Trim().ToLowerInvariant() ?? "none";
        if (deleteMode != "none")
            throw new CliUsageException("第一阶段仅支持 --delete-mode none，不会删除远程对象。");

        var manifestPath = Path.GetFullPath(args.Optional("manifest") ?? Path.Combine(source, DefaultManifestName));
        var localFiles = await PublishManifestUtility.ScanAsync(source, manifestPath, cancellationToken);
        var remoteManifest = args.Flag("full")
            ? null
            : await TryDownloadManifestAsync(
                storage, profile, bucket, CombineKey(prefix, DefaultManifestName), cancellationToken);
        var plan = PublishManifestUtility.CreatePlan(
            localFiles.Select(value => value.Entry).ToArray(), remoteManifest);

        if (args.Flag("dry-run"))
        {
            var preview = new
            {
                success = true,
                profile = profile.Name,
                bucket,
                prefix,
                remoteUri = BuildRemoteUri(profile, bucket, prefix),
                plan
            };
            return new AutomationCommandResult(
                0, preview,
                $"发布预览：新增 {plan.NewFiles:N0}，修改 {plan.ModifiedFiles:N0}，" +
                $"跳过 {plan.UnchangedFiles:N0}，待上传 {FileSizeFormatter.Format(plan.UploadBytes)}");
        }

        ConfirmPublish(args, jsonOutput, plan, bucket, prefix);

        var failures = new List<OperationFailure>();
        var uploadedFiles = 0;
        long uploadedBytes = 0;
        var changed = plan.Items
            .Where(value => value.Change != PublishChangeKind.Unchanged)
            .ToDictionary(value => value.Path, StringComparer.Ordinal);
        foreach (var file in localFiles.Where(value => changed.ContainsKey(value.Entry.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = CombineKey(prefix, file.Entry.Path);
                await storage.UploadFileAsync(
                    profile, bucket, key, file.FullPath, string.Empty,
                    NewTransferContext(), cancellationToken);
                await VerifyRemoteFileAsync(storage, profile, bucket, key, file.Entry, cancellationToken);
                uploadedFiles++;
                uploadedBytes += file.Entry.Size;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures.Add(new OperationFailure
                {
                    Path = file.Entry.Path,
                    Message = SensitiveDataRedactor.Redact(exception.Message)
                });
            }
        }

        var manifest = new PublishManifest
        {
            Project = project,
            Product = product,
            Version = version,
            Profile = profile.Name,
            Bucket = bucket,
            Prefix = prefix,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Files = localFiles.Select(value => value.Entry).OrderBy(value => value.Path, StringComparer.Ordinal).ToList()
        };
        PublishManifestUtility.ValidateManifest(manifest);

        var manifestPublished = false;
        if (failures.Count == 0)
        {
            try
            {
                await WriteManifestAsync(manifestPath, manifest, cancellationToken);
                var manifestKey = CombineKey(prefix, DefaultManifestName);
                await storage.UploadFileAsync(
                    profile, bucket, manifestKey, manifestPath, string.Empty,
                    NewTransferContext(), cancellationToken);
                var manifestEntry = new PublishManifestFile
                {
                    Path = DefaultManifestName,
                    Size = new FileInfo(manifestPath).Length,
                    Sha256 = await PublishManifestUtility.ComputeSha256Async(manifestPath, cancellationToken)
                };
                await VerifyRemoteFileAsync(
                    storage, profile, bucket, manifestKey, manifestEntry, cancellationToken);
                manifestPublished = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures.Add(new OperationFailure
                {
                    Path = DefaultManifestName,
                    Message = SensitiveDataRedactor.Redact(exception.Message)
                });
            }
        }

        string cdnUrl = string.Empty;
        if (failures.Count == 0 && args.Optional("cdn-profile") is { Length: > 0 } cdnProfileName)
        {
            var (cdnProfile, credential) = await ResolveCdnAsync(
                cdnProfileName, cdnConfigurationStore, cdnCredentialStore, cancellationToken);
            cdnUrl = PublishManifestUtility.BuildCdnUri(cdnProfile, prefix + "/").AbsoluteUri;
            if (args.Flag("warmup"))
            {
                var warmupPaths = changed.Keys.Append(DefaultManifestName)
                    .Select(path => CombineKey(prefix, path))
                    .ToArray();
                var cdnResult = await ExecuteCdnAsync(
                    "warmup", cdnProfile, credential, warmupPaths, cdnDeliveryService, cancellationToken);
                foreach (var item in cdnResult.Items.Where(value => !value.Success))
                    failures.Add(new OperationFailure { Path = item.Path, Message = item.Message });
            }
        }

        stopwatch.Stop();
        var result = new PublishResult
        {
            Success = failures.Count == 0,
            Profile = profile.Name,
            Bucket = bucket,
            Prefix = prefix,
            UploadedFiles = uploadedFiles,
            SkippedFiles = plan.UnchangedFiles,
            FailedFiles = failures.Count,
            UploadedBytes = uploadedBytes,
            RemoteUri = BuildRemoteUri(profile, bucket, prefix),
            CdnUrl = cdnUrl,
            ManifestPath = manifestPath,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Failures = failures
        };
        var text = result.Success
            ? $"发布成功：上传 {result.UploadedFiles:N0}，跳过 {result.SkippedFiles:N0}，" +
              $"失败 0，耗时 {stopwatch.Elapsed.TotalSeconds:N1} 秒。\n远程目录：{result.RemoteUri}" +
              (cdnUrl.Length > 0 ? $"\nCDN URL：{cdnUrl}" : string.Empty)
            : manifestPublished
                ? $"资源与 Manifest 已发布，但后处理失败：上传 {result.UploadedFiles:N0}，" +
                  $"跳过 {result.SkippedFiles:N0}，失败 {result.FailedFiles:N0}。"
                : $"发布未完成：上传 {result.UploadedFiles:N0}，跳过 {result.SkippedFiles:N0}，" +
                  $"失败 {result.FailedFiles:N0}。Manifest 未发布。";
        return new AutomationCommandResult(result.Success ? 0 : OperationFailed, result, text);
    }

    private static async Task<AutomationCommandResult> RunVerifyAsync(
        CliArguments args,
        IProfileStore profileStore,
        IS3StorageService storage,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var manifestPath = Path.GetFullPath(args.Require("manifest"));
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        var profileName = args.Optional("profile") ?? manifest.Profile;
        if (string.IsNullOrWhiteSpace(profileName))
            throw new CliUsageException("verify 需要 --profile，或 Manifest 中必须包含 profile。");
        var profile = ResolveProfile(await profileStore.LoadAsync(cancellationToken), profileName);
        var bucket = ResolveBucket(profile, args.Optional("bucket") ?? manifest.Bucket);
        var prefix = NormalizePrefix(args.Optional("prefix") ?? manifest.Prefix);
        var failures = new List<OperationFailure>();
        var verifiedFiles = 0;
        long verifiedBytes = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await VerifyRemoteFileAsync(
                    storage, profile, bucket, CombineKey(prefix, file.Path), file, cancellationToken);
                verifiedFiles++;
                verifiedBytes += file.Size;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures.Add(new OperationFailure
                {
                    Path = file.Path,
                    Message = SensitiveDataRedactor.Redact(exception.Message)
                });
            }
        }

        stopwatch.Stop();
        var result = new VerifyResult
        {
            Success = failures.Count == 0,
            Profile = profile.Name,
            Bucket = bucket,
            Prefix = prefix,
            VerifiedFiles = verifiedFiles,
            FailedFiles = failures.Count,
            VerifiedBytes = verifiedBytes,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Failures = failures
        };
        return new AutomationCommandResult(
            result.Success ? 0 : OperationFailed,
            result,
            result.Success
                ? $"验证成功：{verifiedFiles:N0} 个文件，{FileSizeFormatter.Format(verifiedBytes)}。"
                : $"验证失败：通过 {verifiedFiles:N0}，失败 {failures.Count:N0}。");
    }

    private static async Task<AutomationCommandResult> RunCdnAsync(
        string verb,
        CliArguments args,
        ICdnConfigurationStore configurationStore,
        ICdnCredentialStore credentialStore,
        ICdnDeliveryService deliveryService,
        CancellationToken cancellationToken)
    {
        if (verb is not ("test" or "warmup"))
            throw new CliUsageException("用法：cdn test|warmup --profile <name-or-id> --path <path> [--manifest <file>]");
        var (profile, credential) = await ResolveCdnAsync(
            args.Require("profile"), configurationStore, credentialStore, cancellationToken);
        var paths = new List<string>();
        if (args.Optional("path") is { Length: > 0 } singlePath)
            paths.Add(singlePath);
        if (args.Optional("manifest") is { Length: > 0 } manifestPath)
        {
            var manifest = await ReadManifestAsync(Path.GetFullPath(manifestPath), cancellationToken);
            var prefix = NormalizePrefix(args.Optional("prefix") ?? manifest.Prefix);
            paths.AddRange(manifest.Files.Select(value => CombineKey(prefix, value.Path)));
            if (args.Flag("include-manifest"))
                paths.Add(CombineKey(prefix, DefaultManifestName));
        }
        if (paths.Count == 0)
            throw new CliUsageException("cdn test/warmup 需要 --path 或 --manifest。");
        var result = await ExecuteCdnAsync(
            verb, profile, credential, paths.Distinct(StringComparer.Ordinal).ToArray(),
            deliveryService, cancellationToken);
        return new AutomationCommandResult(
            result.Success ? 0 : OperationFailed,
            result,
            result.Success
                ? $"CDN {verb} 完成：成功 {result.Succeeded:N0}，失败 0。"
                : $"CDN {verb} 完成：成功 {result.Succeeded:N0}，失败 {result.Failed:N0}。");
    }

    private static async Task<CdnBatchResult> ExecuteCdnAsync(
        string verb,
        CdnProfile profile,
        CdnCredential? credential,
        IReadOnlyCollection<string> paths,
        ICdnDeliveryService deliveryService,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var items = new List<CdnItemResult>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = PublishManifestUtility.BuildCdnUri(profile, path);
            try
            {
                if (verb == "test")
                {
                    var probe = await deliveryService.ProbeAsync(
                        profile, credential, url, profile.WarmupRangeBytes, cancellationToken);
                    items.Add(new CdnItemResult
                    {
                        Path = path,
                        Url = probe.FinalUrl.AbsoluteUri,
                        Success = probe.Success,
                        StatusCode = probe.StatusCode,
                        BytesRead = probe.BytesRead,
                        ElapsedMilliseconds = (long)probe.TotalElapsed.TotalMilliseconds,
                        Message = probe.Success
                            ? $"HTTP {probe.StatusCode}，缓存 {probe.CacheStatus}"
                            : $"HTTP {probe.StatusCode} {probe.ReasonPhrase}"
                    });
                }
                else
                {
                    var warmup = await deliveryService.WarmupAsync(
                        profile, credential, url, cancellationToken);
                    items.Add(new CdnItemResult
                    {
                        Path = path,
                        Url = url.AbsoluteUri,
                        Success = warmup.Success,
                        StatusCode = warmup.StatusCode,
                        BytesRead = warmup.BytesRead,
                        ElapsedMilliseconds = (long)warmup.Elapsed.TotalMilliseconds,
                        Message = warmup.Message
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                items.Add(new CdnItemResult
                {
                    Path = path,
                    Url = url.AbsoluteUri,
                    Success = false,
                    Message = SensitiveDataRedactor.Redact(exception.Message)
                });
            }
        }
        stopwatch.Stop();
        return new CdnBatchResult
        {
            Success = items.All(value => value.Success),
            Profile = profile.Name,
            Succeeded = items.Count(value => value.Success),
            Failed = items.Count(value => !value.Success),
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Items = items
        };
    }

    private static async Task<(CdnProfile Profile, CdnCredential? Credential)> ResolveCdnAsync(
        string nameOrId,
        ICdnConfigurationStore configurationStore,
        ICdnCredentialStore credentialStore,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken);
        var credentials = await credentialStore.LoadAsync(cancellationToken);
        var profile = Guid.TryParse(nameOrId, out var id)
            ? configuration.Profiles.FirstOrDefault(value => value.Id == id)
            : configuration.Profiles.FirstOrDefault(value =>
                string.Equals(value.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        if (profile is null) throw new CliNotFoundException($"找不到 CDN 配置：{nameOrId}");
        if (!profile.Enabled) throw new CliUsageException($"CDN 配置已禁用：{profile.Name}");
        var credential = profile.CredentialId is Guid credentialId
            ? credentials.FirstOrDefault(value => value.Id == credentialId)
              ?? throw new CliNotFoundException($"CDN 配置“{profile.Name}”引用的凭据不存在。")
            : null;
        CdnConfigurationValidator.EnsureValid(configuration, credentials);
        return (profile, credential);
    }

    private static async Task<PublishManifest?> TryDownloadManifestAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        if (!await storage.ObjectExistsAsync(profile, bucket, key, cancellationToken))
            return null;
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"s3explorer-manifest-{Guid.NewGuid():N}.json");
        try
        {
            await storage.DownloadFileAsync(
                profile, bucket, key, temporaryPath, NewTransferContext(), cancellationToken);
            return await ReadManifestAsync(temporaryPath, cancellationToken);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<PublishManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到发布 Manifest。", path);
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<PublishManifest>(
            stream, ManifestJsonOptions, cancellationToken)
            ?? throw new InvalidDataException("发布 Manifest 为空。");
        PublishManifestUtility.ValidateManifest(manifest);
        return manifest;
    }

    private static async Task WriteManifestAsync(
        string path,
        PublishManifest manifest,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(
                stream, manifest, ManifestJsonOptions, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private static async Task VerifyRemoteFileAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        string bucket,
        string key,
        PublishManifestFile expected,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"s3explorer-verify-{Guid.NewGuid():N}.tmp");
        try
        {
            await storage.DownloadFileAsync(
                profile, bucket, key, temporaryPath, NewTransferContext(), cancellationToken);
            var info = new FileInfo(temporaryPath);
            if (info.Length != expected.Size)
                throw new InvalidDataException(
                    $"远程大小不匹配：预期 {expected.Size}，实际 {info.Length}。");
            var hash = await PublishManifestUtility.ComputeSha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"远程 SHA-256 不匹配：预期 {expected.Sha256}，实际 {hash}。");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static IReadOnlyList<string> EnumerateSourceFiles(string source)
    {
        if (File.Exists(source)) return [source];
        if (!Directory.Exists(source))
            throw new CliUsageException($"上传源路径不存在：{source}");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        return Directory.EnumerateFiles(source, "*", options)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ConfirmPublish(
        CliArguments args,
        bool jsonOutput,
        PublishPlan plan,
        string bucket,
        string prefix)
    {
        if (args.Flag("yes") || args.Flag("non-interactive") || jsonOutput) return;
        if (Console.IsInputRedirected)
            throw new CliUsageException("非交互发布需要 --non-interactive 或 --yes。");
        Console.Write(
            $"将上传 {plan.NewFiles + plan.ModifiedFiles:N0} 个文件到 s3://{bucket}/{prefix}/，" +
            $"共 {FileSizeFormatter.Format(plan.UploadBytes)}。继续？[y/N] ");
        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new OperationCanceledException();
    }

    private static ConnectionProfile ResolveProfile(
        IEnumerable<ConnectionProfile> profiles,
        string nameOrId)
    {
        var values = profiles.ToArray();
        if (Guid.TryParse(nameOrId, out var id))
            return values.FirstOrDefault(value => value.Id == id)
                   ?? throw new CliNotFoundException($"找不到连接：{nameOrId}");
        return values.FirstOrDefault(value =>
                   string.Equals(value.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? throw new CliNotFoundException($"找不到连接：{nameOrId}");
    }

    private static string ResolveBucket(ConnectionProfile profile, string? value)
    {
        var bucket = string.IsNullOrWhiteSpace(value) ? profile.DefaultBucket : value.Trim();
        if (string.IsNullOrWhiteSpace(bucket))
            throw new CliUsageException("缺少 --bucket，且连接没有默认 Bucket。");
        return bucket;
    }

    private static string NormalizePrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
            throw new CliUsageException($"远程前缀不安全：{value}");
        return normalized;
    }

    private static string CombineKey(string prefix, string relative)
    {
        var normalizedRelative = PublishManifestUtility.NormalizeRelativePath(relative);
        return prefix.Length == 0 ? normalizedRelative : $"{prefix}/{normalizedRelative}";
    }

    private static string BuildRemoteUri(ConnectionProfile profile, string bucket, string prefix) =>
        prefix.Length == 0
            ? $"s3://{profile.Name}/{bucket}/"
            : $"s3://{profile.Name}/{bucket}/{prefix}/";

    private static TransferOperationContext NewTransferContext()
    {
        var limiter = new SharedTransferBandwidthLimiter();
        limiter.Configure(0, 0);
        return new TransferOperationContext(
            new TransferExecutionOptions(), limiter, null, null, _ => { },
            (_, _, _, _) => Task.CompletedTask);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
