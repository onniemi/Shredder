using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Shredder.Cli;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.Diagnostics;
using Shredder.Core.Extensions;
using Shredder.Core.Models;
using Shredder.Core.Reporting;
using Shredder.Core.Security;
using Shredder.Core.Services;

// 控制台编码:appsettings 的关键字默认为中文「粉碎」,显式锁 UTF-8 避免老版 cmd 乱码
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

return await CliRunner.RunAsync(args).ConfigureAwait(false);

namespace Shredder.Cli
{
    /// <summary>
    /// shredder.exe 入口。把命令行参数解析为子命令,调用 Shredder.Core 的对应服务。
    /// 退出码:
    /// <list type="bullet">
    ///   <item>0 — 成功</item>
    ///   <item>1 — 用法错误 / 路径不存在 / 其它通用失败</item>
    ///   <item>2 — 命中安全黑名单(Forbidden)</item>
    ///   <item>3 — 用户拒绝二次确认</item>
    ///   <item>4 — 多路径下部分失败</item>
    ///   <item>5 — 收到 Ctrl-C,操作中止</item>
    /// </list>
    /// </summary>
    internal static class CliRunner
    {
        private const int ExitOk = 0;
        private const int ExitUsage = 1;
        private const int ExitForbidden = 2;
        private const int ExitDeclined = 3;
        private const int ExitPartial = 4;
        private const int ExitCancelled = 5;

        [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "CLI 顶层边界:必须捕获所有异常,转成退出码后返回,否则 .NET 默认会把堆栈打到 stderr 并以非零码退出,用户体验差。")]
        public static async Task<int> RunAsync(string[] args)
        {
            var parsed = CliArgs.Parse(args);
            if (parsed.ShowHelp)    { PrintHelp(); return ExitOk; }
            if (parsed.ShowVersion) { PrintVersion(); return ExitOk; }
            if (parsed.UsageError is not null)
            {
                Console.Error.WriteLine($"参数错误:{parsed.UsageError}");
                Console.Error.WriteLine("使用 --help 查看用法。");
                return ExitUsage;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                if (!cts.IsCancellationRequested)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("收到 Ctrl-C,正在中止…");
                    cts.Cancel();
                }
            };

