using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace S3Explorer.Core;

public sealed record JsonStoreRecoveryInfo(
    string PrimaryPath,
    string? CorruptPath,
    bool RestoredFromBackup,
    bool UsedDefault);

public interface IRecoveryAwareStore
{
    JsonStoreRecoveryInfo? LastRecovery { get; }
}

public sealed class DurableJsonFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _gate;
    private readonly Func<DateTimeOffset> _clock;

    public DurableJsonFile(string path, Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("JSON 存储路径不能为空。", nameof(path));

        _path = System.IO.Path.GetFullPath(path);
        _backupPath = _path + ".bak";
        _temporaryPath = _path + ".tmp";
        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Path => _path;
    public JsonStoreRecoveryInfo? LastRecovery { get; private set; }

    public async Task<T> LoadAsync<T>(
        Func<T> createDefault,
        JsonSerializerOptions options,
        Action<T>? validate = null,
        bool useDefaultWhenUnrecoverable = false,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(createDefault);
        ArgumentNullException.ThrowIfNull(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastRecovery = null;
            if (!File.Exists(_path))
            {
                if (!File.Exists(_backupPath))
                {
                    DeleteStaleTemporaryFile();
                    return createDefault();
                }

                try
                {
                    var recovered = await ReadAsync<T>(_backupPath, options, validate, cancellationToken)
                        .ConfigureAwait(false);
                    RestoreBackup();
                    LastRecovery = new JsonStoreRecoveryInfo(_path, null, true, false);
                    return recovered;
                }
                catch (Exception exception) when (IsRecoverableContentFailure(exception))
                {
                    var corruptBackup = PreserveCorruptFile(_backupPath, "backup");
                    LastRecovery = new JsonStoreRecoveryInfo(_path, corruptBackup, false, useDefaultWhenUnrecoverable);
                    if (useDefaultWhenUnrecoverable)
                        return createDefault();
                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
            }

            try
            {
                var value = await ReadAsync<T>(_path, options, validate, cancellationToken).ConfigureAwait(false);
                DeleteStaleTemporaryFile();
                return value;
            }
            catch (Exception primaryException) when (IsRecoverableContentFailure(primaryException))
            {
                var corruptPrimary = PreserveCorruptFile(_path, "primary");
                if (File.Exists(_backupPath))
                {
                    try
                    {
                        var recovered = await ReadAsync<T>(_backupPath, options, validate, cancellationToken)
                            .ConfigureAwait(false);
                        RestoreBackup();
                        LastRecovery = new JsonStoreRecoveryInfo(_path, corruptPrimary, true, false);
                        return recovered;
                    }
                    catch (Exception backupException) when (IsRecoverableContentFailure(backupException))
                    {
                        PreserveCorruptFile(_backupPath, "backup");
                    }
                }

                DeleteStaleTemporaryFile();
                LastRecovery = new JsonStoreRecoveryInfo(
                    _path, corruptPrimary, false, useDefaultWhenUnrecoverable);
                if (useDefaultWhenUnrecoverable)
                    return createDefault();
                ExceptionDispatchInfo.Capture(primaryException).Throw();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync<T>(
        T value,
        JsonSerializerOptions options,
        Action<T>? validate = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        validate?.Invoke(value);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("JSON 存储路径必须包含目录。");
            Directory.CreateDirectory(directory);

            try
            {
                await using (var stream = new FileStream(
                    _temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await ReadAsync<T>(_temporaryPath, options, validate, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(_path))
                    File.Copy(_path, _backupPath, overwrite: true);
                File.Move(_temporaryPath, _path, overwrite: true);
            }
            finally
            {
                DeleteFileIfPresent(_temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<T> ReadAsync<T>(
        string path,
        JsonSerializerOptions options,
        Action<T>? validate,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken)
            .ConfigureAwait(false);
        if (value is null)
            throw new InvalidDataException("JSON 存储文件为空或根节点为 null。");
        validate?.Invoke(value);
        return value;
    }

    private void RestoreBackup()
    {
        try
        {
            File.Copy(_backupPath, _temporaryPath, overwrite: true);
            using (var stream = new FileStream(
                _temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096,
                FileOptions.WriteThrough))
                stream.Flush(flushToDisk: true);
            File.Move(_temporaryPath, _path, overwrite: true);
        }
        finally
        {
            DeleteFileIfPresent(_temporaryPath);
        }
    }

    private string PreserveCorruptFile(string sourcePath, string label)
    {
        var corruptPath = $"{_path}.corrupt-{_clock():yyyyMMddHHmmssfff}-{label}-{Guid.NewGuid():N}";
        File.Move(sourcePath, corruptPath, overwrite: false);
        return corruptPath;
    }

    private void DeleteStaleTemporaryFile() => DeleteFileIfPresent(_temporaryPath);

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsRecoverableContentFailure(Exception exception) =>
        exception is JsonException or InvalidDataException or InvalidOperationException or
            ArgumentException or FormatException or CryptographicException;
}
