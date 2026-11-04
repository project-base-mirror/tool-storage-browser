using System.Text.Json;
using System.Text.Json.Nodes;

namespace S3Explorer.Core;

/// <summary>
/// Applies only records changed by an editor to the latest configuration. Call inside
/// IExplorerConfigurationStore.UpdateAsync so the comparison and commit share its lock.
/// </summary>
public static class ExplorerConfigurationMerger
{
    public static ExplorerConfiguration Merge(
        ExplorerConfiguration baseline, ExplorerConfiguration proposed, ExplorerConfiguration current)
    {
        var merged = current with
        {
            Storage = MergeStorage(baseline.Storage, proposed.Storage, current.Storage),
            Cdn = new CdnConfiguration(
                MergeRecords(baseline.Cdn.Profiles, proposed.Cdn.Profiles, current.Cdn.Profiles,
                    value => value.Id, "CDN 配置"),
                MergeRecords(baseline.Cdn.Bindings, proposed.Cdn.Bindings, current.Cdn.Bindings,
                    value => value.Id, "CDN 关联")),
            CredentialVault = MergeRecords(baseline.CredentialVault, proposed.CredentialVault, current.CredentialVault,
                value => value.Id, "凭据")
        };
        return merged.ToPersistentSnapshot().ResolveCredentialReferences();
    }

    public static ConnectionProfileConfiguration MergeStorage(
        ConnectionProfileConfiguration baseline,
        ConnectionProfileConfiguration proposed,
        ConnectionProfileConfiguration current)
    {
        baseline.ValidateConfiguration();
        proposed.ValidateConfiguration();
        current.ValidateConfiguration();
        var merged = new ConnectionProfileConfiguration(
            MergeRecords(baseline.Profiles, proposed.Profiles, current.Profiles,
                value => value.Id, "连接", ProfilesEquivalent),
            MergeRecords(baseline.Groups, proposed.Groups, current.Groups,
                value => value.Id, "连接分组"));
        // Do not let Normalize silently detach concurrent additions from a deleted group.
        merged.ValidateConfiguration();
        return merged.Normalize();
    }

    private static IReadOnlyList<T> MergeRecords<T>(
        IReadOnlyList<T> baseline, IReadOnlyList<T> proposed, IReadOnlyList<T> current,
        Func<T, Guid> id, string label, Func<T, T, bool>? equivalent = null) where T : class
    {
        equivalent ??= Equivalent;
        var before = baseline.ToDictionary(id);
        var after = proposed.ToDictionary(id);
        var latest = current.ToDictionary(id);
        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var original);
            after.TryGetValue(key, out var edited);
            if (original is not null && edited is not null && equivalent(original, edited)) continue;
            latest.TryGetValue(key, out var saved);
            var unchanged = original is null ? saved is null : saved is not null && equivalent(original, saved);
            var alreadyApplied = edited is null ? saved is null : saved is not null && equivalent(edited, saved);
            if (!unchanged && !alreadyApplied)
                throw new InvalidOperationException($"{label}（{key}）已被其他窗口或 CLI 修改。请重新加载后再试；本次更改未保存。");
            if (edited is null) latest.Remove(key);
            else if (!alreadyApplied) latest[key] = edited;
        }
        return latest.Values.ToArray();
    }

    private static bool ProfilesEquivalent(ConnectionProfile left, ConnectionProfile right) =>
        Equivalent(WithoutResolvedSecrets(left), WithoutResolvedSecrets(right));

    private static ConnectionProfile WithoutResolvedSecrets(ConnectionProfile profile) => profile with
    {
        AccessKey = string.Empty,
        SecretKey = string.Empty,
        SessionToken = string.Empty,
        AwsExternalId = string.Empty
    };

    // Record equality alone compares list properties (e.g. ExternalBuckets) by reference.
    private static bool Equivalent<T>(T left, T right) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left), JsonSerializer.SerializeToNode(right));
}
