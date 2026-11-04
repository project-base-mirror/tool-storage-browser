using Microsoft.Win32;
using System.Reflection;

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
        if (TryGetInstallerRegistration(out var registeredKind, out _))
            return registeredKind;

        var selfContained = typeof(UpdatePackageDetector).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "S3Explorer.SelfContained")?.Value;
        return PortableKind(selfContained);
    }

    public static bool TryGetInstallerRegistration(
        out UpdatePackageKind packageKind,
        out string installLocation)
    {
        packageKind = UpdatePackageKind.PortableFrameworkDependent;
        installLocation = string.Empty;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(RegistryKeyPath, writable: false);
            var installerFlavor = key?.GetValue("InstallerFlavor") as string;
            var registeredLocation = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installerFlavor) || string.IsNullOrWhiteSpace(registeredLocation))
                return false;
            if (!Path.IsPathFullyQualified(registeredLocation))
                return false;
            var parsedKind = FromInstallerFlavor(installerFlavor);
            if (parsedKind is not (UpdatePackageKind.InstallerFrameworkDependent or UpdatePackageKind.InstallerSelfContained))
                return false;
            if (!MatchesApplicationDirectory(Environment.ProcessPath, registeredLocation))
                return false;
            packageKind = parsedKind;
            installLocation = Path.GetFullPath(registeredLocation);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static UpdatePackageKind PortableKind(string? selfContained) =>
        bool.TryParse(selfContained, out var value) && value
            ? UpdatePackageKind.PortableSelfContained
            : UpdatePackageKind.PortableFrameworkDependent;

    internal static bool MatchesApplicationDirectory(string? applicationPath, string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(applicationPath) || string.IsNullOrWhiteSpace(installLocation) ||
            !Path.IsPathFullyQualified(applicationPath) || !Path.IsPathFullyQualified(installLocation))
            return false;
        var applicationDirectory = Path.GetDirectoryName(Path.GetFullPath(applicationPath));
        return string.Equals(
            applicationDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
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
