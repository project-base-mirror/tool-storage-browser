using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Contracts;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
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

        var output = parsed.Optional("output")?.Trim();
        var json = parsed.Flag("json") || string.Equals(output, "json", StringComparison.OrdinalIgnoreCase);
        CliFileLog? fileLog = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(output) &&
                !string.Equals(output, "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(output, "text", StringComparison.OrdinalIgnoreCase))
                throw new CliUsageException("--output 仅支持 json 或 text。");

            if (parsed.Positionals.Count == 0 || parsed.Positionals[0] is "help" or "--help" or "-h")
            {
                WriteHelp();
                ConsoleLaunchBehavior.PauseAfterHelpWhenDirectlyLaunched(args.Length);
                return 0;
            }
            if (parsed.Positionals[0] == "version")
            {
                parsed.EnsureOnly(GlobalOptions);
                WriteSuccess(json, CreateCompatibilityInfo(), $"s3explorer-cli {Version} · Contract API {ContractCompatibility.CurrentApiVersion} · Manifest Schema {PublishManifest.CurrentSchemaVersion}");
                return 0;
            }

            using var operationCancellation = CliCancellationScope.Create(parsed, cancellation.Token);
            fileLog = CliFileLog.Create(parsed.Optional("log-file"));
            var dataDirectory = parsed.Optional("data-dir") is { Length: > 0 } explicitDirectory
                ? RequireAbsolutePath(explicitDirectory, "--data-dir")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "S3Explorer");
            var profiles = new JsonProfileStore(new DpapiCredentialProtector(), Path.Combine(dataDirectory, "profiles.json"));
            var syncJobs = new JsonFolderSyncJobStore(Path.Combine(dataDirectory, "sync-jobs.json"));
            var cdnConfiguration = new JsonCdnConfigurationStore(Path.Combine(dataDirectory, "cdn-config.json"));
            var cdnCredentials = new JsonCdnCredentialStore(
                new DpapiCdnCredentialProtector(),
                Path.Combine(dataDirectory, "cdn-credentials.json"));
            var cdnDelivery = new GenericHttpCdnDeliveryService();
            var storage = new S3StorageService(new S3ClientFactory());
            var command = parsed.Positionals[0].ToLowerInvariant();
            var verb = parsed.Positionals.Count > 1 ? parsed.Positionals[1].ToLowerInvariant() : string.Empty;
            ValidateCommandOptions(command, verb, parsed);

            var exitCode = command switch
            {
                "profile" or "profiles" => await RunProfileAsync(verb, parsed, profiles, json, operationCancellation.Token),
                "connection" => await RunConnectionAsync(verb, parsed, profiles, storage, json, operationCancellation.Token),
                "bucket" or "buckets" => await RunBucketAsync(verb, parsed, profiles, storage, json, operationCancellation.Token),
                "object" or "objects" => await RunObjectAsync(verb, parsed, profiles, storage, json, operationCancellation.Token),
                "sync" => await RunSyncAsync(verb, parsed, profiles, syncJobs, storage, json, operationCancellation.Token),
                "upload" or "publish" or "verify" or "cdn" => await RunAutomationAsync(
                    command, verb, parsed, profiles, storage,
                    cdnConfiguration, cdnCredentials, cdnDelivery,
                    json, operationCancellation.Token),
                _ => throw new CliUsageException($"未知命令：{command}。运行 s3explorer-cli help 查看可用命令。")
            };
            fileLog.Write($"command={command} verb={verb} exitCode={exitCode}");
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            fileLog?.Write("cancelled exitCode=130");
            WriteError(json, "操作已取消。", 130);
            return 130;
        }
        catch (CliNotFoundException exception)
        {
            fileLog?.Write($"not-found exitCode={NotFound} message={exception.Message}");
            WriteError(json, exception.Message, NotFound);
            return NotFound;
        }
        catch (CliUsageException exception)
        {
            fileLog?.Write($"usage-error exitCode={UsageError} message={exception.Message}");
            WriteError(json, exception.Message, UsageError);
            return UsageError;
        }
        catch (Exception exception)
        {
            var message = SensitiveDataRedactor.Redact(exception.Message);
            fileLog?.Write($"operation-failed exitCode={OperationFailed} message={message}");
            WriteError(json, message, OperationFailed);
            return OperationFailed;
        }
        finally
        {
            fileLog?.Dispose();
        }
    }

    private static async Task<int> RunAutomationAsync(
        string command,
        string verb,
        CliArguments args,
        IProfileStore profileStore,
        IS3StorageService storage,
        ICdnConfigurationStore cdnConfigurationStore,
        ICdnCredentialStore cdnCredentialStore,
        ICdnDeliveryService cdnDeliveryService,
        bool json,
        CancellationToken cancellationToken)
    {
        var result = await AutomationCommands.RunAsync(
            command, verb, args, profileStore, storage,
            cdnConfigurationStore, cdnCredentialStore, cdnDeliveryService,
            json, cancellationToken);
        if (result.ExitCode == 0)
            WriteSuccess(json, result.Data, result.Text);
        else
            WriteOperationFailure(json, result.Text, result.ExitCode, result.Data);
        return result.ExitCode;
    }

    private static readonly string[] GlobalOptions =
    [
        "json", "output", "data-dir", "non-interactive", "timeout", "cancel-file", "log-file"
    ];

    private static readonly string[] TransferOptions =
    [
        "transfers", "multipart-concurrency", "upload-limit", "download-limit",
        "multipart-threshold", "part-size"
    ];

    internal static void ValidateCommandOptions(string command, string verb, CliArguments args)
    {
        string[]? commandOptions = (command, verb) switch
        {
            ("profile" or "profiles", "list" or "show" or "groups") => [],
            ("profile" or "profiles", "add") =>
            [
                "name", "type", "endpoint", "region", "credential-source", "aws-profile",
                "access-key", "secret-key", "secret-key-env", "session-token", "session-token-env",
                "source-profile", "role-arn", "role-session-name", "source-identity",
                "external-id", "external-id-env", "session-duration", "web-identity-token-file", "group",
                "default-bucket", "path-style", "ignore-certificate-errors"
            ],
            ("profile" or "profiles", "delete") => ["yes"],
            ("profile" or "profiles", "group-add") => ["name"],
            ("profile" or "profiles", "group-delete") => ["yes"],
            ("profile" or "profiles", "move") => ["group"],
            ("connection", "test") => ["profile"],
            ("bucket" or "buckets", "list") => ["profile"],
            ("object" or "objects", "list") => ["profile", "bucket", "prefix", "recursive"],
            ("object" or "objects", "versions") => ["page-size", "key-marker", "version-id-marker"],
            ("object" or "objects", "upload") => ["verify", .. TransferOptions],
            ("object" or "objects", "download") => ["recursive", "version-id", .. TransferOptions],
            ("object" or "objects", "delete") => ["recursive", "yes"],
            ("object" or "objects", "restore-version" or "delete-version") => ["version-id", "yes"],
            ("object" or "objects", "clean-delete-markers") => ["yes"],
            ("sync", "list" or "analyze") => [],
            ("sync", "add") =>
            ["name", "local", "remote", "direction", "exclude", "new-only", "changed-only", "delete", "hash"],
            ("sync", "run") => ["yes", .. TransferOptions],
            ("sync", "delete") => ["yes"],
            ("upload", _) => ["profile", "source", "bucket", "prefix", "verify", "header-rules", .. TransferOptions],
            ("publish", _) =>
            [
                "profile", "source", "bucket", "prefix", "project", "product", "version", "manifest",
                "header-rules", "delete-mode", "access", "full", "dry-run", "cdn-profile", "warmup", "yes", .. TransferOptions
            ],
            ("verify", _) => ["manifest", "profile", "bucket", "prefix", .. TransferOptions],
            ("cdn", "test" or "warmup" or "cache-test") => ["profile", "path", "manifest", "prefix", "include-manifest"],
            _ => null
        };
        if (commandOptions is not null)
            args.EnsureOnly(GlobalOptions.Concat(commandOptions));
    }

    private static async Task<int> RunProfileAsync(
        string verb,
        CliArguments args,
        IProfileStore store,
        bool json,
        CancellationToken cancellationToken)
    {
        var configuration = await store.LoadConfigurationAsync(cancellationToken);
        var profiles = configuration.Profiles.ToList();
        var groups = configuration.Groups.ToList();
        switch (verb)
        {
            case "list":
                var list = profiles.Select(profile => ProfileView(profile, GroupName(groups, profile.GroupId))).ToArray();
                WriteSuccess(json, list, list.Length == 0
                    ? "没有已保存的连接。"
                    : string.Join(Environment.NewLine, profiles
                        .OrderBy(item => item.GroupId is null ? int.MaxValue : groups.First(group => group.Id == item.GroupId).SortOrder)
                        .ThenBy(item => item.SortOrder)
                        .Select(item =>
                            $"{item.Name}\t{GroupName(groups, item.GroupId) ?? "未分组"}\t{S3ProviderCatalog.Get(item.ServiceType).DisplayName}\t{item.CredentialSourceDisplayName}\t{item.Endpoint}\t{item.Id}")));
                return 0;

            case "show":
                var shown = ResolveProfile(profiles, RequirePositional(args, 2, "profile show <name-or-id>"));
                WriteSuccess(json, ProfileView(shown, GroupName(groups, shown.GroupId)),
                    $"名称: {shown.Name}\n类型: {S3ProviderCatalog.Get(shown.ServiceType).DisplayName}\nEndpoint: {shown.Endpoint}\nRegion: {shown.EffectiveSignatureRegion}\n凭据来源: {shown.CredentialSourceDisplayName}" +
                    $"\n分组: {GroupName(groups, shown.GroupId) ?? "未分组"}" +
                    (shown.CredentialSource == CredentialSourceKind.StoredKeys ? $"\nAccess Key: {Mask(shown.AccessKey)}" : string.Empty) +
                    (shown.CredentialSource == CredentialSourceKind.AwsAssumeRole
                        ? $"\n源身份: {shown.AwsSourceProfileName}{(string.IsNullOrWhiteSpace(shown.AwsRoleSourceIdentity) ? string.Empty : $" / {shown.AwsRoleSourceIdentity}")}\n目标 Role: {shown.AwsRoleArn}\nExternal ID: {(string.IsNullOrWhiteSpace(shown.AwsExternalId) ? "未配置" : "已配置")}" : string.Empty) +
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
                if (credentialSource is CredentialSourceKind.AwsSharedProfile or CredentialSourceKind.AwsSso && awsProfileName.Length == 0)
                    throw new CliUsageException("--credential-source profile|sso 需要 --aws-profile <name>。");
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
                var groupId = ResolveOptionalGroup(groups, args.Optional("group"));
                var sessionDuration = ParseRoleSessionDuration(args.Optional("session-duration"));
                var profile = preset with
                {
                    GroupId = groupId,
                    SortOrder = profiles.Where(item => item.GroupId == groupId).Select(item => item.SortOrder).DefaultIfEmpty(-1).Max() + 1,
                    Name = name.Trim(),
                    Endpoint = args.Optional("endpoint") ?? definition.DefaultEndpoint,
                    Region = region,
                    SignatureRegion = signingRegion,
                    AccessKey = accessKey,
                    SecretKey = secretKey,
                    SessionToken = sessionToken,
                    CredentialSource = credentialSource,
                    AwsProfileName = credentialSource is CredentialSourceKind.AwsSharedProfile or CredentialSourceKind.AwsSso ? awsProfileName : string.Empty,
                    AwsSourceProfileName = credentialSource == CredentialSourceKind.AwsAssumeRole
                        ? args.Optional("source-profile")?.Trim() ?? string.Empty
                        : string.Empty,
                    AwsRoleArn = credentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                        ? args.Optional("role-arn")?.Trim() ?? string.Empty
                        : string.Empty,
                    AwsRoleSessionName = credentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                        ? args.Optional("role-session-name")?.Trim() ?? string.Empty
                        : string.Empty,
                    AwsRoleSourceIdentity = credentialSource == CredentialSourceKind.AwsAssumeRole
                        ? args.Optional("source-identity")?.Trim() ?? string.Empty
                        : string.Empty,
                    AwsExternalId = credentialSource == CredentialSourceKind.AwsAssumeRole
                        ? ResolveSecret(args, "external-id", "S3EXPLORER_AWS_EXTERNAL_ID", required: false)
                        : string.Empty,
                    AwsSessionDurationSeconds = sessionDuration,
                    AwsWebIdentityTokenFile = credentialSource == CredentialSourceKind.AwsWebIdentity
                        ? RequireAbsolutePath(args.Optional("web-identity-token-file") ?? string.Empty, "--web-identity-token-file")
                        : string.Empty,
                    DefaultBucket = args.Optional("default-bucket") ?? string.Empty,
                    AddressingStyle = args.Flag("path-style") ? AddressingStyle.PathStyle : preset.AddressingStyle,
                    IgnoreCertificateErrors = args.Flag("ignore-certificate-errors")
                };
                profile.Validate();
                profiles.Add(profile);
                await store.SaveConfigurationAsync(new ConnectionProfileConfiguration(profiles, groups).Normalize(), cancellationToken);
                WriteSuccess(json, ProfileView(profile, GroupName(groups, profile.GroupId)), $"已添加连接：{profile.Name} ({profile.Id})");
                return 0;

            case "delete":
                RequireConfirmation(args, "删除连接必须提供 --yes。");
                var deleted = ResolveProfile(profiles, RequirePositional(args, 2, "profile delete <name-or-id> --yes"));
                profiles.RemoveAll(item => item.Id == deleted.Id);
                await store.SaveConfigurationAsync(new ConnectionProfileConfiguration(profiles, groups).Normalize(), cancellationToken);
                WriteSuccess(json, new { deleted.Id, deleted.Name }, $"已删除连接：{deleted.Name}");
                return 0;

            case "groups":
                var groupList = groups.OrderBy(group => group.SortOrder)
                    .Select(group => new { group.Id, group.Name, group.SortOrder, connectionCount = profiles.Count(profile => profile.GroupId == group.Id) })
                    .ToArray();
                WriteSuccess(json, groupList, groupList.Length == 0
                    ? "没有连接分组。"
                    : string.Join(Environment.NewLine, groupList.Select(group => $"{group.Name}\t{group.connectionCount}\t{group.Id}")));
                return 0;

            case "group-add":
                var groupName = args.Require("name").Trim();
                if (groups.Any(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)))
                    throw new CliUsageException($"连接分组已存在：{groupName}");
                var addedGroup = new ConnectionGroup { Name = groupName, SortOrder = groups.Count };
                addedGroup.Validate();
                groups.Add(addedGroup);
                await store.SaveConfigurationAsync(new ConnectionProfileConfiguration(profiles, groups).Normalize(), cancellationToken);
                WriteSuccess(json, addedGroup, $"已添加连接分组：{addedGroup.Name} ({addedGroup.Id})");
                return 0;

            case "group-delete":
                RequireConfirmation(args, "删除连接分组必须提供 --yes。");
                var deletedGroup = ResolveGroup(groups, RequirePositional(args, 2, "profile group-delete <name-or-id> --yes"));
                var withoutGroup = new ConnectionProfileConfiguration(profiles, groups).RemoveGroup(deletedGroup.Id);
                await store.SaveConfigurationAsync(withoutGroup, cancellationToken);
                WriteSuccess(json, new { deletedGroup.Id, deletedGroup.Name }, $"已删除连接分组：{deletedGroup.Name}；其中连接已移到未分组。");
                return 0;

            case "move":
                var moved = ResolveProfile(profiles, RequirePositional(args, 2, "profile move <name-or-id> --group <name-or-id|->"));
                var targetGroup = ResolveOptionalGroup(groups, args.Require("group"));
                var movedConfiguration = new ConnectionProfileConfiguration(profiles, groups)
                    .PlaceProfile(moved.Id, targetGroup, int.MaxValue);
                await store.SaveConfigurationAsync(movedConfiguration, cancellationToken);
                WriteSuccess(json, ProfileView(movedConfiguration.Profiles.First(profile => profile.Id == moved.Id), GroupName(groups, targetGroup)),
                    $"已移动连接“{moved.Name}”到 {GroupName(groups, targetGroup) ?? "未分组"}。");
                return 0;

            default:
                throw new CliUsageException("用法：profile list | show | add | delete | groups | group-add | group-delete | move");
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
        if (verb != "test") throw new CliUsageException("用法：connection test <name-or-id> | connection test --profile <name-or-id>");
        var profileName = args.Optional("profile") is { Length: > 0 } optionProfile
            ? optionProfile
            : RequirePositional(args, 2, "connection test <name-or-id> 或 connection test --profile <name-or-id>");
        var profile = ResolveProfile(await store.LoadAsync(cancellationToken), profileName);
        var result = await storage.TestConnectionAsync(profile, cancellationToken);
        if (!result.Success)
        {
            WriteOperationFailure(json, result.Message, OperationFailed, result);
            return OperationFailed;
        }
        var identityText = result.AwsIdentity is null
            ? string.Empty
            : $"\n源身份: {result.AwsIdentity.SourceIdentity}" +
              (string.IsNullOrWhiteSpace(result.AwsIdentity.TargetRoleArn) ? string.Empty : $"\n目标 Role: {result.AwsIdentity.TargetRoleArn}") +
              (result.AwsIdentity.Source == CredentialSourceKind.AwsAssumeRole ? $"\nExternal ID: {(result.AwsIdentity.ExternalIdConfigured ? "已配置" : "未配置")}" : string.Empty) +
              (result.AwsIdentity.SessionExpiresAtUtc is { } expiration ? $"\n会话到期: {expiration.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : string.Empty) +
              (result.AwsIdentity.UserLoginMayBeRequired ? "\nSSO: 需要用户触发登录或刷新" : string.Empty);
        WriteSuccess(json, result,
            $"{result.Message}\n凭据来源: {result.CredentialSource ?? profile.CredentialSourceDisplayName}{identityText}\n耗时: {result.Elapsed.TotalMilliseconds:N0} ms\nBucket: {result.BucketCount}\nHTTP: {result.HttpStatusCode?.ToString() ?? "-"}");
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
        if (verb != "list") throw new CliUsageException("用法：bucket list <profile-name-or-id> | bucket list --profile <profile-name-or-id>");
        var profileName = args.Optional("profile") is { Length: > 0 } optionProfile
            ? optionProfile
            : RequirePositional(args, 2, "bucket list <profile-name-or-id> 或 bucket list --profile <profile-name-or-id>");
        var profile = ResolveProfile(await store.LoadAsync(cancellationToken), profileName);
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
                ConnectionProfile profile;
                S3Location location;
                if (args.Optional("profile") is { Length: > 0 } profileName)
                {
                    profile = ResolveProfile(profiles, profileName);
                    var bucket = args.Optional("bucket")?.Trim();
                    if (string.IsNullOrWhiteSpace(bucket)) bucket = profile.DefaultBucket?.Trim();
                    if (string.IsNullOrWhiteSpace(bucket))
                        throw new CliUsageException("objects list 使用选项模式时需要 --bucket，或连接必须配置默认 Bucket。");
                    var prefix = args.Optional("prefix")?.Replace('\\', '/').TrimStart('/') ?? string.Empty;
                    if (prefix.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Any(segment => segment is "." or ".."))
                        throw new CliUsageException($"远程前缀不安全：{prefix}");
                    location = new S3Location(profile.Name, bucket, S3Path.NormalizePrefix(prefix));
                }
                else
                {
                    (profile, location) = ResolveLocation(
                        profiles,
                        RequirePositional(args, 2,
                            "object list <s3-uri> 或 objects list --profile <name> --bucket <bucket> [--prefix <prefix>]"));
                }
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
                var transfer = CliTransferRuntime.Create(args);
                var localPath = RequireAbsolutePath(RequirePositional(args, 2, "object upload <local-path> <s3-uri>"), "local-path");
                var (profile, location) = ResolveLocation(profiles, RequirePositional(args, 3, "object upload <local-path> <s3-uri>"));
                var uploaded = await UploadPathAsync(
                    storage, profile, localPath, location.Bucket!, location.Prefix,
                    args.Flag("verify"), transfer, cancellationToken);
                WriteSuccess(
                    json, new { uploaded, verified = args.Flag("verify"), transfer = transfer.Settings },
                    $"上传完成：{uploaded:N0} 个文件" + (args.Flag("verify") ? "，远程回读验证通过" : string.Empty));
                return 0;
            }
            case "download":
            {
                var transfer = CliTransferRuntime.Create(args);
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
                        transfer.CreateContext(), cancellationToken);
                    WriteSuccess(json, new { downloaded = 1, versionId },
                        $"已下载指定版本：{location.Prefix} ({versionId})");
                    return 0;
                }
                var downloaded = await DownloadPathAsync(
                    storage, profile, location, target, args.Flag("recursive"), transfer, cancellationToken);
                WriteSuccess(json, new { downloaded, transfer = transfer.Settings }, $"下载完成：{downloaded:N0} 个文件");
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
                var transfer = CliTransferRuntime.Create(args);
                var executed = await ExecuteSyncPlanAsync(job, profile, plan, storage, transfer, cancellationToken);
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
        CliTransferRuntime transfer,
        CancellationToken cancellationToken)
    {
        var executed = 0;
        await transfer.ForEachAsync(
            plan.Items.Where(item => item.Action != FolderSyncAction.None),
            async (item, token) =>
        {
            var remoteKey = S3Path.Combine(job.Prefix, item.RelativePath);
            var localPath = LocalObjectPath.MapRelativeKey(job.LocalDirectory, item.RelativePath);
            switch (item.Action)
            {
                case FolderSyncAction.Upload:
                    await storage.UploadFileAsync(profile, job.Bucket, remoteKey, localPath, profile.DefaultStorageClass,
                        transfer.CreateContext(), token);
                    break;
                case FolderSyncAction.Download:
                    await storage.DownloadFileAsync(
                        profile, job.Bucket, remoteKey, localPath, transfer.CreateContext(), token);
                    break;
                case FolderSyncAction.DeleteRemote:
                    await storage.DeleteObjectsAsync(profile, job.Bucket, [remoteKey], token);
                    break;
                case FolderSyncAction.DeleteLocal:
                    if (File.Exists(localPath)) File.Delete(localPath);
                    break;
            }
            Interlocked.Increment(ref executed);
        }, cancellationToken);
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
        bool verify,
        CliTransferRuntime transfer,
        CancellationToken cancellationToken)
    {
        if (File.Exists(localPath))
        {
            var key = destination.EndsWith('/') || destination.Length == 0
                ? S3Path.Combine(destination, Path.GetFileName(localPath))
                : destination;
            var expectedSize = new FileInfo(localPath).Length;
            var expectedHash = verify
                ? await PublishManifestUtility.ComputeSha256Async(localPath, cancellationToken)
                : string.Empty;
            await storage.UploadFileAsync(
                profile, bucket, key, localPath, profile.DefaultStorageClass, transfer.CreateContext(), cancellationToken);
            if (verify)
                await CliRemoteVerifier.VerifyAsync(
                    storage, profile, bucket, key, expectedSize, expectedHash, transfer, cancellationToken);
            return 1;
        }
        if (!Directory.Exists(localPath)) throw new FileNotFoundException("本地路径不存在。", localPath);
        var rootName = new DirectoryInfo(localPath).Name;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var files = Directory.EnumerateFiles(localPath, "*", options).ToArray();
        await transfer.ForEachAsync(files, async (file, token) =>
        {
            var relative = Path.GetRelativePath(localPath, file).Replace('\\', '/');
            var key = S3Path.Combine(destination, $"{rootName}/{relative}");
            var expectedSize = new FileInfo(file).Length;
            var expectedHash = verify
                ? await PublishManifestUtility.ComputeSha256Async(file, token)
                : string.Empty;
            await storage.UploadFileAsync(
                profile, bucket, key, file, profile.DefaultStorageClass, transfer.CreateContext(), token);
            if (verify)
                await CliRemoteVerifier.VerifyAsync(
                    storage, profile, bucket, key, expectedSize, expectedHash, transfer, token);
        }, cancellationToken);
        return files.Length;
    }

    private static async Task<int> DownloadPathAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        S3Location location,
        string target,
        bool recursive,
        CliTransferRuntime transfer,
        CancellationToken cancellationToken)
    {
        if (recursive || location.Prefix.EndsWith('/'))
        {
            Directory.CreateDirectory(target);
            var items = await RecursiveObjectListing.ListFilesAsync(
                location.Prefix, 1000, ObjectListingLimits.DefaultCacheLimit,
                (prefix, token, ct) => storage.ListObjectsAsync(profile, location.Bucket!, prefix, token, 1000, ct),
                cancellationToken);
            await transfer.ForEachAsync(items, async (item, token) =>
            {
                var relative = item.Key[location.Prefix.Length..].TrimStart('/');
                await storage.DownloadFileAsync(profile, location.Bucket!, item.Key,
                    LocalObjectPath.MapRelativeKey(target, relative), transfer.CreateContext(), token);
            }, cancellationToken);
            return items.Count;
        }

        if (string.IsNullOrEmpty(location.Prefix)) throw new CliUsageException("下载单个对象时 URI 必须包含对象 Key。");
        if (Directory.Exists(target)) target = Path.Combine(target, S3Path.DisplayName(location.Prefix, false));
        await storage.DownloadFileAsync(
            profile, location.Bucket!, location.Prefix, target, transfer.CreateContext(), cancellationToken);
        return 1;
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
        "sso" or "iam-identity-center" => CredentialSourceKind.AwsSso,
        "assume-role" or "role" => CredentialSourceKind.AwsAssumeRole,
        "web-identity" or "oidc" => CredentialSourceKind.AwsWebIdentity,
        _ => throw new CliUsageException(
            $"不支持的凭据来源：{value}。可选 stored|profile|environment|container|instance|default|sso|assume-role|web-identity。")
    };

    private static int ParseRoleSessionDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 3600;
        if (!int.TryParse(value, out var seconds) || seconds is < 900 or > 43200)
            throw new CliUsageException("--session-duration 必须是 900–43200 的整数秒数。");
        return seconds;
    }

    private static Guid? ResolveOptionalGroup(IReadOnlyCollection<ConnectionGroup> groups, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-") return null;
        return ResolveGroup(groups, value).Id;
    }

    private static ConnectionGroup ResolveGroup(IEnumerable<ConnectionGroup> groups, string nameOrId)
    {
        var values = groups.ToArray();
        if (Guid.TryParse(nameOrId, out var id))
            return values.FirstOrDefault(item => item.Id == id)
                ?? throw new CliNotFoundException($"找不到连接分组：{nameOrId}");
        return values.FirstOrDefault(item => string.Equals(item.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliNotFoundException($"找不到连接分组：{nameOrId}");
    }

    private static string? GroupName(IEnumerable<ConnectionGroup> groups, Guid? groupId) =>
        groupId is null ? null : groups.FirstOrDefault(group => group.Id == groupId)?.Name;

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

    private static object ProfileView(ConnectionProfile profile, string? groupName = null) => new
    {
        profile.Id,
        profile.Name,
        type = S3ProviderCatalog.Get(profile.ServiceType).DisplayName,
        profile.Endpoint,
        region = profile.EffectiveSignatureRegion,
        credentialSource = profile.CredentialSourceDisplayName,
        group = groupName,
        awsProfile = profile.CredentialSource is CredentialSourceKind.AwsSharedProfile or CredentialSourceKind.AwsSso ? profile.AwsProfileName : null,
        sourceProfile = profile.CredentialSource == CredentialSourceKind.AwsAssumeRole ? profile.AwsSourceProfileName : null,
        roleArn = profile.CredentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity ? profile.AwsRoleArn : null,
        roleSessionName = profile.CredentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity ? profile.AwsRoleSessionName : null,
        sourceIdentity = profile.CredentialSource == CredentialSourceKind.AwsAssumeRole ? profile.AwsRoleSourceIdentity : null,
        externalIdConfigured = profile.CredentialSource == CredentialSourceKind.AwsAssumeRole && !string.IsNullOrWhiteSpace(profile.AwsExternalId),
        sessionDurationSeconds = profile.CredentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity ? (int?)profile.AwsSessionDurationSeconds : null,
        webIdentityTokenFile = profile.CredentialSource == CredentialSourceKind.AwsWebIdentity ? profile.AwsWebIdentityTokenFile : null,
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
        if (args.Flag("yes")) return;
        if (args.Flag("non-interactive") || Console.IsInputRedirected)
            throw new CliUsageException(message);

        Console.Write($"{message.Replace("必须提供 --yes。", string.Empty).Trim()} 继续？[y/N] ");
        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new OperationCanceledException();
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

    internal static CliCompatibilityInfo CreateCompatibilityInfo() => new()
    {
        Version = Version
    };

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

        连接与对象:
          s3explorer-cli profiles list [--output json]
          s3explorer-cli profile show <name-or-id> [--output json]
          s3explorer-cli profile add --name <name> --type <amazon|compatible|google|minio|r2|b2|aliyun|tencent|supabase>
              [--endpoint <url>] [--region <region>]
              [--credential-source <stored|profile|environment|container|instance|default|sso|assume-role|web-identity>]
              [--aws-profile <name>] [--source-profile <name>] [--role-arn <arn>] [--role-session-name <name>]
              [--source-identity <value>] [--external-id-env <ENV_NAME>] [--session-duration <seconds>]
              [--web-identity-token-file <absolute-path>] [--group <name-or-id>]
              [--access-key <key>] [--secret-key-env <ENV_NAME>]
          s3explorer-cli profile delete <name-or-id> --yes
          s3explorer-cli profile groups
          s3explorer-cli profile group-add --name <name>
          s3explorer-cli profile group-delete <name-or-id> --yes
          s3explorer-cli profile move <name-or-id> --group <name-or-id|->
          s3explorer-cli connection test --profile <name-or-id> [--output json]
          s3explorer-cli bucket list --profile <name-or-id> [--output json]
          s3explorer-cli objects list --profile <name> --bucket <bucket> [--prefix <prefix>] [--recursive]
          s3explorer-cli object list <s3://profile/bucket/prefix> [--recursive] [--output json]
          s3explorer-cli object versions <s3://profile/bucket/prefix> [--page-size <1-1000>]
              [--key-marker <key>] [--version-id-marker <id>] [--output json]
          s3explorer-cli object upload <absolute-local-path> <s3://profile/bucket/key> [--verify]
          s3explorer-cli object download <s3://profile/bucket/key> <absolute-local-path> [--recursive|--version-id <id>]
          s3explorer-cli object delete <s3://profile/bucket/key> [--recursive] --yes
          s3explorer-cli object restore-version <s3://profile/bucket/key> --version-id <id> --yes
          s3explorer-cli object delete-version <s3://profile/bucket/key> --version-id <id> --yes
          s3explorer-cli object clean-delete-markers <s3://profile/bucket/prefix> --yes

        发布自动化:
          s3explorer-cli upload --profile <name> --source <path> --bucket <bucket> [--prefix <prefix>] [--verify]
          s3explorer-cli publish --profile <name> --source <folder> --bucket <bucket> --prefix <version-prefix>
              [--project <name> --product <platform> --version <version>] [--manifest <path>]
              [--header-rules <json-file>] [--delete-mode none|mirror] [--access preserve|anonymous-read|private]
              [--full] [--dry-run]
              [--cdn-profile <name> --warmup]
          s3explorer-cli verify --manifest <publish-manifest.json> [--profile <name>] [--bucket <bucket>] [--prefix <prefix>]
          s3explorer-cli cdn test --profile <cdn-name> (--path <path> | --manifest <file>)
          s3explorer-cli cdn cache-test --profile <cdn-name> --path <path>
          s3explorer-cli cdn warmup --profile <cdn-name> (--path <path> | --manifest <file>) [--include-manifest]

        文件夹同步:
          s3explorer-cli sync list [--output json]
          s3explorer-cli sync add --name <name> --local <absolute-folder> --remote <s3-uri>
              [--direction upload|download] [--exclude <glob>] [--new-only|--changed-only] [--delete] [--hash]
          s3explorer-cli sync analyze <name-or-id> [--output json]
          s3explorer-cli sync run <name-or-id> [--yes] [--output json]
          s3explorer-cli sync delete <name-or-id> --yes

        全局选项:
          --data-dir <absolute-path>  使用隔离的数据目录，适合自动化与测试
          --output <json|text>        输出稳定 JSON 或普通文本；--json 仍兼容
          --non-interactive           禁止交互提示，适合 Unity 与 CI
          --timeout <seconds>         1–86400 秒后取消
          --cancel-file <path>        文件出现时取消当前操作
          --log-file <path>           追加脱敏操作日志
          --yes                       确认破坏性操作；发布可交互确认

        传输选项:
          --transfers <1-32>                文件级并发数，默认 4
          --multipart-concurrency <1-32>    单个分片上传并发数，默认 4
          --multipart-threshold <MiB>       启用分片上传的阈值，默认 64
          --part-size <MiB>                 分片大小，默认 16
          --upload-limit <KiB/s>            总上传限速，0 表示不限速
          --download-limit <KiB/s>          总下载限速，0 表示不限速
          --verify                         upload/object upload 后回读并校验大小与 SHA-256；publish 始终验证

        凭据建议:
          优先使用 --secret-key-env <变量名> 或 S3EXPLORER_SECRET_KEY，避免密钥进入命令历史。
          AWS 外部来源只适用于 Amazon S3；SSO 浏览器令牌和 Web Identity token 内容不会写入连接文件。
          AssumeRole External ID 使用 --external-id-env 最安全；保存时使用 Windows DPAPI CurrentUser 加密。
        """);
}

internal sealed class CliArguments
{
    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yes", "recursive", "delete", "hash", "new-only", "changed-only",
        "path-style", "ignore-certificate-errors", "non-interactive", "warmup", "dry-run",
        "full", "include-manifest", "verify", "help"
    };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "output", "data-dir", "timeout", "cancel-file", "log-file",
        "profile", "bucket", "prefix", "name", "type", "endpoint", "region",
        "credential-source", "aws-profile", "access-key", "secret-key", "secret-key-env",
        "session-token", "session-token-env", "source-profile", "role-arn", "role-session-name",
        "source-identity", "external-id", "external-id-env", "session-duration", "web-identity-token-file", "group",
        "default-bucket", "direction", "local", "remote",
        "exclude", "page-size", "key-marker", "version-id-marker", "version-id", "source",
        "project", "product", "version", "manifest", "delete-mode", "access", "cdn-profile", "path",
        "header-rules",
        "transfers", "multipart-concurrency", "upload-limit", "download-limit",
        "multipart-threshold", "part-size"
    };
    private static readonly HashSet<string> RepeatableOptions = new(StringComparer.OrdinalIgnoreCase) { "exclude" };
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
            if (!FlagOptions.Contains(key) && !ValueOptions.Contains(key))
                throw new CliUsageException($"未知选项：--{key}。");
            if (result._options.ContainsKey(key) && !RepeatableOptions.Contains(key))
                throw new CliUsageException($"选项 --{key} 不能重复。");

            string value;
            if (FlagOptions.Contains(key)) value = "true";
            else
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new CliUsageException($"选项 --{key} 缺少值。");
                value = args[++index];
            }
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

    public void EnsureOnly(IEnumerable<string> allowedOptions)
    {
        var allowed = new HashSet<string>(allowedOptions, StringComparer.OrdinalIgnoreCase);
        var unsupported = _options.Keys.Where(key => !allowed.Contains(key)).OrderBy(key => key).ToArray();
        if (unsupported.Length > 0)
            throw new CliUsageException(
                $"当前命令不支持选项：{string.Join(", ", unsupported.Select(key => $"--{key}"))}。");
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
internal sealed class CliNotFoundException(string message) : Exception(message);

internal static class ConsoleLaunchBehavior
{
    public static void PauseAfterHelpWhenDirectlyLaunched(int argumentCount)
    {
        if (!ShouldPause(
                argumentCount,
                OperatingSystem.IsWindows(),
                Environment.UserInteractive,
                Console.IsInputRedirected,
                Console.IsOutputRedirected,
                GetAttachedConsoleProcessCount()))
            return;

        Console.WriteLine();
        Console.Write("按任意键退出...");
        _ = Console.ReadKey(intercept: true);
        Console.WriteLine();
    }

    internal static bool ShouldPause(
        int argumentCount,
        bool isWindows,
        bool isUserInteractive,
        bool isInputRedirected,
        bool isOutputRedirected,
        int attachedConsoleProcessCount) =>
        argumentCount == 0 &&
        isWindows &&
        isUserInteractive &&
        !isInputRedirected &&
        !isOutputRedirected &&
        attachedConsoleProcessCount == 1;

    private static int GetAttachedConsoleProcessCount()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var processIds = new uint[2];
        return checked((int)GetConsoleProcessList(processIds, (uint)processIds.Length));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processIds, uint processCount);
}
