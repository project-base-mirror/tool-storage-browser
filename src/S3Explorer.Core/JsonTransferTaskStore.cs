using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Core;

public sealed class JsonTransferTaskStore : ITransferTaskStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JsonTransferTaskStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "transfers.json");
    }

    public async Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new TransferStoreSnapshot();

            try
            {
                await using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var snapshot = await JsonSerializer.DeserializeAsync<TransferStoreSnapshot>(
                    stream, _options, cancellationToken).ConfigureAwait(false)
                    ?? new TransferStoreSnapshot();
                snapshot.Validate();
                return snapshot;
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or ArgumentException)
            {
                PreserveCorruptStore();
                return new TransferStoreSnapshot();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("任务存储路径必须包含目录。");
            Directory.CreateDirectory(directory);

            var temporaryPath = _path + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, _options, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PreserveCorruptStore()
    {
        if (!File.Exists(_path))
            return;

        var backup = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(_path, backup, overwrite: false);
    }
}
