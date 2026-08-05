using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Updater;

internal sealed record UpdateArguments(
    int ParentPid,
    DateTime ParentStartTimeUtc,
    string MsiPath,
    string ExpectedSha256,
    string ApplicationPath,
    string StatePath,
    string LogPath,
    string TargetVersion)
{
    public static UpdateArguments Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("更新器参数必须使用 --name value 格式。");
            if (!values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException($"更新器参数重复：{args[index]}");
        }

        if (!int.TryParse(Required(values, "--parent-pid"), out var parentPid) || parentPid <= 0)
            throw new ArgumentException("--parent-pid 无效。");
        if (!DateTime.TryParse(
                Required(values, "--parent-start-time-utc"),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parentStartTimeUtc))
            throw new ArgumentException("--parent-start-time-utc 无效。");
        parentStartTimeUtc = parentStartTimeUtc.ToUniversalTime();

        var expectedSha256 = Required(values, "--sha256").ToLowerInvariant();
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("--sha256 必须是 64 位十六进制值。");

        var targetVersion = Required(values, "--target-version");
        if (!Version.TryParse(targetVersion, out _))
            throw new ArgumentException("--target-version 无效。");

        return new UpdateArguments(
            parentPid,
            parentStartTimeUtc,
            RequiredAbsoluteFile(values, "--msi", mustExist: true, ".msi"),
            expectedSha256,
            RequiredAbsoluteFile(values, "--application", mustExist: true, ".exe"),
            RequiredAbsoluteFile(values, "--state", mustExist: false, ".json"),
            RequiredAbsoluteFile(values, "--log", mustExist: false, ".log"),
            targetVersion);
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"缺少更新器参数：{name}");

    private static string RequiredAbsoluteFile(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool mustExist,
        string extension)
    {
        var value = Required(values, name);
        if (!Path.IsPathFullyQualified(value) || value.Contains('"'))
            throw new ArgumentException($"{name} 必须是安全的绝对路径。");
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{name} 文件类型无效。");
        if (mustExist && !File.Exists(path))
            throw new FileNotFoundException($"{name} 文件不存在。", path);
        return path;
    }
}

internal sealed record UpdateResult(
    int SchemaVersion,
    string Status,
    string TargetVersion,
    int? InstallerExitCode,
    string Message,
    string LogPath,
    DateTimeOffset CompletedAtUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(UpdateResult))]
internal partial class UpdateJsonContext : JsonSerializerContext;

internal static class UpdateRunner
{
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(5);

    public static int Run(UpdateArguments options)
    {
        int? installerExitCode = null;
        try
        {
            WaitForParent(options.ParentPid, options.ParentStartTimeUtc, ParentExitTimeout);
            Directory.CreateDirectory(Path.GetDirectoryName(options.StatePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath)!);

            using var packageLock = new FileStream(
                options.MsiPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(packageLock)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualSha256),
                    Convert.FromHexString(options.ExpectedSha256)))
                throw new InvalidDataException("安装包 SHA-256 在启动安装前发生变化，已拒绝执行。");
            packageLock.Position = 0;

            using var installer = StartInstaller(options.MsiPath, options.LogPath);
            installer.WaitForExit();
            installerExitCode = installer.ExitCode;
            if (installer.ExitCode is not (0 or 3010))
                throw new InvalidOperationException($"Windows Installer 返回退出码 {installer.ExitCode}。");

            WriteResult(options.StatePath, new UpdateResult(
                1,
                "completed",
                options.TargetVersion,
                installer.ExitCode,
                installer.ExitCode == 3010 ? "更新已安装，Windows 建议重新启动。" : "更新安装成功。",
                options.LogPath,
                DateTimeOffset.UtcNow));
            StartApplication(options.ApplicationPath);
            return 0;
        }
        catch (Exception exception)
        {
            TryWriteFailure(options, installerExitCode, exception);
            if (exception is not TimeoutException)
                StartApplication(options.ApplicationPath);
            return exception is Win32Exception { NativeErrorCode: 1223 } ? 2 : 1;
        }
    }

    internal static void WaitForParent(
        int parentPid,
        DateTime expectedStartTimeUtc,
        TimeSpan timeout)
    {
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parentPid);
            var actualStartTimeUtc = parent.StartTime.ToUniversalTime();
            if (Math.Abs((actualStartTimeUtc - expectedStartTimeUtc).TotalSeconds) > 2)
                return;
            if (!parent.WaitForExit((int)timeout.TotalMilliseconds))
                throw new TimeoutException("主程序未在限定时间内退出，更新已取消。");
        }
        catch (ArgumentException)
        {
            // 主程序已经退出。
        }
        finally
        {
            parent?.Dispose();
        }
    }

    internal static Process StartInstaller(string msiPath, string logPath)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(systemDirectory, "msiexec.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\""
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows Installer。");
    }

    internal static void WriteResult(string path, UpdateResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(result, UpdateJsonContext.Default.UpdateResult));
        File.Move(temporaryPath, path, true);
    }

    private static void TryWriteFailure(UpdateArguments options, int? installerExitCode, Exception exception)
    {
        try
        {
            WriteResult(options.StatePath, new UpdateResult(
                1,
                "failed",
                options.TargetVersion,
                installerExitCode,
                $"{exception.GetType().Name}: {exception.Message}",
                options.LogPath,
                DateTimeOffset.UtcNow));
        }
        catch
        {
            // 失败状态无法写入时仍要尝试恢复启动原程序。
        }
    }

    private static void StartApplication(string applicationPath)
    {
        try
        {
            if (File.Exists(applicationPath))
                Process.Start(new ProcessStartInfo(applicationPath) { UseShellExecute = true });
        }
        catch
        {
            // MSI 日志与状态文件保留，用户仍可从快捷方式重启。
        }
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return UpdateRunner.Run(UpdateArguments.Parse(args));
        }
        catch
        {
            return 64;
        }
    }
}
