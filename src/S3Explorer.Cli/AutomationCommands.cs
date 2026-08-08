using System.Diagnostics;
using System.Collections.Concurrent;
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
        var transfer = CliTransferRuntime.Create(args);
        var profile = ResolveProfile(
            await profileStore.LoadAsync(cancellationToken),
            args.Require("profile"));
        var source = Path.GetFullPath(args.Require("source"));
        var bucket = ResolveBucket(profile, args.Optional("bucket"));
        var prefix = NormalizePrefix(args.Optional("prefix"));
        var headerRules = await LoadHeaderRulesAsync(args.Optional("header-rules"), cancellationToken);
        var files = EnumerateSourceFiles(source);
        long bytes = 0;
        var verify = args.Flag("verify");
        await transfer.ForEachAsync(files, async (file, token) =>
        {
            var relative = File.Exists(source)
                ? Path.GetFileName(source)
                : PublishManifestUtility.NormalizeRelativePath(Path.GetRelativePath(source, file));
            var key = CombineKey(prefix, relative);
            PublishManifestFile? expected = null;
            if (verify)
            {
                expected = new PublishManifestFile
                {
                    Path = relative,
                    Size = new FileInfo(file).Length,
                    Sha256 = await PublishManifestUtility.ComputeSha256Async(file, token)
                };
            }
            await storage.UploadFileAsync(
                profile, bucket, key, file, string.Empty,
                PublishHeaderRuleUtility.ToObjectWriteHeaders(
                    PublishHeaderRuleUtility.Resolve(headerRules, relative)),
                transfer.CreateContext(), token);
            if (expected is not null)
                await CliRemoteVerifier.VerifyAsync(
                    storage, profile, bucket, key, expected.Size, expected.Sha256, transfer, token);
            Interlocked.Add(ref bytes, new FileInfo(file).Length);
        }, cancellationToken);

        var data = new
        {
            success = true,
            profile = profile.Name,
            bucket,
            prefix,
            uploadedFiles = files.Count,
            uploadedBytes = bytes,
            verified = verify,
            transfer = transfer.Settings,
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
        var transfer = CliTransferRuntime.Create(args);
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
        var deleteMode = ParseDeleteMode(args.Optional("delete-mode"));
        var accessMode = ParseAccessMode(args.Optional("access"));
        if (accessMode != PublishAccessMode.Preserve && !args.Flag("dry-run") && !args.Flag("yes"))
            throw new CliUsageException("修改对象 ACL 必须显式提供 --yes；程序不会修改 Bucket Policy 或 Public Access Block。");

        var manifestPath = Path.GetFullPath(args.Optional("manifest") ?? Path.Combine(source, DefaultManifestName));
        var headerRules = await LoadHeaderRulesAsync(args.Optional("header-rules"), cancellationToken);
        var localFiles = await PublishManifestUtility.ScanAsync(
            source, manifestPath, headerRules, cancellationToken);
        var remoteManifest = args.Flag("full")
            ? null
            : await TryDownloadManifestAsync(
                storage, profile, bucket, CombineKey(prefix, DefaultManifestName), transfer, cancellationToken);
        var plan = PublishManifestUtility.CreatePlan(
            localFiles.Select(value => value.Entry).ToArray(), remoteManifest);
        IReadOnlyList<PublishMirrorDeleteCandidate> deletePlan = [];
        if (deleteMode == PublishDeleteMode.Mirror)
        {
            var rootPrefix = prefix + "/";
            var remoteObjects = await RecursiveObjectListing.ListFilesAsync(
                rootPrefix,
                ObjectListingLimits.DefaultPageSize,
                ObjectListingLimits.DefaultCacheLimit,
                (currentPrefix, token, ct) => storage.ListObjectsAsync(
                    profile,
                    bucket,
                    currentPrefix,
                    token,
                    ObjectListingLimits.DefaultPageSize,
                    ct),
                cancellationToken);
            deletePlan = PublishManifestUtility.CreateMirrorDeletePlan(
                localFiles.Select(value => value.Entry).ToArray(),
                remoteObjects,
                prefix,
                DefaultManifestName);
        }
        var deleteBytes = deletePlan.Sum(value => value.Size);

        if (args.Flag("dry-run"))
        {
            var preview = new
            {
                success = true,
                profile = profile.Name,
                bucket,
                prefix,
                remoteUri = BuildRemoteUri(profile, bucket, prefix),
                accessMode,
                deleteMode,
                aclTargets = accessMode == PublishAccessMode.Preserve ? 0 : localFiles.Count + 1,
                plan,
                deletePlan = new
                {
                    files = deletePlan.Count,
                    bytes = deleteBytes,
                    items = deletePlan
                }
            };
            return new AutomationCommandResult(
                0, preview,
                $"发布预览：新增 {plan.NewFiles:N0}，修改 {plan.ModifiedFiles:N0}，" +
                $"跳过 {plan.UnchangedFiles:N0}，待上传 {FileSizeFormatter.Format(plan.UploadBytes)}，" +
                $"待删除 {deletePlan.Count:N0} 个对象（{FileSizeFormatter.Format(deleteBytes)}）");
        }

        ConfirmPublish(args, jsonOutput, plan, deletePlan, bucket, prefix);

        var failures = new ConcurrentBag<OperationFailure>();
        var uploadedFiles = 0;
        var deletedFiles = 0;
        var aclUpdatedFiles = 0;
        long uploadedBytes = 0;
        long deletedBytes = 0;
        var changed = plan.Items
            .Where(value => value.Change != PublishChangeKind.Unchanged)
            .ToDictionary(value => value.Path, StringComparer.Ordinal);
        await transfer.ForEachAsync(
            localFiles.Where(value => changed.ContainsKey(value.Entry.Path)),
            async (file, token) =>
        {
            try
            {
                var key = CombineKey(prefix, file.Entry.Path);
                await storage.UploadFileAsync(
                    profile, bucket, key, file.FullPath, string.Empty,
                    PublishHeaderRuleUtility.ToObjectWriteHeaders(file.Entry.Headers),
                    transfer.CreateContext(), token);
                await CliRemoteVerifier.VerifyAsync(
                    storage, profile, bucket, key, file.Entry.Size, file.Entry.Sha256, transfer, token);
                Interlocked.Increment(ref uploadedFiles);
                Interlocked.Add(ref uploadedBytes, file.Entry.Size);
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
        }, cancellationToken);

        if (failures.Count == 0 && accessMode != PublishAccessMode.Preserve)
        {
            await transfer.ForEachAsync(localFiles, async (file, token) =>
            {
                try
                {
                    await storage.PutObjectAclAsync(
                        profile,
                        bucket,
                        CombineKey(prefix, file.Entry.Path),
                        ToObjectAclMode(accessMode),
                        token);
                    Interlocked.Increment(ref aclUpdatedFiles);
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
            }, cancellationToken);
        }

        if (failures.Count == 0 && deletePlan.Count > 0)
        {
            try
            {
                await storage.DeleteObjectsAsync(
                    profile,
                    bucket,
                    deletePlan.Select(value => value.Key).ToArray(),
                    cancellationToken);
                deletedFiles = deletePlan.Count;
                deletedBytes = deleteBytes;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures.Add(new OperationFailure
                {
                    Path = prefix + "/",
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
            AccessMode = accessMode,
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
                    new ObjectWriteHeaders(ContentType: "application/json", CacheControl: "no-cache"),
                    transfer.CreateContext(), cancellationToken);
                var manifestEntry = new PublishManifestFile
                {
                    Path = DefaultManifestName,
                    Size = new FileInfo(manifestPath).Length,
                    Sha256 = await PublishManifestUtility.ComputeSha256Async(manifestPath, cancellationToken)
                };
                await CliRemoteVerifier.VerifyAsync(
                    storage, profile, bucket, manifestKey, manifestEntry.Size, manifestEntry.Sha256,
                    transfer, cancellationToken);
                if (accessMode != PublishAccessMode.Preserve)
                {
                    await storage.PutObjectAclAsync(
                        profile,
                        bucket,
                        manifestKey,
                        ToObjectAclMode(accessMode),
                        cancellationToken);
                    aclUpdatedFiles++;
                }
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
            AccessMode = accessMode,
            DeleteMode = deleteMode,
            AclUpdatedFiles = aclUpdatedFiles,
            UploadedFiles = uploadedFiles,
            DeletedFiles = deletedFiles,
            SkippedFiles = plan.UnchangedFiles,
            FailedFiles = failures.Count,
            UploadedBytes = uploadedBytes,
            DeletedBytes = deletedBytes,
            RemoteUri = BuildRemoteUri(profile, bucket, prefix),
            CdnUrl = cdnUrl,
            ManifestPath = manifestPath,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Failures = failures.OrderBy(value => value.Path, StringComparer.Ordinal).ToList()
        };
        var text = result.Success
            ? $"发布成功：上传 {result.UploadedFiles:N0}，删除 {result.DeletedFiles:N0}，跳过 {result.SkippedFiles:N0}，" +
              $"失败 0，耗时 {stopwatch.Elapsed.TotalSeconds:N1} 秒。\n远程目录：{result.RemoteUri}" +
              (accessMode == PublishAccessMode.Preserve ? string.Empty : $"\n对象 ACL：{accessMode}，已更新 {aclUpdatedFiles:N0} 项") +
              (cdnUrl.Length > 0 ? $"\nCDN URL：{cdnUrl}" : string.Empty)
            : manifestPublished
                ? $"资源与 Manifest 已发布，但后处理失败：上传 {result.UploadedFiles:N0}，" +
                  $"跳过 {result.SkippedFiles:N0}，失败 {result.FailedFiles:N0}。"
                : $"发布未完成：上传 {result.UploadedFiles:N0}，删除 {result.DeletedFiles:N0}，跳过 {result.SkippedFiles:N0}，" +
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
        var transfer = CliTransferRuntime.Create(args);
        var manifestPath = Path.GetFullPath(args.Require("manifest"));
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        var profileName = args.Optional("profile") ?? manifest.Profile;
        if (string.IsNullOrWhiteSpace(profileName))
            throw new CliUsageException("verify 需要 --profile，或 Manifest 中必须包含 profile。");
        var profile = ResolveProfile(await profileStore.LoadAsync(cancellationToken), profileName);
        var bucket = ResolveBucket(profile, args.Optional("bucket") ?? manifest.Bucket);
        var prefix = NormalizePrefix(args.Optional("prefix") ?? manifest.Prefix);
        var failures = new ConcurrentBag<OperationFailure>();
        var verifiedFiles = 0;
        long verifiedBytes = 0;
        await transfer.ForEachAsync(manifest.Files, async (file, token) =>
        {
            try
            {
                await CliRemoteVerifier.VerifyAsync(
                    storage, profile, bucket, CombineKey(prefix, file.Path), file.Size, file.Sha256,
                    transfer, token);
                Interlocked.Increment(ref verifiedFiles);
                Interlocked.Add(ref verifiedBytes, file.Size);
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
        }, cancellationToken);

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
            Failures = failures.OrderBy(value => value.Path, StringComparer.Ordinal).ToList()
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
        if (verb is not ("test" or "warmup" or "cache-test"))
            throw new CliUsageException("用法：cdn test|cache-test|warmup --profile <name-or-id> --path <path> [--manifest <file>]");
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
            throw new CliUsageException("cdn test/cache-test/warmup 需要 --path 或 --manifest。");
        var result = await ExecuteCdnAsync(
            verb, profile, credential, paths.Distinct(StringComparer.Ordinal).ToArray(),
            deliveryService, cancellationToken);
        var cacheTransition = verb == "cache-test"
            ? string.Join(" → ", result.Items.Select(value => value.CacheStatus))
            : string.Empty;
        return new AutomationCommandResult(
            result.Success ? 0 : OperationFailed,
            result,
            result.Success
                ? $"CDN {verb} 完成：成功 {result.Succeeded:N0}，失败 0。" +
                  (cacheTransition.Length == 0 ? string.Empty : $" 缓存：{cacheTransition}")
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
                if (verb is "test" or "cache-test")
                {
                    var attempts = verb == "cache-test" ? 2 : 1;
                    for (var attempt = 1; attempt <= attempts; attempt++)
                    {
                        var probe = verb == "cache-test"
                            ? await deliveryService.ProbeHeadAsync(
                                profile, credential, url, cancellationToken)
                            : await deliveryService.ProbeAsync(
                                profile, credential, url, profile.WarmupRangeBytes, cancellationToken);
                        items.Add(new CdnItemResult
                        {
                            Path = path,
                            Url = probe.FinalUrl.AbsoluteUri,
                            Success = probe.Success,
                            StatusCode = probe.StatusCode,
                            BytesRead = probe.BytesRead,
                            ElapsedMilliseconds = (long)probe.TotalElapsed.TotalMilliseconds,
                            Attempt = attempt,
                            CacheStatus = probe.CacheStatus,
                            Message = probe.Success
                                ? $"第 {attempt} 次：HTTP {probe.StatusCode}，缓存 {probe.CacheStatus}"
                                : $"第 {attempt} 次：HTTP {probe.StatusCode} {probe.ReasonPhrase}"
                        });
                    }
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
        CliTransferRuntime transfer,
        CancellationToken cancellationToken)
    {
        if (!await storage.ObjectExistsAsync(profile, bucket, key, cancellationToken))
            return null;
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"s3explorer-manifest-{Guid.NewGuid():N}.json");
        try
        {
            await storage.DownloadFileAsync(
                profile, bucket, key, temporaryPath, transfer.CreateContext(), cancellationToken);
            return await ReadManifestAsync(temporaryPath, cancellationToken);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<PublishHeaderRuleSet?> LoadHeaderRulesAsync(
        string? path,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : await PublishHeaderRuleUtility.LoadAsync(path, cancellationToken).ConfigureAwait(false);

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
        IReadOnlyCollection<PublishMirrorDeleteCandidate> deletePlan,
        string bucket,
        string prefix)
    {
        if (args.Flag("yes") || args.Flag("non-interactive") || jsonOutput) return;
        if (Console.IsInputRedirected)
            throw new CliUsageException("非交互发布需要 --non-interactive 或 --yes。");
        Console.Write(
            $"将上传 {plan.NewFiles + plan.ModifiedFiles:N0} 个文件到 s3://{bucket}/{prefix}/，" +
            $"共 {FileSizeFormatter.Format(plan.UploadBytes)}；删除 {deletePlan.Count:N0} 个远端对象，" +
            $"共 {FileSizeFormatter.Format(deletePlan.Sum(value => value.Size))}。继续？[y/N] ");
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

    internal static PublishAccessMode ParseAccessMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "preserve" => PublishAccessMode.Preserve,
            "anonymous-read" or "public-read" => PublishAccessMode.AnonymousRead,
            "private" => PublishAccessMode.Private,
            _ => throw new CliUsageException("--access 只能是 preserve、anonymous-read 或 private。")
        };

    internal static PublishDeleteMode ParseDeleteMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" => PublishDeleteMode.None,
            "mirror" => PublishDeleteMode.Mirror,
            _ => throw new CliUsageException("--delete-mode 只能是 none 或 mirror。")
        };

    private static ObjectAclMode ToObjectAclMode(PublishAccessMode value) => value switch
    {
        PublishAccessMode.AnonymousRead => ObjectAclMode.PublicRead,
        PublishAccessMode.Private => ObjectAclMode.Private,
        _ => throw new InvalidOperationException($"访问模式 {value} 不需要修改对象 ACL。")
    };

    private static string CombineKey(string prefix, string relative)
    {
        var normalizedRelative = PublishManifestUtility.NormalizeRelativePath(relative);
        return prefix.Length == 0 ? normalizedRelative : $"{prefix}/{normalizedRelative}";
    }

    private static string BuildRemoteUri(ConnectionProfile profile, string bucket, string prefix) =>
        prefix.Length == 0
            ? $"s3://{profile.Name}/{bucket}/"
            : $"s3://{profile.Name}/{bucket}/{prefix}/";

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
