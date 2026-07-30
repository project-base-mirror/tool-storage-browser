using Microsoft.Win32;

namespace S3Explorer.App;

internal enum UpdatePackageKind
{
    PortableFrameworkDependent,
    PortableSelfContained,
    InstallerFrameworkDependent,
    InstallerSelfContained
}

internal static class UpdatePackageDetector
{
    internal const string RegistryKeyPath = @"Software\project-base-mirror\S3 Explorer";

    public static UpdatePackageKind Detect()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(RegistryKeyPath, writable: false);
            var installerFlavor = key?.GetValue("InstallerFlavor") as string;
            if (!string.IsNullOrWhiteSpace(installerFlavor))
                return FromInstallerFlavor(installerFlavor);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Registry detection is optional. Restricted and portable builds can still update.
        }

        var processPath = Environment.ProcessPath;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (IsUnderDirectory(processPath, programFiles))
            return UpdatePackageKind.InstallerSelfContained;
        return UpdatePackageKind.PortableFrameworkDependent;
    }

    internal static UpdatePackageKind FromInstallerFlavor(string installerFlavor) =>
        installerFlavor.Trim().ToLowerInvariant() switch
        {
            "self-contained" => UpdatePackageKind.InstallerSelfContained,
            "framework-dependent" => UpdatePackageKind.InstallerFrameworkDependent,
            _ => UpdatePackageKind.PortableFrameworkDependent
        };

    internal static bool IsUnderDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            return false;
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public static string DisplayName(UpdatePackageKind kind) => kind switch
    {
        UpdatePackageKind.InstallerSelfContained => "安装版（自带 .NET 运行时）",
        UpdatePackageKind.InstallerFrameworkDependent => "安装版（依赖 .NET Desktop Runtime）",
        UpdatePackageKind.PortableSelfContained => "便携版（自带 .NET 运行时）",
        _ => "便携版（依赖 .NET Desktop Runtime）"
    };
}
