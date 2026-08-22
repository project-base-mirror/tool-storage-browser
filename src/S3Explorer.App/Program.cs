using System.Diagnostics;
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
            var runtime = ApplicationRuntimeContext.Resolve(
                options,
                developmentMode: IsDebugBuild || Debugger.IsAttached,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using var singleInstance = string.IsNullOrEmpty(runtime.InstanceKey)
                ? null
                : SingleInstanceCoordinator.Acquire(runtime.InstanceKey);
            if (singleInstance is { IsPrimary: false })
                return 0;

            ApplicationConfiguration.Initialize();
            automation = options.Enabled ? new AutomationSession(options) : null;

            var dataRoot = runtime.DataRoot;
            if (runtime.DevelopmentMode)
            {
                var productionRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ApplicationRuntimeContext.ProductionDataDirectoryName);
                DevelopmentConfigurationSnapshot.RefreshAsync(
                        productionRoot,
                        dataRoot)
                    .GetAwaiter()
                    .GetResult();
            }
            var configurationStore = ExplorerConfigurationStore
                .OpenAsync(dataRoot)
                .GetAwaiter()
                .GetResult();
            var profileStore = new ExplorerProfileStore(configurationStore);
            var cdnConfigurationStore = new ExplorerCdnConfigurationStore(configurationStore);
            var credentialStore = new ExplorerCredentialStore(configurationStore);
            var storageService = new S3StorageService(new S3ClientFactory());
            var settingsStore = new AppSettingsStore(Path.Combine(dataRoot, "settings.json"));
            var permissionCheckHistoryStore = new PermissionCheckHistoryStore(
                Path.Combine(dataRoot, "permission-check-history.json"));
            var logger = new SimpleFileLogger(Path.Combine(runtime.LocalDataRoot, "logs"));
            var transferStore = new JsonTransferTaskStore(Path.Combine(dataRoot, "transfers.json"));
            var syncJobStore = new JsonFolderSyncJobStore(Path.Combine(dataRoot, "sync-jobs.json"));
            var cdnDeliveryService = new GenericHttpCdnDeliveryService();
            var cdnJobStore = new JsonCdnJobStore(Path.Combine(dataRoot, "cdn-jobs.json"));
            var cdnJobExecutor = new StoreBackedCdnJobExecutor(
                configurationStore,
                [new GenericHttpCdnProvider(cdnDeliveryService), new AliyunCdnProvider()]);
            var cdnJobQueue = new PersistentCdnJobQueue(cdnJobStore, cdnJobExecutor);
            var cdnCertificateInspector = new TlsCdnCertificateInspector();
            using var updateChecker = new GitHubUpdateChecker(
                cachePath: Path.Combine(runtime.LocalDataRoot, "update-cache.json"));
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
                automation,
                permissionCheckHistoryStore,
                runtime.DevelopmentMode);
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

    private static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
