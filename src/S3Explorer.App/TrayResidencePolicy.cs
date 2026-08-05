namespace S3Explorer.App;

internal static class TrayResidencePolicy
{
    public static bool ShouldHideOnClose(
        bool enabled,
        bool automationEnabled,
        bool explicitExitRequested,
        CloseReason closeReason) =>
        enabled &&
        !automationEnabled &&
        !explicitExitRequested &&
        closeReason == CloseReason.UserClosing;

    public static bool ShouldHideOnMinimize(
        bool enabled,
        bool automationEnabled,
        bool closing) =>
        enabled && !automationEnabled && !closing;
}
