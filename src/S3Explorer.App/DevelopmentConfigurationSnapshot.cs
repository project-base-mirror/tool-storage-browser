using S3Explorer.Core;
using S3Explorer.Infrastructure.Configuration;

namespace S3Explorer.App;

/// <summary>
/// Refreshes the isolated Debug configuration from the installed application's
/// unified configuration. Secrets are decrypted only in memory and are written
/// back through <see cref="ExplorerConfigurationStore"/>, which protects them
/// with the current user's platform protector.
/// </summary>
internal static class DevelopmentConfigurationSnapshot
{
    public static async Task<bool> RefreshAsync(
        string productionRoot,
        string developmentRoot,
        IConfigurationPayloadProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(developmentRoot);

        var productionPath = Path.Combine(productionRoot, "configuration.json");
        var productionBackupPath = productionPath + ".bak";
        if (!File.Exists(productionPath) && !File.Exists(productionBackupPath))
            return false;

        var stagingRoot = Path.Combine(
            developmentRoot,
            ".configuration-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ExplorerConfigurationStore may repair a missing/corrupt primary
            // from its backup while opening. Read encrypted files through a
            // private staging copy so that repair can never mutate production.
            Directory.CreateDirectory(stagingRoot);
            if (File.Exists(productionPath))
            {
                File.Copy(productionPath, Path.Combine(stagingRoot, "configuration.json"), overwrite: true);
            }
            if (File.Exists(productionBackupPath))
            {
                File.Copy(
                    productionBackupPath,
                    Path.Combine(stagingRoot, "configuration.json.bak"),
                    overwrite: true);
            }

            var production = await ExplorerConfigurationStore.OpenAsync(
                    stagingRoot,
                    protector,
                    cancellationToken)
                .ConfigureAwait(false);
            var configuration = await production.LoadAsync(cancellationToken).ConfigureAwait(false);

            await ExplorerConfigurationStore.CreateOrReplaceAsync(
                    developmentRoot,
                    configuration,
                    protector,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A broken or incomplete production configuration must not prevent
            // Debug from starting with its previous isolated snapshot.
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // A stale encrypted staging directory is harmless and does not
                // contain plaintext; startup must not fail because cleanup lost
                // a race with an external scanner.
            }
        }
    }
}
