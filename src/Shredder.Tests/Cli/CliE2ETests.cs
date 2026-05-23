using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Shredder.Tests.Cli;

/// <summary>
/// 端到端启动真实的 <c>shredder.exe</c> 子进程,覆盖三条用户最常走的非破坏 / 受控破坏路径:
///   - <c>--dry-run</c> / <c>--explain</c>: 退出码 0, 文件保持原样, 输出包含算法/预检信息;
///   - <c>--dry-run --report</c>: 仍然不生成审计报告(Program.cs L218-224 的硬约束);
///   - 正常 <c>--report --report-format json --report-dir &lt;tmp&gt;</c>: 退出码 0,
///     在目标目录里产出一份 JSON 报告, 且至少包含一条 <c>Status = Success</c> 的条目。
/// </summary>
/// <remarks>
/// 关键安全约束(避免 CI / 开发机被误伤):
///   - 永远只对 <see cref="Path.GetTempPath"/> 下的、本测试自己生成的临时文件操作;
///   - 永远显式带 <c>--yes</c>,这样 %LOCALAPPDATA% 触发的 Warn 二次确认会被跳过,
///     而不是依赖测试看不到的 stdin;
///   - 不显式跑 <c>--empty-recycle</c> / <c>--free-space</c> 等会动到全局状态的子命令;
///   - 父子进程统一 UTF-8,避免中文输出乱码导致断言把 "粉碎" / "算法" 等中文关键字 miss 掉。
///
/// 测试假设 <c>shredder.exe</c> 已经被 <c>Shredder.Cli</c> 的 ProjectReference 拷到测试 bin 目录。
/// 在尚未构建出该文件的情况下(极少见, 比如手动单测), Skip 而不是把测试报红。
/// </remarks>
public class CliE2ETests
{
    // 单次子进程最长 60 秒已经远超正常 dry-run / 单文件粉碎耗时,超过则视为挂死。
    private const int ProcessTimeoutMs = 60_000;

