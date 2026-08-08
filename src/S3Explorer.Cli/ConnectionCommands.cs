using S3Explorer.Core;

namespace S3Explorer.Cli;

internal static class ConnectionCommands
{
    private const int OperationFailed = 4;

    public static async Task<AutomationCommandResult> RunTestAsync(
        string verb,
        CliArguments args,
        IProfileStore store,
        IS3StorageService storage,
        CancellationToken cancellationToken)
    {
        if (verb != "test")
            throw new CliUsageException(
                "用法：connection test <name-or-id> | connection test --profile <name-or-id>");
        var profileName = args.Optional("profile") is { Length: > 0 } optionProfile
            ? optionProfile
            : RequirePositional(
                args,
                2,
                "connection test <name-or-id> 或 connection test --profile <name-or-id>");
        var profile = ResolveProfile(await store.LoadAsync(cancellationToken), profileName);
        var result = await storage.TestConnectionAsync(profile, cancellationToken);
        if (!result.Success)
            return new AutomationCommandResult(OperationFailed, result, result.Message);

        var identityText = result.AwsIdentity is null
            ? string.Empty
            : $"\n源身份: {result.AwsIdentity.SourceIdentity}" +
              (string.IsNullOrWhiteSpace(result.AwsIdentity.TargetRoleArn)
                  ? string.Empty
                  : $"\n目标 Role: {result.AwsIdentity.TargetRoleArn}") +
              (result.AwsIdentity.Source == CredentialSourceKind.AwsAssumeRole
                  ? $"\nExternal ID: {(result.AwsIdentity.ExternalIdConfigured ? "已配置" : "未配置")}"
                  : string.Empty) +
              (result.AwsIdentity.SessionExpiresAtUtc is { } expiration
                  ? $"\n会话到期: {expiration.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
                  : string.Empty) +
              (result.AwsIdentity.UserLoginMayBeRequired
                  ? "\nSSO: 需要用户触发登录或刷新"
                  : string.Empty);
        var text =
            $"{result.Message}\n凭据来源: {result.CredentialSource ?? profile.CredentialSourceDisplayName}" +
            $"{identityText}\n耗时: {result.Elapsed.TotalMilliseconds:N0} ms\nBucket: {result.BucketCount}" +
            $"\nHTTP: {result.HttpStatusCode?.ToString() ?? "-"}";
        return new AutomationCommandResult(0, result, text);
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

    private static string RequirePositional(CliArguments args, int index, string usage) =>
        args.Positionals.Count > index && args.Positionals[index].Length > 0
            ? args.Positionals[index]
            : throw new CliUsageException($"用法：{usage}");
}
