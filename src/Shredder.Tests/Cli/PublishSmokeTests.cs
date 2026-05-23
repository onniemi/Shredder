using System.Diagnostics;
using System.Text;
using Xunit;

namespace Shredder.Tests.Cli;

/// <summary>
/// Release 发布烟雾测试:跑一遍 <c>dotnet publish</c> 到临时目录,
/// 确保关键交付物(CLI 可执行 + App 可执行 + appsettings.json)都被正确产出。
/// </summary>
/// <remarks>
/// 这一组测试故意保持"轻烟雾"而不是"完整集成":
///   - 默认 framework-dependent(不开 <c>--self-contained</c>),把单次 publish 控制在十几秒级别;
///   - 不验证签名 / 不跑 WiX / 不验证 MSI,只验证编译 + 关键文件落盘;
///   - 如果当前 SDK 找不到 / 解决方案文件找不到 / 项目结构不匹配, 直接 Skip,
///     避免在第三方 build agent / sandbox 上把测试报红。
///
/// 若仍嫌慢,可在 CI 把这组测试单独走一个 stage:它们触发的 publish 大概比单元测试贵 10×。
/// </remarks>
public class PublishSmokeTests
{
    // dotnet publish 在一台冷机器上要走 restore→build→publish,给 5 分钟兜底
    private const int PublishTimeoutMs = 5 * 60 * 1000;

    [Fact]
    public async Task Cli_Publish_ProducesRunnableExeAndAppsettings()
    {
        if (!TryLocateRepo(out var repoRoot, out var slnPath)) return;
        if (!HasDotnetCli()) return;

        // sln 与各项目同处 src/ 目录,因此 repoRoot 即 src/。项目目录是 sln 的兄弟目录。
        var cliProj = Path.Combine(repoRoot, "Shredder.Cli", "Shredder.Cli.csproj");
        if (!File.Exists(cliProj)) return; // 项目结构不匹配,Skip 而不报错

        var publishDir = MakeTempPublishDir("cli");
        try
        {
            var publish = await RunDotnetAsync(repoRoot, new[]
            {
                "publish", cliProj,
                "-c", "Release",
                "-o", publishDir,
                // 不开 --self-contained / PublishSingleFile,保证烟雾测试在 CI 也可控
                "--nologo",
                "--verbosity", "minimal",
            }, PublishTimeoutMs);

            Assert.True(
                publish.ExitCode == 0,
                $"dotnet publish (CLI) 失败 exit={publish.ExitCode}\n--- stdout ---\n{publish.StdOut}\n--- stderr ---\n{publish.StdErr}");

            var exe = Path.Combine(publishDir, "shredder.exe");
            Assert.True(File.Exists(exe), $"发布目录应当包含 shredder.exe,实际目录内容:{ListDir(publishDir)}");
            Assert.True(File.Exists(Path.Combine(publishDir, "appsettings.json")),
                "发布目录应当包含 appsettings.json(CopyToOutputDirectory=PreserveNewest)。");

            // 起一个真子进程跑 --help, 烟雾验证 "发布包能被执行"。
            var help = await RunChildAsync(exe, new[] { "--help" }, timeoutMs: 30_000);
            Assert.Equal(0, help.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(help.StdOut), "已发布 CLI 的 --help 必须有输出。");

            var version = await RunChildAsync(exe, new[] { "--version" }, timeoutMs: 30_000);
            Assert.Equal(0, version.ExitCode);
            Assert.True(version.StdOut.Any(char.IsDigit),
                $"已发布 CLI 的 --version 输出应包含数字,实际:{version.StdOut}");
        }
        finally
        {
            SafeDelete(publishDir);
        }
    }

    [Fact]
    public async Task App_Publish_ProducesExeAndAppsettings()
    {
        if (!TryLocateRepo(out var repoRoot, out _)) return;
        if (!HasDotnetCli()) return;

        var appProj = Path.Combine(repoRoot, "Shredder.App", "Shredder.App.csproj");
        if (!File.Exists(appProj)) return;

        var publishDir = MakeTempPublishDir("app");
        try
        {
            // 注意:WPF (UseWPF=true) 在 framework-dependent publish 下产出 Shredder.exe + .dll,
            // 不需要 GUI 进程在 CI 里真启动,只验证文件存在即可。
            var publish = await RunDotnetAsync(repoRoot, new[]
            {
                "publish", appProj,
                "-c", "Release",
                "-o", publishDir,
                "--nologo",
                "--verbosity", "minimal",
            }, PublishTimeoutMs);

            Assert.True(
                publish.ExitCode == 0,
                $"dotnet publish (App) 失败 exit={publish.ExitCode}\n--- stdout ---\n{publish.StdOut}\n--- stderr ---\n{publish.StdErr}");

            // Shredder.App.csproj 设置了 AssemblyName=Shredder, 所以最终产物名是 Shredder.exe / Shredder.dll
            var exe = Path.Combine(publishDir, "Shredder.exe");
            var dll = Path.Combine(publishDir, "Shredder.dll");
            Assert.True(File.Exists(exe) || File.Exists(dll),
                $"发布目录应当包含 Shredder.exe 或 Shredder.dll,实际:{ListDir(publishDir)}");
            Assert.True(File.Exists(Path.Combine(publishDir, "appsettings.json")),
                "WPF 发布目录应当包含 appsettings.json。");
        }
        finally
        {
            SafeDelete(publishDir);
        }
    }

