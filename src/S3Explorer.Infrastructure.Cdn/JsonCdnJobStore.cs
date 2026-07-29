using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class JsonCdnJobStore : ICdnJobStore, IRecoveryAwareStore
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly DurableJsonFile _file;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JsonCdnJobStore(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        var resolvedPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "cdn-jobs.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _file = new DurableJsonFile(resolvedPath, _clock);
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await _file.LoadAsync(
            () => new CdnJobStoreSnapshot { AutomationStartedAt = _clock() },
            Options,
            static snapshot => snapshot.Validate(),
            useDefaultWhenUnrecoverable: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        CdnJobStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _file.SaveAsync(snapshot, Options, static value => value.Validate(), cancellationToken)
            .ConfigureAwait(false);
    }
}
