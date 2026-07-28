using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.Cli;

internal static class Program
{
    private const int UsageError = 2;
    private const int NotFound = 3;
    private const int OperationFailed = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        CliArguments parsed;
        try
        {
            parsed = CliArguments.Parse(args);
        }
        catch (Exception exception)
        {
            WriteError(false, exception.Message, UsageError);
            return UsageError;
        }

        var json = parsed.Flag("json");
        try
        {
            if (parsed.Positionals.Count == 0 || parsed.Positionals[0] is "help" or "--help" or "-h")
            {
                WriteHelp();
                return 0;
            }
            if (parsed.Positionals[0] == "version")
            {
                WriteSuccess(json, new { version = Version }, $"s3explorer-cli {Version}");
                return 0;
            }

            var dataDirectory = parsed.Optional("data-dir") is { Length: > 0 } explicitDirectory
                ? RequireAbsolutePath(explicitDirectory, "--data-dir")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "S3Explorer");
            var profiles = new JsonProfileStore(new DpapiCredentialProtector(), Path.Combine(dataDirectory, "profiles.json"));
            var syncJobs = new JsonFolderSyncJobStore(Path.Combine(dataDirectory, "sync-jobs.json"));
            var storage = new S3StorageService(new S3ClientFactory());
            var command = parsed.Positionals[0].ToLowerInvariant();
            var verb = parsed.Positionals.Count > 1 ? parsed.Positionals[1].ToLowerInvariant() : string.Empty;

