using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CredentialPermissionMatrixModelTests
{
    [Fact]
    public void Build_AllMappedChecksPassed_ProducesPassedCellsAndLatestTimestamp()
    {
        var credential = Credential();
        var earlier = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        var later = DateTimeOffset.Parse("2026-08-21T10:00:00Z");
        var entries = new[]
        {
            Entry(credential, [Passed("ListBucket"), Passed("HeadObject")], earlier, "bucket/a"),
            Entry(credential, [Passed("PutObject"), Passed("DeleteObject"), Passed("DescribeUserDomains", "cdn")], later, "bucket/b"),
            Entry(credential, [Passed("PutObjectAcl"), Passed("RefreshObjectCaches/PushObjectCache", "cdn")], earlier, "bucket/c"),
            Entry(credential, [Passed("Authentication")], later, "cdn")
        };

        var rows = CredentialPermissionMatrixBuilder.Build(
            [credential],
            entries);

        var row = Assert.Single(rows);
        Assert.Equal(PermissionMatrixCellState.Passed, row.ListBucket);
        Assert.Equal(PermissionMatrixCellState.Passed, row.HeadObject);
        Assert.Equal(PermissionMatrixCellState.Passed, row.PutObject);
        Assert.Equal(PermissionMatrixCellState.Passed, row.DeleteObject);
        Assert.Equal(PermissionMatrixCellState.Passed, row.PutObjectAcl);
        Assert.Equal(PermissionMatrixCellState.Passed, row.CdnQueryOrAuthentication);
        Assert.Equal(PermissionMatrixCellState.Passed, row.RefreshOrPush);
        Assert.Equal(later, row.LastCheckedAtUtc);
    }

    [Fact]
    public void Build_DeniedHasHighestPriorityForPermissionColumn()
    {
        var credential = Credential();
        var entries = new[]
        {
            Entry(credential, [Passed("ListBucket"), Denied("HeadObject")], DateTimeOffset.Parse("2026-08-22T10:00:00Z"), "bucket")
        };

        var row = Assert.Single(CredentialPermissionMatrixBuilder.Build([credential], entries));

        Assert.Equal(PermissionMatrixCellState.Passed, row.ListBucket);
        Assert.Equal(PermissionMatrixCellState.Denied, row.HeadObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.DeleteObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObjectAcl);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.CdnQueryOrAuthentication);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.RefreshOrPush);
    }

    [Fact]
    public void Build_IndeterminateUnsupportedOrSkippedShowsQuestionMark()
    {
        var credential = Credential();
        var entries = new[]
        {
            Entry(credential, [Indeterminate("ListBucket"), Unsupported("HeadObject")], DateTimeOffset.Parse("2026-08-22T10:00:00Z"), "bucket"),
            Entry(credential, [Skipped("PutObject"), Passed("DeleteObject")], DateTimeOffset.Parse("2026-08-22T11:00:00Z"), "bucket")
        };

        var row = Assert.Single(CredentialPermissionMatrixBuilder.Build([credential], entries));

        Assert.Equal(PermissionMatrixCellState.Indeterminate, row.ListBucket);
        Assert.Equal(PermissionMatrixCellState.Indeterminate, row.HeadObject);
        Assert.Equal(PermissionMatrixCellState.Indeterminate, row.PutObject);
        Assert.Equal(PermissionMatrixCellState.Passed, row.DeleteObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObjectAcl);
    }

    [Fact]
    public void Build_GenericCdnAuthenticationMapsToCdnReadPermission()
    {
        var credential = Credential();
        var entries = new[]
        {
            Entry(credential, [Passed("Authentication", "cdn"), Passed("DeliveryEndpoint", "cdn")], DateTimeOffset.Parse("2026-08-22T12:00:00Z"), "cdn")
        };

        var row = Assert.Single(CredentialPermissionMatrixBuilder.Build([credential], entries));

        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.ListBucket);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.HeadObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.DeleteObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObjectAcl);
        Assert.Equal(PermissionMatrixCellState.Passed, row.CdnQueryOrAuthentication);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.RefreshOrPush);
        Assert.Equal(DateTimeOffset.Parse("2026-08-22T12:00:00Z"), row.LastCheckedAtUtc);
    }

    [Fact]
    public void Build_MultipleProfilesPerCredentialKeepLatestCheckTimeAndMergePermissionStates()
    {
        var credential = Credential();
        var scopeA = Entry(credential, [Passed("ListBucket"), Denied("HeadObject")], DateTimeOffset.Parse("2026-08-20T10:00:00Z"), "bucket/scope-a");
        var scopeB = Entry(credential, [Passed("PutObject"), Indeterminate("RefreshObjectCaches/PushObjectCache", "cdn")], DateTimeOffset.Parse("2026-08-22T10:00:00Z"), "bucket/scope-b");
        var entries = new[] { scopeA, scopeB };

        var row = Assert.Single(CredentialPermissionMatrixBuilder.Build([credential], entries));

        Assert.Equal(PermissionMatrixCellState.Passed, row.ListBucket);
        Assert.Equal(PermissionMatrixCellState.Denied, row.HeadObject);
        Assert.Equal(PermissionMatrixCellState.Passed, row.PutObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.DeleteObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObjectAcl);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.CdnQueryOrAuthentication);
        Assert.Equal(PermissionMatrixCellState.Indeterminate, row.RefreshOrPush);
        Assert.Equal(DateTimeOffset.Parse("2026-08-22T10:00:00Z"), row.LastCheckedAtUtc);
    }

    [Fact]
    public void Build_IncludesEmptyCredentialRowsEvenWithoutHistory()
    {
        var credential = Credential();

        var row = Assert.Single(CredentialPermissionMatrixBuilder.Build([credential], Array.Empty<PermissionCheckHistoryEntry>()));

        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.ListBucket);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.HeadObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.DeleteObject);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.PutObjectAcl);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.CdnQueryOrAuthentication);
        Assert.Equal(PermissionMatrixCellState.NotApplicable, row.RefreshOrPush);
        Assert.Equal(DateTimeOffset.MinValue, row.LastCheckedAtUtc);
    }

    private static CredentialProfile Credential() => new()
    {
        Name = "release",
        Provider = CredentialProviderKind.AlibabaCloud,
        Kind = CredentialKind.AccessKeyPair,
        AccessKeyId = "AKID",
        Secret = "secret"
    };

    private static PermissionCheckHistoryEntry Entry(
        CredentialProfile credential,
        PermissionCheck[] checks,
        DateTimeOffset checkedAtUtc,
        string targetScope)
    {
        return new PermissionCheckHistoryEntry(
            credential.Id,
            credential.Name,
            credential.Provider,
            credential.Kind,
            credential.Fingerprint,
            targetScope,
            MutationProbe: false,
            new PermissionCheckResult(credential.Id, checks)
            {
                CheckedAtUtc = checkedAtUtc
            });
    }

    private static PermissionCheck Passed(string name, string subject = "storage") =>
        new(subject, name, PermissionCheckState.Passed);

    private static PermissionCheck Denied(string name) =>
        new("storage", name, PermissionCheckState.Denied);

    private static PermissionCheck Indeterminate(string name, string subject = "storage") =>
        new(subject, name, PermissionCheckState.Indeterminate);

    private static PermissionCheck Unsupported(string name) =>
        new("storage", name, PermissionCheckState.Unsupported);

    private static PermissionCheck Skipped(string name) =>
        new("storage", name, PermissionCheckState.Skipped);
}
