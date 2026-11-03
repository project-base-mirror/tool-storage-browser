using System.Text.Json;

namespace S3Explorer.Core;

public enum CredentialReferenceKind
{
    StorageAccessKey,
    AwsExternalId,
    CdnControl
}

/// <summary>A non-secret description of a reference that would become incompatible.</summary>
public sealed record CredentialReferenceImpact(
    CredentialReferenceKind Kind,
    Guid ProfileId,
    string ProfileName,
    Guid CredentialId,
    string CredentialName)
{
    public string Display => $"{Kind switch
    {
        CredentialReferenceKind.StorageAccessKey => "对象存储",
        CredentialReferenceKind.AwsExternalId => "AWS External ID",
        _ => "CDN 控制面"
    }}：{ProfileName} ({ProfileId}) → {CredentialName}";
}

/// <summary>
/// An immutable preview of credential edits and the exact references approved for clearing.
/// Apply inside IExplorerConfigurationStore.UpdateAsync so checking and saving share its lock.
/// </summary>
public sealed class CredentialChangePlan
{
    private static readonly JsonSerializerOptions SnapshotOptions = new() { IgnoreReadOnlyProperties = true };
    private readonly IReadOnlyDictionary<Guid, CredentialProfile> _original;
    private readonly IReadOnlyDictionary<Guid, CredentialProfile> _proposed;
    private readonly IReadOnlyDictionary<CredentialReferenceImpact, string> _referenceSnapshots;

    private CredentialChangePlan(ExplorerConfiguration original, IReadOnlyList<CredentialProfile> proposed)
    {
        _original = original.CredentialVault.ToDictionary(value => value.Id);
        _proposed = proposed.ToDictionary(value => value.Id);
        var preview = original with { CredentialVault = proposed };
        IncompatibleReferences = Array.AsReadOnly(FindIncompatibleReferences(preview).ToArray());
        _referenceSnapshots = IncompatibleReferences.ToDictionary(value => value, value => Snapshot(original, value));
    }

    public IReadOnlyList<CredentialReferenceImpact> IncompatibleReferences { get; }

