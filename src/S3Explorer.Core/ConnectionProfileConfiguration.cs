namespace S3Explorer.Core;

public sealed record ConnectionGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public bool IsExpanded { get; init; } = true;

    public void Validate()
    {
        if (Id == Guid.Empty)
            throw new ArgumentException("连接分组 ID 不能为空。", nameof(Id));
        var name = Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("连接分组名称不能为空。", nameof(Name));
        if (name.Any(char.IsControl))
            throw new ArgumentException("连接分组名称不能包含控制字符。", nameof(Name));
        if (SortOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(SortOrder), "连接分组排序不能小于 0。");
    }
}

public sealed record ConnectionProfileConfiguration(
    IReadOnlyList<ConnectionProfile> Profiles,
    IReadOnlyList<ConnectionGroup> Groups)
{
    public static ConnectionProfileConfiguration Empty { get; } = new([], []);

    public ConnectionProfileConfiguration Normalize()
    {
        var groups = Groups
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => group with { Name = group.Name.Trim(), SortOrder = index })
            .ToArray();
        var groupIds = groups.Select(group => group.Id).ToHashSet();
        var profiles = Profiles
            .Select(profile => groupIds.Contains(profile.GroupId ?? Guid.Empty)
                ? profile
                : profile with { GroupId = null })
            .GroupBy(profile => profile.GroupId)
            .SelectMany(group => group
                .OrderBy(profile => profile.SortOrder)
                .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select((profile, index) => profile with { SortOrder = index }))
            .ToArray();
        return new(profiles, groups);
    }

    public void Validate()
    {
        if (Profiles is null || Groups is null)
            throw new InvalidDataException("连接配置包含空集合。");
        var groupIds = new HashSet<Guid>();
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            group.Validate();
            if (!groupIds.Add(group.Id))
                throw new InvalidDataException($"连接分组 ID 重复：{group.Id}。");
            if (!groupNames.Add(group.Name.Trim()))
                throw new InvalidDataException($"连接分组名称重复：{group.Name}。");
        }

        var profileIds = new HashSet<Guid>();
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            profile.Validate();
            if (!profileIds.Add(profile.Id))
                throw new InvalidDataException($"连接 ID 重复：{profile.Id}。");
            if (!profileNames.Add(profile.Name.Trim()))
                throw new InvalidDataException($"连接名称重复：{profile.Name}。");
            if (profile.GroupId is Guid groupId && !groupIds.Contains(groupId))
                throw new InvalidDataException($"连接“{profile.Name}”引用了不存在的分组：{groupId}。");
        }
    }

    public ConnectionProfileConfiguration RemoveGroup(Guid groupId) =>
        new ConnectionProfileConfiguration(
            Profiles.Select(profile => profile.GroupId == groupId
                ? profile with { GroupId = null }
                : profile).ToArray(),
            Groups.Where(group => group.Id != groupId).ToArray())
        .Normalize();

    public ConnectionProfileConfiguration MoveGroup(Guid groupId, int offset)
    {
        var ordered = Groups.OrderBy(group => group.SortOrder).ToList();
        var index = ordered.FindIndex(group => group.Id == groupId);
        if (index < 0) throw new ArgumentException("找不到要移动的连接分组。", nameof(groupId));
        var target = Math.Clamp(index + offset, 0, ordered.Count - 1);
        if (target == index) return Normalize();
        var value = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(target, value);
        return new ConnectionProfileConfiguration(
            Profiles,
            ordered.Select((group, order) => group with { SortOrder = order }).ToArray()).Normalize();
    }

    public ConnectionProfileConfiguration PlaceProfile(Guid profileId, Guid? groupId, int targetIndex)
    {
        if (groupId is Guid targetGroup && Groups.All(group => group.Id != targetGroup))
            throw new ArgumentException("目标连接分组不存在。", nameof(groupId));
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new ArgumentException("找不到要移动的连接。", nameof(profileId));
        var remaining = Profiles.Where(item => item.Id != profileId).ToList();
        var targetMembers = remaining
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var insertion = Math.Clamp(targetIndex, 0, targetMembers.Count);
        targetMembers.Insert(insertion, profile with { GroupId = groupId });
        var otherMembers = remaining.Where(item => item.GroupId != groupId);
        return new ConnectionProfileConfiguration(
            otherMembers.Concat(targetMembers.Select((item, order) => item with { SortOrder = order })).ToArray(),
            Groups).Normalize();
    }
}

public sealed record AwsIdentitySummary(
    CredentialSourceKind Source,
    string SourceIdentity,
    string? TargetRoleArn = null,
    bool ExternalIdConfigured = false,
    DateTimeOffset? SessionExpiresAtUtc = null,
    bool UserLoginMayBeRequired = false);
