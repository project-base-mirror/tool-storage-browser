using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using S3Explorer.Infrastructure.Configuration;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AutomationSession? automation = null;
        MainForm? form = null;
        try
        {
            var options = AutomationOptions.Parse(args);
            var instanceKey = options.Enabled
                ? options.InstanceKey
                : SingleInstanceCoordinator.ApplicationInstanceKey;
            using var singleInstance = string.IsNullOrEmpty(instanceKey)
                ? null
                : SingleInstanceCoordinator.Acquire(instanceKey);
            if (singleInstance is { IsPrimary: false })
                return 0;

            ApplicationConfiguration.Initialize();
            automation = options.Enabled ? new AutomationSession(options) : null;

            var dataRoot = options.Enabled
                ? options.DataDirectory.Length > 0
                    ? options.DataDirectory
                    : Path.Combine(Path.GetDirectoryName(options.StatePath)!, "data")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "S3Explorer");
            var configurationStore = ExplorerConfigurationStore
                .OpenAsync(dataRoot)
                .GetAwaiter()
                .GetResult();
            var profileStore = new ExplorerProfileStore(configurationStore);
            var cdnConfigurationStore = new ExplorerCdnConfigurationStore(configurationStore);
            var credentialStore = new ExplorerCredentialStore(configurationStore);
            var storageService = new S3StorageService(new S3ClientFactory());
            var settingsStore = new AppSettingsStore(
                options.Enabled ? Path.Combine(dataRoot, "settings.json") : null);
            var logger = new SimpleFileLogger(
                options.Enabled ? Path.Combine(dataRoot, "logs") : null);
            var transferStore = new JsonTransferTaskStore(
                options.Enabled ? Path.Combine(dataRoot, "transfers.json") : null);
            var syncJobStore = new JsonFolderSyncJobStore(
                options.Enabled ? Path.Combine(dataRoot, "sync-jobs.json") : null);
            var cdnDeliveryService = new GenericHttpCdnDeliveryService();
            var cdnJobStore = new JsonCdnJobStore(
                options.Enabled ? Path.Combine(dataRoot, "cdn-jobs.json") : null);
            var cdnJobExecutor = new StoreBackedCdnJobExecutor(
                configurationStore,
                [new GenericHttpCdnProvider(cdnDeliveryService), new AliyunCdnProvider()]);
            var cdnJobQueue = new PersistentCdnJobQueue(cdnJobStore, cdnJobExecutor);
            var cdnCertificateInspector = new TlsCdnCertificateInspector();
            using var updateChecker = new GitHubUpdateChecker(
                cachePath: options.Enabled ? Path.Combine(dataRoot, "update-cache.json") : null);
            var transferRuntime = new TransferRuntimeConfiguration();
            var transferExecutor = new S3TransferTaskExecutor(
                profileStore,
                storageService,
                transferRuntime,
                cdnConfigurationStore,
                logger);
            var transferQueue = new PersistentTransferQueue(transferStore, transferExecutor);

            Application.ThreadException += (_, eventArgs) =>
            {
                logger.Error("UI thread exception", eventArgs.Exception);
                if (automation is not null)
                    automation.Fail(form, eventArgs.Exception);
                else
                    ErrorDialog.ShowException(null, "应用程序错误", "UI 线程", eventArgs.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                {
                    logger.Error("Unhandled exception", exception);
                    automation?.Fail(form, exception);
                }
            };

            logger.Info($"S3 Explorer started. Version={Application.ProductVersion}");
            form = new MainForm(
                profileStore,
                storageService,
                settingsStore,
                logger,
                transferQueue,
                transferRuntime,
                syncJobStore,
                updateChecker,
                configurationStore,
                cdnConfigurationStore,
                credentialStore,
                cdnDeliveryService,
                cdnJobQueue,
                cdnCertificateInspector,
                automation);
            if (singleInstance is not null)
            {
                _ = form.Handle;
                singleInstance.StartListening(() =>
                {
                    if (form.IsDisposed || form.Disposing || !form.IsHandleCreated)
                        return;
                    try
                    {
                        form.BeginInvoke(new Action(form.ActivateFromSecondaryInstance));
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
            }
            Application.Run(form);
            return Environment.ExitCode;
        }
        catch (Exception exception)
        {
            automation?.Fail(form, exception);
            if (automation is null)
                MessageBox.Show(exception.Message, "S3 Explorer 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