    [Fact]
    public async Task Cli_SingleFilePublish_RunsHelpAndDryRun()
    {
        if (!TryLocateRepo(out var repoRoot, out _)) return;
        if (!HasDotnetCli()) return;

        var cliProj = Path.Combine(repoRoot, "Shredder.Cli", "Shredder.Cli.csproj");
        if (!File.Exists(cliProj)) return;

        var publishDir = MakeTempPublishDir("cli-singlefile");
        try
        {
            var publish = await RunDotnetAsync(repoRoot, new[]
            {
                "publish", cliProj,
                "-c", "Release",
                "-r", "win-x64",
                "--self-contained", "true",
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-o", publishDir,
                "--nologo",
                "--verbosity", "minimal",
            }, PublishTimeoutMs);

            Assert.True(
                publish.ExitCode == 0,
                $"dotnet publish (CLI single-file) failed exit={publish.ExitCode}\n--- stdout ---\n{publish.StdOut}\n--- stderr ---\n{publish.StdErr}");

            var exe = Path.Combine(publishDir, "shredder.exe");
            Assert.True(File.Exists(exe), $"single-file publish should contain shredder.exe, actual:{ListDir(publishDir)}");

            var help = await RunChildAsync(exe, new[] { "--help" }, timeoutMs: 30_000);
            Assert.Equal(0, help.ExitCode);
            Assert.DoesNotContain("No Serilog", help.StdOut + help.StdErr);

            var sample = Path.Combine(publishDir, "sample.txt");
            await File.WriteAllTextAsync(sample, "temporary publish smoke content");
            var dryRun = await RunChildAsync(exe, new[] { sample, "--dry-run", "--algo", "single", "--quiet" }, timeoutMs: 30_000);
            Assert.Equal(0, dryRun.ExitCode);
            Assert.True(File.Exists(sample), "--dry-run must not delete the target file.");
            Assert.DoesNotContain("No Serilog", dryRun.StdOut + dryRun.StdErr);
        }
        finally
        {
            SafeDelete(publishDir);
        }
    }

    [Fact]
    public async Task App_SingleFilePublish_ProducesRunnableExeAndAppsettings()
    {
        if (!TryLocateRepo(out var repoRoot, out _)) return;
        if (!HasDotnetCli()) return;

        var appProj = Path.Combine(repoRoot, "Shredder.App", "Shredder.App.csproj");
        if (!File.Exists(appProj)) return;

        var publishDir = MakeTempPublishDir("app-singlefile");
        try
        {
            var publish = await RunDotnetAsync(repoRoot, new[]
            {
                "publish", appProj,
                "-c", "Release",
                "-r", "win-x64",
                "--self-contained", "true",
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-o", publishDir,
                "--nologo",
                "--verbosity", "minimal",
            }, PublishTimeoutMs);

            Assert.True(
                publish.ExitCode == 0,
                $"dotnet publish (App single-file) failed exit={publish.ExitCode}\n--- stdout ---\n{publish.StdOut}\n--- stderr ---\n{publish.StdErr}");

            Assert.True(File.Exists(Path.Combine(publishDir, "Shredder.exe")),
                $"single-file publish should contain Shredder.exe, actual:{ListDir(publishDir)}");
            Assert.True(File.Exists(Path.Combine(publishDir, "appsettings.json")),
                "single-file WPF publish should include appsettings.json.");
        }
        finally
        {
            SafeDelete(publishDir);
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// 从测试 bin 目录向上找,定位包含 <c>Shredder.sln</c> 的目录(本仓库里就是 <c>src/</c>)。
    /// 找不到则让 caller Skip,而不是抛错。
    /// </summary>
    private static bool TryLocateRepo(out string repoRoot, out string slnPath)
    {
        // 测试运行时 BaseDirectory 通常类似:
        //   <repo>/src/Shredder.Tests/bin/Release/net10.0-windows/
        // 因此向上爬最多 8 层去找 Shredder.sln(命中位置就是 <repo>/src/)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var sln = Path.Combine(dir.FullName, "Shredder.sln");
            if (File.Exists(sln))
            {
                repoRoot = dir.FullName;
                slnPath = sln;
                return true;
            }
        }
        repoRoot = string.Empty;
        slnPath = string.Empty;
        return false;
    }

    private static bool HasDotnetCli()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // dotnet 不在 PATH 上
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process.Start 的另一类失败,例如 PSI 配置无效
            return false;
        }
    }

    private static string MakeTempPublishDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shredder-publish-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<ProcResult> RunDotnetAsync(string cwd, string[] args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await RunProcessAsync(psi, timeoutMs);
    }

    private static async Task<ProcResult> RunChildAsync(string exe, string[] args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await RunProcessAsync(psi, timeoutMs);
    }

    private static async Task<ProcResult> RunProcessAsync(ProcessStartInfo psi, int timeoutMs)
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动子进程: {psi.FileName}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try { proc.StandardInput?.Close(); } catch { /* 部分进程未重定向 stdin */ }

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException(
                $"子进程 {psi.FileName} 超时({timeoutMs}ms)。");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcResult(proc.ExitCode, stdout, stderr);
    }

    private static string ListDir(string dir)
    {
        if (!Directory.Exists(dir)) return "(目录不存在)";
        try
        {
            return string.Join(", ", Directory.EnumerateFiles(dir).Select(Path.GetFileName));
        }
        catch
        {
            return "(枚举失败)";
        }
    }

    private static void SafeDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* OS 重启清理 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    private readonly record struct ProcResult(int ExitCode, string StdOut, string StdErr);
}
