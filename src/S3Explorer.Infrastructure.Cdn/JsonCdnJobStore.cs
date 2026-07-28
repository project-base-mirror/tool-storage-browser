using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class JsonCdnJobStore : ICdnJobStore
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JsonCdnJobStore(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "cdn-jobs.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new CdnJobStoreSnapshot { AutomationStartedAt = _clock() };
            try
            {
                await using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var snapshot = await JsonSerializer.DeserializeAsync<CdnJobStoreSnapshot>(
                    stream, Options, cancellationToken).ConfigureAwait(false)
                    ?? new CdnJobStoreSnapshot { AutomationStartedAt = _clock() };
                snapshot.Validate();
                return snapshot;
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or ArgumentException)
            {
                PreserveCorruptStore();
                return new CdnJobStoreSnapshot { AutomationStartedAt = _clock() };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        CdnJobStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("CDN 任务存储路径必须包含目录。");
            Directory.CreateDirectory(directory);

            var temporaryPath = _path + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream, snapshot, Options, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PreserveCorruptStore()
    {
        if (!File.Exists(_path)) return;
        var backup = $"{_path}.corrupt-{_clock():yyyyMMddHHmmssfff}";
        File.Move(_path, backup, overwrite: false);
    }
}
