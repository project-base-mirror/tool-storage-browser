using S3Explorer.Core;

namespace S3Explorer.Cli;

internal sealed record CredentialUpdateResult(
    CredentialProfile Credential,
    IReadOnlyList<CredentialReferenceImpact> ClearedReferences);

internal static class CredentialUpdateCommand
{
    internal static async Task<CredentialUpdateResult> RunAsync(
        CliArguments args,
        IExplorerConfigurationStore store,
        Func<string, CredentialProviderKind> parseProvider,
        Func<string, CredentialKind> parseKind,
        CancellationToken cancellationToken)
    {
        if (args.Positionals.Count != 3)
            throw new CliUsageException("用法：credential update <name-or-id> [--name <name>] [--provider <provider>] [--kind <kind>] [--secret-env <ENV_NAME>]");
        var original = await store.LoadAsync(cancellationToken);
        var target = args.Positionals[2];
        var credential = Guid.TryParse(target, out var id)
            ? original.FindCredential(id)
            : original.CredentialVault.FirstOrDefault(value => string.Equals(value.Name, target, StringComparison.OrdinalIgnoreCase));
        if (credential is null)
            throw new CliNotFoundException($"找不到凭据：{target}");
        var kind = args.Optional("kind") is { } kindText ? parseKind(kindText) : credential.Kind;
        if (kind != CredentialKind.AccessKeyPair &&
            (args.Optional("access-key-id") is not null || args.Optional("session-token-env") is not null))
            throw new CliUsageException("--access-key-id 和 --session-token-env 仅适用于 AccessKeyPair。");
        if (kind != CredentialKind.CustomHeader && args.Optional("header") is not null)
            throw new CliUsageException("--header 仅适用于 CustomHeader。");
        var candidate = credential with
        {
            Name = args.Optional("name")?.Trim() ?? credential.Name,
            Provider = args.Optional("provider") is { } providerText ? parseProvider(providerText) : credential.Provider,
            Kind = kind,
            AccessKeyId = kind == CredentialKind.AccessKeyPair
                ? args.Optional("access-key-id")?.Trim() ?? credential.AccessKeyId : string.Empty,
            Secret = ReadSecret(args, "secret-env", credential.Secret),
            SessionToken = kind == CredentialKind.AccessKeyPair
                ? ReadSecret(args, "session-token-env", credential.SessionToken) : string.Empty,
            HeaderName = kind == CredentialKind.CustomHeader
                ? args.Optional("header")?.Trim() ?? credential.HeaderName : string.Empty
        };
        candidate.Validate();
        var plan = CredentialChangePlan.Create(original, original.CredentialVault
            .Select(value => value.Id == candidate.Id ? candidate : value).ToArray());
        var clearReferences = args.Flag("clear-incompatible-references");
        if ((plan.IncompatibleReferences.Count > 0 && !clearReferences) || (clearReferences && !args.Flag("yes")))
            throw new CliUsageException(
                (plan.IncompatibleReferences.Count > 0
                    ? "以下引用将不再兼容：\n" + string.Join(Environment.NewLine, plan.IncompatibleReferences.Select(value => value.Display)) + "\n"
                    : string.Empty) +
                "未保存任何更改。清除不兼容引用必须同时提供 --clear-incompatible-references --yes。凭据本身不会删除。");
        try
        {
            IReadOnlyList<CredentialReferenceImpact> cleared = [];
            var updated = await store.UpdateAsync(current =>
            {
                var next = plan.Apply(current, clearReferences);
                cleared = plan.IncompatibleReferences.Where(impact =>
                    StillReferenced(current, impact) && !StillReferenced(next, impact)).ToArray();
                return next;
            }, cancellationToken);
            return new(updated.FindCredential(candidate.Id)!, cleared);
        }
        catch (InvalidOperationException exception)
        {
            throw new CliUsageException(exception.Message);
        }
    }

    private static string ReadSecret(CliArguments args, string option, string fallback)
    {
        if (args.Optional(option) is not { } environmentName)
            return fallback;
        environmentName = environmentName.Trim();
        var value = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrEmpty(value))
            throw new CliUsageException($"环境变量 {environmentName} 未设置或为空。");
        return value;
    }

    private static bool StillReferenced(ExplorerConfiguration configuration, CredentialReferenceImpact impact) => impact.Kind switch
    {
        CredentialReferenceKind.StorageAccessKey => configuration.Storage.Profiles.Any(value => value.Id == impact.ProfileId && value.CredentialId == impact.CredentialId),
        CredentialReferenceKind.AwsExternalId => configuration.Storage.Profiles.Any(value => value.Id == impact.ProfileId && value.AwsExternalIdCredentialId == impact.CredentialId),
        _ => configuration.Cdn.Profiles.Any(value => value.Id == impact.ProfileId && value.ControlCredentialId == impact.CredentialId)
    };
}
