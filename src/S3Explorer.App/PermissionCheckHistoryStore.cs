using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.App;

/// <summary>非敏感的权限检查摘要；不保存任何凭据秘密。</summary>
internal sealed record PermissionCheckHistoryEntry(
    Guid CredentialId,
    string CredentialName,
    CredentialProviderKind Provider,
    CredentialKind Kind,
    string Fingerprint,
    string TargetScope,
    bool MutationProbe,
    PermissionCheckResult Result)
{
    public string Key => $"{CredentialId:N}|{TargetScope}";
    public DateTimeOffset CheckedAtUtc => Result.CheckedAtUtc;
    public int PassedCount => Result.CountByState(PermissionCheckState.Passed);
    public int DeniedCount => Result.CountByState(PermissionCheckState.Denied);
    public int IndeterminateCount => Result.CountByState(PermissionCheckState.Indeterminate);
    public int UnsupportedCount => Result.CountByState(PermissionCheckState.Unsupported);
    public int SkippedCount => Result.CountByState(PermissionCheckState.Skipped);
}

internal sealed class PermissionCheckHistoryStore : IRecoveryAwareStore
{
    private const int MaximumEntries = 100;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DurableJsonFile _file;

    public PermissionCheckHistoryStore(string? path = null)
    {
        _file = new DurableJsonFile(path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "permission-check-history.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;
    public string Path => _file.Path;

    public async Task<IReadOnlyList<PermissionCheckHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await _file.LoadAsync(
            static () => new HistoryDocument(),
            Options,
            Validate,
            useDefaultWhenUnrecoverable: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Sort(document.Entries.Select(ToEntry)).ToArray();
    }

    public async Task UpsertAsync(
        CredentialProfile credential,
        PermissionCheckReport report,
        bool mutationProbe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(report);
        credential.Validate();

        var entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        foreach (var result in report.Results)
        {
            var scope = Safe(result.TargetScope, 2000);
            var safeResult = RedactResult(result, scope);
            var entry = new PermissionCheckHistoryEntry(
                credential.Id,
                Safe(credential.Name, CredentialProfile.MaximumNameLength),
                credential.Provider,
                credential.Kind,
                Safe(credential.Fingerprint, 200),
                scope,
                mutationProbe,
                safeResult);
            entries.RemoveAll(value => value.CredentialId == entry.CredentialId &&
                string.Equals(value.TargetScope, entry.TargetScope, StringComparison.Ordinal));
            entries.Add(entry);
        }

        await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid credentialId, string targetScope,
        CancellationToken cancellationToken = default)
    {
        var entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        entries.RemoveAll(value => value.CredentialId == credentialId &&
            string.Equals(value.TargetScope, Safe(targetScope, 2000), StringComparison.Ordinal));
        await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(Array.Empty<PermissionCheckHistoryEntry>(), cancellationToken);

    private async Task SaveAsync(
        IEnumerable<PermissionCheckHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var document = new HistoryDocument { Entries = Sort(entries).Take(MaximumEntries).Select(ToDto).ToArray() };
        await _file.SaveAsync(document, Options, Validate, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<PermissionCheckHistoryEntry> Sort(
        IEnumerable<PermissionCheckHistoryEntry> entries) => entries
        .Where(value => value is not null && value.CredentialId != Guid.Empty)
        .OrderByDescending(value => value.CheckedAtUtc)
        .ThenBy(value => value.CredentialName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.TargetScope, StringComparer.Ordinal);

    private static PermissionCheckResult RedactResult(PermissionCheckResult result, string scope) =>
        result with
        {
            TargetScope = scope,
            Checks = result.Checks.Select(check => check with
            {
                Subject = Safe(check.Subject, 300),
                Name = Safe(check.Name, 300),
                Message = Safe(check.Message, 4000),
                ProviderCode = Safe(check.ProviderCode, 300),
                RequestId = Safe(check.RequestId, 300)
            }).ToArray()
        };

    private static string Safe(string? value, int maximum) =>
        SensitiveDataRedactor.Redact(value ?? string.Empty).Trim() is { } safe
            ? safe.Length <= maximum ? safe : safe[..maximum]
            : string.Empty;

    private static void Validate(HistoryDocument document)
    {
        if (document.Schema != 1)
            throw new InvalidDataException($"不支持的权限检查记录 Schema：{document.Schema}");
        if (document.Entries is null)
            throw new InvalidDataException("权限检查记录缺少 entries。");
        if (document.Entries.Count > MaximumEntries)
            document.Entries = document.Entries.Take(MaximumEntries).ToArray();
    }

    private static HistoryEntryDto ToDto(PermissionCheckHistoryEntry entry) => new()
    {
        CredentialId = entry.CredentialId,
        CredentialName = entry.CredentialName,
        Provider = entry.Provider,
        Kind = entry.Kind,
        Fingerprint = entry.Fingerprint,
        TargetScope = entry.TargetScope,
        MutationProbe = entry.MutationProbe,
        CheckedAtUtc = entry.CheckedAtUtc,
        Checks = entry.Result.Checks
    };

    private static PermissionCheckHistoryEntry ToEntry(HistoryEntryDto entry) => new(
        entry.CredentialId,
        Safe(entry.CredentialName, CredentialProfile.MaximumNameLength),
        entry.Provider,
        entry.Kind,
        Safe(entry.Fingerprint, 200),
        Safe(entry.TargetScope, 2000),
        entry.MutationProbe,
        new PermissionCheckResult(entry.CredentialId, entry.Checks ?? Array.Empty<PermissionCheck>())
        {
            TargetScope = Safe(entry.TargetScope, 2000),
            CheckedAtUtc = entry.CheckedAtUtc
        });

    private sealed class HistoryDocument
    {
        public int Schema { get; set; } = 1;
        public IReadOnlyList<HistoryEntryDto> Entries { get; set; } = Array.Empty<HistoryEntryDto>();
    }

    private sealed class HistoryEntryDto
    {
        public Guid CredentialId { get; set; }
        public string CredentialName { get; set; } = string.Empty;
        public CredentialProviderKind Provider { get; set; }
        public CredentialKind Kind { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public string TargetScope { get; set; } = string.Empty;
        public bool MutationProbe { get; set; }
        public DateTimeOffset CheckedAtUtc { get; set; }
        public IReadOnlyList<PermissionCheck> Checks { get; set; } = Array.Empty<PermissionCheck>();
    }
}
