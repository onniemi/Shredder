using Shredder.Cli;
using Xunit;

namespace Shredder.Tests.Cli;

/// <summary>
/// 覆盖 <see cref="CliArgs.Parse"/> 的全部分支。Parse 是手写状态机,改一处很容易破坏另一处,
/// 所以每条规则单独立案,出问题时一眼能看出是哪条分支回归。
/// </summary>
public class CliArgsTests
{
    [Fact]
    public void Parse_NullArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CliArgs.Parse(null!));
    }

    [Fact]
    public void Parse_Empty_SetsUsageError_NoCommand()
    {
        var r = CliArgs.Parse(Array.Empty<string>());
        Assert.False(r.ShowHelp);
        Assert.False(r.ShowVersion);
        Assert.Equal(CliCommand.None, r.Command);
        Assert.False(string.IsNullOrEmpty(r.UsageError));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Parse_Help_VariantsAllSetShowHelp(string flag)
    {
        var r = CliArgs.Parse(new[] { flag });
        Assert.True(r.ShowHelp);
        // help/version 优先于 "未指定任何操作" 校验,UsageError 应该是 null
        Assert.Null(r.UsageError);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-V")]
    public void Parse_Version_VariantsSetShowVersion(string flag)
    {
        var r = CliArgs.Parse(new[] { flag });
        Assert.True(r.ShowVersion);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_SinglePath_ImpliesShredCommand()
    {
        var r = CliArgs.Parse(new[] { @"C:\foo.txt" });
        Assert.Equal(CliCommand.Shred, r.Command);
        Assert.Equal(new[] { @"C:\foo.txt" }, r.Paths);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_MultiplePaths_AllCollected()
    {
        var r = CliArgs.Parse(new[] { @"D:\a", @"D:\b", @"D:\c" });
        Assert.Equal(CliCommand.Shred, r.Command);
        Assert.Equal(new[] { @"D:\a", @"D:\b", @"D:\c" }, r.Paths);
    }

    [Theory]
    [InlineData("--algo")]
    [InlineData("-a")]
    public void Parse_AlgoFlag_SeparateArg_SetsAlgorithmId(string flag)
    {
        var r = CliArgs.Parse(new[] { @"D:\x", flag, "dod7" });
        Assert.Equal("dod7", r.AlgorithmId);
        Assert.Equal(CliCommand.Shred, r.Command);
    }

    [Fact]
    public void Parse_AlgoFlag_EqualsForm_SetsAlgorithmId()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--algo=dod7" });
        Assert.Equal("dod7", r.AlgorithmId);
    }

    [Fact]
    public void Parse_AlgoFlag_AtEnd_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--algo" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--algo", r.UsageError);
    }

    [Theory]
    [InlineData("-y")]
    [InlineData("--yes")]
    public void Parse_Yes_VariantsSetAssumeYes(string flag)
    {
        var r = CliArgs.Parse(new[] { @"D:\x", flag });
        Assert.True(r.AssumeYes);
    }

    [Theory]
    [InlineData("-q")]
    [InlineData("--quiet")]
    public void Parse_Quiet_VariantsSetQuiet(string flag)
    {
        var r = CliArgs.Parse(new[] { @"D:\x", flag });
        Assert.True(r.Quiet);
    }

    [Fact]
    public void Parse_EmptyRecycle_SetsCommand()
    {
        var r = CliArgs.Parse(new[] { "--empty-recycle" });
        Assert.Equal(CliCommand.EmptyRecycle, r.Command);
        Assert.Empty(r.Paths);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_FreeSpace_SeparateDrive_SetsCommandAndDrive()
    {
        var r = CliArgs.Parse(new[] { "--free-space", @"D:\" });
        Assert.Equal(CliCommand.FreeSpaceWipe, r.Command);
        Assert.Equal(@"D:\", r.FreeSpaceDrive);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_FreeSpace_EqualsDrive_SetsCommandAndDrive()
    {
        var r = CliArgs.Parse(new[] { @"--free-space=D:\" });
        Assert.Equal(CliCommand.FreeSpaceWipe, r.Command);
        Assert.Equal(@"D:\", r.FreeSpaceDrive);
    }

    [Fact]
    public void Parse_FreeSpace_NoDrive_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { "--free-space" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--free-space", r.UsageError);
    }

    [Fact]
    public void Parse_EmptyRecycleWithPath_Conflict_SetsUsageError()
    {
        // --empty-recycle 后再跟位置参数:位置参数尝试隐式触发 Shred,但 Command 已经是 EmptyRecycle
        var r = CliArgs.Parse(new[] { "--empty-recycle", @"D:\a" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("子命令冲突", r.UsageError);
    }

    [Fact]
    public void Parse_FreeSpaceWithExtraPath_Conflict_SetsUsageError()
    {
        // 第三个参数是位置参数,与 FreeSpaceWipe 冲突
        var r = CliArgs.Parse(new[] { "--free-space", @"D:\", @"D:\a" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("子命令冲突", r.UsageError);
    }

    [Fact]
    public void Parse_PathThenEmptyRecycle_Conflict_SetsUsageError()
    {
        // 路径先到 → Command=Shred,再来 --empty-recycle 应触发冲突
        var r = CliArgs.Parse(new[] { @"D:\a", "--empty-recycle" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("子命令冲突", r.UsageError);
    }

    [Fact]
    public void Parse_UnknownOption_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { "--bogus" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--bogus", r.UsageError);
    }

    [Fact]
    public void Parse_HelpThenBogus_BothFlagsRecorded()
    {
        // 文档化 Parse 行为:同一次解析里 ShowHelp 和 UsageError 都可能被设置;
        // 由 CliRunner 决定谁优先(目前是 ShowHelp 先于 UsageError 检查)。
        var r = CliArgs.Parse(new[] { "--help", "--bogus" });
        Assert.True(r.ShowHelp);
        Assert.False(string.IsNullOrEmpty(r.UsageError));
    }

    [Fact]
    public void Parse_YesAndQuiet_BothSetWithShredCommand()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "-y", "-q" });
        Assert.True(r.AssumeYes);
        Assert.True(r.Quiet);
        Assert.Equal(CliCommand.Shred, r.Command);
    }

    [Fact]
    public void Parse_OnlyYes_NoCommand_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { "-y" });
        Assert.True(r.AssumeYes);
        Assert.Equal(CliCommand.None, r.Command);
        Assert.False(string.IsNullOrEmpty(r.UsageError));
    }

    // --- Dry-run / Explain ------------------------------------------------

    [Fact]
    public void Parse_DryRun_SetsFlagAndShredCommand()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--dry-run" });
        Assert.True(r.DryRun);
        Assert.Equal(CliCommand.Shred, r.Command);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_Explain_SetsFlagAndShredCommand()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--explain" });
        Assert.True(r.Explain);
        Assert.Equal(CliCommand.Shred, r.Command);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_DryRunAndExplain_CoexistWithoutConflict()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--dry-run", "--explain" });
        Assert.True(r.DryRun);
        Assert.True(r.Explain);
        Assert.Null(r.UsageError);
    }

    // --- --report / --report-format / --report-dir ------------------------

    [Fact]
    public void Parse_ReportFlag_SetsReport()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report" });
        Assert.True(r.Report);
        Assert.Null(r.ReportFormat); // 沿用 appsettings
        Assert.Null(r.ReportDir);
        Assert.Null(r.UsageError);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("html")]
    [InlineData("both")]
    public void Parse_ReportFormat_SeparateArg_SetsFormatAndImpliesReport(string fmt)
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-format", fmt });
        Assert.Equal(fmt, r.ReportFormat);
        Assert.True(r.Report); // 指定了格式应隐式启用 --report
        Assert.Null(r.UsageError);
    }

    [Theory]
    [InlineData("JSON", "json")]
    [InlineData("Html", "html")]
    [InlineData("  Both  ", "both")]
    public void Parse_ReportFormat_NormalizesCaseAndWhitespace(string raw, string expected)
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-format", raw });
        Assert.Equal(expected, r.ReportFormat);
        Assert.True(r.Report);
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_ReportFormat_EqualsForm_SetsFormat()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-format=html" });
        Assert.Equal("html", r.ReportFormat);
        Assert.True(r.Report);
    }

    [Fact]
    public void Parse_ReportFormat_AtEnd_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-format" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--report-format", r.UsageError);
    }

    [Fact]
    public void Parse_ReportFormat_InvalidValue_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-format", "xml" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--report-format", r.UsageError);
        Assert.Contains("json", r.UsageError);
    }

    [Fact]
    public void Parse_ReportFormat_EqualsForm_InvalidValue_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-format=yaml" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--report-format", r.UsageError);
    }

    [Fact]
    public void Parse_ReportDir_SeparateArg_SetsDirAndImpliesReport()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-dir", @"C:\Reports" });
        Assert.Equal(@"C:\Reports", r.ReportDir);
        Assert.True(r.Report); // 指定了目录应隐式启用 --report
        Assert.Null(r.UsageError);
    }

    [Fact]
    public void Parse_ReportDir_EqualsForm_SetsDir()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", @"--report-dir=C:\Reports" });
        Assert.Equal(@"C:\Reports", r.ReportDir);
        Assert.True(r.Report);
    }

    [Fact]
    public void Parse_ReportDir_AtEnd_SetsUsageError()
    {
        var r = CliArgs.Parse(new[] { @"D:\x", "--report-dir" });
        Assert.False(string.IsNullOrEmpty(r.UsageError));
        Assert.Contains("--report-dir", r.UsageError);
    }

    [Fact]
    public void Parse_ReportFlags_CombineWithAlgoAndQuiet()
    {
        // 文档化场景:CI 脚本同时指定算法、静默、报告。所有参数应同时生效。
        var r = CliArgs.Parse(new[]
        {
            @"D:\x", "--algo", "dod3", "--quiet", "--yes",
            "--report-format", "both", "--report-dir", @"C:\Out"
        });
        Assert.Equal(CliCommand.Shred, r.Command);
        Assert.Equal("dod3", r.AlgorithmId);
        Assert.True(r.Quiet);
        Assert.True(r.AssumeYes);
        Assert.Equal("both", r.ReportFormat);
        Assert.Equal(@"C:\Out", r.ReportDir);
        Assert.True(r.Report);
        Assert.Null(r.UsageError);
    }
}