    public static CredentialChangePlan Create(
        ExplorerConfiguration original,
        IReadOnlyList<CredentialProfile> proposedCredentials)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(proposedCredentials);
        original.Validate();
        _ = new CredentialVault(proposedCredentials);
        var plan = new CredentialChangePlan(original, proposedCredentials.ToArray());
        // Validate the entire preview before asking the user to approve any destructive edit.
        // This does not modify original or save anything.
        plan.Apply(original, clearIncompatibleReferences: true);
        return plan;
    }

    public ExplorerConfiguration Apply(
        ExplorerConfiguration current,
        bool clearIncompatibleReferences = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        var credentials = current.CredentialVault.ToList();
        foreach (var original in _original.Values)
        {
            var proposed = _proposed.GetValueOrDefault(original.Id);
            if (proposed == original)
                continue;
            var latest = current.FindCredential(original.Id);
            if (latest != original)
                throw new InvalidOperationException($"凭据“{original.Name}”已被其他操作修改或删除，请重新打开并确认更改。");
            var index = credentials.FindIndex(value => value.Id == original.Id);
            if (proposed is null)
                credentials.RemoveAt(index); // Only an explicit deletion in the submitted editor changeset.
            else
                credentials[index] = proposed;
        }
        foreach (var added in _proposed.Values.Where(value => !_original.ContainsKey(value.Id)))
        {
            if (credentials.Any(value => value.Id == added.Id))
                throw new InvalidOperationException("凭据 ID 已被其他操作占用，请重新打开凭据中心。");
            credentials.Add(added);
        }
        _ = new CredentialVault(credentials);
        var next = current with { CredentialVault = credentials.ToArray() };
        var impacts = FindIncompatibleReferences(next).ToArray();
        foreach (var impact in impacts)
        {
            if (!_referenceSnapshots.TryGetValue(impact, out var approvedSnapshot) ||
                !string.Equals(approvedSnapshot, Snapshot(current, impact), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"引用已新增或变更：{impact.Display}。本次未保存任何更改，请重新查看并确认受影响引用。");
        }
        if (impacts.Length > 0 && !clearIncompatibleReferences)
            throw new InvalidOperationException("必须先确认清除以下不兼容引用：\n" +
                string.Join(Environment.NewLine, impacts.Select(value => value.Display)));

        var storageIds = impacts.Where(value => value.Kind == CredentialReferenceKind.StorageAccessKey)
            .Select(value => value.ProfileId).ToHashSet();
        var externalIds = impacts.Where(value => value.Kind == CredentialReferenceKind.AwsExternalId)
            .Select(value => value.ProfileId).ToHashSet();
        var cdnIds = impacts.Where(value => value.Kind == CredentialReferenceKind.CdnControl)
            .Select(value => value.ProfileId).ToHashSet();
        next = next with
        {
            Storage = new ConnectionProfileConfiguration(current.Storage.Profiles.Select(profile =>
            {
                var result = profile;
                if (storageIds.Contains(profile.Id))
                    result = result with
                    {
                        CredentialId = null,
                        AccessKey = string.Empty,
                        SecretKey = string.Empty,
                        SessionToken = string.Empty
                    };
                if (externalIds.Contains(profile.Id))
                    result = result with { AwsExternalIdCredentialId = null, AwsExternalId = string.Empty };
                return result;
            }).ToArray(), current.Storage.Groups),
            Cdn = current.Cdn with
            {
                Profiles = current.Cdn.Profiles.Select(profile => cdnIds.Contains(profile.Id)
                    ? profile with { ControlCredentialId = null }
                    : profile).ToArray()
            }
        };
        // Provider/kind compatibility alone does not validate values such as AssumeRole External ID.
        // Resolve the proposed vault before any caller can persist it, not just the old runtime values.
        _ = next.ResolveCredentialReferences();
        return next;
    }

    private static IEnumerable<CredentialReferenceImpact> FindIncompatibleReferences(ExplorerConfiguration configuration)
    {
        foreach (var profile in configuration.Storage.Profiles)
        {
            if (configuration.FindCredential(profile.CredentialId) is { } credential &&
                !credential.IsCompatibleWith(profile.ServiceType))
                yield return new(CredentialReferenceKind.StorageAccessKey, profile.Id, profile.Name, credential.Id, credential.Name);
            if (configuration.FindCredential(profile.AwsExternalIdCredentialId) is { } externalId &&
                (externalId.Provider != CredentialProviderKind.AmazonWebServices || externalId.Kind != CredentialKind.SecretValue))
                yield return new(CredentialReferenceKind.AwsExternalId, profile.Id, profile.Name, externalId.Id, externalId.Name);
        }
        foreach (var profile in configuration.Cdn.Profiles)
        {
            if (configuration.FindCredential(profile.ControlCredentialId) is { } credential &&
                !credential.IsCompatibleWith(profile.ProviderId))
                yield return new(CredentialReferenceKind.CdnControl, profile.Id, profile.Name, credential.Id, credential.Name);
        }
    }

    private static string Snapshot(ExplorerConfiguration configuration, CredentialReferenceImpact impact)
    {
        if (impact.Kind == CredentialReferenceKind.CdnControl)
            return JsonSerializer.Serialize(configuration.Cdn.Profiles.Single(value => value.Id == impact.ProfileId), SnapshotOptions);
        var profile = configuration.Storage.Profiles.Single(value => value.Id == impact.ProfileId);
        // Resolved runtime secrets may differ from the persisted form without changing a reference.
        return JsonSerializer.Serialize(profile with
        {
            AccessKey = string.Empty,
            SecretKey = string.Empty,
            SessionToken = string.Empty,
            AwsExternalId = string.Empty
        }, SnapshotOptions);
    }
}