            IHost? host = null;
            try
            {
                host = BuildHost(args, parsed);
                await host.StartAsync(cts.Token).ConfigureAwait(false);

                int code = parsed.Command switch
                {
                    CliCommand.Shred         => await RunShredAsync(host.Services, parsed, cts.Token).ConfigureAwait(false),
                    CliCommand.EmptyRecycle  => await RunEmptyRecycleAsync(host.Services, parsed, cts.Token).ConfigureAwait(false),
                    CliCommand.FreeSpaceWipe => await RunFreeSpaceAsync(host.Services, parsed, cts.Token).ConfigureAwait(false),
                    _                        => ExitUsage,
                };
                return code;
            }
            catch (OperationCanceledException)
            {
                return ExitCancelled;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"启动失败:{ex.Message}");
                return ExitUsage;
            }
            finally
            {
                if (host is not null)
                {
                    try { await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* 退出阶段吞掉 */ }
                    host.Dispose();
                }
                Log.CloseAndFlush();
            }
        }

        private static IHost BuildHost(string[] args, CliArgs parsed)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    cfg.AddInMemoryCollection(ShredderDefaultConfiguration.Create());
                    cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                        optional: true, reloadOnChange: true);
                    cfg.AddEnvironmentVariables(prefix: "SHREDDER_");
                    cfg.AddCommandLine(args);
                })
                .UseSerilog((ctx, sp, lc) => SerilogSetup.Configure(ctx.Configuration, sp, lc))
                .ConfigureServices((ctx, services) =>
                {
                    services.AddShredderCore(ctx.Configuration);
                    services.PostConfigure<ShredderOptions>(options =>
                    {
                        options.Reporting.OutputDirectory = ShredderAppPaths.ReportsDirectory;
                        options.Logging.OutputDirectory = ShredderAppPaths.LogsDirectory;
                    });
                    ApplyReportingOverrides(services, parsed);
                })
                .Build();
        }

        /// <summary>
        /// 把 CLI 的 <c>--report / --report-format / --report-dir</c> 覆盖到
        /// <see cref="ShredderOptions.Reporting"/> 上。仅当用户显式指定才覆盖,避免误关闭
        /// appsettings 已配置的报告输出。
        /// </summary>
        internal static void ApplyReportingOverrides(IServiceCollection services, CliArgs parsed)
        {
            if (!parsed.Report
                && string.IsNullOrWhiteSpace(parsed.ReportFormat)
                && string.IsNullOrWhiteSpace(parsed.ReportDir))
            {
                return;
            }

            services.PostConfigure<ShredderOptions>(opts =>
            {
                // 任何一个报告相关 CLI 标志触发都意味着用户「想要报告」。
                opts.Reporting.Enabled = true;

                // --report-format 指定了具体输出格式时,严格按用户意图设置(包括关掉未选中的格式)。
                if (!string.IsNullOrWhiteSpace(parsed.ReportFormat))
                {
                    var fmt = parsed.ReportFormat;
                    opts.Reporting.FormatJson = fmt is "json" or "both";
                    opts.Reporting.FormatHtml = fmt is "html" or "both";
                }

                if (!string.IsNullOrWhiteSpace(parsed.ReportDir))
                {
                    opts.Reporting.OutputDirectory = parsed.ReportDir!;
                }
            });
        }

        [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "CLI 单个目标失败必须捕获:迁移到下一个目标继续粉碎,最后用 ExitPartial 反馈。")]
        private static async Task<int> RunShredAsync(IServiceProvider sp, CliArgs parsed, CancellationToken ct)
        {
            var shred = sp.GetRequiredService<ShredService>();
            var guard = sp.GetRequiredService<PathSafetyGuard>();
            var options = sp.GetRequiredService<IOptions<ShredderOptions>>().Value;
            var reportWriter = sp.GetRequiredService<IShredReportWriter>();

            bool dryRun = parsed.DryRun || parsed.Explain;

            // 安全分级 + 二次确认(dry-run 仍跑安全分级,只是不要求二次确认)
            var checkedPaths = new List<(string Path, bool IsDirectory)>();
            foreach (var raw in parsed.Paths)
            {
                string full;
                try { full = Path.GetFullPath(raw); }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    Console.Error.WriteLine($"[拒绝] {raw}:路径无法解析({ex.Message})");
                    return ExitUsage;
                }

                bool isDir = Directory.Exists(full);
                bool isFile = !isDir && File.Exists(full);
                if (!isDir && !isFile)
                {
                    Console.Error.WriteLine($"[拒绝] {full}:不存在。");
                    return ExitUsage;
                }

                var decision = guard.Evaluate(full);
                switch (decision.Level)
                {
                    case PathSafetyGuard.PathSafetyLevel.Forbidden:
                        Console.Error.WriteLine($"[禁止] {full}:{decision.Reason}");
                        return ExitForbidden;
                    case PathSafetyGuard.PathSafetyLevel.Warn:
                        Console.Error.WriteLine($"[警告] {full}:{decision.Reason}");
                        // dry-run 模式只是预检,不实际删,无需再次拦截
                        if (!dryRun && !ConfirmOrFail(options.Ui.ConfirmationKeyword, parsed.AssumeYes))
                            return ExitDeclined;
                        break;
                }
                checkedPaths.Add((full, isDir));
            }

            string? algoId = ResolveAlgorithmId(parsed.AlgorithmId);

            // --- Dry-run / Explain 分支 ----------------------------------------
            // 必须保证不修改任何文件:
            //   - 不要求最终二次确认(因为不会有任何写入)
            //   - 不调用 ShredService.ShredAsync
            //   - 不生成审计报告(没有「执行结果」可记录)
            //   - 仍调用 PreviewAlgorithm,把会被选中的算法明确展示给用户
            if (dryRun)
            {
                PrintDryRunPreview(shred, checkedPaths, algoId);
                return ExitOk;
            }

            int failures = 0;
            using var renderer = new ConsoleProgressRenderer(parsed.Quiet);
            var entries = new List<ShredAuditEntry>(checkedPaths.Count);
            var batchStartedAt = DateTimeOffset.Now;

            foreach (var (path, isDir) in checkedPaths)
            {
                if (ct.IsCancellationRequested)
                {
                    await TryWriteReportAsync(reportWriter, entries, batchStartedAt).ConfigureAwait(false);
                    return ExitCancelled;
                }

                long sizeBytes = isDir ? 0 : SafeFileLength(path);
                var job = new ShredJob
                {
                    Path = path,
                    IsDirectory = isDir,
                    SizeBytes = sizeBytes,
                    AlgorithmId = algoId,
                };

                // 提前 Preview 一次:即使后续 ShredAsync 抛异常,审计报告里也能有正确的算法记录
                IShredAlgorithm? algoPreview = null;
                try { algoPreview = shred.PreviewAlgorithm(path, algoId); }
                catch (InvalidOperationException) { /* 没有可用算法时,ShredAsync 会再次抛出真实异常 */ }

                var jobStartedAt = DateTimeOffset.Now;
                ShredJobStatus statusOutcome;
                string? errorMessage = null;

                renderer.BeginJob(path);
                try
                {
                    await shred.ShredAsync(job, renderer, ct).ConfigureAwait(false);
                    statusOutcome = ShredJobStatus.Success;
                    renderer.EndJob(success: true, message: null);
                }
                catch (OperationCanceledException)
                {
                    entries.Add(BuildEntry(path, isDir, sizeBytes, algoPreview,
                        jobStartedAt, ShredJobStatus.Cancelled, errorMessage: "已取消"));
                    await TryWriteReportAsync(reportWriter, entries, batchStartedAt).ConfigureAwait(false);
                    return ExitCancelled;
                }
                catch (Exception ex)
                {
                    failures++;
                    statusOutcome = ShredJobStatus.Failed;
                    errorMessage = ex.Message;
                    renderer.EndJob(success: false, message: ex.Message);
                }

                entries.Add(BuildEntry(path, isDir, sizeBytes, algoPreview,
                    jobStartedAt, statusOutcome, errorMessage));
            }

            await TryWriteReportAsync(reportWriter, entries, batchStartedAt).ConfigureAwait(false);

            if (failures == 0) return ExitOk;
            if (failures == checkedPaths.Count) return ExitUsage; // 全失败按通用错误
            return ExitPartial;
        }

        private static void PrintDryRunPreview(
            ShredService shred,
            List<(string Path, bool IsDirectory)> targets,
            string? algoId)
        {
            Console.WriteLine();
            Console.WriteLine($"[dry-run] 共 {targets.Count} 个目标,本次不会修改任何文件:");
            foreach (var (path, isDir) in targets)
            {
                IShredAlgorithm algo;
                try { algo = shred.PreviewAlgorithm(path, algoId); }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"  {(isDir ? "目录" : "文件")}  {path}");
                    Console.WriteLine($"    [无法解析算法] {ex.Message}");
                    continue;
                }

                long size = isDir ? 0 : SafeFileLength(path);
                string kind = isDir ? "目录" : "文件";
                string sizeText = isDir ? "—" : $"{size:N0} 字节";
                Console.WriteLine($"  {kind}  {path}");
                Console.WriteLine($"    算法 = {algo.Id} ({algo.Name}), 趟数 = {algo.PassCount}, 大小 = {sizeText}");
            }
        }

        private static ShredAuditEntry BuildEntry(
            string path,
            bool isDirectory,
            long sizeBytes,
            IShredAlgorithm? algorithm,
            DateTimeOffset startedAt,
            ShredJobStatus status,
            string? errorMessage)
        {
            return new ShredAuditEntry
            {
                Path = path,
                IsDirectory = isDirectory,
                SizeBytes = sizeBytes,
                AlgorithmId = algorithm?.Id,
                AlgorithmName = algorithm?.Name,
                PassCount = algorithm?.PassCount ?? 0,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.Now,
                Status = status,
                ErrorMessage = errorMessage,
            };
        }

        [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "报告写入失败绝不能让整个批次的退出码变成失败 —— 粉碎工作已经完成。")]
        private static async Task TryWriteReportAsync(
            IShredReportWriter writer,
            List<ShredAuditEntry> entries,
            DateTimeOffset batchStartedAt)
        {
            if (entries.Count == 0) return;

            var report = new ShredReport
            {
                ReportId = Guid.NewGuid().ToString("N"),
                StartedAt = batchStartedAt,
                CompletedAt = DateTimeOffset.Now,
                AppVersion = GetInformationalVersion(),
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                Entries = entries.ToArray(),
            };

            try
            {
                // 用 CancellationToken.None:报告是收尾动作,即便用户已 Ctrl-C 也要把已完成的工作落盘
                var outputPath = await writer.WriteAsync(report, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(outputPath))
                    Console.WriteLine($"审计报告已生成:{outputPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"审计报告写入失败:{ex.Message}");
            }
        }

        private static string GetInformationalVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "回收站清空异常必须转成退出码,否则会冲到 main 顶层导致用户只看到堆栈。")]
        private static async Task<int> RunEmptyRecycleAsync(IServiceProvider sp, CliArgs parsed, CancellationToken ct)
        {
            var svc = sp.GetRequiredService<RecycleBinService>();
            var options = sp.GetRequiredService<IOptions<ShredderOptions>>().Value;

            if (!parsed.AssumeYes)
            {
                Console.WriteLine("即将清空回收站(含粉碎覆写),该操作不可逆。");
                if (!ConfirmOrFail(options.Ui.ConfirmationKeyword, assumeYes: false))
                    return ExitDeclined;
            }

            using var renderer = new ConsoleProgressRenderer(parsed.Quiet);
            renderer.BeginJob("(回收站)");
            try
            {
                var result = await svc.EmptyAsync(renderer, ct).ConfigureAwait(false);
                var summary = FormatRecycleSummary(result);
                renderer.EndJob(success: result.OverallSucceeded, message: summary);
                if (!result.OverallSucceeded)
                {
                    // 让脚本调用方能从 stderr 拿到结构化摘要
                    Console.Error.WriteLine(summary);
                }
                return result.OverallSucceeded ? ExitOk : ExitUsage;
            }
            catch (OperationCanceledException) { return ExitCancelled; }
            catch (Exception ex)
            {
                renderer.EndJob(success: false, message: ex.Message);
                return ExitUsage;
            }
        }

        private static string FormatRecycleSummary(Shredder.Core.Models.RecycleBinEmptyResult r)
        {
            var shellPart = r.ShellHResult is null
                ? "shell=(跳过)"
                : (r.ShellHResult is 0 or 1
                    ? $"shell=OK(0x{r.ShellHResult:X8})"
                    : $"shell=FAIL(0x{r.ShellHResult:X8})");
            var basePart = $"枚举 {r.TotalCandidates}, 成功 {r.Succeeded}, 失败 {r.Failed}, 跳过 {r.Skipped}, {shellPart}";
            if (r.Failed == 0) return basePart;

            // 失败明细已脱敏,可以安全打印前几条用于排错
            const int previewCount = 3;
            var preview = string.Join("; ", r.FailedItems
                .Take(previewCount)
                .Select(f => $"{f.PathRedacted}:{f.Reason}"));
            if (r.FailedItems.Count > previewCount)
                preview += $"; (+{r.FailedItems.Count - previewCount} more)";
            return basePart + " | 失败明细: " + preview;
        }

        [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "空闲空间擦写异常必须转成退出码,否则会冲到 main 顶层导致用户只看到堆栈。")]
        private static async Task<int> RunFreeSpaceAsync(IServiceProvider sp, CliArgs parsed, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(parsed.FreeSpaceDrive))
            {
                Console.Error.WriteLine("--free-space 需要指定盘符,例如 D:\\");
                return ExitUsage;
            }
            var svc = sp.GetRequiredService<FreeSpaceService>();
            var options = sp.GetRequiredService<IOptions<ShredderOptions>>().Value;

            var drive = Path.GetFullPath(parsed.FreeSpaceDrive!);
            if (!Directory.Exists(drive))
            {
                Console.Error.WriteLine($"目标盘不存在或不可访问:{drive}");
                return ExitUsage;
            }

            if (!parsed.AssumeYes)
            {
                Console.WriteLine($"即将填满 {drive} 的空闲空间并粉碎临时文件,该操作不可逆。");
                if (!ConfirmOrFail(options.Ui.ConfirmationKeyword, assumeYes: false))
                    return ExitDeclined;
            }

            using var renderer = new ConsoleProgressRenderer(parsed.Quiet);
            renderer.BeginJob($"(空闲空间擦除 {drive})");
            try
            {
                var wipeResult = await svc.WipeAsync(drive, renderer, ct).ConfigureAwait(false);
                string outcomeText = wipeResult.Outcome switch
                {
                    FreeSpaceWipeOutcome.OverwriteCompleted =>
                        wipeResult.Message ?? $"覆写完成,累计写入 {wipeResult.BytesWritten:N0} 字节。",
                    FreeSpaceWipeOutcome.TrimFallbackInvoked =>
                        wipeResult.Message ?? "已对 SSD 重新发送 TRIM(defrag /L)。",
                    FreeSpaceWipeOutcome.SkippedSsdNoFallback =>
                        wipeResult.Message ?? "目标为 SSD/NVMe,已按配置跳过软件覆写。",
                    _ => wipeResult.Message ?? string.Empty,
                };
                renderer.EndJob(success: true, message: outcomeText);
                return ExitOk;
            }
            catch (OperationCanceledException) { return ExitCancelled; }
            catch (Exception ex)
            {
                renderer.EndJob(success: false, message: ex.Message);
                return ExitUsage;
            }
        }

        /// <summary>
        /// 用户友好的算法别名:fast / dod3 / dod7 / single / random / zero / crypto / clear。
        /// 也接受原始算法 ID(FastDelete / Purge-3Pass / Purge-7Pass / Clear / ZeroFill / CryptoErase)。
        /// 返回 null 时由 ShredService 根据 SSD 检测自行选默认算法。
        /// </summary>
        internal static string? ResolveAlgorithmId(string? userAlias)
        {
            if (string.IsNullOrWhiteSpace(userAlias)) return null;
            return userAlias.Trim().ToLowerInvariant() switch
            {
                "fast" or "quick" or "fastdelete" => ShredAlgorithmIds.FastDelete,
                "dod3" or "purge-3pass"          => ShredAlgorithmIds.Purge3Pass,
                "dod7" or "purge-7pass"          => ShredAlgorithmIds.Purge7Pass,
                "single" or "random" or "clear"  => ShredAlgorithmIds.Clear,
                "zero" or "zerofill"             => ShredAlgorithmIds.ZeroFill,
                "crypto" or "cryptoerase"        => ShredAlgorithmIds.CryptoErase,
                _ => userAlias, // 透传,Registry 找不到时会回落到默认
            };
        }

        private static bool ConfirmOrFail(string keyword, bool assumeYes)
        {
            if (assumeYes) return true;
            Console.Write($"请输入「{keyword}」确认继续(其它任何输入或回车将取消):");
            var input = Console.ReadLine();
            if (string.Equals(input?.Trim(), keyword, StringComparison.Ordinal)) return true;
            Console.Error.WriteLine("已取消。");
            return false;
        }

        internal static long SafeFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        private static void PrintHelp()
        {
            var exe = "shredder";
            Console.WriteLine($"""
                {exe} —— 命令行版「粉碎一切」(Windows 安全文件销毁)

                用法:
                  {exe} <路径>... [--algo <算法>] [-y|--yes] [-q|--quiet]
                                  [--dry-run|--explain]
                                  [--report] [--report-format X] [--report-dir DIR]
                  {exe} --empty-recycle [-y|--yes] [-q|--quiet]
                  {exe} --free-space <盘符> [-y|--yes] [-q|--quiet]
                  {exe} --help | --version

                算法别名(--algo):
                  fast        快速粉碎:截断 + 随机改名 + 删除(适合大批量/大文件快速处理)
                  dod3        DoD 5220.22-M 3 趟
                  dod7        DoD 5220.22-M 7 趟        (HDD 默认,更强但更慢)
                  single      单趟随机覆写              (快速,SSD 友好)
                  zero        单趟全零覆写              (TRIM 友好)
                  crypto      加密擦除                  (SSD/NVMe 默认)
                  也可直接传 ID:FastDelete / Purge-3Pass / Purge-7Pass / Clear / ZeroFill / CryptoErase
                  省略时,SSD 走 CryptoErase,HDD/未知走 Purge-7Pass。

                选项:
                  -y, --yes              跳过二次确认(脚本/自动化场景)
                  -q, --quiet            不渲染实时进度,仅输出最终结果
                      --algo X           指定算法,见上表
                      --dry-run          仅做风险预检,不修改任何文件,打印每个目标会走哪个算法
                      --explain          同 --dry-run,语义偏向「解释会发生什么」
                      --report           生成本次批次的审计报告(JSON / HTML)
                      --report-format X  报告格式:json / html / both(指定时隐式启用 --report)
                      --report-dir DIR   报告输出目录(默认 程序目录\data\reports)
                      --help             显示本帮助
                      --version          显示版本号

                退出码:
                  0  成功            2  路径命中黑名单
                  1  用法错误/失败    3  用户取消
                  4  部分失败         5  Ctrl-C 中止

                注意:粉碎操作不可逆。系统目录(Windows / Program Files / 盘符根)
                由内置安全护栏永久禁止,无法用 --yes 跳过。
                --dry-run / --explain 模式下不会写入任何文件,也不会生成审计报告。
                """);
        }

        private static void PrintVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";
            Console.WriteLine($"shredder {ver}");
        }
    }
}
