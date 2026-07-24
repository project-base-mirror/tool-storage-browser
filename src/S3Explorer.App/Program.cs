using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var protector = new DpapiCredentialProtector();
        var profileStore = new JsonProfileStore(protector);
        var storageService = new S3StorageService(new S3ClientFactory());
        var settingsStore = new AppSettingsStore();
        var logger = new SimpleFileLogger();
        var transferStore = new JsonTransferTaskStore();
        var transferExecutor = new S3TransferTaskExecutor(profileStore, storageService);
        var transferQueue = new PersistentTransferQueue(transferStore, transferExecutor);

        Application.ThreadException += (_, args) =>
        {
            logger.Error("UI thread exception", args.Exception);
            ErrorDialog.ShowException(null, "应用程序错误", "UI 线程", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                logger.Error("Unhandled exception", exception);
        };

        logger.Info($"S3 Explorer started. Version={Application.ProductVersion}");
        Application.Run(new MainForm(profileStore, storageService, settingsStore, logger, transferQueue));
    }
}
