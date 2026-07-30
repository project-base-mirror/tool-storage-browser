using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record ConfigurationSnapshot(
    IReadOnlyList<ConnectionProfile> Profiles,
    CdnConfiguration CdnConfiguration,
    IReadOnlyList<CdnCredential> CdnCredentials,
    IReadOnlyList<ConnectionGroup>? ProfileGroups = null);

internal sealed class ConfigurationTransactionInterruptedException(string message) : Exception(message);

internal sealed class ConfigurationTransactionCoordinator
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IProfileStore _profileStore;
    private readonly ICdnConfigurationStore _cdnConfigurationStore;
    private readonly ICdnCredentialStore _cdnCredentialStore;
    private readonly ICredentialProtector _protector;
    private readonly DurableJsonFile _journalFile;
    private readonly Action<int>? _afterCommitStep;

    public ConfigurationTransactionCoordinator(
        IProfileStore profileStore,
        ICdnConfigurationStore cdnConfigurationStore,
        ICdnCredentialStore cdnCredentialStore,
        ICredentialProtector protector,
        string journalPath,
        Action<int>? afterCommitStep = null)
    {
        _profileStore = profileStore;
        _cdnConfigurationStore = cdnConfigurationStore;
        _cdnCredentialStore = cdnCredentialStore;
        _protector = protector;
        _journalFile = new DurableJsonFile(journalPath);
        _afterCommitStep = afterCommitStep;
    }

    public async Task SaveAsync(
        ConfigurationSnapshot previous,
        ConfigurationSnapshot target,
        CancellationToken cancellationToken = default)
    {
        Validate(previous);
        Validate(target);
        var journal = CreateJournal(ConfigurationRecoveryMode.Commit, previous, target);
        await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyAsync(target, cancellationToken).ConfigureAwait(false);
            DeleteJournalFiles();
        }
        catch (ConfigurationTransactionInterruptedException)
        {
            throw;
        }
        catch (Exception saveException)
        {
            try
            {
                await SaveJournalAsync(
                    journal with { RecoveryMode = ConfigurationRecoveryMode.Rollback },
                    CancellationToken.None).ConfigureAwait(false);
                await ApplyAsync(previous, CancellationToken.None).ConfigureAwait(false);
                DeleteJournalFiles();
            }
            catch (Exception rollbackException)
            {
                throw new IOException(
                    "配置事务保存失败，自动回滚也未完成；下次启动会继续恢复导入前配置。",
                    new AggregateException(saveException, rollbackException));
            }

            throw new IOException("配置事务保存失败，已恢复保存前配置。", saveException);
        }
    }

    public async Task<bool> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_journalFile.Path) && !File.Exists(_journalFile.Path + ".bak"))
            return false;

        var journal = await _journalFile.LoadAsync<ConfigurationTransactionJournal>(
            static () => throw new InvalidDataException("配置事务日志缺少内容。"),
            Options,
            ValidateJournal,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var snapshot = DeserializeSnapshot(journal.RecoveryMode == ConfigurationRecoveryMode.Commit
            ? journal.ProtectedTarget
            : journal.ProtectedPrevious);
        Validate(snapshot);
        await ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        DeleteJournalFiles();
        return true;
    }

    private async Task ApplyAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _profileStore.SaveConfigurationAsync(
            new ConnectionProfileConfiguration(snapshot.Profiles, snapshot.ProfileGroups ?? []),
            cancellationToken).ConfigureAwait(false);
        _afterCommitStep?.Invoke(1);
        await _cdnCredentialStore.SaveAsync(snapshot.CdnCredentials, cancellationToken).ConfigureAwait(false);
        _afterCommitStep?.Invoke(2);
        await _cdnConfigurationStore.SaveAsync(snapshot.CdnConfiguration, cancellationToken).ConfigureAwait(false);
        _afterCommitStep?.Invoke(3);
    }

    private ConfigurationTransactionJournal CreateJournal(
        ConfigurationRecoveryMode mode,
        ConfigurationSnapshot previous,
        ConfigurationSnapshot target) =>
        new()
        {
            RecoveryMode = mode,
            ProtectedPrevious = _protector.Protect(JsonSerializer.Serialize(previous, Options)),
            ProtectedTarget = _protector.Protect(JsonSerializer.Serialize(target, Options))
        };

    private ConfigurationSnapshot DeserializeSnapshot(string protectedPayload)
    {
        var json = _protector.Unprotect(protectedPayload);
        return JsonSerializer.Deserialize<ConfigurationSnapshot>(json, Options)
            ?? throw new InvalidDataException("配置事务快照为空。");
    }

    private Task SaveJournalAsync(
        ConfigurationTransactionJournal journal,
        CancellationToken cancellationToken) =>
        _journalFile.SaveAsync(journal, Options, ValidateJournal, cancellationToken);

    private void DeleteJournalFiles()
    {
        foreach (var suffix in new[] { string.Empty, ".bak", ".tmp" })
        {
            var path = _journalFile.Path + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void Validate(ConfigurationSnapshot snapshot)
    {
        if (snapshot.Profiles is null || snapshot.CdnConfiguration is null || snapshot.CdnCredentials is null)
            throw new InvalidDataException("配置事务快照包含空集合。");
        new ConnectionProfileConfiguration(snapshot.Profiles, snapshot.ProfileGroups ?? []).Validate();
        CdnConfigurationValidator.EnsureValid(snapshot.CdnConfiguration, snapshot.CdnCredentials);
    }

    private static void ValidateJournal(ConfigurationTransactionJournal journal)
    {
        if (journal.Version != 1)
            throw new InvalidDataException($"不支持的配置事务日志版本：{journal.Version}。");
        if (string.IsNullOrWhiteSpace(journal.ProtectedPrevious) ||
            string.IsNullOrWhiteSpace(journal.ProtectedTarget))
            throw new InvalidDataException("配置事务日志缺少加密快照。");
    }

    private enum ConfigurationRecoveryMode
    {
        Commit,
        Rollback
    }

    private sealed record ConfigurationTransactionJournal
    {
        public int Version { get; init; } = 1;
        public ConfigurationRecoveryMode RecoveryMode { get; init; }
        public string ProtectedPrevious { get; init; } = string.Empty;
        public string ProtectedTarget { get; init; } = string.Empty;
    }
}
