namespace S3Explorer.App;

internal sealed record AutomationOptions
{
    public bool Enabled => !string.IsNullOrWhiteSpace(StatePath);
    public bool Smoke { get; init; }
    public string StatePath { get; init; } = string.Empty;
    public string ReportPath { get; init; } = string.Empty;
    public string ScreenshotPath { get; init; } = string.Empty;
    public string DataDirectory { get; init; } = string.Empty;
    public string InstanceKey { get; init; } = string.Empty;

    public static AutomationOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new AutomationOptions();

        var options = new AutomationOptions();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--automation-smoke":
                    options = options with { Smoke = true };
                    break;
                case "--automation-state":
                    options = options with { StatePath = ReadAbsolutePath(args, ref index, "--automation-state") };
                    break;
                case "--automation-report":
                    options = options with { ReportPath = ReadAbsolutePath(args, ref index, "--automation-report") };
                    break;
                case "--automation-screenshot":
                    options = options with { ScreenshotPath = ReadAbsolutePath(args, ref index, "--automation-screenshot") };
                    break;
                case "--automation-data-dir":
                    options = options with { DataDirectory = ReadAbsolutePath(args, ref index, "--automation-data-dir") };
                    break;
                case "--automation-instance-key":
                    options = options with { InstanceKey = ReadInstanceKey(args, ref index) };
                    break;
                default:
                    throw new ArgumentException($"不支持的命令行参数: {args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.StatePath))
            throw new ArgumentException("自动化模式必须提供 --automation-state 绝对路径。");
        if (options.Smoke && (string.IsNullOrWhiteSpace(options.ReportPath) || string.IsNullOrWhiteSpace(options.ScreenshotPath)))
            throw new ArgumentException("UI 冒烟模式必须提供 --automation-report 和 --automation-screenshot。");

        return options;
    }

    private static string ReadInstanceKey(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("--automation-instance-key 缺少参数。");
        var value = args[index].Trim();
        if (value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("--automation-instance-key 只能包含 ASCII 字母、数字、点、短横线和下划线，且最长 128 个字符。");
        return value;
    }

    private static string ReadAbsolutePath(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} 缺少路径参数。");
        if (!Path.IsPathFullyQualified(args[index]))
            throw new ArgumentException($"{option} 必须使用绝对路径。");
        return Path.GetFullPath(args[index]);
    }
}

internal sealed record AutomationCheck(string Name, bool Passed, string Detail);

internal sealed record AutomationReport(
    bool Passed,
    string Version,
    string Title,
    int Width,
    int Height,
    IReadOnlyList<AutomationCheck> Checks);

internal sealed record AutomationState(
    string Status,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ProcessPath,
    string Version,
    string Title,
    long WindowHandle,
    bool Passed,
    string ReportPath,
    string ScreenshotPath,
    string Error);
