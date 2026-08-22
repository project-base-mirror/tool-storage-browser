namespace S3Explorer.App;

internal sealed record ApplicationRuntimeContext(
    string InstanceKey,
    string DataRoot,
    string LocalDataRoot,
    bool DevelopmentMode)
{
    public const string DevelopmentInstanceKey = "S3Explorer.App.Development";
    public const string ProductionDataDirectoryName = "S3Explorer";
    public const string DevelopmentDataDirectoryName = "S3Explorer.Debug";

    public static ApplicationRuntimeContext Resolve(
        AutomationOptions automation,
        bool developmentMode,
        string roamingApplicationData,
        string localApplicationData)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingApplicationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        if (automation.Enabled)
        {
            var dataRoot = automation.DataDirectory.Length > 0
                ? automation.DataDirectory
                : Path.Combine(Path.GetDirectoryName(automation.StatePath)!, "data");
            return new ApplicationRuntimeContext(
                automation.InstanceKey,
                Path.GetFullPath(dataRoot),
                Path.GetFullPath(dataRoot),
                DevelopmentMode: false);
        }

        return developmentMode
            ? new ApplicationRuntimeContext(
                DevelopmentInstanceKey,
                Path.Combine(roamingApplicationData, DevelopmentDataDirectoryName),
                Path.Combine(localApplicationData, DevelopmentDataDirectoryName),
                DevelopmentMode: true)
            : new ApplicationRuntimeContext(
                SingleInstanceCoordinator.ApplicationInstanceKey,
                Path.Combine(roamingApplicationData, ProductionDataDirectoryName),
                Path.Combine(localApplicationData, ProductionDataDirectoryName),
                DevelopmentMode: false);
    }
}
