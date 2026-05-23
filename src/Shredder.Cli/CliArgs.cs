using System.Globalization;

namespace Shredder.Cli;

/// <summary>CLI 子命令枚举。</summary>
internal enum CliCommand
{
    None,
    Shred,
    EmptyRecycle,
    FreeSpaceWipe,
}

/// <summary>
/// 命令行参数解析结果。手写解析器,避免引入 System.CommandLine 的额外依赖。
/// 规则:
/// <list type="bullet">
///   <item><c>--help / -h</c> 与 <c>--version</c> 优先级最高,出现即设置标志位、其它解析继续但不报错。</item>
///   <item>子命令互斥:<c>--empty-recycle</c> / <c>--free-space &lt;盘符&gt;</c> 与 "粉碎路径" 互斥。</item>
///   <item>未识别选项或缺少参数 → <see cref="UsageError"/> 非空,调用方按 ExitUsage(1) 退出。</item>
/// </list>
/// </summary>
internal sealed class CliArgs
{
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public string? UsageError { get; private set; }

    public CliCommand Command { get; private set; } = CliCommand.None;
    public IReadOnlyList<string> Paths => _paths;
    private readonly List<string> _paths = new();

    public string? AlgorithmId { get; private set; }
    public bool AssumeYes { get; private set; }
    public bool Quiet { get; private set; }
    public string? FreeSpaceDrive { get; private set; }

    /// <summary>仅做风险预检,绝不修改文件。</summary>
    public bool DryRun { get; private set; }

    /// <summary>等价于 DryRun,但语义偏向「解释会发生什么」。</summary>
    public bool Explain { get; private set; }

    /// <summary>是否生成审计报告;指定 --report-format / --report-dir 时也会自动启用。</summary>
    public bool Report { get; private set; }

    /// <summary>报告格式:json / html / both。null 表示沿用 appsettings。</summary>
    public string? ReportFormat { get; private set; }

    /// <summary>报告输出目录。null 表示沿用 appsettings(默认 %LOCALAPPDATA%\Shredder\Reports)。</summary>
    public string? ReportDir { get; private set; }

    public static CliArgs Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var r = new CliArgs();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.IsNullOrEmpty(a)) continue;

            switch (a)
            {
                case "--help":
                case "-h":
                case "/?":
                    r.ShowHelp = true;
                    break;

                case "--version":
                case "-V":
                    r.ShowVersion = true;
                    break;

                case "-y":
                case "--yes":
                    r.AssumeYes = true;
                    break;

                case "-q":
                case "--quiet":
                    r.Quiet = true;
                    break;

                case "--dry-run":
                    r.DryRun = true;
                    break;

                case "--explain":
                    r.Explain = true;
                    break;

                case "--report":
                    r.Report = true;
                    break;

                case "--report-format":
                    if (i + 1 >= args.Length)
                    {
                        r.UsageError = "--report-format 需要指定:json / html / both";
                        return r;
                    }
                    if (!TrySetReportFormat(r, args[++i], out var fmtError))
                    {
                        r.UsageError = fmtError;
                        return r;
                    }
                    break;

                case "--report-dir":
                    if (i + 1 >= args.Length)
                    {
                        r.UsageError = "--report-dir 需要指定目录路径";
                        return r;
                    }
                    r.ReportDir = args[++i];
                    r.Report = true;
                    break;

                case "--algo":
                case "-a":
                    if (i + 1 >= args.Length)
                    {
                        r.UsageError = $"{a} 需要一个算法名参数";
                        return r;
                    }
                    r.AlgorithmId = args[++i];
                    break;

                case "--empty-recycle":
                    if (r.Command != CliCommand.None && r.Command != CliCommand.EmptyRecycle)
                    {
                        r.UsageError = "子命令冲突:--empty-recycle 不能与其它子命令同时使用";
                        return r;
                    }
                    r.Command = CliCommand.EmptyRecycle;
                    break;

                case "--free-space":
                    if (r.Command != CliCommand.None && r.Command != CliCommand.FreeSpaceWipe)
                    {
                        r.UsageError = "子命令冲突:--free-space 不能与其它子命令同时使用";
                        return r;
                    }
                    if (i + 1 >= args.Length)
                    {
                        r.UsageError = "--free-space 需要指定盘符(例如 D:\\)";
                        return r;
                    }
                    r.Command = CliCommand.FreeSpaceWipe;
                    r.FreeSpaceDrive = args[++i];
                    break;

                default:
                    // 长选项的 = 形式:--algo=dod3
                    if (a.StartsWith("--algo=", StringComparison.Ordinal))
                    {
                        r.AlgorithmId = a.Substring("--algo=".Length);
                        break;
                    }
                    if (a.StartsWith("--report-format=", StringComparison.Ordinal))
                    {
                        if (!TrySetReportFormat(r, a.Substring("--report-format=".Length), out var fmtErrorEq))
                        {
                            r.UsageError = fmtErrorEq;
                            return r;
                        }
                        break;
                    }
                    if (a.StartsWith("--report-dir=", StringComparison.Ordinal))
                    {
                        r.ReportDir = a.Substring("--report-dir=".Length);
                        r.Report = true;
                        break;
                    }
                    if (a.StartsWith("--free-space=", StringComparison.Ordinal))
                    {
                        if (r.Command != CliCommand.None && r.Command != CliCommand.FreeSpaceWipe)
                        {
                            r.UsageError = "子命令冲突:--free-space 不能与其它子命令同时使用";
                            return r;
                        }
                        r.Command = CliCommand.FreeSpaceWipe;
                        r.FreeSpaceDrive = a.Substring("--free-space=".Length);
                        break;
                    }
                    if (a.StartsWith('-'))
                    {
                        r.UsageError = string.Format(CultureInfo.InvariantCulture, "未识别的选项:{0}", a);
                        return r;
                    }
                    // 位置参数:目标路径(隐式触发 Shred 子命令)
                    if (r.Command == CliCommand.None) r.Command = CliCommand.Shred;
                    else if (r.Command != CliCommand.Shred)
                    {
                        r.UsageError = $"子命令冲突:在 {DescribeCommand(r.Command)} 下不接受位置参数 {a}";
                        return r;
                    }
                    r._paths.Add(a);
                    break;
            }
        }

        // 帮助 / 版本 优先,直接返回
        if (r.ShowHelp || r.ShowVersion) return r;

        // 收尾校验
        if (r.Command == CliCommand.Shred && r._paths.Count == 0)
        {
            r.UsageError = "未指定任何要粉碎的路径";
            return r;
        }
        if (r.Command == CliCommand.None)
        {
            r.UsageError = "未指定任何操作";
            return r;
        }

        return r;
    }

    private static string DescribeCommand(CliCommand c) => c switch
    {
        CliCommand.Shred => "粉碎路径",
        CliCommand.EmptyRecycle => "--empty-recycle",
        CliCommand.FreeSpaceWipe => "--free-space",
        _ => c.ToString(),
    };

    /// <summary>
    /// 将用户传入的 --report-format 值规范化到 json / html / both。
    /// 任何非空值都隐式启用 <see cref="Report"/>,避免「指定了格式却忘了 --report」。
    /// </summary>
    private static bool TrySetReportFormat(CliArgs r, string raw, out string? error)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (v)
        {
            case "json":
            case "html":
            case "both":
                r.ReportFormat = v;
                r.Report = true;
                error = null;
                return true;
            default:
                error = $"--report-format 取值无效:{raw}。允许:json / html / both。";
                return false;
        }
    }
}