            return command switch
            {
                "profile" or "profiles" => await RunProfileAsync(verb, parsed, profiles, json, cancellation.Token),
                "connection" => await RunConnectionAsync(verb, parsed, profiles, storage, json, cancellation.Token),
                "bucket" or "buckets" => await RunBucketAsync(verb, parsed, profiles, storage, json, cancellation.Token),
                "object" or "objects" => await RunObjectAsync(verb, parsed, profiles, storage, json, cancellation.Token),
                "sync" => await RunSyncAsync(verb, parsed, profiles, syncJobs, storage, json, cancellation.Token),
                _ => throw new CliUsageException($"未知命令：{command}。运行 s3explorer-cli help 查看可用命令。")
            };
        }
        catch (OperationCanceledException)
        {
            WriteError(json, "操作已取消。", 130);
            return 130;
        }
        catch (CliNotFoundException exception)
        {
            WriteError(json, exception.Message, NotFound);
            return NotFound;
        }
        catch (CliUsageException exception)
        {
            WriteError(json, exception.Message, UsageError);
            return UsageError;
        }
        catch (Exception exception)
        {
            WriteError(json, SensitiveDataRedactor.Redact(exception.Message), OperationFailed);
            return OperationFailed;
        }
    }

    private static async Task<int> RunProfileAsync(
        string verb,
        CliArguments args,
        IProfileStore store,
        bool json,
        CancellationToken cancellationToken)
    {
        var profiles = (await store.LoadAsync(cancellationToken)).ToList();
        switch (verb)
        {
            case "list":
                var list = profiles.Select(ProfileView).ToArray();
                WriteSuccess(json, list, list.Length == 0
                    ? "没有已保存的连接。"
                    : string.Join(Environment.NewLine, profiles.Select(item =>
                        $"{item.Name}\t{S3ProviderCatalog.Get(item.ServiceType).DisplayName}\t{item.CredentialSourceDisplayName}\t{item.Endpoint}\t{item.Id}")));
                return 0;

            case "show":
                var shown = ResolveProfile(profiles, RequirePositional(args, 2, "profile show <name-or-id>"));
                WriteSuccess(json, ProfileView(shown),
                    $"名称: {shown.Name}\n类型: {S3ProviderCatalog.Get(shown.ServiceType).DisplayName}\nEndpoint: {shown.Endpoint}\nRegion: {shown.EffectiveSignatureRegion}\n凭据来源: {shown.CredentialSourceDisplayName}" +
                    (shown.CredentialSource == CredentialSourceKind.StoredKeys ? $"\nAccess Key: {Mask(shown.AccessKey)}" : string.Empty) +
                    $"\nID: {shown.Id}");
                return 0;

            case "add":
                var name = args.Require("name");
                if (profiles.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new CliUsageException($"连接名称已存在：{name}");
                var serviceType = ParseServiceType(args.Require("type"));
                var preset = ConnectionProfile.CreatePreset(serviceType);
                var definition = S3ProviderCatalog.Get(serviceType);
                var credentialSource = ParseCredentialSource(args.Optional("credential-source") ?? "stored");
                if (credentialSource != CredentialSourceKind.StoredKeys && serviceType != S3ServiceType.AmazonS3)
                    throw new CliUsageException("AWS 外部凭据来源仅适用于 --type amazon；S3-compatible 连接必须使用 stored。");
                var awsProfileName = args.Optional("aws-profile")?.Trim() ?? string.Empty;
                if (credentialSource == CredentialSourceKind.AwsSharedProfile && awsProfileName.Length == 0)
                    throw new CliUsageException("--credential-source profile 需要 --aws-profile <name>。");
                if (credentialSource != CredentialSourceKind.StoredKeys &&
                    (args.Optional("access-key") is not null || args.Optional("secret-key") is not null ||
                     args.Optional("secret-key-env") is not null || args.Optional("session-token") is not null ||
                     args.Optional("session-token-env") is not null))
                    throw new CliUsageException("外部凭据来源不能同时提供 Access Key、Secret Key 或 Session Token。");
                var accessKey = credentialSource == CredentialSourceKind.StoredKeys
                    ? args.Optional("access-key") ?? Environment.GetEnvironmentVariable("S3EXPLORER_ACCESS_KEY") ?? string.Empty
                    : string.Empty;
                var secretKey = credentialSource == CredentialSourceKind.StoredKeys
                    ? ResolveSecret(args, "secret-key", "S3EXPLORER_SECRET_KEY")
                    : string.Empty;
                var sessionToken = credentialSource == CredentialSourceKind.StoredKeys
                    ? ResolveSecret(args, "session-token", "S3EXPLORER_SESSION_TOKEN", required: false)
                    : string.Empty;
                var region = args.Optional("region")?.Trim();
                if (string.IsNullOrWhiteSpace(region))
                    region = definition.DefaultRegion;
                var signingRegion = S3ProviderCatalog.ResolveSigningRegion(serviceType, region);
                var profile = preset with
                {
                    Name = name.Trim(),
                    Endpoint = args.Optional("endpoint") ?? definition.DefaultEndpoint,
                    Region = region,
                    SignatureRegion = signingRegion,
                    AccessKey = accessKey,
                    SecretKey = secretKey,
                    SessionToken = sessionToken,
                    CredentialSource = credentialSource,
                    AwsProfileName = credentialSource == CredentialSourceKind.AwsSharedProfile ? awsProfileName : string.Empty,
                    DefaultBucket = args.Optional("default-bucket") ?? string.Empty,
                    AddressingStyle = args.Flag("path-style") ? AddressingStyle.PathStyle : preset.AddressingStyle,
                    IgnoreCertificateErrors = args.Flag("ignore-certificate-errors")
                };
                profile.Validate();
                profiles.Add(profile);
                await store.SaveAsync(profiles, cancellationToken);
                WriteSuccess(json, ProfileView(profile), $"已添加连接：{profile.Name} ({profile.Id})");
                return 0;

            case "delete":
                RequireConfirmation(args, "删除连接必须提供 --yes。");
                var deleted = ResolveProfile(profiles, RequirePositional(args, 2, "profile delete <name-or-id> --yes"));
                profiles.RemoveAll(item => item.Id == deleted.Id);
                await store.SaveAsync(profiles, cancellationToken);
                WriteSuccess(json, new { deleted.Id, deleted.Name }, $"已删除连接：{deleted.Name}");
                return 0;

            default:
                throw new CliUsageException("用法：profile list | show <name-or-id> | add --name ... --type ... | delete <name-or-id> --yes");
        }
    }

    private static async Task<int> RunConnectionAsync(
        string verb,
        CliArguments args,
        IProfileStore store,
        IS3StorageService storage,
        bool json,
        CancellationToken cancellationToken)
    {
        if (verb != "test") throw new CliUsageException("用法：connection test <name-or-id>");
        var profile = ResolveProfile(await store.LoadAsync(cancellationToken), RequirePositional(args, 2, "connection test <name-or-id>"));
        var result = await storage.TestConnectionAsync(profile, cancellationToken);
        if (!result.Success)
        {
            WriteOperationFailure(json, result.Message, OperationFailed, result);
            return OperationFailed;
        }
        WriteSuccess(json, result,
            $"{result.Message}\n凭据来源: {result.CredentialSource ?? profile.CredentialSourceDisplayName}\n耗时: {result.Elapsed.TotalMilliseconds:N0} ms\nBucket: {result.BucketCount}\nHTTP: {result.HttpStatusCode?.ToString() ?? "-"}");
        return 0;
    }

    private static async Task<int> RunBucketAsync(
        string verb,
        CliArguments args,
        IProfileStore store,
        IS3StorageService storage,
        bool json,
        CancellationToken cancellationToken)
    {
        if (verb != "list") throw new CliUsageException("用法：bucket list <profile-name-or-id>");
        var profile = ResolveProfile(await store.LoadAsync(cancellationToken), RequirePositional(args, 2, "bucket list <profile-name-or-id>"));
        var buckets = await storage.ListBucketsAsync(profile, cancellationToken);
        WriteSuccess(json, buckets, buckets.Count == 0
            ? "没有可见的 Bucket。"
            : string.Join(Environment.NewLine, buckets.Select(bucket => $"{bucket.Name}\t{bucket.CreatedAt:u}\t{bucket.Region}")));
        return 0;
    }

    private static async Task<int> RunObjectAsync(
        string verb,
        CliArguments args,
        IProfileStore store,
        IS3StorageService storage,
        bool json,
        CancellationToken cancellationToken)
    {
        var profiles = await store.LoadAsync(cancellationToken);
        switch (verb)
        {
            case "list":
            {
                var (profile, location) = ResolveLocation(profiles, RequirePositional(args, 2, "object list <s3-uri>"));
                var items = args.Flag("recursive")
                    ? await RecursiveObjectListing.ListFilesAsync(
                        location.Prefix, ObjectListingLimits.DefaultPageSize, ObjectListingLimits.DefaultCacheLimit,
                        (prefix, token, ct) => storage.ListObjectsAsync(profile, location.Bucket!, prefix, token, ObjectListingLimits.DefaultPageSize, ct),
                        cancellationToken)
                    : await ListCurrentLevelAsync(storage, profile, location.Bucket!, location.Prefix, cancellationToken);
                WriteSuccess(json, items, items.Count == 0
                    ? "没有对象。"
                    : string.Join(Environment.NewLine, items.Select(item => $"{(item.IsDirectory ? "DIR " : "FILE")}\t{item.Size}\t{item.Key}")));
                return 0;
            }
            case "versions":
            {
                var (profile, location) = ResolveLocation(profiles,
                    RequirePositional(args, 2, "object versions <s3-uri>"));
                var pageSize = ParsePageSize(args.Optional("page-size"));
                var page = await storage.ListObjectVersionsAsync(
                    profile, location.Bucket!, location.Prefix,
                    args.Optional("key-marker"), args.Optional("version-id-marker"),
                    pageSize, cancellationToken);
                var result = new
                {
                    items = page.Items,
                    page.HasMore,
                    page.NextKeyMarker,
                    page.NextVersionIdMarker
                };
                WriteSuccess(json, result, page.Items.Count == 0
                    ? "没有对象版本。"
                    : string.Join(Environment.NewLine, page.Items.Select(item =>
                        $"{(item.IsDeleteMarker ? "DELETE-MARKER" : "VERSION")}" +
                        $"\t{(item.IsLatest ? "CURRENT" : "HISTORY")}" +
                        $"\t{item.Size}\t{item.Key}\t{item.VersionId}")) +
                      (page.HasMore
                          ? $"\n下一页: --key-marker {page.NextKeyMarker} --version-id-marker {page.NextVersionIdMarker}"
                          : string.Empty));
                return 0;
            }
            case "upload":
            {
                var localPath = RequireAbsolutePath(RequirePositional(args, 2, "object upload <local-path> <s3-uri>"), "local-path");
                var (profile, location) = ResolveLocation(profiles, RequirePositional(args, 3, "object upload <local-path> <s3-uri>"));
                var uploaded = await UploadPathAsync(storage, profile, localPath, location.Bucket!, location.Prefix, cancellationToken);
                WriteSuccess(json, new { uploaded }, $"上传完成：{uploaded:N0} 个文件");
                return 0;
            }
            case "download":
            {
                var (profile, location) = ResolveLocation(profiles, RequirePositional(args, 2, "object download <s3-uri> <local-path>"));
                var target = RequireAbsolutePath(RequirePositional(args, 3, "object download <s3-uri> <local-path>"), "local-path");
                var versionId = args.Optional("version-id")?.Trim();
                if (!string.IsNullOrWhiteSpace(versionId))
                {
                    if (args.Flag("recursive"))
                        throw new CliUsageException("指定 --version-id 时不能使用 --recursive。");
                    if (string.IsNullOrWhiteSpace(location.Prefix))
                        throw new CliUsageException("指定版本下载必须包含对象 Key。");
                    await storage.DownloadObjectVersionAsync(
                        profile, location.Bucket!, location.Prefix, versionId, target,
                        NewTransferContext(), cancellationToken);
                    WriteSuccess(json, new { downloaded = 1, versionId },
                        $"已下载指定版本：{location.Prefix} ({versionId})");
                    return 0;
                }
                var downloaded = await DownloadPathAsync(storage, profile, location, target, args.Flag("recursive"), cancellationToken);
                WriteSuccess(json, new { downloaded }, $"下载完成：{downloaded:N0} 个文件");
                return 0;
            }
            case "delete":
            {
                RequireConfirmation(args, "删除对象必须提供 --yes。");
                var (profile, location) = ResolveLocation(profiles, RequirePositional(args, 2, "object delete <s3-uri> --yes"));
                if (string.IsNullOrEmpty(location.Prefix)) throw new CliUsageException("不能用 object delete 删除整个 Bucket。");
                IReadOnlyList<string> keys;
                if (args.Flag("recursive"))
                {
                    keys = (await RecursiveObjectListing.ListFilesAsync(
                        location.Prefix, 1000, ObjectListingLimits.DefaultCacheLimit,
                        (prefix, token, ct) => storage.ListObjectsAsync(profile, location.Bucket!, prefix, token, 1000, ct),
                        cancellationToken))
                        .Select(item => item.Key)
                        .Append(location.Prefix)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }
                else keys = [location.Prefix];
                foreach (var chunk in keys.Chunk(1000))
                    await storage.DeleteObjectsAsync(profile, location.Bucket!, chunk, cancellationToken);
                WriteSuccess(json, new { deleted = keys.Count }, $"已删除 {keys.Count:N0} 个对象。");
                return 0;
            }
            case "restore-version":
            {
                RequireConfirmation(args, "恢复历史版本必须提供 --yes。");
                var (profile, location) = ResolveLocation(profiles,
                    RequirePositional(args, 2, "object restore-version <s3-uri> --version-id <id> --yes"));
                if (string.IsNullOrWhiteSpace(location.Prefix))
                    throw new CliUsageException("恢复历史版本必须包含对象 Key。");
                var versionId = args.Require("version-id");
                await storage.RestoreObjectVersionAsync(
                    profile, location.Bucket!, location.Prefix, versionId, cancellationToken);
                WriteSuccess(json, new { location.Prefix, versionId },
                    $"已将版本 {versionId} 恢复为对象 {location.Prefix} 的新当前版本。");
                return 0;
            }
            case "delete-version":
            {
                RequireConfirmation(args, "永久删除对象版本必须提供 --yes。");
                var (profile, location) = ResolveLocation(profiles,
                    RequirePositional(args, 2, "object delete-version <s3-uri> --version-id <id> --yes"));
                if (string.IsNullOrWhiteSpace(location.Prefix))
                    throw new CliUsageException("删除对象版本必须包含对象 Key。");
                var versionId = args.Require("version-id");
                await storage.DeleteObjectVersionAsync(
                    profile, location.Bucket!, location.Prefix, versionId, cancellationToken);
                WriteSuccess(json, new { location.Prefix, versionId },
                    $"已永久删除版本：{location.Prefix} ({versionId})");
                return 0;
            }
            case "clean-delete-markers":
            {
                RequireConfirmation(args, "批量清理 Delete Marker 必须提供 --yes。");
                var (profile, location) = ResolveLocation(profiles,
                    RequirePositional(args, 2, "object clean-delete-markers <s3-uri> --yes"));
                var markers = new List<ObjectVersionIdentity>();
                string? keyMarker = null;
                string? versionMarker = null;
                bool hasMore;
                do
                {
                    var page = await storage.ListObjectVersionsAsync(
                        profile, location.Bucket!, location.Prefix,
                        keyMarker, versionMarker, 1000, cancellationToken);
                    markers.AddRange(page.Items.Where(item => item.IsDeleteMarker)
                        .Select(item => new ObjectVersionIdentity(item.Key, item.VersionId)));
                    hasMore = page.HasMore;
                    keyMarker = hasMore ? page.NextKeyMarker : null;
                    versionMarker = hasMore ? page.NextVersionIdMarker : null;
                } while (hasMore);
                await storage.DeleteObjectVersionsAsync(
                    profile, location.Bucket!, markers, cancellationToken);
                WriteSuccess(json, new { deleted = markers.Count },
                    $"已永久删除 {markers.Count:N0} 个 Delete Marker。");
                return 0;
            }
            default:
                throw new CliUsageException("用法：object list|versions|upload|download|delete|restore-version|delete-version|clean-delete-markers。运行 help 查看示例。");
        }
    }

    private static async Task<int> RunSyncAsync(
        string verb,
        CliArguments args,
        IProfileStore profileStore,
        IFolderSyncJobStore jobStore,
        IS3StorageService storage,
        bool json,
        CancellationToken cancellationToken)
    {
        var profiles = await profileStore.LoadAsync(cancellationToken);
        var jobs = (await jobStore.LoadAsync(cancellationToken)).ToList();
        switch (verb)
        {
            case "list":
                var summaries = jobs.Select(job => new
                {
                    job.Id,
                    job.Name,
                    job.Direction,
                    job.LocalDirectory,
                    remote = job.S3Location,
                    job.LastRunAt
                }).ToArray();
                WriteSuccess(json, summaries, summaries.Length == 0
                    ? "没有已保存的同步任务。"
                    : string.Join(Environment.NewLine, summaries.Select(item => $"{item.Name}\t{item.Direction}\t{item.LocalDirectory}\t{item.remote}\t{item.Id}")));
                return 0;

            case "add":
            {
                var name = args.Require("name").Trim();
                if (jobs.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new CliUsageException($"同步任务名称已存在：{name}");
                var local = RequireAbsolutePath(args.Require("local"), "--local");
                var (profile, location) = ResolveLocation(profiles, args.Require("remote"));
                var direction = args.Optional("direction")?.ToLowerInvariant() switch
                {
                    "download" => FolderSyncDirection.Download,
                    null or "upload" => FolderSyncDirection.Upload,
                    _ => throw new CliUsageException("--direction 只能是 upload 或 download。")
                };
                var includeNew = !args.Flag("changed-only");
                var includeChanged = !args.Flag("new-only");
                if (!includeNew && !includeChanged && !args.Flag("delete"))
                    throw new CliUsageException("--new-only 与 --changed-only 不能同时使用，除非任务启用了 --delete。");
                var job = new FolderSyncJob
                {
                    Name = name,
                    LocalDirectory = local,
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Bucket = location.Bucket!,
                    Prefix = location.Prefix,
                    Direction = direction,
                    PropagateDeletions = args.Flag("delete"),
                    CompareHashesWhenAvailable = args.Flag("hash"),
                    IncludeNewFiles = includeNew,
                    IncludeChangedFiles = includeChanged,
                    ExclusionPatterns = args.Values("exclude")
                };
                job.Validate();
                jobs.Add(job);
                await jobStore.SaveAsync(jobs, cancellationToken);
                WriteSuccess(json, new { job.Id, job.Name, job.Direction, job.LocalDirectory, remote = job.S3Location }, $"已添加同步任务：{job.Name}");
                return 0;
            }
            case "delete":
            {
                RequireConfirmation(args, "删除同步任务必须提供 --yes。");
                var job = ResolveJob(jobs, RequirePositional(args, 2, "sync delete <name-or-id> --yes"));
                jobs.RemoveAll(item => item.Id == job.Id);
                await jobStore.SaveAsync(jobs, cancellationToken);
                WriteSuccess(json, new { job.Id, job.Name }, $"已删除同步任务：{job.Name}");
                return 0;
            }
            case "analyze":
            case "run":
            {
                var job = ResolveJob(jobs, RequirePositional(args, 2, $"sync {verb} <name-or-id>"));
                var profile = ResolveProfile(profiles, job.ProfileId.ToString());
                var plan = await FolderSyncAnalyzer.AnalyzeAsync(job, profile, storage, cancellationToken: cancellationToken);
                if (verb == "analyze")
                {
                    WriteSuccess(json, PlanView(plan), FormatPlan(plan));
                    return 0;
                }
                if (plan.Items.Any(item => item.Action is FolderSyncAction.DeleteLocal or FolderSyncAction.DeleteRemote))
                    RequireConfirmation(args, "同步计划包含删除操作，运行时必须提供 --yes。");
                var executed = await ExecuteSyncPlanAsync(job, profile, plan, storage, cancellationToken);
                var index = jobs.FindIndex(item => item.Id == job.Id);
                jobs[index] = job with { LastRunAt = DateTimeOffset.UtcNow };
                await jobStore.SaveAsync(jobs, cancellationToken);
                WriteSuccess(json, new { executed, plan = PlanView(plan) }, $"同步完成：执行 {executed:N0} 项。\n{FormatPlan(plan)}");
                return 0;
            }
            default:
                throw new CliUsageException("用法：sync list | add ... | analyze <name-or-id> | run <name-or-id> [--yes] | delete <name-or-id> --yes");
        }
    }

    private static async Task<int> ExecuteSyncPlanAsync(
        FolderSyncJob job,
        ConnectionProfile profile,
        FolderSyncPlan plan,
        IS3StorageService storage,
        CancellationToken cancellationToken)
    {
        var executed = 0;
        foreach (var item in plan.Items.Where(item => item.Action != FolderSyncAction.None))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteKey = S3Path.Combine(job.Prefix, item.RelativePath);
            var localPath = LocalObjectPath.MapRelativeKey(job.LocalDirectory, item.RelativePath);
            switch (item.Action)
            {
                case FolderSyncAction.Upload:
                    await storage.UploadFileAsync(profile, job.Bucket, remoteKey, localPath, profile.DefaultStorageClass,
                        NewTransferContext(), cancellationToken);
                    break;
                case FolderSyncAction.Download:
                    await storage.DownloadFileAsync(profile, job.Bucket, remoteKey, localPath, NewTransferContext(), cancellationToken);
                    break;
                case FolderSyncAction.DeleteRemote:
                    await storage.DeleteObjectsAsync(profile, job.Bucket, [remoteKey], cancellationToken);
                    break;
                case FolderSyncAction.DeleteLocal:
                    if (File.Exists(localPath)) File.Delete(localPath);
                    break;
            }
            executed++;
        }
        return executed;
    }

    private static async Task<IReadOnlyList<S3ObjectEntry>> ListCurrentLevelAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        string bucket,
        string prefix,
        CancellationToken cancellationToken)
    {
        var result = new List<S3ObjectEntry>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await storage.ListObjectsAsync(profile, bucket, prefix, token, 1000, cancellationToken);
            result.AddRange(page.Items);
            if (!page.HasMore) break;
            token = page.ContinuationToken;
            if (string.IsNullOrWhiteSpace(token) || !tokens.Add(token))
                throw new InvalidOperationException("对象列表分页令牌无效或重复。");
        } while (true);
        return result;
    }

    private static async Task<int> UploadPathAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        string localPath,
        string bucket,
        string destination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(localPath))
        {
            var key = destination.EndsWith('/') || destination.Length == 0
                ? S3Path.Combine(destination, Path.GetFileName(localPath))
                : destination;
            await storage.UploadFileAsync(profile, bucket, key, localPath, profile.DefaultStorageClass, NewTransferContext(), cancellationToken);
            return 1;
        }
        if (!Directory.Exists(localPath)) throw new FileNotFoundException("本地路径不存在。", localPath);
        var rootName = new DirectoryInfo(localPath).Name;
        var count = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var file in Directory.EnumerateFiles(localPath, "*", options))
        {
            var relative = Path.GetRelativePath(localPath, file).Replace('\\', '/');
            var key = S3Path.Combine(destination, $"{rootName}/{relative}");
            await storage.UploadFileAsync(profile, bucket, key, file, profile.DefaultStorageClass, NewTransferContext(), cancellationToken);
            count++;
        }
        return count;
    }

    private static async Task<int> DownloadPathAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        S3Location location,
        string target,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (recursive || location.Prefix.EndsWith('/'))
        {
            Directory.CreateDirectory(target);
            var items = await RecursiveObjectListing.ListFilesAsync(
                location.Prefix, 1000, ObjectListingLimits.DefaultCacheLimit,
                (prefix, token, ct) => storage.ListObjectsAsync(profile, location.Bucket!, prefix, token, 1000, ct),
                cancellationToken);
            foreach (var item in items)
            {
                var relative = item.Key[location.Prefix.Length..].TrimStart('/');
                await storage.DownloadFileAsync(profile, location.Bucket!, item.Key,
                    LocalObjectPath.MapRelativeKey(target, relative), NewTransferContext(), cancellationToken);
            }
            return items.Count;
        }

        if (string.IsNullOrEmpty(location.Prefix)) throw new CliUsageException("下载单个对象时 URI 必须包含对象 Key。");
        if (Directory.Exists(target)) target = Path.Combine(target, S3Path.DisplayName(location.Prefix, false));
        await storage.DownloadFileAsync(profile, location.Bucket!, location.Prefix, target, NewTransferContext(), cancellationToken);
        return 1;
    }

    private static TransferOperationContext NewTransferContext()
    {
        var limiter = new SharedTransferBandwidthLimiter();
        limiter.Configure(0, 0);
        return new TransferOperationContext(
            new TransferExecutionOptions(), limiter, null, null, _ => { },
            (_, _, _, _) => Task.CompletedTask);
    }

    private static (ConnectionProfile Profile, S3Location Location) ResolveLocation(
        IReadOnlyList<ConnectionProfile> profiles,
        string value)
    {
        S3Location location;
        try { location = S3Location.Parse(value); }
        catch (FormatException exception) { throw new CliUsageException(exception.Message); }
        if (string.IsNullOrWhiteSpace(location.Bucket))
            throw new CliUsageException("S3 URI 必须包含 Bucket，例如 s3://profile/bucket/path。");
        return (ResolveProfile(profiles, location.Profile), location);
    }

    private static ConnectionProfile ResolveProfile(IEnumerable<ConnectionProfile> profiles, string nameOrId)
    {
        var values = profiles.ToArray();
        if (Guid.TryParse(nameOrId, out var id))
            return values.FirstOrDefault(item => item.Id == id)
                ?? throw new CliNotFoundException($"找不到连接：{nameOrId}");
        return values.FirstOrDefault(item => string.Equals(item.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliNotFoundException($"找不到连接：{nameOrId}");
    }

    private static FolderSyncJob ResolveJob(IEnumerable<FolderSyncJob> jobs, string nameOrId)
    {
        var values = jobs.ToArray();
        if (Guid.TryParse(nameOrId, out var id))
            return values.FirstOrDefault(item => item.Id == id)
                ?? throw new CliNotFoundException($"找不到同步任务：{nameOrId}");
        return values.FirstOrDefault(item => string.Equals(item.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliNotFoundException($"找不到同步任务：{nameOrId}");
    }

    private static S3ServiceType ParseServiceType(string value) => value.ToLowerInvariant() switch
    {
        "amazon" or "amazon-s3" or "s3" => S3ServiceType.AmazonS3,
        "compatible" or "custom" or "s3-compatible" => S3ServiceType.Custom,
        "google" or "gcs" => S3ServiceType.GoogleCloudStorage,
        "minio" => S3ServiceType.MinIO,
        "r2" or "cloudflare-r2" => S3ServiceType.CloudflareR2,
        "b2" or "backblaze-b2" => S3ServiceType.BackblazeB2,
        "aliyun" or "oss" => S3ServiceType.AliyunOss,
        "tencent" or "cos" => S3ServiceType.TencentCos,
        "supabase" => S3ServiceType.SupabaseStorage,
        _ => throw new CliUsageException($"不支持的连接类型：{value}")
    };

    private static CredentialSourceKind ParseCredentialSource(string value) => value.Trim().ToLowerInvariant() switch
    {
        "stored" or "keys" or "saved" => CredentialSourceKind.StoredKeys,
        "profile" or "shared-profile" or "aws-profile" => CredentialSourceKind.AwsSharedProfile,
        "environment" or "env" => CredentialSourceKind.AwsEnvironmentVariables,
        "container" or "container-role" or "ecs" => CredentialSourceKind.AwsContainerRole,
        "instance" or "instance-role" or "ec2" => CredentialSourceKind.AwsInstanceRole,
        "default" or "default-chain" or "chain" => CredentialSourceKind.AwsDefaultChain,
        _ => throw new CliUsageException(
            $"不支持的凭据来源：{value}。可选 stored|profile|environment|container|instance|default。")
    };

    private static string ResolveSecret(CliArguments args, string option, string environmentName, bool required = true)
    {
        var value = args.Optional(option);
        var environmentOption = args.Optional(option + "-env");
        if (!string.IsNullOrWhiteSpace(environmentOption))
            value = Environment.GetEnvironmentVariable(environmentOption);
        value ??= Environment.GetEnvironmentVariable(environmentName);
        if (required && string.IsNullOrEmpty(value))
            throw new CliUsageException($"缺少 --{option}、--{option}-env 或环境变量 {environmentName}。");
        return value ?? string.Empty;
    }

    private static object ProfileView(ConnectionProfile profile) => new
    {
        profile.Id,
        profile.Name,
        type = S3ProviderCatalog.Get(profile.ServiceType).DisplayName,
        profile.Endpoint,
        region = profile.EffectiveSignatureRegion,
        credentialSource = profile.CredentialSourceDisplayName,
        awsProfile = profile.CredentialSource == CredentialSourceKind.AwsSharedProfile ? profile.AwsProfileName : null,
        accessKey = profile.CredentialSource == CredentialSourceKind.StoredKeys ? Mask(profile.AccessKey) : null,
        hasSessionToken = profile.UsesTemporarySessionCredentials,
        profile.DefaultBucket
    };

    private static object PlanView(FolderSyncPlan plan) => new
    {
        plan.JobId,
        plan.AnalyzedAt,
        plan.ActionCount,
        plan.NewCount,
        plan.ChangedCount,
        plan.DeletedCount,
        plan.ExcludedCount,
        plan.TransferBytes,
        items = plan.Items.Select(item => new
        {
            item.RelativePath,
            item.Change,
            item.Action,
            localSize = item.Local?.Size,
            remoteSize = item.Remote?.Size,
            item.Reason
        })
    };

    private static string FormatPlan(FolderSyncPlan plan) =>
        $"操作: {plan.ActionCount:N0}，新增: {plan.NewCount:N0}，更改: {plan.ChangedCount:N0}，删除: {plan.DeletedCount:N0}，排除: {plan.ExcludedCount:N0}，传输: {FileSizeFormatter.Format(plan.TransferBytes)}";

    private static string RequirePositional(CliArguments args, int index, string usage) =>
        args.Positionals.Count > index ? args.Positionals[index] : throw new CliUsageException($"用法：{usage}");

    private static string RequireAbsolutePath(string value, string label)
    {
        if (!Path.IsPathFullyQualified(value)) throw new CliUsageException($"{label} 必须使用绝对路径。");
        return Path.GetFullPath(value);
    }

    private static void RequireConfirmation(CliArguments args, string message)
    {
        if (!args.Flag("yes")) throw new CliUsageException(message);
    }

    private static int ParsePageSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 200;
        if (!int.TryParse(value, out var parsed) || parsed is < 1 or > 1000)
            throw new CliUsageException("--page-size 必须是 1–1000 的整数。");
        return parsed;
    }

    private static string Mask(string value) => value.Length <= 4 ? new string('*', value.Length) : value[..4] + new string('*', 8);

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } value
        ? $"{value.Major}.{value.Minor}.{value.Build}"
        : "unknown";

    private static void WriteSuccess(bool json, object data, string text)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { ok = true, data }, JsonOptions));
        else Console.WriteLine(text);
    }

    private static void WriteError(bool json, string message, int exitCode)
    {
        if (json) Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { message, exitCode } }, JsonOptions));
        else Console.Error.WriteLine($"错误: {message}");
    }

    private static void WriteOperationFailure(bool json, string message, int exitCode, object details)
    {
        message = SensitiveDataRedactor.Redact(message);
        if (json)
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { message, exitCode, details } }, JsonOptions));
        else
            Console.Error.WriteLine($"错误: {message}");
    }

    private static void WriteHelp() => Console.WriteLine(
        """
        S3 Explorer CLI

        用法:
          s3explorer-cli profile list [--json]
          s3explorer-cli profile show <name-or-id> [--json]
          s3explorer-cli profile add --name <name> --type <amazon|compatible|google|minio|r2|b2|aliyun|tencent|supabase>
              [--endpoint <url>] [--region <region>]
              [--credential-source <stored|profile|environment|container|instance|default>]
              [--aws-profile <name>] [--access-key <key>] [--secret-key-env <ENV_NAME>]
          s3explorer-cli profile delete <name-or-id> --yes
          s3explorer-cli connection test <name-or-id> [--json]
          s3explorer-cli bucket list <name-or-id> [--json]
          s3explorer-cli object list <s3://profile/bucket/prefix> [--recursive] [--json]
          s3explorer-cli object versions <s3://profile/bucket/prefix> [--page-size <1-1000>]
              [--key-marker <key>] [--version-id-marker <id>] [--json]
          s3explorer-cli object upload <absolute-local-path> <s3://profile/bucket/key>
          s3explorer-cli object download <s3://profile/bucket/key> <absolute-local-path> [--recursive|--version-id <id>]
          s3explorer-cli object delete <s3://profile/bucket/key> [--recursive] --yes
          s3explorer-cli object restore-version <s3://profile/bucket/key> --version-id <id> --yes
          s3explorer-cli object delete-version <s3://profile/bucket/key> --version-id <id> --yes
          s3explorer-cli object clean-delete-markers <s3://profile/bucket/prefix> --yes
          s3explorer-cli sync list [--json]
          s3explorer-cli sync add --name <name> --local <absolute-folder> --remote <s3-uri>
              [--direction upload|download] [--exclude <glob>] [--new-only|--changed-only] [--delete] [--hash]
          s3explorer-cli sync analyze <name-or-id> [--json]
          s3explorer-cli sync run <name-or-id> [--yes] [--json]
          s3explorer-cli sync delete <name-or-id> --yes

        全局选项:
          --data-dir <absolute-path>  使用隔离的数据目录，适合自动化与测试
          --json                      输出稳定 JSON；错误仍写入 stderr

        凭据建议:
          优先使用 --secret-key-env <变量名> 或 S3EXPLORER_SECRET_KEY，避免密钥进入命令历史。
          AWS 外部来源只适用于 Amazon S3；profile 名称会保存，但环境和角色凭据不会写入连接文件。
        """);
}

internal sealed class CliArguments
{
    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yes", "recursive", "delete", "hash", "new-only", "changed-only",
        "path-style", "ignore-certificate-errors"
    };
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();
        for (var index = 0; index < args.Count; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(current);
                continue;
            }

            var key = current[2..];
            if (key.Length == 0) throw new CliUsageException("选项名称不能为空。");
            var value = "true";
            if (!FlagOptions.Contains(key) && index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++index];
            if (!result._options.TryGetValue(key, out var values))
                result._options[key] = values = [];
            values.Add(value);
        }
        return result;
    }

    public bool Flag(string name) =>
        _options.TryGetValue(name, out var values) &&
        values.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    public string? Optional(string name) =>
        _options.TryGetValue(name, out var values) ? values.LastOrDefault() : null;

    public string Require(string name) =>
        Optional(name) is { Length: > 0 } value && value != "true"
            ? value
            : throw new CliUsageException($"缺少 --{name} 参数。");

    public IReadOnlyList<string> Values(string name) =>
        _options.TryGetValue(name, out var values)
            ? values.Where(value => value != "true").ToArray()
            : Array.Empty<string>();
}

internal sealed class CliUsageException(string message) : Exception(message);
internal sealed class CliNotFoundException(string message) : Exception(message);
