using System.Runtime.Versioning;

namespace S3Explorer.Infrastructure.Configuration;

[SupportedOSPlatform("windows")]
public static class ConfigurationRecovery
{
    /// <summary>
    /// Validates an encrypted canonical configuration through an isolated copy
    /// before replacing the target. Neither the selected source nor quarantined
    /// target files are modified, including when validation fails.
    /// </summary>
    public static async Task<ExplorerConfigurationStore> RestoreFromFileAsync(
        string dataRoot,
        string sourcePath,
        IConfigurationPayloadProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var stagingRoot = System.IO.Path.Combine(dataRoot, ".configuration-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stagingRoot);
            File.Copy(sourcePath, System.IO.Path.Combine(stagingRoot, "configuration.json"));
            var source = await ExplorerConfigurationStore.OpenAsync(stagingRoot, protector, cancellationToken)
                .ConfigureAwait(false);
            var configuration = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
            return await ExplorerConfigurationStore.CreateOrReplaceAsync(dataRoot, configuration, protector, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Staging contains encrypted bytes only. A cleanup failure must not
            // turn a completed restore into an apparent failure/repeated reset.
            try
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
