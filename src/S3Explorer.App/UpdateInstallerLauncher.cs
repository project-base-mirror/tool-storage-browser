using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace S3Explorer.App;

internal sealed record UpdateInstallResult(
    string Status,
    Version TargetVersion,
    int? InstallerExitCode,
    string Message,
    string LogPath,
    DateTimeOffset CompletedAtUtc)
{
    public bool Succeeded => string.Equals(Status, "completed", StringComparison.Ordinal);
}

internal static class UpdateInstallerLauncher
{
    private const string UpdaterFileName = "S3Explorer.Updater.exe";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "S3Explorer",
        "updates",
        "update-result.json");

    public static bool CanApply(GitHubReleaseInfo release)
    {
        if (!release.HasVerifiedInstallerDownload ||
            !UpdatePackageDetector.TryGetInstallerRegistration(out var installedKind, out var installLocation) ||
            installedKind != release.RecommendedPackage)
            return false;

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return false;
        var registeredApplication = Path.Combine(installLocation, "S3Explorer.exe");
        if (!string.Equals(
                Path.GetFullPath(processPath),
                Path.GetFullPath(registeredApplication),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return File.Exists(Path.Combine(AppContext.BaseDirectory, UpdaterFileName));
    }

    public static Process Launch(VerifiedUpdatePackage package)
    {
        var sourceUpdater = Path.Combine(AppContext.BaseDirectory, UpdaterFileName);
        var applicationPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定主程序路径。");
        if (!File.Exists(sourceUpdater))
            throw new FileNotFoundException("安装版维护程序不存在。", sourceUpdater);

        var runnerDirectory = Path.Combine(
            Path.GetDirectoryName(package.PackagePath)!,
            "runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runnerDirectory);
        var updaterPath = Path.Combine(runnerDirectory, UpdaterFileName);
        File.Copy(sourceUpdater, updaterPath, overwrite: false);
        VerifyCopiedUpdater(sourceUpdater, updaterPath);

        var statePath = StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        if (File.Exists(statePath))
            File.Delete(statePath);
        var logPath = Path.Combine(
            Path.GetDirectoryName(package.PackagePath)!,
            $"install-{package.Version.ToString(3)}.log");

        using var current = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = runnerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "--parent-pid", Environment.ProcessId.ToString(),
            "--parent-start-time-utc", current.StartTime.ToUniversalTime().ToString("O"),
            "--msi", package.PackagePath,
            "--sha256", package.Sha256,
            "--application", applicationPath,
            "--state", statePath,
            "--log", logPath,
            "--target-version", package.Version.ToString(3)
        })
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动安装版维护程序。");
    }

    public static UpdateInstallResult? TryConsumeResult()
    {
        var path = StatePath;
        if (!File.Exists(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > 64 * 1024)
                throw new InvalidDataException("升级结果文件大小无效。");
            var payload = File.ReadAllText(path);
            var result = ParseResult(payload);
            File.Delete(path);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            try
            {
                File.Move(path, path + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", true);
            }
            catch
            {
                // 保留原文件供诊断，启动不应因此失败。
            }
            return null;
        }
    }

    internal static UpdateInstallResult ParseResult(string payload)
    {
        var document = JsonSerializer.Deserialize<UpdateResultDocument>(payload, JsonOptions)
            ?? throw new InvalidDataException("升级结果为空。");
        if (document.SchemaVersion != 1 ||
            document.Status is not ("completed" or "failed") ||
            !Version.TryParse(document.TargetVersion, out var version) ||
            string.IsNullOrWhiteSpace(document.Message) ||
            string.IsNullOrWhiteSpace(document.LogPath) ||
            !Path.IsPathFullyQualified(document.LogPath) ||
            document.CompletedAtUtc == default)
            throw new InvalidDataException("升级结果内容无效。");
        return new UpdateInstallResult(
            document.Status,
            version,
            document.InstallerExitCode,
            document.Message.Trim(),
            Path.GetFullPath(document.LogPath),
            document.CompletedAtUtc);
    }

    private static void VerifyCopiedUpdater(string sourcePath, string destinationPath)
    {
        using var source = File.OpenRead(sourcePath);
        using var destination = File.OpenRead(destinationPath);
        var sourceHash = SHA256.HashData(source);
        var destinationHash = SHA256.HashData(destination);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            throw new InvalidDataException("维护程序复制校验失败。");
    }

    private sealed class UpdateResultDocument
    {
        public int SchemaVersion { get; set; }
        public string Status { get; set; } = string.Empty;
        public string TargetVersion { get; set; } = string.Empty;
        public int? InstallerExitCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public string LogPath { get; set; } = string.Empty;
        public DateTimeOffset CompletedAtUtc { get; set; }
    }
}