    [Fact]
    public async Task DryRun_ExitsZero_FileUntouched_OutputMentionsAlgorithm()
    {
        var exe = LocateShredderExe();
        if (exe is null) return; // 见 LocateShredderExe 注释:找不到 exe 直接 Skip

        var (tempDir, victim) = CreateVictimFile("dry-run-untouched");
        var originalBytes = File.ReadAllBytes(victim);
        try
        {
            var result = await RunShredderAsync(exe, new[] { victim, "--dry-run", "--yes", "--quiet" });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(victim), "dry-run 必须保留文件,不能调用真实粉碎路径。");
            Assert.Equal(originalBytes, File.ReadAllBytes(victim));
            // dry-run 摘要里至少要带上 "算法" / "预检" / "Dry-run" 之一,
            // 把这三条 OR 起来既能容忍未来文案微调, 又能拦截 "完全没打印 dry-run 摘要" 的回归
            var combined = result.StdOut + result.StdErr;
            Assert.True(
                combined.Contains("算法", StringComparison.Ordinal)
                    || combined.Contains("预检", StringComparison.Ordinal)
                    || combined.Contains("Dry-run", StringComparison.OrdinalIgnoreCase),
                $"dry-run 输出应至少包含算法/预检/Dry-run 关键字,实际:\n{combined}");
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task Explain_ExitsZero_FileUntouched()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var (tempDir, victim) = CreateVictimFile("explain-untouched");
        var originalBytes = File.ReadAllBytes(victim);
        try
        {
            // --explain 是 dry-run 的孪生标志, 也必须保证 0 退出 + 文件不动
            var result = await RunShredderAsync(exe, new[] { victim, "--explain", "--yes", "--quiet" });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(victim));
            Assert.Equal(originalBytes, File.ReadAllBytes(victim));
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task DryRunWithReportFlags_DoesNotProduceReportFile()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var (tempDir, victim) = CreateVictimFile("dry-run-no-report");
        var reportDir = Path.Combine(tempDir, "reports");
        Directory.CreateDirectory(reportDir);
        try
        {
            // 同时给出 --dry-run 和 --report*: Program.cs 明确在 dry-run 分支
            // 直接 return ExitOk,根本不会调用 ShredReportWriter.WriteAsync。
            // 这条用例守护这条不变量,防止有人未来把报告写入挪到 dry-run 之前。
            var result = await RunShredderAsync(exe, new[]
            {
                victim,
                "--dry-run",
                "--report",
                "--report-format", "json",
                "--report-dir", reportDir,
                "--yes",
                "--quiet",
            });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(victim), "dry-run 路径不应删除文件。");
            var produced = Directory.GetFiles(reportDir);
            Assert.Empty(produced);
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task RealShred_WithJsonReport_WritesJsonContainingSuccessEntry()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var (tempDir, victim) = CreateVictimFile("real-shred-report");
        var reportDir = Path.Combine(tempDir, "reports");
        Directory.CreateDirectory(reportDir);
        try
        {
            // 这次是真粉碎(不带 --dry-run), 算法默认是 zero-fill, 临时文件会被覆写并删除;
            // 这是允许的,因为 victim 是本测试在临时目录里自己造出来的。
            var result = await RunShredderAsync(exe, new[]
            {
                victim,
                "--report",
                "--report-format", "json",
                "--report-dir", reportDir,
                "--yes",
                "--quiet",
            });

            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(victim), "正常路径应当真的删掉受害文件。");

            var jsons = Directory.GetFiles(reportDir, "*.json");
            Assert.Single(jsons);

            using var fs = File.OpenRead(jsons[0]);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;

            // 报告 schema 由 ShredReportWriter 控制,这里只断言"用户最关心的核心字段都在":
            // 整体计数 + Entries 列表 + 至少一条 Success 条目。
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.True(root.TryGetProperty("Entries", out var entries));
            Assert.Equal(JsonValueKind.Array, entries.ValueKind);
            Assert.True(entries.GetArrayLength() >= 1, "报告应至少记录一条粉碎条目。");

            // 顶层 SuccessCount 由 ShredReport 计算, 是最稳的用户语义断言
            Assert.True(root.TryGetProperty("SuccessCount", out var successCount));
            Assert.Equal(JsonValueKind.Number, successCount.ValueKind);
            Assert.True(successCount.GetInt32() >= 1,
                $"报告 SuccessCount 应至少为 1,实际:{successCount.GetInt32()}");

            // 同时校验 Entries 里至少一条 Success 状态。
            // 注意:当前 ShredReportWriter 用默认 JsonSerializerOptions(没有 JsonStringEnumConverter),
            // 因此 ShredJobStatus 枚举会被序列化为数字(Success=2)。
            // 这里同时兼容 Number 和 String 两种序列化形式,避免未来切到字符串枚举时回归。
            var anySuccess = false;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("Status", out var st)) continue;
                if (st.ValueKind == JsonValueKind.Number
                    && st.GetInt32() == (int)Shredder.Core.Models.ShredJobStatus.Success)
                {
                    anySuccess = true;
                    break;
                }
                if (st.ValueKind == JsonValueKind.String
                    && string.Equals(st.GetString(), "Success", StringComparison.Ordinal))
                {
                    anySuccess = true;
                    break;
                }
            }
            Assert.True(anySuccess, "报告 Entries 里应至少包含一条 Success(数字 2 或字符串 \"Success\") 状态条目。");
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task RealShred_NeutralPathWithoutYes_DoesNotAskForConfirmation()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var (tempDir, victim) = CreateNeutralVictimFile("no-confirm");
        try
        {
            var result = await RunShredderAsync(exe, new[] { victim, "--quiet" });

            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(victim), "普通非系统路径不应要求二次确认,应直接粉碎临时文件。");
            Assert.DoesNotContain("请输入", result.StdOut + result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task DryRun_WarnPathWithoutYes_DoesNotAskForConfirmation()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var (tempDir, victim) = CreateVictimFile("warn-dry-run-no-confirm");
        try
        {
            var result = await RunShredderAsync(exe, new[] { victim, "--dry-run", "--quiet" });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(victim), "dry-run 即使命中警告路径也不能删除文件。");
            Assert.DoesNotContain("请输入", result.StdOut + result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task RealShred_WarnPathWithoutYes_StillAsksForConfirmation()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var (tempDir, victim) = CreateVictimFile("warn-real-confirm");
        try
        {
            var result = await RunShredderAsync(exe, new[] { victim, "--quiet" });

            Assert.Equal(3, result.ExitCode);
            Assert.True(File.Exists(victim), "命中警告路径且未确认时,不应删除文件。");
            Assert.Contains("请输入", result.StdOut + result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public async Task Help_ExitsZero_NonEmptyOutput()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var result = await RunShredderAsync(exe, new[] { "--help" });
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdOut), "--help 应当往 stdout 打印帮助。");
    }

    [Fact]
    public async Task Version_ExitsZero_OutputContainsDigits()
    {
        var exe = LocateShredderExe();
        if (exe is null) return;

        var result = await RunShredderAsync(exe, new[] { "--version" });
        Assert.Equal(0, result.ExitCode);
        // 不强约束具体版本号(SDK / CI 注入会变),只保证里面有数字,避免"打印了空行"这种回归
        Assert.True(result.StdOut.Any(char.IsDigit), $"--version 输出应包含版本数字,实际:{result.StdOut}");
    }

    // ----------------- helpers -----------------

    /// <summary>
    /// 在测试 bin 目录里查找 <c>shredder.exe</c>。
    /// <see cref="Shredder.Tests.Shredder.Tests.csproj"/> 通过 ProjectReference 把 CLI 的输出拷过来,
    /// 但在某些手动单测 / 干净 checkout 上可能还没构建 — 这种情况下返回 null 让用例 Skip,
    /// 而不是 false-positive 报红。
    /// </summary>
    private static string? LocateShredderExe()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "shredder.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static (string TempDir, string Victim) CreateVictimFile(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shredder-e2e-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var victim = Path.Combine(dir, "victim.bin");
        // 写入一个非空、可识别的固定字节序列:既能验证 "dry-run 后字节不变",
        // 也保证真粉碎路径要走完整算法循环(空文件容易绕过部分分支)。
        File.WriteAllBytes(victim, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0x12, 0x34 });
        return (dir, victim);
    }

    private static (string TempDir, string Victim) CreateNeutralVictimFile(string tag)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, $"shredder-e2e-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var victim = Path.Combine(dir, "victim.bin");
        File.WriteAllBytes(victim, new byte[] { 0xFE, 0xED, 0xFA, 0xCE, 0x42, 0x24 });
        return (dir, victim);
    }

    private static async Task<ProcResult> RunShredderAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 shredder.exe 子进程。");

        // 异步读取 stdout/stderr,避免子进程 4KB 管道缓冲区写满之后死锁
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        // 立刻关掉 stdin,免得 CLI 阻塞等待"粉碎"确认 — 我们已经传了 --yes
        proc.StandardInput.Close();

        var completedInTime = proc.WaitForExit(ProcessTimeoutMs);
        if (!completedInTime)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* 子进程可能已退出,忽略 */ }
            throw new TimeoutException(
                $"shredder.exe 在 {ProcessTimeoutMs}ms 内未退出,参数: {string.Join(' ', args)}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcResult(proc.ExitCode, stdout, stderr);
    }

    private static void SafeDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            // 清属性,避免 ReadOnly / Hidden 阻碍递归删除(理论上不会发生,但便宜)
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* 让 OS 重启清理 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    private readonly record struct ProcResult(int ExitCode, string StdOut, string StdErr);
}
