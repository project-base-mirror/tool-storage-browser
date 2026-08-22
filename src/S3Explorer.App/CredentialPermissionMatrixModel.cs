using S3Explorer.Core;

namespace S3Explorer.App;

internal enum PermissionMatrixCellState
{
    Passed,
    Denied,
    Indeterminate,
    NotApplicable
}

internal static class PermissionMatrixCellStateExtensions
{
    public static string DisplaySymbol(this PermissionMatrixCellState state) => state switch
    {
        PermissionMatrixCellState.Passed => "√",
        PermissionMatrixCellState.Denied => "×",
        PermissionMatrixCellState.Indeterminate => "?",
        PermissionMatrixCellState.NotApplicable => "—",
        _ => "-"
    };
}

internal sealed record PermissionMatrixRow(
    CredentialProfile Credential,
    PermissionMatrixCellState ListBucket,
    PermissionMatrixCellState HeadObject,
    PermissionMatrixCellState GetObject,
    PermissionMatrixCellState PutObject,
    PermissionMatrixCellState DeleteObject,
    PermissionMatrixCellState PutObjectAcl,
    PermissionMatrixCellState CdnControlQuery,
    PermissionMatrixCellState RefreshOrPush,
    DateTimeOffset LastCheckedAtUtc);

internal static class CredentialPermissionMatrixBuilder
{
    public static IReadOnlyList<PermissionMatrixRow> Build(
        IReadOnlyList<CredentialProfile> credentials,
        IReadOnlyList<PermissionCheckHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(entries);

        var checksByCredential = entries
            .Where(entry => entry.CredentialId != Guid.Empty && entry.Result is not null)
            .GroupBy(entry => entry.CredentialId)
            .ToDictionary(group => group.Key, group => group.ToArray(), EqualityComparer<Guid>.Default);

        return credentials.Select(credential => BuildRow(
            credential,
            checksByCredential.GetValueOrDefault(credential.Id, Array.Empty<PermissionCheckHistoryEntry>()))).ToArray();
    }

    private static PermissionMatrixRow BuildRow(
        CredentialProfile credential,
        IReadOnlyList<PermissionCheckHistoryEntry> entries)
    {
        var allChecks = entries.SelectMany(entry => entry.Result.Checks).ToArray();
        return new PermissionMatrixRow(
            credential,
            AggregateCell(allChecks, "storage", "ListBucket"),
            AggregateCell(allChecks, "storage", "HeadObject"),
            AggregateCell(allChecks, "storage", "GetObject"),
            AggregateCell(allChecks, "storage", "PutObject"),
            AggregateCell(allChecks, "storage", "DeleteObject"),
            AggregateCell(allChecks, "storage", "PutObjectAcl"),
            AggregateCell(
                allChecks,
                ("cdn-control", "DescribeUserDomains"),
                ("cdn-control", "ControlEndpoint")),
            AggregateCell(
                allChecks,
                ("cdn-control", "RefreshObjectCaches/PushObjectCache"),
                ("cdn-control", "Purge")),
            LastChecked(entries));
    }

    private static PermissionMatrixCellState AggregateCell(
        IEnumerable<PermissionCheck> checks,
        string subject,
        string permissionName) => AggregateCell(checks, (subject, permissionName));

    private static PermissionMatrixCellState AggregateCell(
        IEnumerable<PermissionCheck> checks,
        params (string Subject, string Name)[] permissions)
    {
        var matched = checks
            .Where(check => permissions.Any(permission =>
                string.Equals(check.Subject, permission.Subject, StringComparison.Ordinal) &&
                string.Equals(check.Name, permission.Name, StringComparison.Ordinal)))
            .ToArray();

        if (matched.Length == 0)
            return PermissionMatrixCellState.NotApplicable;

        if (matched.Any(check => check.State == PermissionCheckState.Denied))
            return PermissionMatrixCellState.Denied;

        if (matched.Any(check =>
            check.State == PermissionCheckState.Indeterminate ||
            check.State == PermissionCheckState.Unsupported ||
            check.State == PermissionCheckState.Skipped))
            return PermissionMatrixCellState.Indeterminate;

        return PermissionMatrixCellState.Passed;
    }

    private static DateTimeOffset LastChecked(IReadOnlyList<PermissionCheckHistoryEntry> entries)
    {
        if (entries.Count == 0)
            return DateTimeOffset.MinValue;

        return entries.Max(entry => entry.CheckedAtUtc);
    }
}
