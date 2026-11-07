using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Core;

public sealed class JsonTransferTaskStore : ITransferTaskStore, IRecoveryAwareStore
{
    private readonly DurableJsonFile _file;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JsonTransferTaskStore(string? path = null)
    {
        _file = new DurableJsonFile(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "transfers.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await _file.LoadAsync(
            static () => new TransferStoreSnapshot(),
            _options,
            static snapshot => snapshot.Validate(),
            useDefaultWhenUnrecoverable: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        // Older applications know only one connection per task. Do not let them silently
        // execute a cross-connection copy against the source account after a downgrade.
        snapshot.Validate();
        snapshot = snapshot with { SchemaVersion = snapshot.Tasks.Any(task => task.IsCrossConnectionCopy) ? 2 : 1 };
        await _file.SaveAsync(snapshot, _options, static value => value.Validate(), cancellationToken)
            .ConfigureAwait(false);
    }
}
